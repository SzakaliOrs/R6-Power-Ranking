using Rankings.Core;
using Rankings.Job;
using Rankings.Sim;

namespace Rankings.Tests;

public class BacktestTests
{
    static readonly DateTimeOffset SeasonOne = new(2023, 1, 15, 0, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset SeasonTwo = new(2024, 1, 15, 0, 0, 0, TimeSpan.Zero);

    static MapRecord Map(string id, DateTimeOffset playedAt, int teamA, int teamB, int roundsA, int roundsB) =>
        new(id, id, playedAt, 1, 1, teamA, teamB, roundsA, roundsB, [], []);

    /// <summary>Teams 1–3 in EU and 4 in NA over two seasons; team 5 debuts in the second.</summary>
    static History SmallHistory() => new(
        new Dictionary<int, string> { [1] = "EU", [2] = "EU", [3] = "EU", [4] = "NA", [5] = "NA" },
        [
            Map("a", SeasonOne.AddDays(20), 1, 2, 7, 3),
            Map("b", SeasonOne.AddDays(21), 1, 3, 7, 5),
            Map("c", SeasonOne.AddDays(22), 2, 3, 7, 6),
            Map("d", SeasonOne.AddDays(30), 1, 4, 7, 2),
            Map("e", SeasonTwo.AddDays(10), 5, 4, 7, 4),
            Map("f", SeasonTwo.AddDays(11), 2, 4, 5, 7),
        ],
        [new TeamFreeze(SeasonTwo.AddDays(5), 3)],
        [SeasonOne, SeasonTwo]);

    // Replay

    [Fact]
    public void Replay_StartsFirstSeasonTeamsEvenAndSeedsLaterOnes()
    {
        var state = Replay.Run(SmallHistory(), new RatingSettings());

        Assert.Equal(SeasonTwo, state.WarmUpUntil);
        Assert.Equal(SeasonTwo, state.SeasonStartedAt);
        Assert.True(state.Team(3).IsFrozen);
        Assert.Equal(1, state.Team(5).RatedMaps);
        Assert.Equal([1, 2, 4, 5], Replay.Ranking(state).Order());
    }

    [Fact]
    public void Replay_GivesThePredictionBeforeEachMap()
    {
        var predictions = new List<(string MapId, double Chance)>();

        Replay.Run(SmallHistory(), new RatingSettings(), (map, chance) => predictions.Add((map.MapId, chance)));

        Assert.Equal(["a", "b", "c", "d", "e", "f"], predictions.Select(prediction => prediction.MapId));
        Assert.Equal(0.5, predictions[0].Chance, 12);
        // Team 1 won its first map, so it is favoured in its second.
        Assert.True(predictions[1].Chance > 0.5);
    }

    // Scoring

    [Fact]
    public void MapLogLoss_IsLnTwoForACoinFlipAndPunishesConfidentMistakes()
    {
        Assert.Equal(Backtest.CoinFlip, Backtest.MapLogLoss(0.5, teamAWon: true), 12);
        Assert.Equal(-Math.Log(0.9), Backtest.MapLogLoss(0.9, teamAWon: true), 12);
        Assert.Equal(-Math.Log(0.1), Backtest.MapLogLoss(0.9, teamAWon: false), 12);
    }

    [Fact]
    public void LogLoss_IsACoinFlipWhenNothingMovesRatings()
    {
        var frozen = new RatingSettings { KRegional = 0, KInternational = 0, KLeague = 0, KSeries = 0, NewcomerGap = 0 };
        var everything = new ScoringWindow(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);

        Assert.Equal(Backtest.CoinFlip, Backtest.LogLoss(SmallHistory(), frozen, everything), 12);
    }

    [Theory]
    [InlineData(new double[] { 1, 2, 3, 4 }, new double[] { 10, 20, 30, 40 }, 1)]
    [InlineData(new double[] { 1, 2, 3, 4 }, new double[] { 40, 30, 20, 10 }, -1)]
    [InlineData(new double[] { 1, 1, 2, 3 }, new double[] { 5, 5, 6, 7 }, 1)]
    public void RankCorrelation_FollowsTheOrderNotTheValues(double[] first, double[] second, double expected)
    {
        var firstValues = first.Select((value, index) => (index, value)).ToDictionary(pair => pair.index, pair => pair.value);
        var secondValues = second.Select((value, index) => (index, value)).ToDictionary(pair => pair.index, pair => pair.value);

        Assert.Equal(expected, Backtest.RankCorrelation(firstValues, secondValues), 9);
    }

    [Fact]
    public void LeagueSeededOffsets_RunFromPlusToMinusTheSpreadByFirstSeasonStandings()
    {
        var offsets = Backtest.LeagueSeededOffsets(SmallHistory());

        // EU standings by map win share: team 1 (2–0), team 2 (1–1), team 3 (0–2). Team 4 played no
        // regional map and team 5 nothing in the first season, so the replay starts them at 0.
        Assert.Equal(100, offsets[1]);
        Assert.Equal(0, offsets[2]);
        Assert.Equal(-100, offsets[3]);
        Assert.False(offsets.ContainsKey(4));
        Assert.False(offsets.ContainsKey(5));
    }

    // On synthetic seasons

    [Fact]
    public void Synthetic_EngineRankingFollowsTheHiddenStrengths()
    {
        var world = SimulatorTests.World.Value;

        var correlation = SyntheticBacktest.RankCorrelation(world, world.History, new RatingSettings());

        Assert.True(correlation > 0.7, $"Spearman {correlation:0.000}");
    }

    [Fact]
    public void Synthetic_PredictionsBeatACoinFlipButNotTheHiddenStrengths()
    {
        var world = SimulatorTests.World.Value;
        var lastSeason = new ScoringWindow(world.History.SeasonStarts[^1], DateTimeOffset.MaxValue);
        var bestPossible = world.History.Maps
            .Where(map => lastSeason.Contains(map.PlayedAt))
            .Average(map => Backtest.MapLogLoss(world.TrueChances[map.MapId], map.RoundsA > map.RoundsB));

        var engine = Backtest.LogLoss(world.History, new RatingSettings(), lastSeason);

        Assert.InRange(engine, bestPossible, Backtest.CoinFlip);
    }

    [Fact]
    public void Synthetic_TuningNeverEndsWorseOnTheSeasonsItTunesOn()
    {
        var world = SimulatorTests.World.Value;
        var starts = world.History.SeasonStarts;
        SettingAxis[] axes =
        [
            new("K_reg", [8, 24], settings => settings.KRegional, (settings, value) => settings with { KRegional = value }),
        ];

        var tuning = Backtest.Tune(world.History, new RatingSettings(), axes,
            new ScoringWindow(starts[1], starts[^1]), new ScoringWindow(starts[^1], DateTimeOffset.MaxValue), passes: 1);

        Assert.True(tuning.BestTuneScore <= tuning.StartTuneScore);
        Assert.Equal(3, tuning.Tried);
        Assert.False(double.IsNaN(tuning.BestCheckScore));
    }

    [Fact]
    public void Synthetic_StartingSetupBarelyChangesTheTopTwenty()
    {
        var sensitivity = Backtest.Sensitivity(SimulatorTests.World.Value.History, new RatingSettings());

        Assert.Equal(20, sensitivity.EqualStartTop.Count);
        Assert.True(sensitivity.Overlap >= 15, $"{sensitivity.Overlap} of 20");
    }

    [Fact]
    public void Report_RunsEndToEnd()
    {
        SettingAxis[] axes =
        [
            new("K_reg", [24], settings => settings.KRegional, (settings, value) => settings with { KRegional = value }),
        ];

        var report = SyntheticBacktest.Report(new SimulationOptions { Seasons = 3 }, axes);

        Assert.Contains("Synthetic seasons 2023–2025 (seed 1)", report);
        Assert.Contains("hidden strengths", report);
        Assert.Contains("Sensitivity:", report);
    }
}
