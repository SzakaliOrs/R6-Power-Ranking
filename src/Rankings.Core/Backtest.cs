namespace Rankings.Core;

/// <summary>
/// Scores settings by how well a replay predicts each map before rating it (log loss, lower is
/// better), searches for better settings on older seasons and checks them on a later one.
/// Pure computation: it runs the same on synthetic seasons and on real history.
/// </summary>
public static class Backtest
{
    /// <summary>Log loss of a coin flip, the score of a model that knows nothing.</summary>
    public static readonly double CoinFlip = Math.Log(2);

    /// <summary>−ln of the chance the prediction gave the actual winner.</summary>
    public static double MapLogLoss(double chanceTeamAWins, bool teamAWon)
    {
        var chanceForWinner = teamAWon ? chanceTeamAWins : 1 - chanceTeamAWins;
        return -Math.Log(Math.Clamp(chanceForWinner, 1e-12, 1));
    }

    /// <summary>Mean log loss over the maps played in <paramref name="window"/>, from a replay of the whole history.</summary>
    public static double LogLoss(History history, RatingSettings settings, ScoringWindow window,
        IReadOnlyDictionary<int, double>? startingOffsets = null)
    {
        double total = 0;
        var scored = 0;
        Replay.Run(history, settings, (map, chanceTeamAWins) =>
        {
            if (!window.Contains(map.PlayedAt))
                return;
            total += MapLogLoss(chanceTeamAWins, map.RoundsA > map.RoundsB);
            scored++;
        }, startingOffsets);
        return scored == 0 ? double.NaN : total / scored;
    }

    /// <summary>
    /// Tries each value of each setting in turn, keeping any that lowers the log loss on the
    /// tuning window, for up to <paramref name="passes"/> rounds; then scores the start and the
    /// result on the check window, which the search never saw.
    /// </summary>
    public static TuningResult Tune(History history, RatingSettings start, IReadOnlyList<SettingAxis> axes,
        ScoringWindow tuneWindow, ScoringWindow checkWindow, int passes = 2)
    {
        var best = start;
        var bestScore = LogLoss(history, start, tuneWindow);
        var startScore = bestScore;
        var tried = 1;
        for (var pass = 0; pass < passes; pass++)
        {
            var improved = false;
            foreach (var axis in axes)
            {
                foreach (var value in axis.Values)
                {
                    var candidate = axis.Apply(best, value);
                    if (candidate == best)
                        continue;
                    var score = LogLoss(history, candidate, tuneWindow);
                    tried++;
                    if (score < bestScore - 1e-9)
                    {
                        (best, bestScore) = (candidate, score);
                        improved = true;
                    }
                }
            }
            if (!improved)
                break;
        }
        return new TuningResult(start, startScore, LogLoss(history, start, checkWindow),
            best, bestScore, LogLoss(history, best, checkWindow), tried);
    }

    /// <summary>The settings the plan names for tuning, each over a few values around its start.</summary>
    public static IReadOnlyList<SettingAxis> DefaultAxes { get; } =
    [
        new("K_reg", [8, 12, 16, 24, 32],
            settings => settings.KRegional, (settings, value) => settings with { KRegional = value }),
        new("K_int", [16, 24, 32, 48],
            settings => settings.KInternational, (settings, value) => settings with { KInternational = value }),
        new("K_league", [1, 3, 6, 10],
            settings => settings.KLeague, (settings, value) => settings with { KLeague = value }),
        new("Margin weight", [0, 0.25, 0.5, 1],
            settings => settings.MarginWeight, (settings, value) => settings with { MarginWeight = value }),
        new("K_series", [0, 4, 8, 16],
            settings => settings.KSeries, (settings, value) => settings with { KSeries = value }),
        new("New-player pull", [0, 0.1, 0.15, 0.25],
            settings => settings.NewPlayerPull, (settings, value) => settings with { NewPlayerPull = value }),
        new("Seeding share", [0.25, 0.5, 0.75],
            settings => settings.SeedingShare, (settings, value) => settings with { SeedingShare = value }),
        new("Season-start pull", [0, 0.1, 0.2, 0.4],
            settings => settings.SeasonStartPull, (settings, value) => settings with { SeasonStartPull = value }),
    ];

    /// <summary>
    /// Spearman rank correlation between two sets of values over the teams both hold:
    /// 1 when they order the teams alike, 0 when unrelated.
    /// </summary>
    public static double RankCorrelation(IReadOnlyDictionary<int, double> first, IReadOnlyDictionary<int, double> second)
    {
        var shared = first.Keys.Where(second.ContainsKey).ToList();
        if (shared.Count < 2)
            return double.NaN;
        var firstRanks = Ranks(shared, first);
        var secondRanks = Ranks(shared, second);
        var meanRank = (shared.Count + 1) / 2.0;
        double covariance = 0, firstVariance = 0, secondVariance = 0;
        foreach (var teamId in shared)
        {
            var (firstDeviation, secondDeviation) = (firstRanks[teamId] - meanRank, secondRanks[teamId] - meanRank);
            covariance += firstDeviation * secondDeviation;
            firstVariance += firstDeviation * firstDeviation;
            secondVariance += secondDeviation * secondDeviation;
        }
        return covariance / Math.Sqrt(firstVariance * secondVariance);
    }

