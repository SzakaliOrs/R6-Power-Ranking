using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Rankings.Core;
using Rankings.Job;
using Rankings.Job.Archive;
using Rankings.Job.Storage;
using Rankings.Sim;

namespace Rankings.Tests;

public sealed class DailyRunTests : IDisposable
{
    /// <summary>A Monday in the 2026 first stage, with league maps every week.</summary>
    static readonly DateTimeOffset FirstRunAt = DateTimeOffset.Parse("2026-03-02T04:17:00Z", CultureInfo.InvariantCulture);

    const string Synthetic = SyntheticSetup.DataSource;

    readonly string _archiveRoot = Path.Combine(Path.GetTempPath(), $"daily-archive-{Guid.NewGuid():N}");
    readonly string _siteRoot = Path.Combine(Path.GetTempPath(), $"daily-site-{Guid.NewGuid():N}");
    readonly List<RankingsDb> _databases = [];

    public void Dispose()
    {
        foreach (var db in _databases)
            db.Dispose();
        foreach (var directory in (string[])[_archiveRoot, _siteRoot])
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
    }

    static SimulatedWorld World => SimulatorTests.World.Value;

    RankingsDb NewDatabase()
    {
        var db = RankingsDb.Open(":memory:");
        SyntheticSetup.Ensure(db, World);
        _databases.Add(db);
        return db;
    }

    DailyRun Runner(RankingsDb db, IMatchSource source, TimeProvider clock)
    {
        var archive = new LocalArchiveStore(_archiveRoot);
        return new DailyRun(db, source, [archive], archive, new RatingSettings(), clock);
    }

    static DailyRunOptions Daily => new() { DataSource = Synthetic };

    static DailyRunOptions FullFromSimulation => new() { DataSource = Synthetic, Full = true, FetchWholeHistory = true };

    static DailyRunOptions FullFromArchive => new() { DataSource = Synthetic, Full = true };

    /// <summary>A full start, then one run a day for a week, as the schedule would make them.</summary>
    async Task<(DailyRunSummary Last, FakeTimeProvider Clock)> RunAWeek(RankingsDb db, Func<TimeProvider, IMatchSource> source)
    {
        var clock = new FakeTimeProvider(FirstRunAt);
        var last = await Runner(db, source(clock), clock).RunAsync(FullFromSimulation);
        for (var runs = 0; runs < 7; runs++)
        {
            clock.Advance(TimeSpan.FromDays(1));
            last = await Runner(db, source(clock), clock).RunAsync(Daily);
        }
        return (last, clock);
    }

    // The plan's check: daily runs and a full recalculation give the same ratings

    [Fact]
    public async Task AWeekOfDailyRuns_GivesTheSameRatingsAsAFullRecalculationFromTheArchive()
    {
        var daily = NewDatabase();
        var (last, clock) = await RunAWeek(daily, _ => new SimulatedMatchSource(World));

        var recalculated = NewDatabase();
        await Runner(recalculated, new SimulatedMatchSource(World), clock).RunAsync(FullFromArchive);

        // The last run starts on 2026-03-09, rates that day so far and settles yesterday − 2.
        var runDay = new DateOnly(2026, 3, 9);
        Assert.Equal(runDay, last.RatedThrough);
        Assert.Equal(runDay.AddDays(-3), last.SettledThrough);
        Assert.True(last.RatedMaps > 0, "the week has league maps");
        AssertSameRatings(daily, recalculated);
    }

    [Fact]
    public async Task LateResults_AreRatedOnTheDayTheyWerePlayed()
    {
        // Every result reaches the source only two days after its day, so each run sees the
        // day before yesterday for the first time, inside the lookback window.
        var daily = NewDatabase();
        var (last, clock) = await RunAWeek(daily, clock => new DelayedSource(World, clock));

        var complete = NewDatabase();
        await Runner(complete, new SimulatedMatchSource(World), clock).RunAsync(FullFromSimulation);

        var settled = last.SettledThrough!.Value;
        Assert.Equal(Rows(complete, $"SELECT * FROM team_snapshots WHERE date <= '{settled:yyyy-MM-dd}' ORDER BY date, team_id"),
            Rows(daily, $"SELECT * FROM team_snapshots WHERE date <= '{settled:yyyy-MM-dd}' ORDER BY date, team_id"));
        Assert.Equal(State(complete, RatingsStore.SettledStateKey), State(daily, RatingsStore.SettledStateKey));
        var weekMaps = World.History.Maps.Count(map => RatingsStore.DayOf(map.PlayedAt) is var day
            && day >= new DateOnly(2026, 3, 1) && day <= settled);
        Assert.True(weekMaps > 0);
        Assert.Equal(weekMaps, Convert.ToInt32(daily.Scalar(
            $"SELECT COUNT(*) FROM rated_maps WHERE played_at >= {Start(new DateOnly(2026, 3, 1))} AND played_at < {Start(settled.AddDays(1))}")));
    }

