using System.Text.Json;
using System.Text.Json.Nodes;
using Rankings.Core;
using Rankings.Job.Archive;
using Rankings.Job.Liquipedia;

namespace Rankings.Tests;

public class LpdbArchiveTests
{
    static readonly DateTimeOffset FetchedAt = DateTimeOffset.Parse("2026-11-02T04:17:00Z");

    static RawResponse Page(string body) => new("match?test", FetchedAt, body);

    [Fact]
    public void Key_IsOneFilePerDay()
    {
        Assert.Equal("matches/2026/07/08.json.gz", LpdbArchive.Key(new DateOnly(2026, 7, 8)));
    }

    [Fact]
    public void SplitByDay_GroupsSeriesByStartDay_KeepsThemAsReceived_AndWritesEmptyDays()
    {
        var days = LpdbArchive.SplitByDay([Page(LpdbMatchParserTests.Fixture)],
            new DateOnly(2026, 7, 7), new DateOnly(2026, 7, 8), FetchedAt);

        Assert.Equal([new DateOnly(2026, 7, 7), new DateOnly(2026, 7, 8)], days.Keys);
        Assert.Empty(JsonNode.Parse(days[new DateOnly(2026, 7, 7)])!["result"]!.AsArray());

        var day = JsonNode.Parse(days[new DateOnly(2026, 7, 8)])!;
        Assert.Equal(FetchedAt, day["fetchedAt"]!.GetValue<DateTimeOffset>());
        var original = JsonNode.Parse(LpdbMatchParserTests.Fixture)!["result"]!.AsArray();
        Assert.True(JsonNode.DeepEquals(original, day["result"]));
    }

    [Fact]
    public void SplitByDay_KeepsASeriesRepeatedAcrossPagesOnce()
    {
        var days = LpdbArchive.SplitByDay([Page(LpdbMatchParserTests.Fixture), Page(LpdbMatchParserTests.Fixture)],
            new DateOnly(2026, 7, 8), new DateOnly(2026, 7, 8), FetchedAt);

        Assert.Equal(2, JsonNode.Parse(days[new DateOnly(2026, 7, 8)])!["result"]!.AsArray().Count);
    }

    [Fact]
    public void SplitByDay_RefusesASeriesOutsideTheWindowRatherThanWriteAPartialDay()
    {
        // The fixture's series are dated 2026-07-08; a file for that day would replace a complete one.
        var refusal = Assert.Throws<InvalidOperationException>(() => LpdbArchive.SplitByDay([Page(LpdbMatchParserTests.Fixture)],
            new DateOnly(2026, 7, 9), new DateOnly(2026, 7, 10), FetchedAt));

        Assert.Contains("2026-07-08", refusal.Message);
    }

    [Fact]
    public void SplitByDay_FilesBySeriesDateFieldNotTheTimestamp()
    {
        var body = LpdbMatchParserTests.Fixture.Replace("\"timestamp\": 1783501200", "\"timestamp\": 1783400000");

        var days = LpdbArchive.SplitByDay([Page(body)], new DateOnly(2026, 7, 8), new DateOnly(2026, 7, 8), FetchedAt);

        Assert.Equal(2, JsonNode.Parse(days[new DateOnly(2026, 7, 8)])!["result"]!.AsArray().Count);
    }

    [Fact]
    public void ArchivedDay_ParsesToTheSameMapsAsTheResponse()
    {
        var day = LpdbArchive.SplitByDay([Page(LpdbMatchParserTests.Fixture)],
            new DateOnly(2026, 7, 8), new DateOnly(2026, 7, 8), FetchedAt)[new DateOnly(2026, 7, 8)];
        var fromResponse = new List<SourceMap>();
        var fromArchive = new List<SourceMap>();

        LpdbArchive.Parse(LpdbMatchParserTests.Fixture, fromResponse, []);
        LpdbArchive.Parse(LpdbArchive.Decompress(LpdbArchive.Compress(day)), fromArchive, []);

        Assert.Equal(JsonSerializer.Serialize(fromResponse), JsonSerializer.Serialize(fromArchive));
    }

    [Fact]
    public async Task LocalStore_RoundTripsAndReportsMissingKeys()
    {
        var root = Path.Combine(Path.GetTempPath(), $"archive-{Guid.NewGuid():N}");
        try
        {
            var store = new LocalArchiveStore(root);
            Assert.Null(await store.ReadAsync("matches/2026/07/08.json.gz"));

            await store.WriteAsync("matches/2026/07/08.json.gz", [1, 2, 3]);
            await store.WriteAsync("matches/2026/07/08.json.gz", [4, 5]);

            Assert.Equal([4, 5], await store.ReadAsync("matches/2026/07/08.json.gz"));
            Assert.True(File.Exists(Path.Combine(root, "matches", "2026", "07", "08.json.gz")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("https://s3.eu-central-003.backblazeb2.com", "eu-central-003")]
    [InlineData("s3.us-west-004.backblazeb2.com", "us-west-004")]
    public void B2Region_IsReadFromTheEndpoint(string endpoint, string region)
    {
        Assert.Equal(region, S3ArchiveStore.B2Region(endpoint));
    }

    [Fact]
    public void B2Region_RefusesAnotherEndpoint()
    {
        Assert.Throws<ArgumentException>(() => S3ArchiveStore.B2Region("https://example.r2.cloudflarestorage.com"));
    }
}
