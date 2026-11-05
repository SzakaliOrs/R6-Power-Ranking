using Rankings.Core;

namespace Rankings.Sim;

/// <summary>Knobs for the synthetic seasons; strengths are on the rating scale (400 points = 10 to 1).</summary>
public sealed record SimulationOptions
{
    public int Seed { get; init; } = 1;

    public int Seasons { get; init; } = 4;

    public int FirstYear { get; init; } = 2023;

    public int TeamsPerLeague { get; init; } = 10;

    /// <summary>
    /// A league that only starts in the latest season, as China did in 2026: its teams appear then
    /// and it gets its international spots then. Null starts every league in the first season.
    /// </summary>
    public string? LateLeague { get; init; } = "CN";

    /// <summary>Spread (standard deviation) of the leagues' average strength.</summary>
    public double LeagueSpread { get; init; } = 60;

    /// <summary>Spread of a team's own share of its strength (coaching, system), which stays with the team.</summary>
    public double OrganisationSpread { get; init; } = 60;

    /// <summary>Spread of player skill; a team's players add their average skill.</summary>
    public double PlayerSpread { get; init; } = 100;

    /// <summary>Average skill of a player new to the top level.</summary>
    public double NewcomerSkill { get; init; } = -60;

    /// <summary>Spread of each player's change in skill between seasons.</summary>
    public double SkillDriftPerSeason { get; init; } = 25;

    /// <summary>Chance that each league's last team is relegated at the end of a season.</summary>
    public double RelegationChance { get; init; } = 0.7;

    /// <summary>Chance that a relegation spot goes back to an earlier relegated team instead of a new one.</summary>
    public double ReturnChance { get; init; } = 0.3;

    public double DisbandChance { get; init; } = 0.04;

    public double OneChangeChance { get; init; } = 0.45;

    public double TwoChangesChance { get; init; } = 0.15;

    /// <summary>Chance a team changes three players between seasons, which leaves a new roster core.</summary>
    public double NewCoreChance { get; init; } = 0.05;

    /// <summary>Chance a team changes one player between the two stages.</summary>
    public double MidSeasonChangeChance { get; init; } = 0.15;
}

/// <summary>Synthetic seasons with the hidden strengths that produced them.</summary>
/// <param name="History">What the engine sees: teams and leagues, maps, freezes, season starts.</param>
/// <param name="TrueStrengths">Hidden strength of every team still active at the end.</param>
/// <param name="TrueChances">Team A's true win chance on each map, by map ID: the best prediction possible.</param>
/// <param name="Series">Every series as played, for provider-shaped responses.</param>
public sealed record SimulatedWorld(
    History History,
    IReadOnlyDictionary<int, double> TrueStrengths,
    IReadOnlyDictionary<string, double> TrueChances,
    IReadOnlyList<SimulatedSeries> Series);

public sealed record SimulatedSeries(
    string SeriesId,
    DateTimeOffset PlayedAt,
    int BestOf,
    string Tournament,
    string Tier,
    SimulatedSide TeamA,
    SimulatedSide TeamB,
    IReadOnlyList<(int RoundsA, int RoundsB)> Maps);

/// <param name="Key">The provider-style team key, as a Liquipedia team template would be.</param>
public sealed record SimulatedSide(int TeamId, string Key, string Name, IReadOnlyList<int> PlayerIds);
