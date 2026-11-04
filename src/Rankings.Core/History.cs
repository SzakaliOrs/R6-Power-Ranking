namespace Rankings.Core;

/// <summary>
/// Everything a replay needs. The simulator builds it for synthetic seasons; for real history
/// the job will build it from rankings.db and the archived days in B2.
/// </summary>
/// <param name="TeamLeagues">Every team that plays, with its league.</param>
/// <param name="Freezes">When teams were relegated or disbanded.</param>
/// <param name="SeasonStarts">Season starts, as in the seasons table; the first season is the warm-up.</param>
/// <param name="LeagueEntryLevels">Admin entry levels for leagues that enter later, as in the regions table.</param>
public sealed record History(
    IReadOnlyDictionary<int, string> TeamLeagues,
    IReadOnlyList<MapRecord> Maps,
    IReadOnlyList<TeamFreeze> Freezes,
    IReadOnlyList<DateTimeOffset> SeasonStarts,
    IReadOnlyDictionary<string, double>? LeagueEntryLevels = null);

/// <summary>A team relegated or disbanded at <paramref name="At"/>; it is rated again if it plays again.</summary>
public sealed record TeamFreeze(DateTimeOffset At, int TeamId);
