using System.Net;
using Rankings.Admin;
using Rankings.Core;
using Rankings.Job.Storage;

namespace Rankings.Tests;

public sealed class AdminTests : IDisposable
{
    readonly string _path = Path.Combine(Path.GetTempPath(), $"rankings-admin-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        File.Delete(_path);
        File.Delete(_path + ".r2.json");
    }

    AdminStore StoreWithDatabase()
    {
        using (RankingsDb.Open(_path)) { }
        return new AdminStore(_path);
    }

    /// <summary>A database that has seen two Liquipedia teams, neither assigned yet.</summary>
    AdminStore StoreWithUnassignedTeams()
    {
        var store = StoreWithDatabase();
        using var db = RankingsDb.Open(_path);
        var player = new SourcePlayer("Zaka", "Zaka");
        new ProviderIds(db).Resolve([new SourceMap("m/1", "m", DateTimeOffset.Parse("2026-07-08T09:00:00Z"), 1, 1,
            new SourceTeam("scarz 2022", "SCARZ", [player]), new SourceTeam("dplus orig", "DPlus", []), 7, 5, "2", "APAC League")]);
        return store;
    }

    // The store

    [Fact]
    public void Store_RefusesToWorkWithoutADatabase()
    {
        var store = new AdminStore(_path);

        Assert.Throws<AdminException>(() => store.Teams());
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void CreateTeam_LinksTheKeySoItsMapsAreNoLongerSkipped()
    {
        var store = StoreWithUnassignedTeams();

        var teamId = store.CreateTeam("APAC-N", "SCARZ", "SZ", "2026-01-01", "scarz 2022");

        var team = Assert.Single(store.Teams());
        Assert.Equal((teamId, "APAC-N", "SCARZ", "SZ"), (team.TeamId, team.League, team.Name, team.ShortName));
        Assert.Equal(["scarz 2022"], team.ProviderKeys);
        Assert.Equal(["dplus orig"], store.UnassignedTeams().Select(unassigned => unassigned.ProviderKey));
    }

    [Fact]
    public void CreateTeam_RefusesTheApacGroupAndUnknownKeys()
    {
        var store = StoreWithUnassignedTeams();

        Assert.Throws<AdminException>(() => store.CreateTeam("APAC", "SCARZ", null, "2026-01-01", null));
        Assert.Throws<AdminException>(() => store.CreateTeam("EU", "Nobody", null, "2026-01-01", "no such key"));
        Assert.Empty(store.Teams());
    }

    [Fact]
    public void LinkTeam_ARebrandPointsTheNewKeyAtTheSameTeam()
    {
        var store = StoreWithUnassignedTeams();
        var teamId = store.CreateTeam("APAC-N", "SCARZ", null, "2026-01-01", "scarz 2022");

        store.LinkTeam("dplus orig", teamId);

        Assert.Equal(["dplus orig", "scarz 2022"], Assert.Single(store.Teams()).ProviderKeys);
        Assert.Throws<AdminException>(() => store.LinkTeam("dplus orig", 999));
    }

    [Fact]
    public void SetLeague_FixesAWrongAssignmentBeforeTheTeamIsRated()
    {
        var store = StoreWithUnassignedTeams();
        var teamId = store.CreateTeam("EU", "SCARZ", null, "2026-01-01", "scarz 2022");

        var fullRecalculationNeeded = store.SetLeague(teamId, "APAC-N");

        Assert.False(fullRecalculationNeeded);
        Assert.Equal("APAC-N", Assert.Single(store.Teams()).League);
    }

    [Fact]
    public void SetLeague_RefusesTheApacGroupAndUnknownLeagues()
    {
        var store = StoreWithUnassignedTeams();
        var teamId = store.CreateTeam("EU", "SCARZ", null, "2026-01-01", null);

        Assert.Throws<AdminException>(() => store.SetLeague(teamId, "APAC"));
        Assert.Throws<AdminException>(() => store.SetLeague(teamId, "MARS"));
        Assert.Throws<AdminException>(() => store.SetLeague(999, "EU"));
        Assert.Equal("EU", Assert.Single(store.Teams()).League);
    }

    [Fact]
    public void SetLeague_OnARatedTeam_SaysAFullRecalculationIsNeeded()
    {
        var store = StoreWithUnassignedTeams();
        var teamId = store.CreateTeam("EU", "SCARZ", null, "2026-01-01", null);
        using (var db = RankingsDb.Open(_path))
            db.Execute($"INSERT INTO team_ratings (team_id, rating_offset) VALUES ({teamId}, 12.5)");

        Assert.True(store.SetLeague(teamId, "APAC-N"));
    }

    [Fact]
    public void MergeTeams_MovesKeysAndMapsAndDeletesTheDuplicate()
    {
        var store = StoreWithUnassignedTeams();
        var kept = store.CreateTeam("APAC-N", "SCARZ", "SZ", "2026-01-01", "scarz 2022");
        var duplicate = store.CreateTeam("ASIA", "SCARZ (old)", null, "2026-01-01", "dplus orig");
        var opponent = store.CreateTeam("APAC-N", "Opponent", null, "2026-01-01", null);
        using (var db = RankingsDb.Open(_path))
            db.Execute($"""
                INSERT INTO maps (map_id, series_id, played_at, map_number, maps_in_series, team_a, team_b, rounds_a, rounds_b)
                VALUES ('s/1', 's', 1783501200, 1, 1, {duplicate}, {opponent}, 7, 3)
                """);

        var fullRecalculationNeeded = store.MergeTeams(duplicate, kept);

        Assert.False(fullRecalculationNeeded);
        var teams = store.Teams();
        Assert.Equal([kept, opponent], teams.Select(team => team.TeamId).Order());
        var keptTeam = teams.Single(team => team.TeamId == kept);
        Assert.Equal(("APAC-N", "SCARZ"), (keptTeam.League, keptTeam.Name));
        Assert.Equal(["dplus orig", "scarz 2022"], keptTeam.ProviderKeys);
        using var check = RankingsDb.Open(_path);
        Assert.Equal((long)kept, check.Scalar("SELECT team_a FROM maps WHERE map_id = 's/1'"));
    }

    [Fact]
    public void MergeTeams_RefusesTeamsThatPlayedEachOther()
    {
        var store = StoreWithUnassignedTeams();
        var first = store.CreateTeam("EU", "First", null, "2026-01-01", null);
        var second = store.CreateTeam("EU", "Second", null, "2026-01-01", null);
        using (var db = RankingsDb.Open(_path))
            db.Execute($"""
                INSERT INTO maps (map_id, series_id, played_at, map_number, maps_in_series, team_a, team_b, rounds_a, rounds_b)
                VALUES ('s/1', 's', 1783501200, 1, 1, {first}, {second}, 7, 3)
                """);

        var error = Assert.Throws<AdminException>(() => store.MergeTeams(first, second));
        Assert.Contains("played each other", error.Message);
        Assert.Throws<AdminException>(() => store.MergeTeams(first, first));
        Assert.Throws<AdminException>(() => store.MergeTeams(first, 999));
        Assert.Equal(2, store.Teams().Count);
    }

    [Fact]
    public void MergeTeams_OfARatedTeam_SaysAFullRecalculationIsNeeded()
    {
        var store = StoreWithUnassignedTeams();
        var kept = store.CreateTeam("EU", "Kept", null, "2026-01-01", null);
        var duplicate = store.CreateTeam("EU", "Duplicate", null, "2026-01-01", null);
        using (var db = RankingsDb.Open(_path))
        {
            db.Execute($"INSERT INTO team_ratings (team_id, rating_offset) VALUES ({duplicate}, 12.5)");
            db.Execute($"""
                INSERT INTO team_snapshots (date, team_id, rank, rating, rating_offset, region_rating, change, maps_played, algorithm_version)
                VALUES ('2026-03-01', {duplicate}, 1, 1512.5, 12.5, 1500, 0, 3, 'elo-1')
                """);
        }

        Assert.True(store.MergeTeams(duplicate, kept));
        using var check = RankingsDb.Open(_path);
        Assert.Equal(0L, check.Scalar($"SELECT COUNT(*) FROM team_snapshots WHERE team_id = {duplicate}"));
    }

    [Fact]
    public void SetDisplay_ANewNameAppliesFromItsDate()
    {
        var store = StoreWithUnassignedTeams();
        var teamId = store.CreateTeam("EU", "Old Name", null, "2025-01-01", null);

        store.SetDisplay(teamId, "2026-01-01", "New Name", "NN", "12.svg");
        store.SetDisplay(teamId, "2999-01-01", "Future Name", null, null);

        var team = Assert.Single(store.Teams());
        Assert.Equal(("New Name", "NN", "12.svg"), (team.Name, team.ShortName, team.Logo));
    }

    [Fact]
    public void SetStatus_UpdatesTheTeamAndRecordsAnOverrideForReplays()
    {
        var store = StoreWithUnassignedTeams();
        var teamId = store.CreateTeam("EU", "Team", null, "2025-01-01", null);

        store.SetStatus(teamId, "disbanded", "2026-06-01", "org left");

        Assert.Equal("disbanded", Assert.Single(store.Teams()).Status);
        using var db = RankingsDb.Open(_path);
        Assert.Equal("""{"status":"disbanded"}""", db.Scalar("SELECT payload FROM overrides WHERE kind = 'status'"));
        Assert.Throws<AdminException>(() => store.SetStatus(teamId, "retired", "2026-06-01", null));
    }

    [Fact]
    public void Rankings_ShowsTheChosenDayWithNamesInUseAndTheWeekChange()
    {
        var store = StoreWithUnassignedTeams();
        Assert.Null(store.Rankings(null).Date);
        var leader = store.CreateTeam("EU", "Old Name", null, "2025-01-01", null);
        var unranked = store.CreateTeam("NA", "Unranked", null, "2025-01-01", null);
        store.SetDisplay(leader, "2026-03-05", "New Name", null, null);
        using (var db = RankingsDb.Open(_path))
        {
            using var insert = db.Connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO team_snapshots (date, team_id, rank, rating, rating_offset, region_rating, change, maps_played, algorithm_version) VALUES
                    ('2026-03-01', $leader, 1, 1550, 50, 1500, 0, 12, 'elo-1'),
                    ('2026-03-08', $leader, 1, 1562.5, 60, 1502.5, 2, 15, 'elo-1'),
                    ('2026-03-08', $unranked, NULL, 1480, -20, 1500, 0, 4, 'elo-1');
                INSERT INTO region_snapshots (date, region_id, rating, algorithm_version) VALUES
                    ('2026-03-01', 'EU', 1500, 'elo-1'), ('2026-03-08', 'EU', 1502.5, 'elo-1');
                """;
            insert.Parameters.AddWithValue("$leader", leader);
            insert.Parameters.AddWithValue("$unranked", unranked);
            insert.ExecuteNonQuery();
        }

        var latest = store.Rankings(null);
        Assert.Equal(("2026-03-08", "2026-03-01", "2026-03-08"), (latest.Date, latest.FirstDate, latest.LastDate));
        Assert.Equal([leader, unranked], latest.Teams.Select(team => team.TeamId));
        var leaderRow = latest.Teams[0];
        Assert.Equal(("New Name", 12.5, false), (leaderRow.Name, leaderRow.WeekChange, leaderRow.Provisional));
        Assert.True(latest.Teams[1].Provisional);
        Assert.Null(latest.Teams[1].Rank);
        Assert.Equal(2.5, Assert.Single(latest.Leagues).WeekChange);

        var earlier = store.Rankings("2026-03-04");
        Assert.Equal("2026-03-01", earlier.Date);
        Assert.Equal("Old Name", Assert.Single(earlier.Teams).Name);
    }

    [Fact]
    public void SetStatuses_ChangesEverySelectedTeamOrNoneOfThem()
    {
        var store = StoreWithUnassignedTeams();
        var first = store.CreateTeam("EU", "First", null, "2025-01-01", null);
        var second = store.CreateTeam("NA", "Second", null, "2025-01-01", null);
        var untouched = store.CreateTeam("EU", "Untouched", null, "2025-01-01", null);

        store.SetStatuses([first, second], "relegated", "2026-06-01", "end of stage");

        var statuses = store.Teams().ToDictionary(team => team.TeamId, team => team.Status);
        Assert.Equal(("relegated", "relegated", "active"), (statuses[first], statuses[second], statuses[untouched]));
        using (var db = RankingsDb.Open(_path))
            Assert.Equal(2L, db.Scalar("SELECT COUNT(*) FROM overrides WHERE kind = 'status' AND effective_on = '2026-06-01'"));

        Assert.Throws<AdminException>(() => store.SetStatuses([untouched, 9999], "disbanded", "2026-07-01", null));
        Assert.Equal("active", store.Teams().Single(team => team.TeamId == untouched).Status);
        Assert.Throws<AdminException>(() => store.SetStatuses([], "disbanded", "2026-07-01", null));
    }

    [Fact]
    public void LinkPlayer_ARenamedKeyPointsAtTheOldPlayer()
    {
        var store = StoreWithDatabase();
        // Zaka as first seen, and the same player again under a new key after a rename.
        using (var db = RankingsDb.Open(_path))
            db.Execute("""
                INSERT INTO players (player_id, name) VALUES (1, 'Zaka'), (50, 'Zaka2');
                INSERT INTO provider_players VALUES ('Zaka', 1), ('Zaka2', 50);
                """);
        var zaka = store.FindPlayers("zaka").Single(player => player.Name == "Zaka");

        store.LinkPlayer("Zaka2", zaka.PlayerId);

        Assert.Equal(["Zaka", "Zaka2"], store.FindPlayers("Zaka").Single(player => player.PlayerId == zaka.PlayerId).ProviderKeys);
        Assert.Throws<AdminException>(() => store.LinkPlayer("never seen", zaka.PlayerId));
    }

    [Fact]
    public void Seasons_AddRenameAndDelete()
    {
        var store = StoreWithDatabase();

        store.SetSeason("2026-02-20", "2026");
        store.SetSeason("2026-02-20", "Season 2026");
        store.SetSeason("2027-02-19", "2027");
        store.DeleteSeason("2027-02-19");

        Assert.Equal([new AdminSeason("2026-02-20", "Season 2026")], store.Seasons());
        Assert.Throws<AdminException>(() => store.SetSeason("20/02/2026", "2026"));
    }

    [Fact]
    public void SetEntryLevel_OnlyOnRatedLeagues()
    {
        var store = StoreWithDatabase();

        store.SetEntryLevel("CN", -40);

        Assert.Equal(-40, store.Regions().Single(region => region.RegionId == "CN").EntryLevel);
        store.SetEntryLevel("CN", null);
        Assert.Null(store.Regions().Single(region => region.RegionId == "CN").EntryLevel);
        Assert.Throws<AdminException>(() => store.SetEntryLevel("APAC", 10));
    }

    // R2 sync guards

    static readonly SyncRecord Pulled = new("\"abc\"", DateTimeOffset.Parse("2026-11-04T10:00:00Z"), "HASH-AT-PULL");

    [Theory]
    [InlineData("2026-11-04T03:59:00Z", false)]
    [InlineData("2026-11-04T04:00:00Z", true)]
    [InlineData("2026-11-04T05:59:00Z", true)]
    [InlineData("2026-11-04T06:00:00Z", false)]
    public void JobWindow_IsFourToSixUtc(string now, bool inWindow)
    {
        Assert.Equal(inWindow, R2Sync.InJobWindow(DateTimeOffset.Parse(now)));
    }

    [Fact]
    public void PushRefusal_AllowsAPushOfChangesToTheCopyPulled()
    {
        Assert.Null(R2Sync.PushRefusal(DateTimeOffset.Parse("2026-11-04T12:00:00Z"), Pulled, "\"abc\"", "HASH-NOW"));
    }

    [Theory]
    [InlineData("2026-11-04T04:30:00Z", "\"abc\"", "HASH-NOW", "04:00")]
    [InlineData("2026-11-04T12:00:00Z", "\"newer\"", "HASH-NOW", "changed since you pulled")]
    [InlineData("2026-11-04T12:00:00Z", "\"abc\"", "HASH-AT-PULL", "Nothing changed")]
    public void PushRefusal_RefusesDuringTheJobAfterTheJobAndWithoutChanges(string now, string remoteETag, string localHash, string reason)
    {
        Assert.Contains(reason, R2Sync.PushRefusal(DateTimeOffset.Parse(now), Pulled, remoteETag, localHash));
    }

    [Fact]
    public void PushRefusal_RefusesAFileThatNeverCameFromR2()
    {
        Assert.Contains("Pull from R2 first", R2Sync.PushRefusal(DateTimeOffset.Parse("2026-11-04T12:00:00Z"), null, "\"abc\"", "HASH"));
    }

    [Fact]
    public async Task Sync_WithoutR2SettingsSaysSoInsteadOfFailingLater()
    {
        var sync = new R2Sync(_path, s3: null, bucket: null);

        Assert.False(sync.Status().Configured);
        var refusal = await Assert.ThrowsAsync<AdminException>(() => sync.PushAsync());
        Assert.Contains("R2 is not set up", refusal.Message);
    }

    // Local only

    [Fact]
    public void LocalOnly_AnswersThisMachineOnly()
    {
        Assert.Null(LocalOnly.Refusal(IPAddress.Loopback, "GET", null));
        Assert.Null(LocalOnly.Refusal(IPAddress.IPv6Loopback, "GET", null));
        Assert.NotNull(LocalOnly.Refusal(IPAddress.Parse("192.168.1.20"), "GET", null));
        Assert.NotNull(LocalOnly.Refusal(null, "GET", null));
    }

    [Fact]
    public void LocalOnly_ChangesNeedTheDashboardsHeader()
    {
        Assert.NotNull(LocalOnly.Refusal(IPAddress.Loopback, "POST", null));
        Assert.NotNull(LocalOnly.Refusal(IPAddress.Loopback, "DELETE", "yes"));
        Assert.Null(LocalOnly.Refusal(IPAddress.Loopback, "POST", "1"));
    }
}
