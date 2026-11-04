using System.Text.Json;
using Rankings.Core;
using Rankings.Job.Liquipedia;

namespace Rankings.Tests;

public class LpdbMatchParserTests
{
    /// Two APL 2026 North playoff BO3s as LPDB returned them, trimmed to the fields that matter.
    public static readonly string Fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lpdb-apl-north-playoffs.json"));

    static (List<SourceMap> Maps, List<SkippedSourceMap> Skipped) Parse(string json)
    {
        var maps = new List<SourceMap>();
        var skipped = new List<SkippedSourceMap>();
        using var document = JsonDocument.Parse(json);
        foreach (var match in document.RootElement.GetProperty("result").EnumerateArray())
            LpdbMatchParser.Parse(match, maps, skipped);
        return (maps, skipped);
    }

    /// The first fixture series with one top-level field replaced.
    static string FirstSeriesWith(string property, string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(Fixture)!;
        var series = node["result"]![0]!;
        series[property] = System.Text.Json.Nodes.JsonNode.Parse(json);
        return $$"""{"result":[{{series.ToJsonString()}}]}""";
    }

    [Fact]
    public void Parse_ReadsEveryMapOfTheFixture()
    {
        var (maps, skipped) = Parse(Fixture);

        Assert.Empty(skipped);
        Assert.Equal(
            ["XtD9IqfaeJ_R01-M001/1", "XtD9IqfaeJ_R01-M001/2", "XtD9IqfaeJ_R01-M001/3",
             "XtD9IqfaeJ_R01-M002/1", "XtD9IqfaeJ_R01-M002/2", "XtD9IqfaeJ_R01-M002/3"],
            maps.Select(m => m.MapId));
    }

    [Fact]
    public void Parse_MapsTheFieldsOfAnOvertimeMap()
    {
        var map = Parse(Fixture).Maps[0];

        Assert.Equal("XtD9IqfaeJ_R01-M001", map.SeriesId);
        Assert.Equal(DateTimeOffset.Parse("2026-07-08T09:00:00Z"), map.PlayedAt);
        Assert.Equal((1, 3), (map.MapNumber, map.MapsInSeries));
        Assert.Equal(("cag by varrel orig", "scarz 2022"), (map.TeamA.Key, map.TeamB.Key));
        Assert.Equal(("CAG by VARREL", "SCARZ"), (map.TeamA.Name, map.TeamB.Name));
        Assert.Equal((7, 8), (map.RoundsA, map.RoundsB));
        Assert.Equal("2", map.Tier);
        Assert.Equal("Asia Pacific League 2026 - North - Stage 1", map.Tournament);
    }

    [Fact]
    public void Parse_KeepsEveryListedPlayer_KeyedByPageName()
    {
        var maps = Parse(Fixture).Maps;

        // SCARZ list six, including the substitute.
        Assert.Equal(["YuKiz", "Wqsyo1", "Rec", "Pyon", "Nina", "FishLike"], maps[0].TeamB.Players.Select(p => p.Key));
        var gatorada = maps[3].TeamB.Players[1];
        Assert.Equal(("Gatorada", "gatorada"), (gatorada.Key, gatorada.Name));
    }

    [Fact]
    public void Parse_IgnoresAnUnplayedThirdMap()
    {
        var json = FirstSeriesWith("match2games", """
            [
              {"match2gameid":1,"winner":"1","opponents":[{"score":7},{"score":3}]},
              {"match2gameid":2,"winner":"1","opponents":[{"score":7},{"score":5}]},
              {"match2gameid":3,"winner":"","resulttype":"np","opponents":[{"score":0},{"score":0}]},
              {"match2gameid":4,"winner":"","map":"","scores":[]}
            ]
            """);

        var (maps, skipped) = Parse(json);

        Assert.Equal(["XtD9IqfaeJ_R01-M001/1", "XtD9IqfaeJ_R01-M001/2"], maps.Select(m => m.MapId));
        Assert.Empty(skipped);
    }

    [Fact]
    public void Parse_IgnoresAnUnplayedMapInThe2023Shape()
    {
        // As in Six Invitational 2023: the decider of a 2–0, with opponents as empty arrays.
        var json = FirstSeriesWith("match2games", """
            [
              {"map":"Kafe Dostoyevsky","match2gameid":1,"scores":[7,4],"winner":"1","walkover":"","resulttype":"",
               "opponents":[{"status":"S","score":7},{"status":"S","score":4}],"extradata":{"timestamp":1675782000}},
              {"map":"Clubhouse","match2gameid":2,"scores":[7,4],"winner":"1","walkover":"","resulttype":"",
               "opponents":[{"status":"S","score":7},{"status":"S","score":4}],"extradata":{"timestamp":1675782000}},
              {"map":"Oregon","match2gameid":3,"scores":[],"opponents":[[],[]],"status":"notplayed",
               "winner":"","walkover":"","resulttype":"np",
               "extradata":{"timestamp":1675782000,"t1halfs":[],"t2halfs":[]}}
            ]
            """);

        var (maps, skipped) = Parse(json);

        Assert.Equal([(7, 4), (7, 4)], maps.Select(m => (m.RoundsA, m.RoundsB)));
        Assert.Empty(skipped);
    }

