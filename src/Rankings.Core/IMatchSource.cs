namespace Rankings.Core;

/// <summary>
/// Where maps come from: the synthetic generator while testing, the Liquipedia adapter for real data.
/// </summary>
public interface IMatchSource
{
    /// <summary>Finished maps of series that started in [from, to), plus the raw responses they came from.</summary>
    Task<SourceBatch> FetchAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
}

/// <param name="Responses">Response bodies as received, for the B2 archive.</param>
public sealed record SourceBatch(
    IReadOnlyList<SourceMap> Maps,
    IReadOnlyList<SkippedSourceMap> Skipped,
    IReadOnlyList<RawResponse> Responses);

/// <summary>
/// A map as the provider names it: teams and players by provider key, not yet by own ID.
/// Becomes a <see cref="MapRecord"/> once the keys are resolved.
/// </summary>
public sealed record SourceMap(
    string MapId,
    string SeriesId,
    DateTimeOffset PlayedAt,
    int MapNumber,
    int MapsInSeries,
    SourceTeam TeamA,
    SourceTeam TeamB,
    int RoundsA,
    int RoundsB,
    string Tier,
    string Tournament);

/// <param name="Key">Provider's stable team key; changes when a team rebrands.</param>
/// <param name="Players">Players the provider lists for the whole series.</param>
public sealed record SourceTeam(string Key, string Name, IReadOnlyList<SourcePlayer> Players);

/// <param name="Key">Provider's stable player key; changes when a player is renamed.</param>
public sealed record SourcePlayer(string Key, string Name);

/// <param name="MapId">The map's ID, or the series ID when the whole series was skipped.</param>
public sealed record SkippedSourceMap(string MapId, string? SeriesId, DateTimeOffset? PlayedAt, string Reason);

/// <param name="Request">The request path and query, without credentials.</param>
public sealed record RawResponse(string Request, DateTimeOffset FetchedAt, string Body);
