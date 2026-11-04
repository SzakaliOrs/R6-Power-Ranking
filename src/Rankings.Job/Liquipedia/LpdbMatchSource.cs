using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Rankings.Core;

namespace Rankings.Job.Liquipedia;

/// <summary>
/// Reads finished tier 1–2 Rainbow Six matches from Liquipedia's LPDB match endpoint, a page at a time.
/// Every field is requested so the archived responses can answer questions asked later.
/// The daily job is the only caller of Liquipedia, so its call log covers the whole budget;
/// anything else that needs match data reads what the job stored.
/// </summary>
public sealed class LpdbMatchSource : IMatchSource
{
    readonly HttpClient _http;
    readonly LpdbOptions _options;
    readonly HourlyCallLimiter _limiter;
    readonly TimeProvider _time;

    public LpdbMatchSource(HttpClient http, LpdbOptions options, HourlyCallLimiter limiter, TimeProvider? time = null)
    {
        if (options.PageSize is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(options), options.PageSize, "PageSize must be between 1 and 100.");
        _http = http;
        _options = options;
        _limiter = limiter;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>An HttpClient that accepts gzip, which Liquipedia requires.</summary>
    public static HttpClient CreateHttpClient() =>
        new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.GZip });

    public async Task<SourceBatch> FetchAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        var maps = new List<SourceMap>();
        var skipped = new List<SkippedSourceMap>();
        var responses = new List<RawResponse>();

        if (from < _options.EarliestDate)
            from = _options.EarliestDate;
        if (to <= from)
            return new SourceBatch(maps, skipped, responses);

        for (var offset = 0; ; offset += _options.PageSize)
        {
            var request = MatchRequest(from, to, offset);
            await _limiter.WaitAsync(cancellationToken);

            using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(_options.BaseUri, request));
            message.Headers.Authorization = new AuthenticationHeaderValue("Apikey", _options.ApiKey);
            message.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
            using var response = await _http.SendAsync(message, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _limiter.MarkExhausted();
                throw new LpdbRateLimitedException(request, response.Headers.RetryAfter?.Delta);
            }
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"LPDB answered {(int)response.StatusCode} to {request}: {Shorten(body)}", null, response.StatusCode);
            responses.Add(new RawResponse(request, _time.GetUtcNow(), body));

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            // LPDB reports a bad query inside a 200 response.
            if (root.TryGetProperty("error", out var error) && HasContent(error))
                throw new InvalidOperationException($"LPDB reported an error for {request}: {Shorten(error.GetRawText())}");

            var result = root.GetProperty("result");
            foreach (var match in result.EnumerateArray())
                LpdbMatchParser.Parse(match, maps, skipped);
            if (result.GetArrayLength() < _options.PageSize)
                break;
        }

        return new SourceBatch(maps, skipped, responses);
    }

    /// <summary>The request path and query for one page, relative to the base URI.</summary>
    public string MatchRequest(DateTimeOffset from, DateTimeOffset to, int offset)
    {
        var tiers = string.Join(" OR ", _options.Tiers.Select(tier => $"[[liquipediatier::{tier}]]"));
        var excluded = string.Concat(_options.ExcludedTierTypes.Select(type => $" AND [[liquipediatiertype::!{type}]]"));
        // The date comparison is strict, so step back a second to include a series starting exactly at `from`.
        var conditions = $"({tiers}){excluded} AND [[finished::1]] "
            + $"AND [[date::>{Lpdb(from.AddSeconds(-1))}]] AND [[date::<{Lpdb(to)}]]";
        (string Name, string Value)[] query =
        [
            ("wiki", _options.Wiki),
            ("conditions", conditions),
            // A stable order keeps offset paging from skipping or repeating series that share a start time.
            ("order", "date ASC, match2id ASC"),
            ("limit", _options.PageSize.ToString(CultureInfo.InvariantCulture)),
            ("offset", offset.ToString(CultureInfo.InvariantCulture)),
        ];
        return "match?" + string.Join("&", query.Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value)}"));
    }

    static string Lpdb(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    static bool HasContent(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => element.GetArrayLength() > 0,
        JsonValueKind.Object => element.EnumerateObject().Any(),
        JsonValueKind.String => !string.IsNullOrEmpty(element.GetString()),
        JsonValueKind.Null or JsonValueKind.False => false,
        _ => true,
    };

    static string Shorten(string text) => text.Length <= 300 ? text : text[..300] + "…";
}

/// <summary>
/// Liquipedia refused a call for the rate limit. The run stops here and nothing retries it:
/// the count was wrong somewhere, and retrying would only spend more of the budget.
/// </summary>
public sealed class LpdbRateLimitedException(string request, TimeSpan? retryAfter)
    : HttpRequestException(
        $"Liquipedia rate limit reached (429) on {request}. Stopping without retrying; "
        + "the call log now counts this hour as full"
        + (retryAfter is { } wait ? $", and Liquipedia asks to wait {wait}." : "."),
        null, HttpStatusCode.TooManyRequests)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
