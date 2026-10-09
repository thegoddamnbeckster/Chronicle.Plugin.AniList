using System.Net;
using System.Text;
using System.Text.Json;
using Chronicle.Plugin.AniList;

namespace Chronicle.Plugin.AniList.Tests;

/// <summary>A stand-in for graphql.anilist.co: answers each query from a small in-memory catalogue and counts requests.</summary>
internal sealed class FakeAniList : HttpMessageHandler
{
    public sealed record Entry(
        int Id, string Romaji, string? English = null, string Format = "TV", int? Episodes = null, int? Year = null,
        int? Prequel = null, int? Sequel = null, int? IdMal = null, string? Description = null,
        (int Ep, string Title)[]? Streaming = null, (int Ep, long At)[]? Airing = null,
        string[]? Synonyms = null, bool Adult = false);

    private readonly Dictionary<int, Entry> _entries = new();
    public int Requests { get; private set; }
    public List<string> Queries { get; } = [];
    public HttpStatusCode? FailWith { get; set; }

    public FakeAniList Add(Entry e) { _entries[e.Id] = e; return this; }

    public AniListClient Client() => new(new HttpClient(this), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, TimeSpan.Zero);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests++;
        var body = await request.Content!.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var query = doc.RootElement.GetProperty("query").GetString()!;
        Queries.Add(query);
        var vars = doc.RootElement.TryGetProperty("variables", out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

        if (FailWith is { } status) return new HttpResponseMessage(status) { Content = Json("{}") };

        object? data;
        if (query.Contains("airingSchedule"))
        {
            var id = vars.GetProperty("id").GetInt32();
            var page = vars.GetProperty("page").GetInt32();
            var nodes = page == 1 && _entries.TryGetValue(id, out var ae) ? (ae.Airing ?? []).Select(a => new { episode = a.Ep, airingAt = a.At }).ToArray() : [];
            data = new { Media = new { airingSchedule = new { pageInfo = new { hasNextPage = false }, nodes } } };
        }
        else if (query.Contains("Page("))
        {
            var text = vars.GetProperty("q").GetString()!.ToLowerInvariant();
            var formats = vars.GetProperty("formats").EnumerateArray().Select(f => f.GetString()).ToList();
            var hits = _entries.Values.Where(e => formats.Contains(e.Format) && (e.Romaji + " " + e.English).ToLowerInvariant().Contains(text)).Select(Media).ToArray();
            data = new { Page = new { media = hits } };
        }
        else if (vars.ValueKind == JsonValueKind.Object && vars.TryGetProperty("mal", out var mal))
        {
            var found = _entries.Values.FirstOrDefault(e => e.IdMal == mal.GetInt32());
            return found is null ? NotFound() : Ok(new { Media = Media(found) });
        }
        else if (vars.ValueKind == JsonValueKind.Object && vars.TryGetProperty("id", out var idEl))
        {
            return _entries.TryGetValue(idEl.GetInt32(), out var e) ? Ok(new { Media = Media(e) }) : NotFound();
        }
        else if (query.Contains("Media(id: 1"))
            return Ok(new { Media = new { id = 1 } });
        else return NotFound();

        return Ok(data);
    }

    private object Media(Entry e) => new
    {
        id = e.Id, idMal = e.IdMal, type = "ANIME", format = e.Format, status = "FINISHED", seasonYear = e.Year, episodes = e.Episodes, duration = 24,
        isAdult = e.Adult, averageScore = 84, description = e.Description, synonyms = e.Synonyms ?? [],
        title = new { romaji = e.Romaji, english = e.English, native = "日本語" },
        startDate = new { year = e.Year, month = 4, day = 7 },
        genres = new[] { "Action", "Drama" },
        coverImage = new { extraLarge = $"https://img/{e.Id}.jpg", color = "#e4a15d" }, bannerImage = $"https://img/{e.Id}-banner.jpg",
        tags = new object[] { new { name = "Military", rank = 90, isMediaSpoiler = false }, new { name = "Big Twist", rank = 80, isMediaSpoiler = true } },
        studios = new { edges = new object[] { new { isMain = true, node = new { id = 7, name = "Wit Studio", isAnimationStudio = true } },
                                               new { isMain = false, node = new { id = 8, name = "Some Publisher", isAnimationStudio = false } } } },
        characters = new { edges = new object[] { new { role = "MAIN", node = new { id = 1, name = new { full = "Eren Yeager" } },
            voiceActors = new object[] { new { id = 99, name = new { full = "Yuki Kaji" }, image = new { large = "https://img/va.jpg" } } } } } },
        staff = new { edges = new object[] { new { role = "Director", node = new { id = 55, name = new { full = "Tetsuro Araki" } } } } },
        streamingEpisodes = (e.Streaming ?? []).Select(s => new { title = $"Episode {s.Ep} - {s.Title}", thumbnail = $"https://img/e{s.Ep}.jpg" }).ToArray(),
        relations = new
        {
            edges = new[]
            {
                e.Prequel is { } p ? new { relationType = "PREQUEL", node = RelationNode(p) } : null,
                e.Sequel is { } s ? new { relationType = "SEQUEL", node = RelationNode(s) } : null,
            }.Where(x => x is not null).ToArray(),
        },
    };

    private object RelationNode(int id) => new { id, type = "ANIME", format = _entries[id].Format, title = new { romaji = _entries[id].Romaji } };

    private static HttpResponseMessage Ok(object data) => new(HttpStatusCode.OK) { Content = Json(JsonSerializer.Serialize(new { data })) };

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound)
    {
        Content = Json("{\"data\":{\"Media\":null},\"errors\":[{\"message\":\"Not Found.\",\"status\":404}]}"),
    };

    private static StringContent Json(string s) => new(s, Encoding.UTF8, "application/json");
}
