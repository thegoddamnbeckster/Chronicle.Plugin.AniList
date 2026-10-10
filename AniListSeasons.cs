using System.Text.RegularExpressions;

namespace Chronicle.Plugin.AniList;

/// <summary>
/// AniList has no "series with seasons": every season (and every split cour) is its own entry, joined to its neighbours by
/// PREQUEL / SEQUEL relations. Chronicle's anime type is Show / Season / Episode, so this turns the relation chain back
/// into that shape: the root entry is the show, each following TV-like sequel is a season, and a sequel whose title says it
/// is "Part 2" or "2nd Cour" is folded into the season before it so episode numbers keep counting up.
/// Specials, OVAs, movies and music between seasons are left out of the chain.
/// </summary>
internal static class AniListSeasons
{
    private const int MaxHops = 25;

    private static readonly Regex ContinuationTitle = new(
        @"\b(part\s*(2|3|4|ii|iii|iv)|(2nd|second|3rd|third)\s*cour|cour\s*(2|3)|final\s*chapters?(\s*part\s*[2-4])?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public sealed record Season(int Number, List<AniMedia> Parts)
    {
        public AniMedia First => Parts[0];
        public int? TotalEpisodes => Parts.All(p => p.Episodes.HasValue) ? Parts.Sum(p => p.Episodes!.Value) : null;
    }

    public sealed record Layout(AniMedia Root, List<Season> Seasons);

    public static bool IsTvLike(string? format) => format is "TV" or "TV_SHORT" or "ONA";

    public static bool IsContinuationPart(AniMedia media) =>
        new[] { media.Title?.English, media.Title?.Romaji }.Any(t => !string.IsNullOrWhiteSpace(t) && ContinuationTitle.IsMatch(t));

    private static int? YearOf(AniMedia m) => m.SeasonYear ?? m.StartDate?.Year;

    /// <summary>The TV-like neighbour reached by <paramref name="relation"/>. AniList's links are community-maintained and
    /// sometimes loose (a 1999 show listing a 2024 special as its "prequel"), so a prequel that started after this entry, or a
    /// sequel that started before it, is not followed.</summary>
    private static AniMedia? Related(AniMedia media, string relation, bool preferTv = true)
    {
        var here = YearOf(media);
        bool InOrder(AniMedia n)
        {
            var year = YearOf(n);
            if (here is null || year is null) return true;
            return relation == "PREQUEL" ? year <= here : year >= here;
        }
        var nodes = (media.Relations?.Edges ?? [])
            .Where(e => e.RelationType == relation && e.Node is { Type: "ANIME" } n && IsTvLike(n.Format) && InOrder(n))
            .Select(e => e.Node!).ToList();
        return preferTv ? nodes.FirstOrDefault(n => n.Format == "TV") ?? nodes.FirstOrDefault() : nodes.FirstOrDefault();
    }

    /// <summary>True when this entry has a TV-like prequel, i.e. it is not the first season.</summary>
    public static bool HasPrequel(AniMedia media) => Related(media, "PREQUEL") is not null;

    /// <summary>Walks PREQUEL links back to the first season.</summary>
    public static async Task<AniMedia> FindRootAsync(
        AniMedia from, Func<int, CancellationToken, Task<AniMedia?>> load, CancellationToken ct)
    {
        var current = from;
        var seen = new HashSet<int> { from.Id };
        for (var hop = 0; hop < MaxHops; hop++)
        {
            var prequel = Related(current, "PREQUEL");
            if (prequel is null || !seen.Add(prequel.Id)) break;
            var loaded = await load(prequel.Id, ct).ConfigureAwait(false);
            if (loaded is null) break;
            current = loaded;
        }
        return current;
    }

    /// <summary>The seasons of the show whose first entry is <paramref name="root"/>.</summary>
    public static async Task<Layout> BuildAsync(
        AniMedia root, Func<int, CancellationToken, Task<AniMedia?>> load, CancellationToken ct)
    {
        var seasons = new List<Season> { new(1, [root]) };
        var seen = new HashSet<int> { root.Id };
        var current = root;
        for (var hop = 0; hop < MaxHops; hop++)
        {
            var sequel = Related(current, "SEQUEL");
            if (sequel is null || !seen.Add(sequel.Id)) break;
            var loaded = await load(sequel.Id, ct).ConfigureAwait(false);
            if (loaded is null) break;

            if (IsContinuationPart(loaded)) seasons[^1].Parts.Add(loaded);
            else seasons.Add(new Season(seasons.Count + 1, [loaded]));
            current = loaded;
        }
        return new Layout(root, seasons);
    }
}
