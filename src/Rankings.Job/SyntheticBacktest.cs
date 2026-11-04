using System.Globalization;
using System.Text;
using Rankings.Core;
using Rankings.Sim;

namespace Rankings.Job;

/// <summary>
/// The backtest on synthetic seasons: the first season is the warm-up, the middle seasons tune
/// the settings and the last season checks them. Runs locally or as a manual Actions run; it
/// reads and writes nothing, so it costs no storage and never calls Liquipedia.
/// </summary>
public static class SyntheticBacktest
{
    public static string Report(SimulationOptions options, IReadOnlyList<SettingAxis>? axes = null)
    {
        if (options.Seasons < 3)
            throw new ArgumentOutOfRangeException(nameof(options), options.Seasons, "The backtest needs at least 3 seasons: warm-up, tuning and check.");

        var world = Simulator.Run(options);
        var history = world.History;
        var seasonStarts = history.SeasonStarts.Order().ToList();
        var end = history.Maps.Max(map => map.PlayedAt).AddDays(1);
        var tuneWindow = new ScoringWindow(seasonStarts[1], seasonStarts[^1]);
        var checkWindow = new ScoringWindow(seasonStarts[^1], end);
        var startSettings = new RatingSettings();

        var tuning = Backtest.Tune(history, startSettings, axes ?? Backtest.DefaultAxes, tuneWindow, checkWindow);
        var bestPossible = history.Maps
            .Where(map => checkWindow.Contains(map.PlayedAt))
            .Average(map => Backtest.MapLogLoss(world.TrueChances[map.MapId], map.RoundsA > map.RoundsB));
        var startRanking = RankCorrelation(world, history, startSettings);
        var tunedRanking = RankCorrelation(world, history, tuning.Best);
        var sensitivity = Backtest.Sensitivity(history, tuning.Best);

        var firstYear = options.FirstYear;
        var lastYear = firstYear + options.Seasons - 1;
        var report = new StringBuilder();
        Line(report, $"Synthetic seasons {firstYear}–{lastYear} (seed {options.Seed}): {history.TeamLeagues.Count} teams, "
            + $"{history.Maps.Count} maps, {history.Freezes.Count} relegations or disbandings.");
        Line(report, $"Warm-up {firstYear}, tuned on {firstYear + 1}–{lastYear - 1}, checked on {lastYear}.");
        Line(report, "");
        Line(report, $"Log loss on {lastYear}, lower is better:");
        Line(report, $"  coin flip                 {Backtest.CoinFlip:0.0000}");
        Line(report, $"  starting settings         {tuning.StartCheckScore:0.0000}");
        Line(report, $"  tuned settings            {tuning.BestCheckScore:0.0000}");
        Line(report, $"  hidden strengths          {bestPossible:0.0000}   (the best any rating could do)");
        Line(report, "");
        Line(report, $"Tuning ({tuning.Tried} replays): {tuning.StartTuneScore:0.0000} → {tuning.BestTuneScore:0.0000} on {firstYear + 1}–{lastYear - 1}.");
        foreach (var axis in axes ?? Backtest.DefaultAxes)
        {
            var (before, after) = (axis.Read(tuning.Start), axis.Read(tuning.Best));
            if (before != after)
                Line(report, $"  {axis.Name}: {before:0.###} → {after:0.###}");
        }
        Line(report, "");
        Line(report, $"Ranking against the hidden strengths at the end (Spearman, 1 = same order): "
            + $"starting {startRanking:0.000}, tuned {tunedRanking:0.000}.");
        Line(report, $"Sensitivity: {sensitivity.Overlap} of the top {sensitivity.EqualStartTop.Count} are the same "
            + "whether the replay starts all equal or from league standings.");
        return report.ToString();
    }

    /// <summary>How closely the replay's final ranking follows the hidden strengths of the teams still active.</summary>
    public static double RankCorrelation(SimulatedWorld world, History history, RatingSettings settings)
    {
        var state = Replay.Run(history, settings);
        var ratings = Replay.Ranking(state).ToDictionary(teamId => teamId, state.Rating);
        return Backtest.RankCorrelation(ratings, world.TrueStrengths);
    }

    static void Line(StringBuilder report, FormattableString text) =>
        report.AppendLine(text.ToString(CultureInfo.InvariantCulture));

    static void Line(StringBuilder report, string text) => report.AppendLine(text);
}
