namespace Rankings.Core;

/// <summary>
/// Rates a whole history the way the daily job would have: one run per UTC day, with that
/// day's freezes applied before its maps. Every league starts at the starting rating; teams
/// that play in the first (warm-up) season start at offset 0, later ones are seeded from their
/// players, except in a league that enters later, whose teams start level with it.
/// </summary>
public static class Replay
{
    /// <param name="onPrediction">Called before each map with team A's win chance.</param>
    /// <param name="startingOffsets">Offsets for first-season teams instead of 0, e.g. from league standings.</param>
    /// <param name="onDayRated">Called after each day's run, e.g. to take that day's snapshot.</param>
    public static RatingState Run(
        History history,
        RatingSettings settings,
        Action<MapRecord, double>? onPrediction = null,
        IReadOnlyDictionary<int, double>? startingOffsets = null,
        Action<DateOnly, RatingState, RatingRun>? onDayRated = null)
    {
        var state = new RatingState();
        if (history.Maps.Count == 0)
            return state;

        var engine = new RatingEngine(settings, history.SeasonStarts);
        var seasonStarts = history.SeasonStarts.Order().Cast<DateTimeOffset?>().ToList();
        var firstMapAt = history.Maps.Min(map => map.PlayedAt);
        // The season the replay opens in needs no pull, and the warm-up lasts until the next one starts.
        state.SeasonStartedAt = seasonStarts.LastOrDefault(start => start <= firstMapAt);
        var warmUpEnd = seasonStarts.FirstOrDefault(start => start > firstMapAt);
        state.WarmUpUntil = warmUpEnd;

        foreach (var league in history.TeamLeagues.Values.Distinct().Order(StringComparer.Ordinal))
            state.AddLeague(league, settings.StartingLeagueRating);
        foreach (var (league, level) in history.LeagueEntryLevels ?? new Dictionary<string, double>())
            if (state.Leagues.ContainsKey(league))
                state.SetLeagueEntryLevel(league, level);

        var firstPlayed = new Dictionary<int, DateTimeOffset>();
        foreach (var map in history.Maps)
            foreach (var teamId in (ReadOnlySpan<int>)[map.TeamA, map.TeamB])
                if (!firstPlayed.TryGetValue(teamId, out var first) || map.PlayedAt < first)
                    firstPlayed[teamId] = map.PlayedAt;
        foreach (var (teamId, league) in history.TeamLeagues.OrderBy(team => team.Key))
        {
            if (!firstPlayed.TryGetValue(teamId, out var debut))
                continue;
            if (warmUpEnd is null || debut < warmUpEnd)
                state.AddTeam(teamId, league, startingOffsets?.GetValueOrDefault(teamId) ?? 0);
            else
                state.AddNewTeam(teamId, league);
        }

        var mapsByDay = history.Maps.ToLookup(map => DayOf(map.PlayedAt));
        var freezesByDay = history.Freezes.ToLookup(freeze => DayOf(freeze.At));
        var lastDay = DayOf(history.Maps.Max(map => map.PlayedAt));
        for (var day = DayOf(firstMapAt); day <= lastDay; day = day.AddDays(1))
        {
            foreach (var freeze in freezesByDay[day])
                if (state.Teams.ContainsKey(freeze.TeamId))
                    RatingEngine.Freeze(state, freeze.TeamId);
            var endOfDay = new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var run = engine.Rate(state, mapsByDay[day], endOfDay, onPrediction);
            onDayRated?.Invoke(day, state, run);
        }
        return state;
    }

    /// <summary>Rated teams, highest rating first; frozen and never-seeded teams are not ranked.</summary>
    public static List<int> Ranking(RatingState state) =>
        [.. state.Teams.Values
            .Where(team => team.Seeded && !team.IsFrozen)
            .OrderByDescending(state.Rating)
            .ThenBy(team => team.TeamId)
            .Select(team => team.TeamId)];

    static DateOnly DayOf(DateTimeOffset instant) => DateOnly.FromDateTime(instant.UtcDateTime);
}