    [Fact]
    public async Task ARun_RatesItsOwnDayUpToItsStartAndTheNextRunRatesTheWholeDay()
    {
        // The first day after the full start with series at two or more times, so a run can start between them.
        var runDay = World.History.Maps
            .Where(map => map.PlayedAt > FirstRunAt)
            .GroupBy(map => RatingsStore.DayOf(map.PlayedAt))
            .OrderBy(day => day.Key)
            .First(day => day.Select(map => map.PlayedAt).Distinct().Count() >= 2);
        var startedAt = runDay.Min(map => map.PlayedAt).AddMinutes(1);
        var playedBeforeTheRun = runDay.Count(map => map.PlayedAt < startedAt);
        var dayStart = Start(runDay.Key);
        var dayEnd = Start(runDay.Key.AddDays(1));

        var db = NewDatabase();
        var clock = new FakeTimeProvider(FirstRunAt);
        await Runner(db, new SimulatedMatchSource(World), clock).RunAsync(FullFromSimulation);
        clock.SetUtcNow(startedAt);
        var duringTheDay = await Runner(db, new SimulatedMatchSource(World), clock).RunAsync(Daily);

        Assert.Equal(runDay.Key, duringTheDay.RatedThrough);
        Assert.Equal(playedBeforeTheRun, Convert.ToInt32(db.Scalar(
            $"SELECT COUNT(*) FROM rated_maps WHERE played_at >= {dayStart} AND played_at < {dayEnd}")));
        Assert.Equal(runDay.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            db.GetState(RatingsStore.RatedThroughKey));

        clock.SetUtcNow(new DateTimeOffset(runDay.Key.AddDays(1).ToDateTime(new TimeOnly(4, 17)), TimeSpan.Zero));
        await Runner(db, new SimulatedMatchSource(World), clock).RunAsync(Daily);

        Assert.Equal(runDay.Count(), Convert.ToInt32(db.Scalar(
            $"SELECT COUNT(*) FROM rated_maps WHERE played_at >= {dayStart} AND played_at < {dayEnd}")));
    }

    [Fact]
    public async Task ATeamMarkedDisbandedAndSetBackToActiveTheSameDay_StaysRanked()
    {
        var baseline = NewDatabase();
        await RunAWeek(baseline, _ => new SimulatedMatchSource(World));
        var lastDay = "2026-03-08";
        var rankedTeam = int.Parse(Rows(baseline, $"""
            SELECT team_id FROM team_snapshots
            WHERE date = '{lastDay}' AND rank IS NOT NULL
              AND team_id NOT IN (SELECT team_id FROM overrides WHERE team_id IS NOT NULL)
            ORDER BY rank LIMIT 1
            """).Single(), CultureInfo.InvariantCulture);

        // The admin marks every team disbanded, then sets the ones still playing back to active.
        var db = NewDatabase();
        var changeDay = "2026-03-05";
        db.Execute($$"""
            INSERT INTO overrides (kind, team_id, effective_on, payload) VALUES
                ('status', {{rankedTeam}}, '{{changeDay}}', '{"status":"disbanded"}'),
                ('status', {{rankedTeam}}, '{{changeDay}}', '{"status":"active"}');
            """);
        await RunAWeek(db, _ => new SimulatedMatchSource(World));

        Assert.DoesNotContain(rankedTeam, new RatingsStore(db).Freezes()[DateOnly.Parse(changeDay, CultureInfo.InvariantCulture)]);
        Assert.Equal(Rows(baseline, $"SELECT date, rank, rating FROM team_snapshots WHERE team_id = {rankedTeam} ORDER BY date"),
            Rows(db, $"SELECT date, rank, rating FROM team_snapshots WHERE team_id = {rankedTeam} ORDER BY date"));
    }

