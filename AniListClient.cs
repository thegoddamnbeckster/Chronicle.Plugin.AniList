using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Chronicle.Plugin.AniList;

/// <summary>
/// HTTP wrapper for AniList's public GraphQL API (https://graphql.anilist.co). No account or key is needed for reads.
/// AniList's limit is 30 requests a minute (it says so in the X-RateLimit-Limit header of every answer; its documentation
/// calls the old 90 "temporarily degraded", and it has been 30 for a long time), so requests are spaced just over two seconds
/// apart, following whatever limit the header reports. Chronicle gives a plugin 25 seconds per call, so a 429 is not waited
/// out: a short Retry-After is waited once, anything longer is reported as an HTTP 429 error, which Chronicle reads as "the
/// provider is rate limited" and leaves the item Pending for the next run instead of marking it not found.
/// A media id that does not exist comes back as a GraphQL "Not Found" error; every lookup turns that into null.
/// </summary>
internal sealed class AniListClient : IDisposable
{
    public const string Endpoint = "https://graphql.anilist.co";

    private static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(8);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private TimeSpan _minInterval;
    private readonly bool _fixedInterval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    public AniListClient(HttpClient http, ILogger logger, TimeSpan? minInterval = null)
    {
        _http = http;
        _logger = logger;
        _fixedInterval = minInterval is not null;
        _minInterval = minInterval ?? TimeSpan.FromMilliseconds(2100);
    }

    // ── queries ───────────────────────────────────────────────────────────────

    // What a search result or a relation node needs: enough to score it and to follow its prequel/sequel links.
    private const string LightFields = """
        id idMal type format status season seasonYear episodes duration isAdult averageScore popularity
        title { romaji english native } synonyms
        startDate { year month day } endDate { year month day }
        genres coverImage { extraLarge large color } bannerImage description(asHtml: false)
        relations { edges { relationType node { id idMal type format title { romaji english native } startDate { year } episodes } } }
        """;

    // Everything shown on the item's own page: the light fields plus credits, tags, studios, links and episode titles.
    private const string FullFields = LightFields + """

        countryOfOrigin source meanScore favourites
        tags { name rank isMediaSpoiler }
        studios { edges { isMain node { id name isAnimationStudio } } }
        trailer { id site }
        externalLinks { site url }
        streamingEpisodes { title thumbnail url }
        nextAiringEpisode { episode airingAt }
        characters(perPage: 25, sort: [ROLE, RELEVANCE]) {
          edges { role node { id name { full native } image { large } }
                  voiceActors(language: JAPANESE, sort: RELEVANCE) { id name { full native } image { large } } }
        }
        staff(perPage: 25, sort: RELEVANCE) { edges { role node { id name { full native } image { large } } } }
        """;

    public async Task<AniMedia?> GetMediaAsync(int id, CancellationToken ct)
    {
        var data = await QueryAsync<MediaEnvelope>(
            $"query ($id: Int) {{ Media(id: $id, type: ANIME) {{ {FullFields} }} }}", new { id }, ct).ConfigureAwait(false);
        return data?.Media;
    }

    public async Task<AniMedia?> GetMediaByMalIdAsync(int malId, CancellationToken ct)
    {
        var data = await QueryAsync<MediaEnvelope>(
            $"query ($mal: Int) {{ Media(idMal: $mal, type: ANIME) {{ {FullFields} }} }}", new { mal = malId }, ct).ConfigureAwait(false);
        return data?.Media;
    }

