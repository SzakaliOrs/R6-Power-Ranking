using System.Text.Json;
using Rankings.Core;
using Rankings.Job.Site;
using Rankings.Sim;

namespace Rankings.Tests;

public sealed class SiteExportTests : IDisposable
{
    static readonly Lazy<SiteInput> Sample = new(() => SampleSite.Build(new SimulationOptions()));

    readonly string _directory = Path.Combine(Path.GetTempPath(), $"site-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    JsonElement Read(string file) => JsonDocument.Parse(File.ReadAllText(Path.Combine(_directory, file))).RootElement;

    // Snapshots

    [Fact]
    public void Snapshot_RanksRatedTeamsAndKeepsFrozenOnesUnranked()
    {
        var state = new RatingState();
        state.AddLeague("EU", 1500);
        state.AddTeam(1, "EU", 20);
        state.AddTeam(2, "EU", -20);
        state.AddTeam(3, "EU", 50);
        state.AddNewTeam(4, "EU");
        RatingEngine.Freeze(state, 3);
        var day = new DateOnly(2026, 3, 1);

        var snapshot = Snapshots.Take(state, day, new RatingSettings(), previous: null);

        Assert.Equal([(1, (int?)1), (2, 2), (3, null)], snapshot.Teams.Select(team => (team.TeamId, team.Rank)));
        Assert.All(snapshot.Teams, team => Assert.Equal(0, team.Change));
        Assert.Equal(1500, Assert.Single(snapshot.Leagues).Rating);
    }

    [Fact]
    public void Snapshot_ChangeIsSinceThePreviousSnapshot()
    {
        var state = new RatingState();
        state.AddLeague("EU", 1500);
        state.AddTeam(1, "EU", 20);
        var settings = new RatingSettings();
        var first = Snapshots.Take(state, new DateOnly(2026, 3, 1), settings, null);
        state.Team(1).Offset = 32.5;

        var second = Snapshots.Take(state, new DateOnly(2026, 3, 2), settings, first);

        Assert.Equal(12.5, Assert.Single(second.Teams).Change, 9);
    }

    // Site files

    [Fact]
    public void Write_RankingsListEveryRankedTeamInOrder()
    {
        SiteExport.Write(Sample.Value, _directory);

        var teams = Read("rankings.json").GetProperty("teams").EnumerateArray().ToList();
        Assert.Equal(70, teams.Count);
        Assert.Equal(Enumerable.Range(1, 70), teams.Select(team => team.GetProperty("rank").GetInt32()));
        var ratings = teams.Select(team => team.GetProperty("rating").GetDouble()).ToList();
        Assert.Equal(ratings.OrderDescending(), ratings);
        Assert.Equal(JsonValueKind.Null, teams[0].GetProperty("team").GetProperty("logo").ValueKind);
    }

    [Fact]
    public void Write_RegionsShowApacAsOneRowWithItsSubregionsBeneath()
    {
        SiteExport.Write(Sample.Value, _directory);

        var regions = Read("regions.json").GetProperty("regions").EnumerateArray().ToList();
        Assert.Equal(["APAC", "CN", "EU", "NA", "SA"], regions.Select(region => region.GetProperty("id").GetString()).Order());
        var apac = regions.Single(region => region.GetProperty("id").GetString() == "APAC");
        var subregions = apac.GetProperty("subregions").EnumerateArray().ToList();
        Assert.Equal(["APAC-N", "ASIA", "OCE"], subregions.Select(region => region.GetProperty("id").GetString()).Order());
        Assert.Equal(subregions.Average(region => region.GetProperty("rating").GetDouble()), apac.GetProperty("rating").GetDouble(), 0);
        Assert.Equal(30, apac.GetProperty("teams").GetInt32());
    }

    [Fact]
    public void Write_MoversCoverSevenAndThirtyDays()
    {
        SiteExport.Write(Sample.Value, _directory);

        var periods = Read("movers.json").GetProperty("periods").EnumerateArray().ToList();
        Assert.Equal([7, 30], periods.Select(period => period.GetProperty("days").GetInt32()));
        Assert.All(periods, period => Assert.All(period.GetProperty("rises").EnumerateArray(),
            mover => Assert.True(mover.GetProperty("change").GetDouble() > 0)));
    }

    [Fact]
    public void Write_EveryTeamGetsATeamViewWithHistoryAndRecentMaps()
    {
        SiteExport.Write(Sample.Value, _directory);

        var latest = Sample.Value.Days[^1];
        Assert.Equal(latest.Teams.Count, Directory.GetFiles(Path.Combine(_directory, "teams")).Length);
        var leader = Read($"teams/{latest.Teams[0].TeamId}.json");
        Assert.Equal(1, leader.GetProperty("rank").GetInt32());
        Assert.True(leader.GetProperty("history").GetArrayLength() > 100);
        var recent = leader.GetProperty("recent").EnumerateArray().ToList();
        Assert.Equal(20, recent.Count);
        Assert.All(recent, map => Assert.Equal(map.GetProperty("roundsFor").GetInt32() > map.GetProperty("roundsAgainst").GetInt32(),
            map.GetProperty("won").GetBoolean()));
    }

    [Fact]
    public void Write_ReplacesTeamFilesFromAnEarlierExport()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "teams"));
        File.WriteAllText(Path.Combine(_directory, "teams", "9999.json"), "{}");

        SiteExport.Write(Sample.Value, _directory);

        Assert.False(File.Exists(Path.Combine(_directory, "teams", "9999.json")));
        Assert.Equal("sample", Read("meta.json").GetProperty("algorithmVersion").GetString());
    }
}
