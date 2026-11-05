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
