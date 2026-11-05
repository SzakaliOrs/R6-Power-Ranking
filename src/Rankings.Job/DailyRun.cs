using Rankings.Core;
using Rankings.Job.Archive;
using Rankings.Job.Site;
using Rankings.Job.Storage;

namespace Rankings.Job;

public sealed record DailyRunOptions
{
    /// <summary>liquipedia or synthetic; a database is only ever rated from one of them.</summary>
    public required string DataSource { get; init; }

    /// <summary>Days before yesterday fetched again in every run, for results that arrive late.</summary>
    public int LookbackDays { get; init; } = 2;

    /// <summary>Rebuild every rating from the archive instead of carrying on from the last settled day.</summary>
    public bool Full { get; init; }

    /// <summary>
    /// With <see cref="Full"/>: fetch the whole history from the source instead of reading the
    /// archive. Only for the synthetic source, which costs nothing to ask.
    /// </summary>
    public bool FetchWholeHistory { get; init; }

    /// <summary>The oldest day a full recalculation reads.</summary>
    public DateOnly EarliestDay { get; init; } = new(2023, 1, 1);
}

/// <param name="RatedFrom">First day rated this run; null when there was nothing to rate.</param>
/// <param name="SettledThrough">The checkpoint after this run; null until a day has settled.</param>
/// <param name="LateMaps">Maps stored for days already settled, e.g. after a team was assigned late; only a full recalculation rates them.</param>
/// <param name="ReparsedDays">Archived days read back this run.</param>
/// <param name="MissingDays">Days a full recalculation found no archive file for.</param>
public sealed record DailyRunSummary(
    DateOnly? RatedFrom,
    DateOnly RatedThrough,
    DateOnly? SettledThrough,
    int FetchedMaps,
    int RatedMaps,
    int Skipped,
    int UnassignedTeams,
    int LateMaps,
    int ReparsedDays,
    IReadOnlyList<DateOnly> MissingDays);

