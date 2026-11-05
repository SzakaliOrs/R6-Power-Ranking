using System.Globalization;
using Rankings.Core;
using Rankings.Job.Archive;
using Rankings.Job.Liquipedia;
using Rankings.Job.Storage;

namespace Rankings.Job;

/// <param name="Days">Days archived (fetch) or read back (reparse).</param>
/// <param name="UnassignedTeams">Team keys still waiting for an assignment after this import.</param>
/// <param name="StoppedForBudget">A backfill stopped early because the hourly budget was spent.</param>
/// <param name="MissingDays">Days a reparse found no archive file for.</param>
public sealed record ImportSummary(
    int Days,
    int Maps,
    int Skipped,
    int UnassignedTeams,
    bool StoppedForBudget = false,
    IReadOnlyList<DateOnly>? MissingDays = null);

/// <summary>
/// Gets maps into the database: fetched from the provider and archived first, or read back
/// from the archive. Works on whole UTC days, so an archived day is always complete and a
/// later fetch of the same day can simply replace it.
/// </summary>
public sealed class MatchImport(RankingsDb db, IReadOnlyList<IArchiveStore> archives, TimeProvider? time = null)
{
    public const string BackfillStateKey = "backfill_through";

    readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Fetches the days from..to inclusive, archives every day, then stores the maps.</summary>
    public async Task<ImportSummary> FetchAsync(IMatchSource source, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var batch = await source.FetchAsync(StartOf(from), StartOf(to.AddDays(1)), cancellationToken);
        var days = LpdbArchive.SplitByDay(batch.Responses, from, to, _time.GetUtcNow());

        // The archive is the source of truth, so it is complete before the database changes.
        foreach (var (day, json) in days)
        {
            var content = LpdbArchive.Compress(json);
            foreach (var archive in archives)
                await archive.WriteAsync(LpdbArchive.Key(day), content, cancellationToken);
        }
        return Store(batch.Maps, batch.Skipped, days.Count);
    }

    /// <summary>
    /// Reads archived days back through the parser, e.g. after a team was assigned, so maps
    /// skipped for it are stored without fetching again.
    /// </summary>
    public async Task<ImportSummary> ReparseAsync(IArchiveStore archive, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var maps = new List<SourceMap>();
        var skipped = new List<SkippedSourceMap>();
        var missing = new List<DateOnly>();
        var days = 0;
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            if (await archive.ReadAsync(LpdbArchive.Key(day), cancellationToken) is not { } content)
            {
                missing.Add(day);
                continue;
            }
            LpdbArchive.Parse(LpdbArchive.Decompress(content), maps, skipped);
            days++;
        }
        return Store(maps, skipped, days) with { MissingDays = missing };
    }

    /// <summary>
    /// Fetches a month at a time from <paramref name="from"/>, or from the day after the last
    /// finished one, and records progress after each month so a stopped run carries on from there.
    /// Stops before a month when <paramref name="remainingCalls"/> says the hourly budget is spent.
    /// </summary>
    /// <param name="onMonthDone">Called after each month is archived and stored, with its first and last day.</param>
    public async Task<ImportSummary> BackfillAsync(
        IMatchSource source, Func<int> remainingCalls, DateOnly from, DateOnly to,
        bool restart = false, Action<DateOnly, DateOnly, ImportSummary>? onMonthDone = null,
        CancellationToken cancellationToken = default)
    {
        var start = from;
        if (!restart && db.GetState(BackfillStateKey) is { } done
            && DateOnly.ParseExact(done, "yyyy-MM-dd", CultureInfo.InvariantCulture) is var through && through >= from)
            start = through.AddDays(1);

        var total = new ImportSummary(0, 0, 0, new ProviderIds(db).UnassignedTeams().Count);
        for (var month = start; month <= to; month = new DateOnly(month.Year, month.Month, 1).AddMonths(1))
        {
            if (remainingCalls() == 0)
                return total with { StoppedForBudget = true };

            var lastDay = new DateOnly(month.Year, month.Month, DateTime.DaysInMonth(month.Year, month.Month));
            var end = lastDay < to ? lastDay : to;
            ImportSummary summary;
            try
            {
                summary = await FetchAsync(source, month, end, cancellationToken);
            }
            catch (LpdbBudgetSpentException)
            {
                // Spent partway through the month: nothing of it was archived, so it is fetched whole next time.
                return total with { StoppedForBudget = true };
            }
            db.SetState(BackfillStateKey, end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            onMonthDone?.Invoke(month, end, summary);
            total = new ImportSummary(total.Days + summary.Days, total.Maps + summary.Maps,
                total.Skipped + summary.Skipped, summary.UnassignedTeams);
        }
        return total;
    }

    ImportSummary Store(IReadOnlyList<SourceMap> maps, IReadOnlyList<SkippedSourceMap> parserSkips, int days)
    {
        var ids = new ProviderIds(db);
        var resolved = ids.Resolve(maps);
        db.SaveSeries(maps);
        db.SaveMaps(resolved.Maps);
        // A map stored now may have been skipped before, e.g. while its team was unassigned.
        db.ClearSkipped(resolved.Maps.SelectMany(m => new[] { m.MapId, m.SeriesId }));

        var skipped = parserSkips.Concat(resolved.Skipped).ToList();
        db.SaveSkipped(skipped, _time.GetUtcNow());
        return new ImportSummary(days, resolved.Maps.Count, skipped.Count, ids.UnassignedTeams().Count);
    }

    static DateTimeOffset StartOf(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}
