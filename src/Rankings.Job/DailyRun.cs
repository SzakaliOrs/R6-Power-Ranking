using Rankings.Core;
using Rankings.Job.Archive;
using Rankings.Job.Site;
using Rankings.Job.Storage;

namespace Rankings.Job;

public sealed record DailyRunOptions
{
    /// <summary>liquipedia or synthetic; a database is only ever rated from one of them.</summary>
    public required string DataSource { get; init; }

    /// <summary>Days before yesterday fetched again in every run, for results that arrive late; the oldest settles.</summary>
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

    /// <summary>Progress lines as the run goes, e.g. to the console, so a long run shows where it is.</summary>
    public Action<string>? Log { get; init; }
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
    IReadOnlyList<DateOnly> MissingDays)
{
    /// <summary>
    /// Teams the saved rating state still held but the database no longer has (merged in the admin
    /// dashboard); a run that finds any rebuilds every rating instead of carrying on.
    /// </summary>
    public IReadOnlyList<int> RemovedTeams { get; init; } = [];
}

/// <summary>
/// One daily run. It fetches everything up to the run's start time: yesterday, the
/// <see cref="DailyRunOptions.LookbackDays"/> days before it and the run day so far. It archives
/// them, then rates from the day after the last settled day through the run day, whose snapshot
/// the next run replaces with the whole day. The oldest day of the lookback window will never be
/// fetched again, so it becomes
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
    /// <summary>Stamped on every snapshot. elo-2: a team that signs another team's core takes over its rating.</summary>
    public const string AlgorithmVersion = "elo-2";

    readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<DailyRunSummary> RunAsync(DailyRunOptions options, CancellationToken cancellationToken = default)
    {
        var store = new RatingsStore(db);
        store.CheckDataSource(options.DataSource);
        // Matches are fetched and rated up to the moment the run starts.
        var startedAt = _time.GetUtcNow();
        var today = RatingsStore.DayOf(startedAt);
        var yesterday = today.AddDays(-1);
        if (yesterday < options.EarliestDay)
            throw new DailyRunException($"Yesterday ({yesterday:yyyy-MM-dd}) is before {options.EarliestDay:yyyy-MM-dd}; there is nothing to rate.");

        // A merge deletes a team the saved state still rates; carrying on from it would save
        // snapshots of a team that no longer exists, so such a run rebuilds everything instead.
        List<int> removedTeams = options.Full ? [] : store.TeamsRemovedSinceSettled();
        options = removedTeams.Count > 0 ? options with { Full = true } : options;
        var log = options.Log;
        if (removedTeams.Count > 0)
            log?.Invoke($"Team(s) {string.Join(", ", removedTeams)} were merged away since the last full recalculation: rebuilding every rating.");
        if (options.Full)
        {
            log?.Invoke("Full recalculation: clearing the stored ratings.");
            store.ResetForFullRecalculation();
        }
        var settled = store.SettledThrough;
        var import = new MatchImport(db, archives, _time);

        // From the day after the checkpoint, which also covers days a missed run never fetched;
        // a settled day is never fetched again, since its maps are rated and gone.
        var fetchFrom = settled?.AddDays(1) ?? Max(yesterday.AddDays(-options.LookbackDays), options.EarliestDay);
        var reparsedDays = 0;
        IReadOnlyList<DateOnly> missingDays = [];
        ImportSummary fetched;
        if (options.Full && options.FetchWholeHistory)
            fetched = await import.FetchAsync(source, options.EarliestDay, today, cancellationToken, until: startedAt);
        else
        {
            // Older days first, so players are numbered in the order they first played.
            if (options.Full && fetchFrom > options.EarliestDay)
            {
                log?.Invoke($"Reading {options.EarliestDay:yyyy-MM-dd}..{fetchFrom.AddDays(-1):yyyy-MM-dd} back from {archiveToRead.Name}.");
                var reparsed = await import.ReparseAsync(archiveToRead, options.EarliestDay, fetchFrom.AddDays(-1), cancellationToken, log);
                (reparsedDays, missingDays) = (reparsed.Days, reparsed.MissingDays ?? []);
            }
            else if (!options.Full)
                reparsedDays = await ReparseNewlyAssignedAsync(store, import, fetchFrom, today, cancellationToken);
            if (fetchFrom <= today)
                log?.Invoke($"Fetching {fetchFrom:yyyy-MM-dd} 00:00..{startedAt:yyyy-MM-dd HH:mm:ss} UTC from the source.");
            fetched = fetchFrom <= today
                ? await import.FetchAsync(source, fetchFrom, today, cancellationToken, until: startedAt)
                : new ImportSummary(0, 0, 0, new ProviderIds(db).UnassignedTeams().Count);
            log?.Invoke($"Fetched {fetched.Maps} map(s), {fetched.Skipped} skipped.");
        }

        var maps = db.LoadMaps();
        var ratedFrom = settled?.AddDays(1)
            ?? Min(maps.Count > 0 ? RatingsStore.DayOf(maps[0].PlayedAt) : fetchFrom, fetchFrom);
        var lateMaps = maps.Count(map => RatingsStore.DayOf(map.PlayedAt) < ratedFrom);
        if (ratedFrom > today)
            return new DailyRunSummary(null, today, settled, fetched.Maps, 0, fetched.Skipped, fetched.UnassignedTeams,
                lateMaps, reparsedDays, missingDays) { RemovedTeams = removedTeams };

        var newSettled = yesterday.AddDays(-options.LookbackDays);
        var mapsByDay = maps.Where(map => RatingsStore.DayOf(map.PlayedAt) >= ratedFrom).ToLookup(map => RatingsStore.DayOf(map.PlayedAt));
        var state = settled is null ? new RatingState() : store.LoadSettledState();
        var previous = settled is { } settledDay ? store.LoadSnapshot(settledDay, settings) : null;
        var rater = new DailyRating(settings, db.LoadSeasonStarts());
        var leagues = store.Leagues();
        var teamLeagues = store.TeamLeagues();
        var entryLevels = db.LoadLeagueEntryLevels();
        var freezes = store.Freezes();
        var unrankedTeams = store.UnrankedTeams();
        var droppedTeams = store.DroppedTeams();

        var ratedMaps = 0;
        log?.Invoke($"Rating {ratedFrom:yyyy-MM-dd}..{today:yyyy-MM-dd} ({today.DayNumber - ratedFrom.DayNumber + 1} day(s), {maps.Count} map(s) waiting).");
        using (var tx = db.Connection.BeginTransaction())
        {
            store.ClearFrom(ratedFrom, tx);
            for (var day = ratedFrom; day <= today; day = day.AddDays(1))
            {
                var run = rater.RateDay(state, day, [.. mapsByDay[day]], leagues, teamLeagues, entryLevels, freezes[day], droppedTeams[day]);
                var snapshot = Snapshots.Take(state, day, settings, previous, unrankedTeams.On(day));
                store.SaveDay(snapshot, run, AlgorithmVersion, tx);
                previous = snapshot;
                ratedMaps += run.Rated;
                if (day == newSettled)
                {
                    store.Settle(day, ratedFrom, state, tx);
                    settled = day;
                }
                // A line per finished month on long runs; a daily run's few days need none.
                if (day.AddDays(1).Day == 1 && today.DayNumber - ratedFrom.DayNumber > 31)
                    log?.Invoke($"Rated through {day:yyyy-MM-dd}: {ratedMaps} map(s), {state.Teams.Count} team(s) so far.");
            }
            log?.Invoke("Saving the ratings.");
            store.SaveCurrentRatings(state, today, tx);
            tx.Commit();
        }
        log?.Invoke($"Rated {ratedMaps} map(s) through {startedAt:yyyy-MM-dd HH:mm:ss} UTC.");

        return new DailyRunSummary(ratedFrom, today, settled, fetched.Maps, ratedMaps, fetched.Skipped,
            fetched.UnassignedTeams, lateMaps, reparsedDays, missingDays) { RemovedTeams = removedTeams };
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
