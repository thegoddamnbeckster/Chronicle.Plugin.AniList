using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Chronicle.Plugin.AniList;

/// <summary>
/// Chronicle metadata provider for AniList (anime). No API key or account is required.
///
/// Media types and what they map to:
///   anime         Show / Season / Episode. AniList has one entry per season, so the show is the first season's entry and the
///                 seasons are found by following SEQUEL links (see <see cref="AniListSeasons"/>).
///   anime_movies  A single film: an entry whose format is MOVIE.
///
/// External ID formats stored (source = "anilist"):
///   show:{id}                          a show (the first season's AniList id)
///   show:{id}/season:{n}               a season
///   show:{id}/season:{n}/episode:{e}   an episode (numbered across the whole season, even when a season is split in cours)
///   movie:{id}                         a film
/// A bare number, an anilist.co URL, "mal:{id}" and a myanimelist.net URL are all accepted by Fix match.
///
/// Cross-reference ids read from KnownExternalIds: "anilist", "parent_anilist", "mal", "parent_mal".
/// </summary>
public sealed class AniListMetadataProvider : IMetadataProvider, IDisposable
{
    public string PluginId => "chronicle.plugin.anilist";
    public string Name     => "AniList";
    public string Version  => "1.0.4";
    public string Author   => "Chronicle Contributors";

    // Many shows and films are filed on AniList as OVAs or specials (a 1988 feature-length OVA, a 22-episode OVA series), so
    // they are searched too; the title and year decide the match.
    private static readonly string[] ShowFormats  = ["TV", "TV_SHORT", "ONA", "OVA"];
    private static readonly string[] MovieFormats = ["MOVIE", "OVA", "SPECIAL"];

    private AniListClient? _client;
    private HttpClient?    _ownedHttp;
    private readonly ILogger _logger;
    private string _titleLanguage = "english";
    private bool   _includeAdult;

    // A short-lived memory of entries just fetched, so walking a season chain does not fetch the same entry once per
    // question. It holds nothing between app runs and is never authoritative.
    private readonly ConcurrentDictionary<int, (DateTimeOffset At, AniMedia Media)> _recent = new();
    private readonly ConcurrentDictionary<int, (DateTimeOffset At, Dictionary<int, string> Dates)> _aired = new();
    private static readonly TimeSpan RecentFor = TimeSpan.FromMinutes(10);
    private const int RecentMax = 400;

    public AniListMetadataProvider() : this(NullLogger.Instance) { }
    public AniListMetadataProvider(ILogger logger) => _logger = logger;
    internal AniListMetadataProvider(AniListClient client) : this(NullLogger.Instance) => _client = client;

    // ── capabilities ──────────────────────────────────────────────────────────

    private static readonly MediaTypeSupport[] SupportedTypes =
    [
        new MediaTypeSupport
        {
            MediaTypeName   = "anime",
            DisplayName     = "Anime",
            HierarchyLevels = 3,
            HierarchyLabels = ["Show", "Season", "Episode"],
            DefaultPriority = 12,
            SupportedFields = ["title", "overview", "year", "poster_url", "banner_url", "genres", "tags", "cast", "crew", "rating"],
            LevelFields = new Dictionary<int, List<string>>
            {
                [1] = ["title", "overview", "year", "poster_url"],
                [2] = ["title", "year", "poster_url"],
            },
        },
        // Standalone anime films are flat like movies; the same type TMDB and Simkl declare.
        new MediaTypeSupport
        {
            MediaTypeName   = "anime_movies",
            DisplayName     = "Anime Movies",
            HierarchyLevels = 1,
            HierarchyLabels = ["Movie"],
            DefaultPriority = 12,
            SupportedFields = ["title", "overview", "year", "poster_url", "banner_url", "runtime_minutes", "genres", "tags", "cast", "crew", "rating"],
        },
    ];

    public MediaTypeSupport[] GetSupportedMediaTypes() => SupportedTypes;

    public IReadOnlyList<string> GetAcceptedCrossRefPrefixes() => ["anilist:", "mal:"];

