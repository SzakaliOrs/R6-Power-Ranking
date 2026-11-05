namespace Rankings.Core;

/// <summary>
/// One played map in the project's own format. The provider adapter fills it;
/// nothing past the adapter reads provider data.
/// </summary>
/// <param name="MapId">Unique per map, so a rerun overwrites instead of duplicating.</param>
/// <param name="SeriesId">Groups the maps of one series for the series bonus.</param>
/// <param name="PlayedAt">Start of the series in UTC; all maps of a series share it.</param>
/// <param name="MapNumber">1-based position in the series; orders maps that share a start time.</param>
/// <param name="MapsInSeries">Best-of count: 1, 3 or 5.</param>
/// <param name="TeamA">Own team ID (roster core), not the provider's.</param>
/// <param name="TeamB">Own team ID (roster core), not the provider's.</param>
/// <param name="RoundsA">Rounds won by team A, overtime included.</param>
/// <param name="RoundsB">Rounds won by team B, overtime included.</param>
/// <param name="PlayersA">Own player IDs listed for team A; usually 5, more when the provider lists a substitute.</param>
/// <param name="PlayersB">Own player IDs listed for team B; usually 5, more when the provider lists a substitute.</param>
public sealed record MapRecord(
    string MapId,
    string SeriesId,
    DateTimeOffset PlayedAt,
    int MapNumber,
    int MapsInSeries,
    int TeamA,
    int TeamB,
    int RoundsA,
    int RoundsB,
    IReadOnlyList<int> PlayersA,
    IReadOnlyList<int> PlayersB);
