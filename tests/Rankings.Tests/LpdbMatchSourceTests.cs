using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Rankings.Job.Liquipedia;

namespace Rankings.Tests;

/// No test here reaches Liquipedia: every response comes from a stub handler.
public class LpdbMatchSourceTests
{
    static readonly DateTimeOffset From = DateTimeOffset.Parse("2026-07-08T00:00:00Z");
    static readonly DateTimeOffset To = DateTimeOffset.Parse("2026-07-09T00:00:00Z");

    sealed record SentRequest(Uri Uri, string? Authorization, string UserAgent);

    sealed class StubHandler(Func<int, string> page, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<SentRequest> Sent { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent.Add(new(request.RequestUri!, request.Headers.Authorization?.ToString(),
                string.Join(" ", request.Headers.GetValues("User-Agent"))));
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(page(Sent.Count - 1)) });
        }
    }

    static LpdbMatchSource Source(StubHandler handler, int pageSize = 100, FakeTimeProvider? time = null)
    {
        time ??= new FakeTimeProvider(From);
        var options = new LpdbOptions { ApiKey = "test-key", UserAgent = "R6PowerRankings/test (contact@example.com)", PageSize = pageSize };
        return new LpdbMatchSource(new HttpClient(handler), options, new HourlyCallLimiter(60, time), time);
    }

    /// The fixture's series one per page, then an empty page.
    static string FixturePage(int index)
    {
        var all = JsonNode.Parse(LpdbMatchParserTests.Fixture)!["result"]!.AsArray();
        var page = new JsonArray();
        if (index < all.Count)
            page.Add(all[index]!.DeepClone());
        return new JsonObject { ["result"] = page }.ToJsonString();
    }

    [Fact]
    public async Task Fetch_SendsTheKeyAndUserAgentButKeepsTheKeyOutOfTheArchive()
    {
        var handler = new StubHandler(_ => LpdbMatchParserTests.Fixture);

        var batch = await Source(handler).FetchAsync(From, To);

        var sent = Assert.Single(handler.Sent);
        Assert.Equal("Apikey test-key", sent.Authorization);
        Assert.Equal("R6PowerRankings/test (contact@example.com)", sent.UserAgent);
        Assert.StartsWith("https://api.liquipedia.net/api/v3/match?", sent.Uri.AbsoluteUri);
        var raw = Assert.Single(batch.Responses);
        Assert.DoesNotContain("test-key", raw.Request);
        Assert.Equal(LpdbMatchParserTests.Fixture, raw.Body);
        Assert.Equal(6, batch.Maps.Count);
    }

    [Fact]
    public void Request_AsksForFinishedTier1And2MatchesInTheWindow_WithoutQualifiersOrShowmatches()
    {
        var request = Uri.UnescapeDataString(Source(new StubHandler(_ => "")).MatchRequest(From, To, 200));

        Assert.Contains("wiki=rainbowsix", request);
        Assert.Contains(
            "conditions=([[liquipediatier::1]] OR [[liquipediatier::2]]) "
            + "AND [[liquipediatiertype::!Qualifier]] AND [[liquipediatiertype::!Showmatch]] AND [[finished::1]] "
            + "AND [[date::>2026-07-07 23:59:59]] AND [[date::<2026-07-09 00:00:00]]", request);
        Assert.Contains("order=date ASC, match2id ASC", request);
        Assert.Contains("limit=100", request);
        Assert.Contains("offset=200", request);
    }

    [Fact]
    public async Task Fetch_PagesUntilAShortPage()
    {
        var handler = new StubHandler(FixturePage);

        var batch = await Source(handler, pageSize: 1).FetchAsync(From, To);

        Assert.Equal(["offset=0", "offset=1", "offset=2"],
            handler.Sent.Select(s => s.Uri.Query.Split('&').Single(p => p.StartsWith("offset="))));
        Assert.Equal(3, batch.Responses.Count);
        Assert.Equal(6, batch.Maps.Count);
    }

    [Fact]
    public async Task Fetch_FailsOnAnErrorStatus()
    {
        var handler = new StubHandler(_ => "server error", HttpStatusCode.InternalServerError);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Source(handler).FetchAsync(From, To));

        Assert.Equal(HttpStatusCode.InternalServerError, error.StatusCode);
    }

    [Fact]
    public async Task Fetch_StopsOnARateLimitWithoutRetryingAndCountsTheHourAsFull()
    {
        var time = new FakeTimeProvider(From);
        var handler = new StubHandler(_ => "rate limited", HttpStatusCode.TooManyRequests);
        var limiter = new HourlyCallLimiter(60, time);
        var source = new LpdbMatchSource(new HttpClient(handler),
            new LpdbOptions { ApiKey = "k", UserAgent = "ua", PageSize = 1 }, limiter, time);

        await Assert.ThrowsAsync<LpdbRateLimitedException>(() => source.FetchAsync(From, To));

        Assert.Single(handler.Sent);
        Assert.Equal(60, limiter.RecentCalls.Count);
    }

    [Fact]
    public async Task Fetch_SavesTheCallLogBeforeEachRequestGoesOut()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lpdb-calls-{Guid.NewGuid():N}.json");
        try
        {
            var time = new FakeTimeProvider(From);
            var log = new CallLogFile(path);
            var savedAtSend = new List<int>();
            var handler = new StubHandler(index =>
            {
                savedAtSend.Add(log.Load().Count);
                return FixturePage(index);
            });
            var source = new LpdbMatchSource(new HttpClient(handler),
                new LpdbOptions { ApiKey = "k", UserAgent = "ua", PageSize = 1 }, log.CreateLimiter(60, time), time);

            await source.FetchAsync(From, To);

            Assert.Equal([1, 2, 3], savedAtSend);
            Assert.Equal(3, log.Load().Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Fetch_FailsOnAnErrorInsideAnOkResponse()
    {
        var handler = new StubHandler(_ => """{"result":[],"error":["Invalid condition"]}""");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Source(handler).FetchAsync(From, To));

        Assert.Contains("Invalid condition", error.Message);
    }

    [Fact]
    public async Task Fetch_NeverAsksForAnythingBefore2023()
    {
        var handler = new StubHandler(_ => """{"result":[]}""");

        await Source(handler).FetchAsync(DateTimeOffset.Parse("2007-01-01T00:00:00Z"), DateTimeOffset.Parse("2023-02-01T00:00:00Z"));

        var query = Uri.UnescapeDataString(Assert.Single(handler.Sent).Uri.Query);
        Assert.Contains("[[date::>2022-12-31 23:59:59]]", query);
    }

    [Fact]
    public async Task Fetch_MakesNoCallForAWindowEntirelyBefore2023()
    {
        var handler = new StubHandler(_ => """{"result":[]}""");

        var batch = await Source(handler).FetchAsync(DateTimeOffset.Parse("2020-01-01T00:00:00Z"), DateTimeOffset.Parse("2023-01-01T00:00:00Z"));

        Assert.Empty(handler.Sent);
        Assert.Empty(batch.Responses);
    }

    [Fact]
    public void PageSizeOver100_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Source(new StubHandler(_ => ""), pageSize: 101));
    }
}
