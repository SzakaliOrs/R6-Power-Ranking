using Rankings.Core;
using Rankings.Job.Storage;

namespace Rankings.Job.Site;

/// <summary>The site's input as rankings.db holds it after a daily run.</summary>
public static class SiteData
{
    /// <summary>Null while no day has been rated yet.</summary>
    public static SiteInput? Load(RankingsDb db, RatingSettings settings, DateTimeOffset updatedAt, string algorithmVersion, string dataSource)
    {
        var store = new RatingsStore(db);
        var days = store.LoadSnapshots(settings);
        if (days.Count == 0)
            return null;
        var lastDay = days[^1].Date;
        var seasonStart = db.LoadSeasonStarts()
            .Where(start => RatingsStore.DayOf(start) <= lastDay)
            .Cast<DateTimeOffset?>()
            .LastOrDefault();
        return new SiteInput(updatedAt, algorithmVersion, seasonStart, store.SiteTeams(RatingsStore.DayOf(updatedAt)),
            store.SiteRegions(), days, store.LoadRatedMaps(), store.LoadRosterEvents(), dataSource);
    }
}
