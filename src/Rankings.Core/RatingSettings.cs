namespace Rankings.Core;

/// <summary>
/// Every tunable value of the rating engine, so the backtest can change them.
/// Defaults are the plan's starting values.
/// </summary>
public sealed record RatingSettings
{
    /// <summary>K for team offsets on a map between two teams of the same league (K_reg).</summary>
    public double KRegional { get; init; } = 16;

    /// <summary>K for team offsets on a map between two leagues (K_int).</summary>
    public double KInternational { get; init; } = 32;

    /// <summary>K for league ratings on a map between two leagues (K_region in the plan).</summary>
    public double KLeague { get; init; } = 3;

    /// <summary>Weight of ln(round margin) in the margin multiplier.</summary>
    public double MarginWeight { get; init; } = 0.5;

    /// <summary>Damps the margin multiplier when the favourite wins, raises it on an upset.</summary>
    public double FavouriteCorrection { get; init; } = 2.2;

    /// <summary>Players a lineup must keep from the team's last one to stay the same roster core.</summary>
    public int RosterCoreSize { get; init; } = 3;

    /// <summary>Share of the gap to the league average that each new player pulls a team's offset.</summary>
    public double NewPlayerPull { get; init; } = 0.15;

    /// <summary>K multiplier for a team's maps after a roster change.</summary>
    public double RosterChangeBoost { get; init; } = 1.5;

    /// <summary>How many maps the roster-change boost lasts.</summary>
    public int RosterChangeBoostMaps { get; init; } = 5;

    /// <summary>Share of its players' previous average offset that a new team starts with.</summary>
    public double SeedingShare { get; init; } = 0.5;

    /// <summary>Rating below the league rating counted for a player with no rated history.</summary>
    public double NewcomerGap { get; init; } = 50;

    /// <summary>Share of every offset pulled toward zero at the start of a season.</summary>
    public double SeasonStartPull { get; init; } = 0.2;

    /// <summary>K for the one extra update a BO3 or BO5 winner gets.</summary>
    public double KSeries { get; init; } = 8;

    /// <summary>A team is provisional until it has this many rated maps.</summary>
    public int ProvisionalUntilMaps { get; init; } = 10;

    /// <summary>Rating every league starts at.</summary>
    public double StartingLeagueRating { get; init; } = 1500;

    /// <summary>Multiplier on every K during the first replayed season.</summary>
    public double WarmUpKMultiplier { get; init; } = 2;

    /// <summary>Days without a rated map before inactivity decay starts.</summary>
    public int InactivityThresholdDays { get; init; } = 90;

    /// <summary>Share of the gap to the newcomer level closed per decay period past the threshold.</summary>
    public double InactivityDecay { get; init; } = 0.1;

    /// <summary>Length of one inactivity decay period in days.</summary>
    public int InactivityDecayPeriodDays { get; init; } = 30;
}