    [Theory]
    [InlineData("""{"match2gameid":1,"winner":"","map":"Bank","opponents":[{"score":6},{"score":6}]}""", "no winner")]
    [InlineData("""{"match2gameid":1,"winner":"1","map":"Bank","opponents":[]}""", "no round scores")]
    [InlineData("""{"match2gameid":1,"winner":"2","opponents":[{"score":7},{"score":3}]}""", "disagrees")]
    [InlineData("""{"match2gameid":1,"winner":"1","walkover":"1","opponents":[{"score":7},{"score":0}]}""", "not played out")]
    [InlineData("""{"match2gameid":4,"winner":"1","opponents":[{"score":7},{"score":3}]}""", "past the best-of-3")]
    public void Parse_SkipsABadMapWithItsReason(string game, string reason)
    {
        var (maps, skipped) = Parse(FirstSeriesWith("match2games", $"[{game}]"));

        Assert.Empty(maps);
        Assert.Contains(reason, Assert.Single(skipped).Reason);
    }

    [Theory]
    [InlineData("walkover", "\"2\"", "not played out")]
    [InlineData("resulttype", "\"default\"", "not played out")]
    [InlineData("bestof", "0", "no best-of")]
    [InlineData("match2opponents", "[]", "two opponents")]
    public void Parse_SkipsABadSeriesOnce(string property, string json, string reason)
    {
        var (maps, skipped) = Parse(FirstSeriesWith(property, json));

        Assert.Empty(maps);
        var skip = Assert.Single(skipped);
        Assert.Equal("XtD9IqfaeJ_R01-M001", skip.MapId);
        Assert.Contains(reason, skip.Reason);
    }

    [Fact]
    public void Parse_SkipsALegacySeriesWithoutGames()
    {
        // From a real response: MLG Charlotte 2007, Rainbow Six Vegas, 4-player teams, no map data.
        var json = """
            {"result":[{
              "objectname":"70650_5MIatZG2Ep_R01-M001","match2id":"5MIatZG2Ep_R01-M001",
              "winner":"1","walkover":"","resulttype":"","finished":1,"game":"vegas",
              "date":"2007-04-15 00:00:00","dateexact":0,"stream":[],"links":[],"bestof":0,
              "tournament":"MLG Charlotte 2007","liquipediatier":"2","liquipediatiertype":"General",
              "extradata":{"timestamp":1176595200},
              "match2opponents":[
                {"type":"team","name":"X (Vegas)","template":"x (vegas)","score":3,
                 "match2players":[{"name":"DarkZone","displayname":"DarkZone","extradata":[]}],"extradata":[]},
                {"type":"team","name":"Eternal Existence (Vegas)","template":"eternal existence (vegas)","score":2,
                 "match2players":[{"name":"Trigga_Man","displayname":"Trigga Man","extradata":[]}],"extradata":[]}],
              "match2games":[]
            }]}
            """;

        var (maps, skipped) = Parse(json);

        Assert.Empty(maps);
        var skip = Assert.Single(skipped);
        Assert.Equal("5MIatZG2Ep_R01-M001", skip.MapId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1176595200), skip.PlayedAt);
        Assert.Contains("no best-of", skip.Reason);
    }

    [Fact]
    public void Parse_IgnoresAnUnfinishedSeries()
    {
        var (maps, skipped) = Parse(FirstSeriesWith("finished", "0"));

        Assert.Empty(maps);
        Assert.Empty(skipped);
    }

    [Fact]
    public void Parse_AcceptsNumbersAsStringsAndFallsBackToTheDateField()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(FirstSeriesWith("match2games", """
            [{"match2gameid":"1","winner":"1","scores":["7","4"],"extradata":[]}]
            """))!;
        var series = node["result"]![0]!;
        series["bestof"] = "3";
        series["finished"] = "1";
        series["extradata"] = new System.Text.Json.Nodes.JsonArray();

        var map = Assert.Single(Parse(node.ToJsonString()).Maps);

        Assert.Equal((7, 4, 3), (map.RoundsA, map.RoundsB, map.MapsInSeries));
        Assert.Equal(DateTimeOffset.Parse("2026-07-08T09:00:00Z"), map.PlayedAt);
    }
}
