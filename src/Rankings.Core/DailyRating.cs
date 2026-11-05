namespace Rankings.Core;

/// <summary>
/// Rates one UTC day at a time and keeps everything it needs between days in the
/// <see cref="RatingState"/>, so the daily job can save the state after any day and carry on in
/// the next run. Rating a stretch of days in one go or across many runs gives identical ratings,
/// which is what lets a full recalculation reproduce the daily runs.
/// </summary>
/// <remarks>
/// Unlike <see cref="Replay"/>, which knows the whole history in advance, it learns about a team
/// only when the team first plays. A team that debuts during the warm-up season starts at offset 0;
/// one that debuts later is seeded from its players, as in a replay.
/// </remarks>
public sealed class DailyRating(RatingSettings settings, IReadOnlyList<DateTimeOffset> seasonStarts)
{
    readonly DateTimeOffset[] _seasonStarts = [.. seasonStarts.Order()];
    readonly RatingEngine _engine = new(settings, seasonStarts);

    public RatingSettings Settings { get; } = settings;

    /// <param name="maps">The maps played on <paramref name="day"/>, in any order.</param>
    /// <param name="leagues">Every rated league; one not in the state yet is added at the starting rating.</param>
    /// <param name="teamLeagues">The league of every team that may play.</param>
    /// <param name="entryLevels">Admin entry levels for leagues that enter later.</param>
    /// <param name="frozenTeams">Teams relegated or disbanded on this day; frozen before its maps.</param>
    public RatingRun RateDay(
        RatingState state,
        DateOnly day,
        IReadOnlyList<MapRecord> maps,
        IEnumerable<string> leagues,
        IReadOnlyDictionary<int, string> teamLeagues,
        IReadOnlyDictionary<string, double> entryLevels,
        IEnumerable<int> frozenTeams)
    {
        if (maps.FirstOrDefault(map => DayOf(map.PlayedAt) != day) is { } stray)
            throw new ArgumentException($"Map {stray.MapId} was not played on {day:yyyy-MM-dd}.", nameof(maps));

        foreach (var league in leagues.Order(StringComparer.Ordinal))
            if (!state.Leagues.ContainsKey(league))
                state.AddLeague(league, Settings.StartingLeagueRating);
        foreach (var (league, level) in entryLevels)
            if (state.Leagues.ContainsKey(league))
                state.SetLeagueEntryLevel(league, level);

        if (state.FirstMapAt is null && maps.Count > 0)
            Start(state, maps.Min(map => map.PlayedAt));
        if (state.FirstMapAt is null)
            return new RatingRun([], [], []);

        foreach (var teamId in frozenTeams)
            if (state.Teams.ContainsKey(teamId))
                RatingEngine.Freeze(state, teamId);

        var debuts = new SortedDictionary<int, DateTimeOffset>();
        foreach (var map in maps)
            foreach (var teamId in (ReadOnlySpan<int>)[map.TeamA, map.TeamB])
                if (!state.Teams.ContainsKey(teamId) && (!debuts.TryGetValue(teamId, out var first) || map.PlayedAt < first))
                    debuts[teamId] = map.PlayedAt;
        foreach (var (teamId, debut) in debuts)
        {
            var league = teamLeagues.TryGetValue(teamId, out var found)
                ? found
                : throw new KeyNotFoundException($"Team {teamId} has no league.");
            if (state.WarmUpUntil is not { } warmUpEnd || debut < warmUpEnd)
                state.AddTeam(teamId, league);
            else
                state.AddNewTeam(teamId, league);
        }

        var endOfDay = new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return _engine.Rate(state, maps, endOfDay);
    }

    /// <summary>
    /// The first rated map opens the history: its season needs no pull, and the warm-up lasts
    /// until the next season starts, as in a replay.
    /// </summary>
    void Start(RatingState state, DateTimeOffset firstMapAt)
    {
        state.FirstMapAt = firstMapAt;
        state.SeasonStartedAt = _seasonStarts.Where(start => start <= firstMapAt).Cast<DateTimeOffset?>().LastOrDefault();
        state.WarmUpUntil = _seasonStarts.Where(start => start > firstMapAt).Cast<DateTimeOffset?>().FirstOrDefault();
    }

    static DateOnly DayOf(DateTimeOffset instant) => DateOnly.FromDateTime(instant.UtcDateTime);
}
