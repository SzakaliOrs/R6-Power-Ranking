using System.Text.Json;
using Rankings.Core;
using Rankings.Job.Liquipedia;
using Rankings.Job.Storage;

namespace Rankings.Tests;

public class ProviderIdsTests
{
    static List<SourceMap> FixtureMaps()
    {
        var maps = new List<SourceMap>();
        using var document = JsonDocument.Parse(LpdbMatchParserTests.Fixture);
        foreach (var match in document.RootElement.GetProperty("result").EnumerateArray())
            LpdbMatchParser.Parse(match, maps, []);
        return maps;
    }

    static RankingsDb DbWithTeams()
    {
        var db = RankingsDb.Open(":memory:");
        db.Execute("""
            INSERT INTO teams (team_id, region_id, created_on) VALUES
                (1, 'APAC-N', '2026-01-01'), (2, 'APAC-N', '2026-01-01'),
                (3, 'APAC-N', '2026-01-01'), (4, 'APAC-N', '2026-01-01');
            """);
        return db;
    }

    [Fact]
    public void Resolve_SkipsMapsWithUnassignedTeamsAndListsThoseTeams()
    {
        using var db = DbWithTeams();
        var ids = new ProviderIds(db);

        var result = ids.Resolve(FixtureMaps());

        Assert.Empty(result.Maps);
        Assert.Equal(6, result.Skipped.Count);
        Assert.Contains("cag by varrel orig, scarz 2022", result.Skipped[0].Reason);
        var unassigned = ids.UnassignedTeams();
        Assert.Equal(["dplus orig", "kinotrope gaming", "cag by varrel orig", "scarz 2022"], unassigned.Select(t => t.ProviderKey));
        Assert.Equal("Asia Pacific League 2026 - North - Stage 1", unassigned[0].LastTournament);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1783515600), unassigned[0].LastSeenAt);
    }

    [Fact]
    public void Resolve_UsesLinkedTeamsAndCreatesPlayersOnce()
    {
        using var db = DbWithTeams();
        var ids = new ProviderIds(db);
        var maps = FixtureMaps();
        ids.Resolve(maps);
        ids.LinkTeam("cag by varrel orig", 1);
        ids.LinkTeam("scarz 2022", 2);

        var result = ids.Resolve(maps);

        Assert.Equal(3, result.Maps.Count);
        Assert.All(result.Skipped, s => Assert.Contains("dplus orig, kinotrope gaming", s.Reason));
        var first = result.Maps[0];
        Assert.Equal((1, 2), (first.TeamA, first.TeamB));
        Assert.Equal(5, first.PlayersA.Count);
        Assert.Equal(6, first.PlayersB.Count);
        // The same players on every map of the series, created once.
        Assert.All(result.Maps, m => Assert.Equal(first.PlayersB, m.PlayersB));
        Assert.Equal(11L, db.Scalar("SELECT COUNT(*) FROM players"));
        Assert.Equal("Wqsyo1", db.Scalar($"SELECT name FROM players WHERE player_id = {first.PlayersB[1]}"));
    }

    [Fact]
    public void Resolve_ResolvedMapsCanBeSaved()
    {
        using var db = DbWithTeams();
        var ids = new ProviderIds(db);
        var maps = FixtureMaps();
        ids.Resolve(maps);
        ids.LinkTeam("cag by varrel orig", 1);
        ids.LinkTeam("scarz 2022", 2);
        ids.LinkTeam("dplus orig", 3);
        ids.LinkTeam("kinotrope gaming", 4);

        var result = ids.Resolve(maps);
        db.SaveMaps(result.Maps);

        Assert.Empty(result.Skipped);
        Assert.Equal(6, db.LoadMaps().Count);
    }

    [Fact]
    public void LinkTeam_ARebrandedKeyResolvesToTheSameTeam()
    {
        using var db = DbWithTeams();
        var ids = new ProviderIds(db);
        var maps = FixtureMaps().Take(1).ToList();
        ids.Resolve(maps);
        ids.LinkTeam("cag by varrel orig", 1);
        ids.LinkTeam("scarz 2022", 2);
        var rebranded = maps[0] with { TeamB = maps[0].TeamB with { Key = "scarz 2027", Name = "SCARZ Tokyo" } };

        Assert.Empty(ids.Resolve([rebranded]).Maps);
        ids.LinkTeam("scarz 2027", 2);

        Assert.Equal(2, Assert.Single(ids.Resolve([rebranded]).Maps).TeamB);
    }

    [Fact]
    public void LinkTeam_RefusesAKeyNeverSeen()
    {
        using var db = DbWithTeams();

        Assert.Throws<KeyNotFoundException>(() => new ProviderIds(db).LinkTeam("nobody", 1));
    }

    [Fact]
    public void LinkPlayer_ARenamedKeyResolvesToTheSamePlayer()
    {
        using var db = DbWithTeams();
        var ids = new ProviderIds(db);
        var maps = FixtureMaps().Take(1).ToList();
        ids.Resolve(maps);
        ids.LinkTeam("cag by varrel orig", 1);
        ids.LinkTeam("scarz 2022", 2);
        var zaka = ids.Resolve(maps).Maps[0].PlayersA[0];

        ids.LinkPlayer("Zaka2", zaka);
        var players = maps[0].TeamA.Players.Select(p => p.Key == "Zaka" ? p with { Key = "Zaka2" } : p).ToList();
        var renamed = maps[0] with { TeamA = maps[0].TeamA with { Players = players } };

        Assert.Equal(zaka, ids.Resolve([renamed]).Maps[0].PlayersA[0]);
    }
}