    [Fact]
    public void Freezes_FollowTheLastStatusEnteredForTheDay()
    {
        var db = NewDatabase();
        var untouchedTeams = Rows(db, "SELECT team_id FROM teams WHERE team_id NOT IN (SELECT team_id FROM overrides WHERE team_id IS NOT NULL) ORDER BY team_id LIMIT 2")
            .Select(teamId => int.Parse(teamId, CultureInfo.InvariantCulture)).ToList();
        var (setBackToActive, disbandedAfterAll) = (untouchedTeams[0], untouchedTeams[1]);
        db.Execute($$"""
            INSERT INTO overrides (kind, team_id, effective_on, payload) VALUES
                ('status', {{setBackToActive}}, '2026-03-05', '{"status":"disbanded"}'),
                ('status', {{disbandedAfterAll}}, '2026-03-05', '{"status":"active"}'),
                ('status', {{setBackToActive}}, '2026-03-05', '{"status":"active"}'),
                ('status', {{disbandedAfterAll}}, '2026-03-05', '{"status":"disbanded"}');
            """);

        var frozenThatDay = new RatingsStore(db).Freezes()[new DateOnly(2026, 3, 5)].ToList();

        Assert.DoesNotContain(setBackToActive, frozenThatDay);
        Assert.Contains(disbandedAfterAll, frozenThatDay);
    }

    // What a run leaves behind

    [Fact]
    public async Task TeamsRemovedSinceSettled_FindsATeamTheSavedStateStillRates()
    {
        var db = NewDatabase();
        var clock = new FakeTimeProvider(FirstRunAt);
        await Runner(db, new SimulatedMatchSource(World), clock).RunAsync(FullFromSimulation);
        var store = new RatingsStore(db);
        Assert.Empty(store.TeamsRemovedSinceSettled());

        // What a merge in the admin dashboard leaves: the team is gone, the saved state still has it.
        var mergedAway = store.LoadSettledState().Teams.Keys.Max();
        db.Execute($"PRAGMA foreign_keys = OFF; DELETE FROM teams WHERE team_id = {mergedAway}; PRAGMA foreign_keys = ON;");

        Assert.Equal([mergedAway], store.TeamsRemovedSinceSettled());
    }

    [Fact]
    public async Task SettledDays_LeaveTheMapsTableButKeepTheirRatedMaps()
    {
        var db = NewDatabase();
        var (last, _) = await RunAWeek(db, _ => new SimulatedMatchSource(World));

        var settledEnd = Start(last.SettledThrough!.Value.AddDays(1));
        Assert.Equal(0L, db.Scalar($"SELECT COUNT(*) FROM maps WHERE played_at < {settledEnd}"));
        Assert.Equal(World.History.Maps.Count(map => map.PlayedAt.ToUnixTimeSeconds() < settledEnd),
            Convert.ToInt32(db.Scalar($"SELECT COUNT(*) FROM rated_maps WHERE played_at < {settledEnd}")));
        // A snapshot for every day from the first map on, the unsettled ones included.
        var firstDay = RatingsStore.DayOf(World.History.Maps.Min(map => map.PlayedAt));
        Assert.Equal(last.RatedThrough.DayNumber - firstDay.DayNumber + 1,
            Convert.ToInt32(db.Scalar("SELECT COUNT(DISTINCT date) FROM region_snapshots")));
    }