    /// <summary>
    /// First-season offsets from each league's regional standings, by map win share: from
    /// +<paramref name="spread"/> for first place down to −<paramref name="spread"/> for last.
    /// </summary>
    public static Dictionary<int, double> LeagueSeededOffsets(History history, double spread = 100)
    {
        var seasonStarts = history.SeasonStarts.Order().ToList();
        var firstMapAt = history.Maps.Min(map => map.PlayedAt);
        var firstSeasonEnd = seasonStarts.Where(start => start > firstMapAt).Cast<DateTimeOffset?>().FirstOrDefault()
            ?? DateTimeOffset.MaxValue;

        var record = new Dictionary<int, (int Won, int Played)>();
        foreach (var map in history.Maps.Where(map => map.PlayedAt < firstSeasonEnd))
        {
            if (history.TeamLeagues[map.TeamA] != history.TeamLeagues[map.TeamB] || map.RoundsA == map.RoundsB)
                continue;
            foreach (var (teamId, won) in (ReadOnlySpan<(int, bool)>)[(map.TeamA, map.RoundsA > map.RoundsB), (map.TeamB, map.RoundsB > map.RoundsA)])
            {
                var (wonSoFar, playedSoFar) = record.GetValueOrDefault(teamId);
                record[teamId] = (wonSoFar + (won ? 1 : 0), playedSoFar + 1);
            }
        }

        var offsets = new Dictionary<int, double>();
        foreach (var league in record.Keys.GroupBy(teamId => history.TeamLeagues[teamId]))
        {
            var standings = league
                .OrderByDescending(teamId => (double)record[teamId].Won / record[teamId].Played)
                .ThenBy(teamId => teamId)
                .ToList();
            for (var place = 0; place < standings.Count; place++)
                offsets[standings[place]] = standings.Count == 1 ? 0 : spread - 2 * spread * place / (standings.Count - 1);
        }
        return offsets;
    }

    /// <summary>
    /// Replays from all-equal offsets and from league-seeded ones and compares the two top lists:
    /// if they barely differ, the starting values do not matter.
    /// </summary>
    public static SensitivityResult Sensitivity(History history, RatingSettings settings, int top = 20)
    {
        var equalStart = Replay.Ranking(Replay.Run(history, settings)).Take(top).ToList();
        var seededStart = Replay.Ranking(Replay.Run(history, settings, startingOffsets: LeagueSeededOffsets(history)))
            .Take(top).ToList();
        return new SensitivityResult(equalStart, seededStart, equalStart.Intersect(seededStart).Count());
    }

    static Dictionary<int, double> Ranks(List<int> teamIds, IReadOnlyDictionary<int, double> values)
    {
        // Ties share the average of the ranks they span.
        var ordered = teamIds.OrderBy(teamId => values[teamId]).ToList();
        var ranks = new Dictionary<int, double>();
        for (var start = 0; start < ordered.Count;)
        {
            var end = start;
            while (end + 1 < ordered.Count && values[ordered[end + 1]] == values[ordered[start]])
                end++;
            for (var position = start; position <= end; position++)
                ranks[ordered[position]] = (start + end) / 2.0 + 1;
            start = end + 1;
        }
        return ranks;
    }
}

/// <summary>Maps played in [<paramref name="From"/>, <paramref name="Until"/>).</summary>
public sealed record ScoringWindow(DateTimeOffset From, DateTimeOffset Until)
{
    public bool Contains(DateTimeOffset playedAt) => playedAt >= From && playedAt < Until;
}

/// <summary>One setting the search may change, with the values it tries.</summary>
public sealed record SettingAxis(
    string Name,
    IReadOnlyList<double> Values,
    Func<RatingSettings, double> Read,
    Func<RatingSettings, double, RatingSettings> Apply);

/// <param name="Tried">Settings replayed during the search, the start included.</param>
public sealed record TuningResult(
    RatingSettings Start, double StartTuneScore, double StartCheckScore,
    RatingSettings Best, double BestTuneScore, double BestCheckScore, int Tried);

/// <param name="Overlap">Teams in both top lists.</param>
public sealed record SensitivityResult(IReadOnlyList<int> EqualStartTop, IReadOnlyList<int> SeededStartTop, int Overlap);