/// <summary>
/// One daily run. It fetches yesterday and the <see cref="DailyRunOptions.LookbackDays"/> days
/// before it, archives them, then rates from the day after the last settled day through
/// yesterday. The oldest day of the lookback window will never be fetched again, so it becomes
/// the new settled day: the rating state at its end is saved as the checkpoint and its maps
/// leave the maps table. The newer days are rated again by the next run, with whatever arrived
/// late. A full recalculation runs the same days through the same code from an empty state, so
/// it reproduces the daily runs exactly.
/// </summary>
public sealed class DailyRun(
    RankingsDb db,
    IMatchSource source,
    IReadOnlyList<IArchiveStore> archives,
    IArchiveStore archiveToRead,
    RatingSettings settings,
    TimeProvider? time = null)
{
    public const string AlgorithmVersion = "elo-1";

    readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<DailyRunSummary> RunAsync(DailyRunOptions options, CancellationToken cancellationToken = default)
    {
        var store = new RatingsStore(db);
        store.CheckDataSource(options.DataSource);
        var yesterday = RatingsStore.DayOf(_time.GetUtcNow()).AddDays(-1);
        if (yesterday < options.EarliestDay)
            throw new DailyRunException($"Yesterday ({yesterday:yyyy-MM-dd}) is before {options.EarliestDay:yyyy-MM-dd}; there is nothing to rate.");

        if (options.Full)
            store.ResetForFullRecalculation();
        var settled = store.SettledThrough;
        var import = new MatchImport(db, archives, _time);

        // From the day after the checkpoint, which also covers days a missed run never fetched;
        // a settled day is never fetched again, since its maps are rated and gone.
        var fetchFrom = settled?.AddDays(1) ?? Max(yesterday.AddDays(-options.LookbackDays), options.EarliestDay);
        var reparsedDays = 0;
        IReadOnlyList<DateOnly> missingDays = [];
        ImportSummary fetched;
        if (options.Full && options.FetchWholeHistory)
            fetched = await import.FetchAsync(source, options.EarliestDay, yesterday, cancellationToken);
        else
        {
            // Older days first, so players are numbered in the order they first played.
            if (options.Full && fetchFrom > options.EarliestDay)
            {
                var reparsed = await import.ReparseAsync(archiveToRead, options.EarliestDay, fetchFrom.AddDays(-1), cancellationToken);
                (reparsedDays, missingDays) = (reparsed.Days, reparsed.MissingDays ?? []);
            }
            else if (!options.Full)
                reparsedDays = await ReparseNewlyAssignedAsync(store, import, fetchFrom, yesterday, cancellationToken);
            fetched = fetchFrom <= yesterday
                ? await import.FetchAsync(source, fetchFrom, yesterday, cancellationToken)
                : new ImportSummary(0, 0, 0, new ProviderIds(db).UnassignedTeams().Count);
        }

        var maps = db.LoadMaps();
        var ratedFrom = settled?.AddDays(1)
            ?? Min(maps.Count > 0 ? RatingsStore.DayOf(maps[0].PlayedAt) : fetchFrom, fetchFrom);
        var lateMaps = maps.Count(map => RatingsStore.DayOf(map.PlayedAt) < ratedFrom);
        if (ratedFrom > yesterday)
            return new DailyRunSummary(null, yesterday, settled, fetched.Maps, 0, fetched.Skipped, fetched.UnassignedTeams,
                lateMaps, reparsedDays, missingDays);

        var newSettled = yesterday.AddDays(-options.LookbackDays);
        var mapsByDay = maps.Where(map => RatingsStore.DayOf(map.PlayedAt) >= ratedFrom).ToLookup(map => RatingsStore.DayOf(map.PlayedAt));
        var state = settled is null ? new RatingState() : store.LoadSettledState();
        var previous = settled is { } settledDay ? store.LoadSnapshot(settledDay, settings) : null;
        var rater = new DailyRating(settings, db.LoadSeasonStarts());
        var leagues = store.Leagues();
        var teamLeagues = store.TeamLeagues();
        var entryLevels = db.LoadLeagueEntryLevels();
        var freezes = store.Freezes();

        var ratedMaps = 0;
        using (var tx = db.Connection.BeginTransaction())
        {
            store.ClearFrom(ratedFrom, tx);
            for (var day = ratedFrom; day <= yesterday; day = day.AddDays(1))
            {
                var run = rater.RateDay(state, day, [.. mapsByDay[day]], leagues, teamLeagues, entryLevels, freezes[day]);
                var snapshot = Snapshots.Take(state, day, settings, previous);
                store.SaveDay(snapshot, run, AlgorithmVersion, tx);
                previous = snapshot;
                ratedMaps += run.Rated;
                if (day == newSettled)
                {
                    store.Settle(day, ratedFrom, state, tx);
                    settled = day;
                }
            }
            store.SaveCurrentRatings(state, yesterday, tx);
            tx.Commit();
        }

        return new DailyRunSummary(ratedFrom, yesterday, settled, fetched.Maps, ratedMaps, fetched.Skipped,
            fetched.UnassignedTeams, lateMaps, reparsedDays, missingDays);
    }

    /// <summary>Writes the site's JSON from what the runs stored; false while nothing is rated yet.</summary>
    public bool ExportSite(string dataDirectory, string dataSource)
    {
        if (SiteData.Load(db, settings, _time.GetUtcNow(), AlgorithmVersion, dataSource) is not { } input)
            return false;
        SiteExport.Write(input, dataDirectory);
        return true;
    }

    /// <summary>
    /// Reads back archived days whose maps were skipped for a team that has been assigned since,
    /// so they are stored without asking the provider again. Days in this run's fetch window are
    /// left out, since the fetch stores them anyway.
    /// </summary>
    async Task<int> ReparseNewlyAssignedAsync(RatingsStore store, MatchImport import, DateOnly windowFrom, DateOnly windowTo,
        CancellationToken cancellationToken)
    {
        var days = 0;
        foreach (var day in store.DaysWithNewlyAssignedTeams().Where(day => day < windowFrom || day > windowTo))
        {
            await import.ReparseAsync(archiveToRead, day, day, cancellationToken);
            days++;
        }
        return days;
    }

    static DateOnly Min(DateOnly first, DateOnly second) => first < second ? first : second;

    static DateOnly Max(DateOnly first, DateOnly second) => first > second ? first : second;
}