    [Fact]
    public async Task ExportSite_WritesTheSiteFromTheDatabase()
    {
        var db = NewDatabase();
        var clock = new FakeTimeProvider(FirstRunAt);
        var runner = Runner(db, new SimulatedMatchSource(World), clock);
        await runner.RunAsync(FullFromSimulation);

        Assert.True(runner.ExportSite(_siteRoot, Synthetic));

        using var rankings = JsonDocument.Parse(File.ReadAllText(Path.Combine(_siteRoot, "rankings.json")));
        var teams = rankings.RootElement.GetProperty("teams");
        Assert.True(teams.GetArrayLength() > 50);
        var leader = teams[0].GetProperty("team");
        Assert.Equal(SampleSiteName(leader.GetProperty("id").GetInt32()), leader.GetProperty("name").GetString());
        using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(_siteRoot, "meta.json")));
        Assert.Equal(Synthetic, meta.RootElement.GetProperty("dataSource").GetString());
        // The run day itself, rated up to the run's start.
        Assert.Equal("2026-03-02", meta.RootElement.GetProperty("snapshotDate").GetString());
        using var teamView = JsonDocument.Parse(File.ReadAllText(Path.Combine(_siteRoot, "teams", $"{leader.GetProperty("id").GetInt32()}.json")));
        Assert.NotEmpty(teamView.RootElement.GetProperty("recent").EnumerateArray());
        Assert.False(string.IsNullOrEmpty(teamView.RootElement.GetProperty("recent")[0].GetProperty("tournament").GetString()));
    }

    // Refusals and repairs

    [Fact]
    public async Task ADatabaseFromOneDataSource_RefusesTheOther()
    {
        var db = NewDatabase();
        var clock = new FakeTimeProvider(FirstRunAt);
        await Runner(db, new SimulatedMatchSource(World), clock).RunAsync(FullFromSimulation);

        var error = await Assert.ThrowsAsync<DailyRunException>(() =>
            Runner(db, new SimulatedMatchSource(World), clock).RunAsync(new DailyRunOptions { DataSource = "liquipedia" }));
        Assert.Contains("synthetic", error.Message);
    }

    [Fact]
    public async Task ATeamAssignedAfterItsDaysSettled_IsReadBackAndRatedByAFullRecalculation()
    {
        var db = NewDatabase();
        var latecomer = World.Series.First(series => series.PlayedAt >= FirstRunAt.AddDays(-30)).TeamA;
        db.Execute($"UPDATE provider_teams SET team_id = NULL WHERE provider_key = '{latecomer.Key}'");
        var clock = new FakeTimeProvider(FirstRunAt);
        await Runner(db, new SimulatedMatchSource(World), clock).RunAsync(FullFromSimulation);
        Assert.Equal(0L, db.Scalar($"SELECT COUNT(*) FROM team_ratings WHERE team_id = {latecomer.TeamId}"));

        new ProviderIds(db).LinkTeam(latecomer.Key, latecomer.TeamId);
        clock.Advance(TimeSpan.FromDays(1));
        var next = await Runner(db, new SimulatedMatchSource(World), clock).RunAsync(Daily);

        Assert.True(next.ReparsedDays > 0);
        Assert.True(next.LateMaps > 0, "the team's old maps are stored but not rated");

        var full = await Runner(db, new SimulatedMatchSource(World), clock).RunAsync(FullFromArchive);
        Assert.Equal(0, full.LateMaps);
        Assert.Equal(1L, db.Scalar($"SELECT COUNT(*) FROM team_ratings WHERE team_id = {latecomer.TeamId}"));
    }

    // The checkpoint

    [Fact]
    public void RatingStateJson_ReadsBackBitForBit()
    {
        var state = Replay.Run(World.History, new RatingSettings());
        state.SetLeagueEntryLevel("CN", -25.5);

        var json = RatingStateJson.Write(state);
        var readBack = RatingStateJson.Read(json);

        Assert.Equal(json, RatingStateJson.Write(readBack));
        Assert.Equal(state.Teams.Keys, readBack.Teams.Keys);
        Assert.All(state.Teams.Keys, teamId => Assert.Equal(state.Rating(teamId), readBack.Rating(teamId)));
    }

    // Helpers

    /// <summary>Every rating the runs store, compared exactly.</summary>
    static void AssertSameRatings(RankingsDb expected, RankingsDb actual)
    {
        foreach (var sql in (string[])[
            "SELECT * FROM team_snapshots ORDER BY date, team_id",
            "SELECT * FROM region_snapshots ORDER BY date, region_id",
            "SELECT * FROM rated_maps ORDER BY map_id",
            "SELECT date, at, team_id, kind, new_players, other_team_id FROM roster_events ORDER BY date, at, team_id, kind, other_team_id",
            "SELECT * FROM team_ratings ORDER BY team_id",
            "SELECT region_id, rating FROM regions ORDER BY region_id",
            "SELECT map_id FROM maps ORDER BY map_id",
        ])
            Assert.Equal(Rows(expected, sql), Rows(actual, sql));
        foreach (var key in (string[])[RatingsStore.SettledThroughKey, RatingsStore.SettledStateKey, RatingsStore.RatedThroughKey])
            Assert.Equal(State(expected, key), State(actual, key));
    }

    static string? State(RankingsDb db, string key) => db.GetState(key);

    static List<string> Rows(RankingsDb db, string sql)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount)
                .Select(column => Convert.ToString(reader.GetValue(column), CultureInfo.InvariantCulture))));
        return rows;
    }

    static long Start(DateOnly day) => RatingsStore.StartOf(day).ToUnixTimeSeconds();

    static string SampleSiteName(int teamId) => Rankings.Job.Site.SampleSite.TeamName(teamId);

    /// <summary>The synthetic world as a source that only shows a day's results two days later.</summary>
    sealed class DelayedSource(SimulatedWorld world, TimeProvider clock) : IMatchSource
    {
        public Task<SourceBatch> FetchAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
        {
            var lastVisibleDay = RatingsStore.DayOf(clock.GetUtcNow()).AddDays(-2);
            var visible = world with { Series = [.. world.Series.Where(series => RatingsStore.DayOf(series.PlayedAt) <= lastVisibleDay)] };
            return new SimulatedMatchSource(visible).FetchAsync(from, to, cancellationToken);
        }
    }
}