    public PluginSettingsSchema GetSettingsSchema() => new()
    {
        Settings =
        [
            new SettingDefinition
            {
                Key = "title_language", Label = "Title language", Type = SettingType.Dropdown, DefaultValue = "english",
                Description = "Which of AniList's titles is used as the item's name. The other titles are kept as alternate names.",
                Options =
                [
                    new SelectOption { Value = "english", Label = "English (falls back to romaji)" },
                    new SelectOption { Value = "romaji",  Label = "Romaji" },
                    new SelectOption { Value = "native",  Label = "Native (Japanese)" },
                ],
            },
            new SettingDefinition
            {
                Key = "include_adult", Label = "Include adult titles", Type = SettingType.Boolean, DefaultValue = "false",
                Description = "Allow AniList entries marked adult to be matched.",
            },
        ],
    };

    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        _titleLanguage = settings.TryGetValue("title_language", out var lang) && lang is "english" or "romaji" or "native" ? lang : "english";
        _includeAdult  = settings.TryGetValue("include_adult", out var adult) && bool.TryParse(adult, out var on) && on;

        if (_client is not null && _ownedHttp is null) return;   // a client was supplied directly (tests)
        _client?.Dispose();
        _ownedHttp?.Dispose();
        _ownedHttp = new HttpClient { DefaultRequestHeaders = { { "User-Agent", "Chronicle/1.0" } } };
        _client    = new AniListClient(_ownedHttp, _logger);
        _logger.LogInformation("AniList plugin configured (no API key required)");
    }

    // ── search ────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ScoredCandidate>> SearchAsync(MediaSearchContext context, CancellationToken ct = default)
    {
        EnsureConfigured();
        var movie = string.Equals(context.MediaTypeName, "anime_movies", StringComparison.OrdinalIgnoreCase);
        return context.HierarchyLevel switch
        {
            0 => await SearchTitleAsync(context, movie, ct).ConfigureAwait(false),
            1 when !movie => await SearchSeasonAsync(context, ct).ConfigureAwait(false),
            2 when !movie => await SearchEpisodeAsync(context, ct).ConfigureAwait(false),
            _ => [],
        };
    }

    private async Task<IReadOnlyList<ScoredCandidate>> SearchTitleAsync(MediaSearchContext context, bool movie, CancellationToken ct)
    {
        // 1. An id we already know: our own, or a MyAnimeList id another plugin stored.
        var known = await ResolveKnownEntryAsync(context.KnownExternalIds, parent: false, ct).ConfigureAwait(false);
        if (known is not null && (movie ? MovieFormats : ShowFormats).Contains(known.Format ?? ""))
            return [new ScoredCandidate(Map(await ForDisplayAsync(known, movie, ct).ConfigureAwait(false), movie), 100, "known id") { IdentifierMatch = true }];

        // 2. Search by name, then by each alternate title.
        var formats = movie ? MovieFormats : ShowFormats;
        var queries = new List<string> { context.Name };
        if (context.PreciseName is { Length: > 0 } precise && !queries.Contains(precise, StringComparer.OrdinalIgnoreCase)) queries.Add(precise);
        foreach (var alt in (context.AltTitles ?? []).Take(1))   // each extra name costs a request, and AniList allows 30 a minute
            if (!string.IsNullOrWhiteSpace(alt) && !queries.Contains(alt, StringComparer.OrdinalIgnoreCase)) queries.Add(alt);

        var scored = new List<(AniMedia Media, int Score)>();
        foreach (var query in queries)
        {
            var results = await _client!.SearchAsync(query, formats, _includeAdult, ct).ConfigureAwait(false);
            foreach (var r in results)
            {
                if (scored.Any(s => s.Media.Id == r.Id)) continue;
                var score = Score(r, context, query);
                if (score >= 40) scored.Add((r, score));
            }
            if (scored.Count > 0 && scored.Max(s => s.Score) >= 80) break;   // a confident match: no need to try more names
        }
        if (scored.Count == 0) return [];

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        var candidates = new List<ScoredCandidate>();
        for (var i = 0; i < Math.Min(scored.Count, 5); i++)
        {
            var (media, score) = scored[i];
            // The best candidate gets the whole record (and is turned into its show's first season); the others are
            // shown from what the search returned.
            var shown = i == 0 ? await ForDisplayAsync(media, movie, ct).ConfigureAwait(false) : media;
            candidates.Add(new ScoredCandidate(Map(shown, movie), score, i == 0 ? "title search" : "title search (other)"));
        }
        return candidates;
    }

    private async Task<IReadOnlyList<ScoredCandidate>> SearchSeasonAsync(MediaSearchContext context, CancellationToken ct)
    {
        var (show, season) = await ResolveParentAsync(context.KnownExternalIds, ct).ConfigureAwait(false);
        season = context.ItemNumber ?? season;
        if (show is null || season is null) return [];

        var layout = await AniListSeasons.BuildAsync(show, LoadAsync, ct).ConfigureAwait(false);
        var match = layout.Seasons.FirstOrDefault(s => s.Number == season);
        return match is null ? [] : [new ScoredCandidate(MapSeason(layout, match), 100, "season number match") { IdentifierMatch = true }];
    }

    private async Task<IReadOnlyList<ScoredCandidate>> SearchEpisodeAsync(MediaSearchContext context, CancellationToken ct)
    {
        var (show, season) = await ResolveParentAsync(context.KnownExternalIds, ct).ConfigureAwait(false);
        if (show is null || season is null || context.ItemNumber is not { } number) return [];

        var layout = await AniListSeasons.BuildAsync(show, LoadAsync, ct).ConfigureAwait(false);
        var match = layout.Seasons.FirstOrDefault(s => s.Number == season);
        if (match is null) return [];

        var episode = (await EpisodesAsync(match, ct).ConfigureAwait(false)).FirstOrDefault(e => e.Number == number);
        return episode is null ? [] : [new ScoredCandidate(MapEpisode(layout.Root.Id, match.Number, episode), 100, "episode number match") { IdentifierMatch = true }];
    }

    // ── fetch by id ───────────────────────────────────────────────────────────

    public async Task<MediaMetadata> GetByIdAsync(string externalId, CancellationToken ct = default)
    {
        EnsureConfigured();
        var raw = externalId.Trim();
        var parsed = ParseId(raw);
        if (parsed is null) return Empty(raw);

        AniMedia? media;
        if (parsed.Mal)
            media = await _client!.GetMediaByMalIdAsync(parsed.Id, ct).ConfigureAwait(false);
        else
            media = await LoadAsync(parsed.Id, ct).ConfigureAwait(false);
        if (media is null) return Empty(raw);

        if (parsed.Season is null) return Map(media, parsed.Movie || media.Format == "MOVIE");

        var layout = await AniListSeasons.BuildAsync(media, LoadAsync, ct).ConfigureAwait(false);
        var season = layout.Seasons.FirstOrDefault(s => s.Number == parsed.Season);
        if (season is null) return Empty(raw);
        if (parsed.Episode is null) return MapSeason(layout, season);

        var episode = (await EpisodesAsync(season, ct).ConfigureAwait(false)).FirstOrDefault(e => e.Number == parsed.Episode);
        return episode is null ? Empty(raw) : MapEpisode(layout.Root.Id, season.Number, episode);
    }

    public async Task<IReadOnlyList<ProviderEpisodeSummary>> GetEpisodeListAsync(
        string showExternalId, int seasonNumber, CancellationToken ct = default)
    {
        EnsureConfigured();
        var parsed = ParseId(showExternalId.Trim());
        if (parsed is null) return [];
        var media = parsed.Mal ? await _client!.GetMediaByMalIdAsync(parsed.Id, ct).ConfigureAwait(false)
                               : await LoadAsync(parsed.Id, ct).ConfigureAwait(false);
        if (media is null) return [];

        var layout = await AniListSeasons.BuildAsync(media, LoadAsync, ct).ConfigureAwait(false);
        var season = layout.Seasons.FirstOrDefault(s => s.Number == seasonNumber);
        if (season is null) return [];
        return (await EpisodesAsync(season, ct).ConfigureAwait(false))
            .Select(e => new ProviderEpisodeSummary(e.Number, e.Title, null, e.Thumbnail, e.AirDate)).ToList();
    }

    public Task<byte[]> GetImageAsync(string url, CancellationToken ct = default)
    {
        EnsureConfigured();
        return _client!.GetImageBytesAsync(url, ct);
    }

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        if (_client is null)
        {
            _logger.LogWarning("AniList health check skipped: plugin not configured");
            return false;
        }
        return await _client.HealthCheckAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _client?.Dispose();
        _ownedHttp?.Dispose();
        _client = null;
        _ownedHttp = null;
    }

    // ── loading ───────────────────────────────────────────────────────────────

    private async Task<AniMedia?> LoadAsync(int id, CancellationToken ct)
    {
        if (_recent.TryGetValue(id, out var hit) && DateTimeOffset.UtcNow - hit.At < RecentFor) return hit.Media;
        var media = await _client!.GetMediaAsync(id, ct).ConfigureAwait(false);
        if (media is null) return null;
        if (_recent.Count >= RecentMax) _recent.Clear();
        _recent[id] = (DateTimeOffset.UtcNow, media);
        return media;
    }

    /// <summary>The record to show for a search hit: full details, and for a show the first season's entry.</summary>
    private async Task<AniMedia> ForDisplayAsync(AniMedia hit, bool movie, CancellationToken ct)
    {
        var full = await LoadAsync(hit.Id, ct).ConfigureAwait(false) ?? hit;
        if (movie || !AniListSeasons.HasPrequel(full)) return full;
        var root = await AniListSeasons.FindRootAsync(full, LoadAsync, ct).ConfigureAwait(false);
        return await LoadAsync(root.Id, ct).ConfigureAwait(false) ?? root;
    }

    private async Task<AniMedia?> ResolveKnownEntryAsync(IReadOnlyDictionary<string, string>? ids, bool parent, CancellationToken ct)
    {
        var prefix = parent ? "parent_" : "";
        if (ids is null) return null;
        if (ids.TryGetValue(prefix + "anilist", out var own) && ParseId(own) is { } p)
            return await LoadAsync(p.Id, ct).ConfigureAwait(false);
        foreach (var key in new[] { prefix + "mal", prefix + "myanimelist" })
            if (ids.TryGetValue(key, out var mal) && int.TryParse(mal, out var malId))
                return await _client!.GetMediaByMalIdAsync(malId, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>The show entry (and, when the stored parent id names one, the season number) that a season or episode belongs to.</summary>
    private async Task<(AniMedia? Show, int? Season)> ResolveParentAsync(
        IReadOnlyDictionary<string, string>? ids, CancellationToken ct)
    {
        int? season = null;
        if (ids is not null)
        {
            if (ids.TryGetValue("parent_anilist", out var parentId) && ParseId(parentId) is { } pp) season = pp.Season;
            else if (ids.TryGetValue("anilist", out var ownId) && ParseId(ownId) is { } op) season = op.Season;
        }
        var show = await ResolveKnownEntryAsync(ids, parent: true, ct).ConfigureAwait(false)
                ?? await ResolveKnownEntryAsync(ids, parent: false, ct).ConfigureAwait(false);
        return (show, season);
    }

    // ── episodes ──────────────────────────────────────────────────────────────

    private sealed record EpisodeInfo(int Number, string Title, string? AirDate, string? Thumbnail);

    private static readonly Regex EpisodeTitle =
        new(@"^\s*(?:episode|ep\.?)\s*(\d+)\s*[-:–—]\s*(.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Every episode of a season, numbered across its parts, with titles and thumbnails (from the streaming
    /// listings AniList carries) and air dates (from its airing schedule). Episodes AniList has no details for are
    /// still listed, as "Episode N".</summary>
    private async Task<List<EpisodeInfo>> EpisodesAsync(AniListSeasons.Season season, CancellationToken ct)
    {
        var result = new List<EpisodeInfo>();
        var offset = 0;
        foreach (var part in season.Parts)
        {
            var titles = new Dictionary<int, (string Title, string? Thumb)>();
            var streaming = part.StreamingEpisodes ?? [];
            for (var i = 0; i < streaming.Count; i++)
            {
                var s = streaming[i];
                if (string.IsNullOrWhiteSpace(s.Title)) continue;
                var m = EpisodeTitle.Match(s.Title);
                var number = m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : i + 1;
                titles.TryAdd(number, (m.Success ? m.Groups[2].Value : s.Title.Trim(), s.Thumbnail));
            }

            var aired = await AiredAsync(part.Id, ct).ConfigureAwait(false);

            var count = part.Episodes ?? Math.Max(titles.Count == 0 ? 0 : titles.Keys.Max(), aired.Count == 0 ? 0 : aired.Keys.Max());
            for (var e = 1; e <= count; e++)
            {
                titles.TryGetValue(e, out var t);
                aired.TryGetValue(e, out var date);
                result.Add(new EpisodeInfo(offset + e, t.Title ?? $"Episode {offset + e}", date, t.Thumb));
            }
            offset += count;
        }
        return result;
    }

    /// <summary>Air dates by episode number for one AniList entry. Remembered briefly: a season's episodes are asked for one
    /// at a time, and each ask would otherwise fetch the whole schedule again.</summary>
    private async Task<Dictionary<int, string>> AiredAsync(int mediaId, CancellationToken ct)
    {
        if (_aired.TryGetValue(mediaId, out var hit) && DateTimeOffset.UtcNow - hit.At < RecentFor) return hit.Dates;
        var aired = new Dictionary<int, string>();
        for (var page = 1; page <= 10; page++)
        {
            var schedule = await _client!.GetAiringPageAsync(mediaId, page, ct).ConfigureAwait(false);
            foreach (var node in schedule?.Nodes ?? [])
                aired.TryAdd(node.Episode, DateTimeOffset.FromUnixTimeSeconds(node.AiringAt).UtcDateTime.ToString("yyyy-MM-dd"));
            if (schedule?.PageInfo?.HasNextPage != true) break;
        }
        if (_aired.Count >= RecentMax) _aired.Clear();
        _aired[mediaId] = (DateTimeOffset.UtcNow, aired);
        return aired;
    }

    // ── mapping ───────────────────────────────────────────────────────────────

    private MediaMetadata Map(AniMedia m, bool movie)
    {
        var title = ChooseTitle(m);

        var cast = new List<CastMember>();
        var characters = new List<Dictionary<string, object?>>();
        foreach (var edge in m.Characters?.Edges ?? [])
        {
            if (edge.Node?.Name?.Full is not { Length: > 0 } character) continue;
            var voices = (edge.VoiceActors ?? []).Where(v => v.Name?.Full is { Length: > 0 }).ToList();
            foreach (var v in voices)
                cast.Add(new CastMember(v.Name!.Full!, character, v.Id.ToString(), v.Image?.Large));
            characters.Add(new Dictionary<string, object?>
            {
                ["id"] = edge.Node.Id, ["name"] = character, ["native"] = edge.Node.Name.Native, ["role"] = edge.Role,
                ["image"] = edge.Node.Image?.Large,
                ["voice_actors"] = voices.Select(v => new { id = v.Id, name = v.Name!.Full, native = v.Name.Native, image = v.Image?.Large }).ToList(),
            });
        }

        var crew = new List<CrewMember>();
        foreach (var edge in m.Staff?.Edges ?? [])
            if (edge.Node?.Name?.Full is { Length: > 0 } name)
                crew.Add(new CrewMember(name, edge.Role, edge.Node.Id.ToString(), edge.Node.Image?.Large));
        foreach (var edge in m.Studios?.Edges ?? [])
            if (edge.Node is { IsAnimationStudio: true, Name: { Length: > 0 } studio })
                crew.Add(new CrewMember(studio, "Animation Studio", edge.Node.Id.ToString()));

        var extra = new Dictionary<string, object?>
        {
            ["ids"] = IdsOf(m),
            ["format"] = m.Format, ["status"] = m.Status, ["episodes"] = m.Episodes, ["duration"] = m.Duration,
            ["season"] = m.Season, ["season_year"] = m.SeasonYear, ["source"] = m.Source, ["country"] = m.CountryOfOrigin,
            ["is_adult"] = m.IsAdult, ["popularity"] = m.Popularity, ["favourites"] = m.Favourites,
            ["mean_score"] = m.MeanScore, ["average_score"] = m.AverageScore,
            ["title_romaji"] = m.Title?.Romaji, ["title_english"] = m.Title?.English, ["title_native"] = m.Title?.Native,
            ["cover_color"] = m.CoverImage?.Color,
        };
        if (DateOf(m.StartDate) is { } start) extra["air_date"] = start;
        if (DateOf(m.EndDate) is { } end) extra["end_date"] = end;
        if (m.NextAiringEpisode is { } next)
            extra["next_airing"] = new { episode = next.Episode, at = DateTimeOffset.FromUnixTimeSeconds(next.AiringAt).UtcDateTime.ToString("O") };
        if (m.Trailer is { Id: { Length: > 0 } tid, Site: { Length: > 0 } site }) extra["trailer"] = new { site, id = tid };
        if (m.ExternalLinks is { Count: > 0 } links)
            extra["external_links"] = links.Where(l => !string.IsNullOrWhiteSpace(l.Url)).Select(l => new { site = l.Site, url = l.Url }).ToList();
        if (m.Studios?.Edges is { Count: > 0 } studios)
            extra["studios"] = studios.Where(s => s.Node?.Name is not null)
                .Select(s => new { id = s.Node!.Id, name = s.Node.Name, is_animation_studio = s.Node.IsAnimationStudio, is_main = s.IsMain }).ToList();
        if (characters.Count > 0) extra["characters"] = characters;
        if (m.Relations?.Edges is { Count: > 0 } relations)
            extra["relations"] = relations.Where(r => r.Node is not null).Select(r => new
            {
                type = r.RelationType, id = r.Node!.Id, media_type = r.Node.Type, format = r.Node.Format,
                title = r.Node.Title?.English ?? r.Node.Title?.Romaji, year = r.Node.StartDate?.Year,
            }).ToList();

        return new MediaMetadata
        {
            ExternalId = movie ? $"movie:{m.Id}" : $"show:{m.Id}",
            Source = "anilist",
            Title = title,
            Overview = CleanDescription(m.Description),
            Year = YearOf(m),
            PosterUrl = m.CoverImage?.ExtraLarge ?? m.CoverImage?.Large,
            BannerUrl = m.BannerImage,
            RuntimeMinutes = movie ? m.Duration : null,   // a show's per-episode length stays in ExtendedData.duration
            Genres = m.Genres ?? [],
            Tags = (m.Tags ?? []).Where(t => t.IsMediaSpoiler != true && !string.IsNullOrWhiteSpace(t.Name)).Select(t => t.Name!).ToList(),
            AlternateNames = AlternateNamesOf(m, title),
            Rating = m.AverageScore is { } score ? Math.Round(score / 10.0, 1) : null,
            Cast = cast,
            Crew = crew,
            ExtendedData = JsonSerializer.SerializeToElement(extra),
        };
    }

    private MediaMetadata MapSeason(AniListSeasons.Layout layout, AniListSeasons.Season season)
    {
        var first = season.First;
        var extra = new Dictionary<string, object?>
        {
            ["ids"] = IdsOf(first),
            ["anilist_ids"] = season.Parts.Select(p => p.Id).ToList(),
            ["episodes"] = season.TotalEpisodes,
            ["format"] = first.Format, ["status"] = season.Parts[^1].Status,
        };
        if (DateOf(first.StartDate) is { } start) extra["air_date"] = start;

        return new MediaMetadata
        {
            ExternalId = $"show:{layout.Root.Id}/season:{season.Number}",
            Source = "anilist",
            Title = $"Season {season.Number}",
            Overview = CleanDescription(first.Description),
            Year = YearOf(first),
            PosterUrl = first.CoverImage?.ExtraLarge ?? first.CoverImage?.Large,
            ExtendedData = JsonSerializer.SerializeToElement(extra),
        };
    }

    private static MediaMetadata MapEpisode(int showId, int season, EpisodeInfo e) => new()
    {
        ExternalId = $"show:{showId}/season:{season}/episode:{e.Number}",
        Source = "anilist",
        Title = e.Title,
        Year = e.AirDate is { Length: >= 4 } d && int.TryParse(d[..4], out var y) ? y : null,
        PosterUrl = e.Thumbnail,
        // The full air date, not just its year: Chronicle's scraper API reads it from extendedData "air_date".
        ExtendedData = e.AirDate is null ? null : JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["air_date"] = e.AirDate }),
    };

    private static MediaMetadata Empty(string externalId) => new() { ExternalId = externalId, Source = "anilist" };

    private static Dictionary<string, object?> IdsOf(AniMedia m)
    {
        var ids = new Dictionary<string, object?> { ["anilist"] = m.Id };
        if (m.IdMal is { } mal) ids["mal"] = mal;
        return ids;
    }

    private string ChooseTitle(AniMedia m)
    {
        var t = m.Title;
        var choice = _titleLanguage switch
        {
            "romaji" => t?.Romaji ?? t?.English,
            "native" => t?.Native ?? t?.Romaji ?? t?.English,
            _        => t?.English ?? t?.Romaji,
        };
        return string.IsNullOrWhiteSpace(choice) ? t?.Native ?? $"AniList {m.Id}" : choice;
    }

    private static List<string> AlternateNamesOf(AniMedia m, string chosen) =>
        new[] { m.Title?.English, m.Title?.Romaji, m.Title?.Native }.Concat(m.Synonyms ?? [])
            .Where(n => !string.IsNullOrWhiteSpace(n) && !string.Equals(n, chosen, StringComparison.OrdinalIgnoreCase))
            .Select(n => n!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static int? YearOf(AniMedia m) => m.SeasonYear ?? m.StartDate?.Year;

    private static string? DateOf(AniDate? d) =>
        d?.Year is { } y ? $"{y:D4}-{(d.Month ?? 1):D2}-{(d.Day ?? 1):D2}" : null;

    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);

    internal static string? CleanDescription(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var text = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = WebUtility.HtmlDecode(Tags.Replace(text, ""));
        text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
        return text.Length == 0 ? null : text;
    }

    // ── ids ───────────────────────────────────────────────────────────────────

    internal sealed record ParsedId(int Id, int? Season, int? Episode, bool Mal, bool Movie = false);

    private static readonly Regex IdPattern = new(
        @"^(?:(?:show|movie|anilist):)?(\d+)(?:/season:(\d+))?(?:/episode:(\d+))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AniListUrl = new(@"anilist\.co/anime/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MalUrl     = new(@"myanimelist\.net/anime/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static ParsedId? ParseId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();

        if (AniListUrl.Match(value) is { Success: true } a) return new ParsedId(int.Parse(a.Groups[1].Value), null, null, false);
        if (MalUrl.Match(value) is { Success: true } mu) return new ParsedId(int.Parse(mu.Groups[1].Value), null, null, true);
        if (value.StartsWith("mal:", StringComparison.OrdinalIgnoreCase) && int.TryParse(value[4..], out var mal))
            return new ParsedId(mal, null, null, true);

        var m = IdPattern.Match(value);
        if (!m.Success) return null;
        return new ParsedId(
            int.Parse(m.Groups[1].Value),
            m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : null,
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : null,
            false,
            value.StartsWith("movie:", StringComparison.OrdinalIgnoreCase));
    }

    // ── scoring ───────────────────────────────────────────────────────────────

    private static int Score(AniMedia candidate, MediaSearchContext context, string query)
    {
        var wanted = Normalise(query);
        var names = new[] { candidate.Title?.English, candidate.Title?.Romaji, candidate.Title?.Native }
            .Concat(candidate.Synonyms ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Select(Normalise).ToList();

        var score = 0;
        if (wanted.Length > 0)
        {
            if (names.Any(n => n == wanted)) score += 60;
            else if (names.Any(n => n.Contains(wanted, StringComparison.Ordinal) || wanted.Contains(n, StringComparison.Ordinal) && n.Length >= 4)) score += 35;
        }

        if (context.Year is { } year)
        {
            if (YearOf(candidate) is { } y && Math.Abs(y - year) <= 1) score += 30;
        }
        else score += 10;

        // A regular TV series or feature film beats an OVA or special of the same name.
        if (candidate.Format is "TV" or "MOVIE") score += 5;

        return Math.Min(score, 99);
    }

    private static string Normalise(string? s) =>
        new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private void EnsureConfigured()
    {
        if (_client is null)
            throw new InvalidOperationException("AniList plugin is not configured. Call Configure() first.");
    }
}