    public async Task<List<AniMedia>> SearchAsync(string text, string[] formats, bool includeAdult, CancellationToken ct)
    {
        // "isAdult: null" does not mean "either": AniList then matches nothing at all. To allow adult titles the filter is
        // left out of the query altogether; to exclude them it is set to false.
        var query = includeAdult
            ? $"query ($q: String, $formats: [MediaFormat]) {{ Page(perPage: 10) {{ " +
              $"media(search: $q, type: ANIME, format_in: $formats, sort: SEARCH_MATCH) {{ {LightFields} }} }} }}"
            : $"query ($q: String, $formats: [MediaFormat]) {{ Page(perPage: 10) {{ " +
              $"media(search: $q, type: ANIME, format_in: $formats, isAdult: false, sort: SEARCH_MATCH) {{ {LightFields} }} }} }}";
        var data = await QueryAsync<PageEnvelope>(query, new { q = text, formats }, ct).ConfigureAwait(false);
        return data?.Page?.Media ?? [];
    }

    /// <summary>One page (50) of an entry's airing schedule: when each episode aired, or will air.</summary>
    public async Task<AniAiringSchedule?> GetAiringPageAsync(int id, int page, CancellationToken ct)
    {
        var data = await QueryAsync<MediaEnvelope>(
            "query ($id: Int, $page: Int) { Media(id: $id, type: ANIME) { airingSchedule(page: $page, perPage: 50) " +
            "{ pageInfo { hasNextPage } nodes { episode airingAt } } } }", new { id, page }, ct).ConfigureAwait(false);
        return data?.Media?.AiringSchedule;
    }

    public async Task<bool> HealthCheckAsync(CancellationToken ct)
    {
        try
        {
            // Cowboy Bebop, AniList id 1.
            var data = await QueryAsync<MediaEnvelope>("query { Media(id: 1, type: ANIME) { id } }", null, ct).ConfigureAwait(false);
            return data?.Media?.Id == 1;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AniList health check failed");
            return false;
        }
    }

    public Task<byte[]> GetImageBytesAsync(string url, CancellationToken ct) => _http.GetByteArrayAsync(url, ct);

    // ── transport ─────────────────────────────────────────────────────────────

    private async Task<T?> QueryAsync<T>(string query, object? variables, CancellationToken ct) where T : class
    {
        var body = JsonSerializer.Serialize(new { query, variables });
        for (var attempt = 0; ; attempt++)
        {
            using var response = await SendAsync(body, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60);
                if (attempt == 0 && wait <= MaxRetryWait)
                {
                    _logger.LogWarning("AniList: rate limited; waiting {Seconds}s before one retry", wait.TotalSeconds);
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                    continue;
                }
                throw new HttpRequestException(
                    $"AniList is rate limiting requests (retry in about {wait.TotalSeconds:0}s)", null, HttpStatusCode.TooManyRequests);
            }

            // A "not found" is reported as HTTP 404 with a GraphQL error; read the body before deciding.
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            GqlResponse<T>? parsed = null;
            try { parsed = JsonSerializer.Deserialize<GqlResponse<T>>(text, Json); }
            catch (JsonException) { /* handled below by status */ }

            if (parsed?.Errors is { Count: > 0 } errors)
            {
                if (errors.Any(e => e.Status == 404 || string.Equals(e.Message, "Not Found.", StringComparison.OrdinalIgnoreCase)))
                    return null;
                throw new HttpRequestException("AniList: " + string.Join("; ", errors.Select(e => e.Message)));
            }
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"AniList answered {(int)response.StatusCode} {response.ReasonPhrase}");
            return parsed?.Data;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string body, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _minInterval - (DateTimeOffset.UtcNow - _last);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.ParseAdd("application/json");
            var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            _last = DateTimeOffset.UtcNow;
            FollowReportedLimit(response);
            return response;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Spaces requests to the limit AniList itself reports (60 seconds divided by it, with a little margin).</summary>
    private void FollowReportedLimit(HttpResponseMessage response)
    {
        if (_fixedInterval) return;
        if (response.Headers.TryGetValues("X-RateLimit-Limit", out var values) && int.TryParse(values.FirstOrDefault(), out var limit) && limit > 0)
            _minInterval = TimeSpan.FromMilliseconds(Math.Clamp(60_000.0 / limit * 1.05, 700, 6_000));
    }

    public void Dispose() => _gate.Dispose();
}
