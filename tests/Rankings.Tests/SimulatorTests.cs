using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Rankings.Core;
using Rankings.Job;
using Rankings.Job.Archive;
using Rankings.Job.Liquipedia;
using Rankings.Job.Storage;
using Rankings.Sim;

namespace Rankings.Tests;

public sealed class SimulatorTests : IDisposable
{
    internal static readonly Lazy<SimulatedWorld> World = new(() => Simulator.Run());

    readonly string _archiveRoot = Path.Combine(Path.GetTempPath(), $"archive-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_archiveRoot))
            Directory.Delete(_archiveRoot, recursive: true);
    }

    // Map scores

    [Fact]
    public void MapScores_EvenRoundsGiveAnEvenMap()
    {
        Assert.Equal(0.5, MapScores.MapWinChance(0.5), 12);
    }

    [Theory]
    [InlineData(0.3)]
    [InlineData(0.64)]
    [InlineData(0.9)]
    public void MapScores_RoundChanceReproducesTheMapChance(double mapWinChance)
    {
        Assert.Equal(mapWinChance, MapScores.MapWinChance(MapScores.RoundWinChanceFor(mapWinChance)), 9);
    }

    [Fact]
    public void MapScores_PlayedMapsAreWonAsOftenAsTheChanceSays()
    {
        var random = new Random(5);
        const int maps = 20000;

        var wins = Enumerable.Range(0, maps).Count(_ => MapScores.Play(0.64, random) is var (roundsA, roundsB) && roundsA > roundsB);

        Assert.Equal(0.64, (double)wins / maps, 2);
    }

    [Theory]
    [InlineData(7, 5, true)]
    [InlineData(7, 6, false)]
    [InlineData(8, 6, true)]
    [InlineData(8, 7, true)]
    [InlineData(6, 6, false)]
    public void MapScores_FirstToSevenOrToEightFromSixAll(int roundsA, int roundsB, bool finished)
    {
        Assert.Equal(finished, MapScores.IsFinished(roundsA, roundsB));
    }

    // Seasons

    [Fact]
    public void Run_SameSeedGivesTheSameWorld()
    {
        var again = Simulator.Run();
        var other = Simulator.Run(new SimulationOptions { Seed = 2 });

        Assert.Equal(Scores(World.Value), Scores(again));
        Assert.NotEqual(Scores(World.Value), Scores(other));
    }

    [Fact]
    public void Run_EveryMapEndsAsSiegeMapsDo()
    {
        Assert.All(World.Value.History.Maps, map =>
        {
            Assert.True(MapScores.IsFinished(map.RoundsA, map.RoundsB), $"{map.RoundsA}–{map.RoundsB}");
            Assert.NotEqual(map.RoundsA, map.RoundsB);
        });
        Assert.Contains(World.Value.History.Maps, map => Math.Max(map.RoundsA, map.RoundsB) == 8);
    }

    [Fact]
    public void Run_EverySeriesStopsWhenOneTeamHasWonIt()
    {
        foreach (var series in World.Value.Series)
        {
            var mapsToWin = series.BestOf / 2 + 1;
            var winsA = series.Maps.Count(map => map.RoundsA > map.RoundsB);
            var winsB = series.Maps.Count - winsA;
            Assert.Equal(mapsToWin, Math.Max(winsA, winsB));
            Assert.InRange(series.Maps.Count, mapsToWin, series.BestOf);
        }
        Assert.Equal([1, 3, 5], World.Value.Series.Select(series => series.BestOf).Distinct().Order());
    }

    [Fact]
    public void Run_SevenLeaguesOfTenTeamsWithRegionalAndInternationalMaps()
    {
        var history = World.Value.History;
        var firstSeasonTeams = history.Maps
            .Where(map => map.PlayedAt < history.SeasonStarts[1])
            .SelectMany(map => new[] { map.TeamA, map.TeamB })
            .Distinct()
            .ToList();

        Assert.Equal(["APAC-N", "ASIA", "CN", "EU", "NA", "OCE", "SA"], history.TeamLeagues.Values.Distinct().Order());
        Assert.Equal(70, firstSeasonTeams.Count);
        Assert.Equal(70, World.Value.TrueStrengths.Count);
        Assert.Contains(history.Maps, map => history.TeamLeagues[map.TeamA] != history.TeamLeagues[map.TeamB]);
        Assert.Equal(4, history.SeasonStarts.Count);
    }

    [Fact]
    public void Run_TeamsChangeBetweenSeasons()
    {
        var history = World.Value.History;

        Assert.NotEmpty(history.Freezes);
        Assert.True(history.TeamLeagues.Count > 70, "new teams are promoted or replace disbanded ones");
        // Some team lists a different lineup in a later season.
        var lineups = history.Maps
            .GroupBy(map => map.TeamA)
            .Select(maps => maps.Select(map => string.Join(",", map.PlayersA)).Distinct().Count());
        Assert.Contains(lineups, distinct => distinct > 1);
    }

    // The provider path

    [Fact]
    public async Task MatchSource_ResponsesParseToTheSameMapsAsLiquipediasWould()
    {
        var source = new SimulatedMatchSource(World.Value, pageSize: 10);
        var week = World.Value.History.SeasonStarts[1].AddDays(17);

        var batch = await source.FetchAsync(week, week.AddDays(7));
        var parsed = new List<SourceMap>();
        var skipped = new List<SkippedSourceMap>();
        foreach (var response in batch.Responses)
            LpdbArchive.Parse(response.Body, parsed, skipped);

        Assert.NotEmpty(batch.Maps);
        Assert.True(batch.Responses.Count > 1, "pages of 10 series");
        Assert.Empty(skipped);
        Assert.Equal(JsonSerializer.Serialize(batch.Maps), JsonSerializer.Serialize(parsed));
    }

    [Fact]
    public async Task MatchSource_RunsThroughTheArchiveAndBackAsTheDailyJobWould()
    {
        using var db = RankingsDb.Open(":memory:");
        var world = World.Value;
        foreach (var (teamId, league) in world.History.TeamLeagues)
            db.Execute($"INSERT INTO teams (team_id, region_id, created_on) VALUES ({teamId}, '{league}', '2023-01-01')");
        var store = new LocalArchiveStore(_archiveRoot);
        var import = new MatchImport(db, [store], new FakeTimeProvider(DateTimeOffset.Parse("2026-11-04T04:17:00Z")));
        var from = DateOnly.FromDateTime(world.History.SeasonStarts[1].UtcDateTime).AddDays(17);
        var to = from.AddDays(6);

        await import.FetchAsync(new SimulatedMatchSource(world), from, to);
        var ids = new ProviderIds(db);
        foreach (var team in ids.UnassignedTeams())
            ids.LinkTeam(team.ProviderKey, int.Parse(team.ProviderKey["sim team ".Length..]));
        var reparsed = await import.ReparseAsync(store, from, to);

        var expected = world.History.Maps.Where(map =>
            DateOnly.FromDateTime(map.PlayedAt.UtcDateTime) is var day && day >= from && day <= to).ToList();
        Assert.Equal(expected.Count, reparsed.Maps);
        Assert.Equal(
            expected.Select(map => (map.MapId, map.TeamA, map.TeamB, map.RoundsA, map.RoundsB)).Order(),
            db.LoadMaps().Select(map => (map.MapId, map.TeamA, map.TeamB, map.RoundsA, map.RoundsB)).Order());
    }

    static List<string> Scores(SimulatedWorld world) =>
        [.. world.History.Maps.Select(map => $"{map.MapId}:{map.TeamA}-{map.TeamB}:{map.RoundsA}-{map.RoundsB}")];
}
