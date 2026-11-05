namespace Rankings.Core;

/// <summary>
/// Rates maps in play order. A map within one league moves the two teams' offsets with
/// K_reg; a map between two leagues moves their offsets with K_int and both league
/// ratings with K_league. A BO3 or BO5 winner gets one extra update. Before a team's map
/// the engine seeds it, brings it back from a freeze, decays it for inactivity and checks
/// its lineup against its roster core. After each run every league's offsets are re-centred on zero.
/// </summary>
/// <param name="seasonStarts">When each top-level season starts; each one pulls every offset toward zero once.</param>
public sealed class RatingEngine(RatingSettings settings, IEnumerable<DateTimeOffset>? seasonStarts = null)
{
    public const string NoWinner = "no winner";

    readonly DateTimeOffset[] _seasonStarts = [.. (seasonStarts ?? []).Order()];

    public RatingSettings Settings { get; } = settings;

    /// <summary>
    /// One run, as the daily job makes it: rates <paramref name="maps"/> series by series in
    /// play order, applies season starts and inactivity decay up to <paramref name="asOf"/>,
    /// then re-centres every league. A replay calls this once per day so it matches the daily runs.
    /// </summary>
    /// <param name="onPrediction">Called before each map is rated with team A's win chance, for the backtest.</param>
    public RatingRun Rate(RatingState state, IEnumerable<MapRecord> maps, DateTimeOffset asOf,
        Action<MapRecord, double>? onPrediction = null)
    {
        var skipped = new List<SkippedMap>();
        var events = new List<RosterEvent>();
        var ratedMaps = new List<RatedMap>();
        var teamsThatPlayed = new HashSet<int>();

        foreach (var series in BySeries(InPlayOrder(maps)))
        {
            var mapsWithWinner = new List<MapRecord>();
            foreach (var map in series)
            {
                if (map.RoundsA == map.RoundsB)
                    skipped.Add(new SkippedMap(map.MapId, NoWinner));
                else
                    mapsWithWinner.Add(map);
            }
            if (mapsWithWinner.Count == 0)
                continue;

            StartSeasons(state, mapsWithWinner[0].PlayedAt);
            Dictionary<int, double>? ratingsBeforeSeries = null;
            foreach (var map in mapsWithWinner)
            {
                Prepare(state, state.Team(map.TeamA), map.PlayersA, map.PlayedAt, events);
                Prepare(state, state.Team(map.TeamB), map.PlayersB, map.PlayedAt, events);
                ratingsBeforeSeries ??= new()
                {
                    [map.TeamA] = state.Rating(map.TeamA),
                    [map.TeamB] = state.Rating(map.TeamB),
                };
                var (ratingA, ratingB) = (state.Rating(map.TeamA), state.Rating(map.TeamB));
                onPrediction?.Invoke(map, Elo.Expected(ratingA, ratingB));
                RateMap(state, map);
                ratedMaps.Add(new RatedMap(map, state.Rating(map.TeamA) - ratingA, state.Rating(map.TeamB) - ratingB));
                teamsThatPlayed.Add(map.TeamA);
                teamsThatPlayed.Add(map.TeamB);
            }
            SeriesBonus(state, mapsWithWinner, ratingsBeforeSeries!);
        }

        StartSeasons(state, asOf);
        foreach (var team in state.Teams.Values.Where(team => team.Seeded && !team.IsFrozen))
            Decay(team, asOf);
        Recentre(state);

        foreach (var team in teamsThatPlayed.Select(state.Team))
            foreach (var playerId in team.Lineup)
                state.SetPlayer(playerId, new PlayerRating(team.TeamId, team.Offset));

        return new RatingRun(ratedMaps, skipped, events);
    }

    /// <summary>
    /// The per-map update alone, Δ = K · M · (S − E) for each team with its own K, without
    /// seeding, decay, roster rules or re-centring.
    /// </summary>
    public void RateMap(RatingState state, MapRecord map)
    {
        if (map.RoundsA == map.RoundsB)
            throw new ArgumentException($"Map {map.MapId} has no winner.", nameof(map));

        var teamA = state.Team(map.TeamA);
        var teamB = state.Team(map.TeamB);
        var (ratingA, ratingB) = (state.Rating(teamA), state.Rating(teamB));
        var teamAWon = map.RoundsA > map.RoundsB;
        var winnerLead = teamAWon ? ratingA - ratingB : ratingB - ratingA;
        var marginMultiplier = Elo.MarginMultiplier(Math.Abs(map.RoundsA - map.RoundsB), winnerLead, Settings);
        var expectedA = Elo.Expected(ratingA, ratingB);
        var scoreA = teamAWon ? 1.0 : 0.0;

        var international = teamA.League != teamB.League;
        var teamK = KFactor(state, international ? Settings.KInternational : Settings.KRegional, map.PlayedAt);
        teamA.Offset += Elo.Delta(BoostedK(teamK, teamA), marginMultiplier, scoreA, expectedA);
        teamB.Offset += Elo.Delta(BoostedK(teamK, teamB), marginMultiplier, 1 - scoreA, 1 - expectedA);
        if (international)
        {
            var leagueChange = Elo.Delta(KFactor(state, Settings.KLeague, map.PlayedAt), marginMultiplier, scoreA, expectedA);
            state.MoveLeague(teamA.League, leagueChange);
            state.MoveLeague(teamB.League, -leagueChange);
        }

        foreach (var team in (ReadOnlySpan<TeamRating>)[teamA, teamB])
        {
            team.RatedMaps++;
            team.LastPlayedAt = map.PlayedAt;
            team.DecayPeriods = 0;
            if (team.BoostMapsLeft > 0)
                team.BoostMapsLeft--;
        }
    }

    /// <summary>
    /// Stops rating a relegated or disbanded team: its rating is kept as it stands, and its
    /// players carry their last offsets into their next teams. It comes back if it plays again.
    /// </summary>
    public static void Freeze(RatingState state, int teamId)
    {
        var team = state.Team(teamId);
        if (team.Seeded && !team.IsFrozen)
            team.FrozenRating = state.Rating(team);
    }

    /// <summary>
    /// Shifts each league's offsets to average zero and moves the difference into the league
    /// rating, so a league's rating is its average strength. No team's rating changes. Frozen
    /// teams keep their own rating and teams not yet seeded have none, so neither counts.
    /// </summary>
    public static void Recentre(RatingState state)
    {
        var ratedTeams = state.Teams.Values.Where(team => team.Seeded && !team.IsFrozen);
        foreach (var league in ratedTeams.GroupBy(team => team.League))
        {
            var averageOffset = league.Average(team => team.Offset);
            foreach (var team in league)
                team.Offset -= averageOffset;
            state.MoveLeague(league.Key, averageOffset);
        }
    }

    /// <summary>Series start, then series ID so same-time series always replay alike, then map number.</summary>
    public static IEnumerable<MapRecord> InPlayOrder(IEnumerable<MapRecord> maps) =>
        maps.OrderBy(map => map.PlayedAt)
            .ThenBy(map => map.SeriesId, StringComparer.Ordinal)
            .ThenBy(map => map.MapNumber);

    /// <summary>Gets a team ready for its next map: seeded, back from a freeze, decayed, its lineup checked.</summary>
    void Prepare(RatingState state, TeamRating team, IReadOnlyList<int> lineup, DateTimeOffset playedAt, List<RosterEvent> events)
    {
        if (!team.Seeded)
        {
            if (InNewLeague(state, team.League, playedAt))
                StartLevel(team, lineup);
            else
                Seed(state, team, lineup);
            events.Add(new RosterEvent(playedAt, team.TeamId, RosterEventKind.Seeded));
            NoteSharedCores(state, team, playedAt, events);
            return;
        }

        var returning = team.IsFrozen;
        if (returning)
        {
            team.Offset = team.FrozenRating!.Value - state.Leagues[team.League];
            team.FrozenRating = null;
            events.Add(new RosterEvent(playedAt, team.TeamId, RosterEventKind.Returned));
        }
        Decay(team, playedAt);

        // No lineup to compare (a provider gap, or the team's first map with players listed).
        if (lineup.Count == 0 || team.Lineup.Count == 0)
        {
            if (lineup.Count > 0)
                team.Lineup = lineup;
            if (returning)
                team.BoostMapsLeft = Settings.RosterChangeBoostMaps;
            return;
        }

        var playersKept = lineup.Count(team.Lineup.Contains);
        if (playersKept < Settings.RosterCoreSize)
        {
            // Too few of the core stayed: a new team under the same ID, seeded from its players.
            Seed(state, team, lineup);
            events.Add(new RosterEvent(playedAt, team.TeamId, RosterEventKind.NewCore));
            NoteSharedCores(state, team, playedAt, events);
            return;
        }

        var newPlayers = lineup.Count - playersKept;
        if (newPlayers > 0)
        {
            // Each new player pulls the offset part-way toward the league average, which is zero.
            team.Offset *= Math.Pow(1 - Settings.NewPlayerPull, newPlayers);
            events.Add(new RosterEvent(playedAt, team.TeamId, RosterEventKind.RosterChange, newPlayers));
        }
        if (newPlayers > 0 || returning)
            team.BoostMapsLeft = Settings.RosterChangeBoostMaps;
        team.Lineup = lineup;
    }

    /// <summary>
    /// Starting offset from the players: a share of each one's last offset, or the newcomer gap
    /// below the league rating for a player with no rated history.
    /// </summary>
    void Seed(RatingState state, TeamRating team, IReadOnlyList<int> lineup)
    {
        team.Offset = lineup.Count == 0
            ? -Settings.NewcomerGap
            : lineup.Average(playerId => state.Players.TryGetValue(playerId, out var lastRating)
                ? Settings.SeedingShare * lastRating.Offset
                : -Settings.NewcomerGap);
        team.Seeded = true;
        team.RatedMaps = 0;
        team.BoostMapsLeft = 0;
        team.DecayPeriods = 0;
        team.FrozenRating = null;
        team.Lineup = lineup;
    }

    /// <summary>
    /// Whether a team joining <paramref name="leagueId"/> joins a league new to the ratings, as
    /// China was in 2026: one with no rated team yet, or still in the season it entered in. The
    /// first such team enters the league, which then starts at the average established league plus
    /// its entry level. Its teams start level with it, since they come from an established scene
    /// the data did not cover, not from a lower division.
    /// </summary>
    bool InNewLeague(RatingState state, string leagueId, DateTimeOffset playedAt)
    {
        if (!state.LeagueEnteredAt.TryGetValue(leagueId, out var enteredAt))
        {
            if (state.Teams.Values.Any(team => team.League == leagueId && team.Seeded))
                return false;
            var establishedLeagues = state.Teams.Values
                .Where(team => team.Seeded && team.League != leagueId)
                .Select(team => team.League)
                .Distinct()
                .ToList();
            if (establishedLeagues.Count > 0)
                state.SetLeagueRating(leagueId, establishedLeagues.Average(league => state.Leagues[league])
                    + state.LeagueEntryLevels.GetValueOrDefault(leagueId));
            state.SetLeagueEnteredAt(leagueId, playedAt);
            enteredAt = playedAt;
        }
        var firstSeasonEnd = _seasonStarts.Where(start => start > enteredAt).Cast<DateTimeOffset?>().FirstOrDefault()
            ?? enteredAt.AddDays(365);
        return playedAt < firstSeasonEnd;
    }

    /// <summary>A team in a new league starts at its league's rating, whoever its players are.</summary>
    static void StartLevel(TeamRating team, IReadOnlyList<int> lineup)
    {
        team.Offset = 0;
        team.Seeded = true;
        team.RatedMaps = 0;
        team.BoostMapsLeft = 0;
        team.DecayPeriods = 0;
        team.FrozenRating = null;
        team.Lineup = lineup;
    }

    /// <summary>
    /// A new core that shares a core with another team may be that core under a new name; the
    /// admin can link the two, since a core that moves organisation keeps its rating.
    /// </summary>
    void NoteSharedCores(RatingState state, TeamRating team, DateTimeOffset playedAt, List<RosterEvent> events)
    {
        if (team.Lineup.Count == 0)
            return;
        foreach (var otherTeam in state.Teams.Values)
        {
            if (otherTeam.TeamId != team.TeamId
                && otherTeam.Lineup.Count(team.Lineup.Contains) >= Settings.RosterCoreSize)
                events.Add(new RosterEvent(playedAt, team.TeamId, RosterEventKind.SharesCore, OtherTeamId: otherTeam.TeamId));
        }
    }

    /// <summary>
    /// Past the inactivity threshold, each started period pulls the offset a share of the way
    /// toward the newcomer level. Periods already applied are not applied again.
    /// </summary>
    void Decay(TeamRating team, DateTimeOffset asOf)
    {
        if (team.LastPlayedAt is not { } lastPlayed)
            return;
        var daysPastThreshold = (asOf - lastPlayed).TotalDays - Settings.InactivityThresholdDays;
        if (daysPastThreshold <= 0)
            return;
        var periodsAway = (int)Math.Ceiling(daysPastThreshold / Settings.InactivityDecayPeriodDays);
        var periodsToApply = periodsAway - team.DecayPeriods;
        if (periodsToApply <= 0)
            return;
        var newcomerOffset = -Settings.NewcomerGap;
        team.Offset = newcomerOffset + (team.Offset - newcomerOffset) * Math.Pow(1 - Settings.InactivityDecay, periodsToApply);
        team.DecayPeriods = periodsAway;
    }

    /// <summary>Applies every season start up to <paramref name="asOf"/> not applied yet.</summary>
    void StartSeasons(RatingState state, DateTimeOffset asOf)
    {
        foreach (var seasonStart in _seasonStarts)
        {
            if (seasonStart > asOf || seasonStart <= state.SeasonStartedAt)
                continue;
            foreach (var team in state.Teams.Values.Where(team => team.Seeded && !team.IsFrozen))
                team.Offset *= 1 - Settings.SeasonStartPull;
            state.SeasonStartedAt = seasonStart;
        }
    }

    /// <summary>
    /// One extra update for a BO3 or BO5 winner, scaled by how unlikely the series win was
    /// from the ratings before its first map. A series a skipped map left undecided gets none.
    /// </summary>
    void SeriesBonus(RatingState state, List<MapRecord> maps, Dictionary<int, double> ratingsBeforeSeries)
    {
        var bestOf = maps[0].MapsInSeries;
        if (bestOf < 3 || bestOf % 2 == 0)
            return;

        var mapWins = ratingsBeforeSeries.Keys.ToDictionary(teamId => teamId, teamId => maps.Count(map =>
            map.RoundsA > map.RoundsB ? map.TeamA == teamId : map.TeamB == teamId));
        var (first, second) = (mapWins.First(), mapWins.Last());
        var (winnerId, loserId) = first.Value > second.Value ? (first.Key, second.Key) : (second.Key, first.Key);
        if (mapWins[winnerId] < bestOf / 2 + 1)
            return;

        var mapWinChance = Elo.Expected(ratingsBeforeSeries[winnerId], ratingsBeforeSeries[loserId]);
        var seriesWinChance = Elo.SeriesExpected(mapWinChance, bestOf);
        var bonus = KFactor(state, Settings.KSeries, maps[0].PlayedAt) * (1 - seriesWinChance);
        state.Team(winnerId).Offset += bonus;
        state.Team(loserId).Offset -= bonus;
    }

    /// <summary>A K value, multiplied during the warm-up season.</summary>
    double KFactor(RatingState state, double baseK, DateTimeOffset playedAt) =>
        playedAt < state.WarmUpUntil ? baseK * Settings.WarmUpKMultiplier : baseK;

    double BoostedK(double kFactor, TeamRating team) =>
        team.BoostMapsLeft > 0 ? kFactor * Settings.RosterChangeBoost : kFactor;

    /// <summary>Maps in play order, grouped by consecutive series ID.</summary>
    static IEnumerable<List<MapRecord>> BySeries(IEnumerable<MapRecord> mapsInPlayOrder)
    {
        List<MapRecord>? currentSeries = null;
        foreach (var map in mapsInPlayOrder)
        {
            if (currentSeries is not null && currentSeries[0].SeriesId != map.SeriesId)
            {
                yield return currentSeries;
                currentSeries = null;
            }
            (currentSeries ??= []).Add(map);
        }
        if (currentSeries is not null)
            yield return currentSeries;
    }
}

/// <param name="RatedMaps">Maps that moved ratings, in play order, with what each did to both teams.</param>
/// <param name="Events">Roster changes the run found, for the log and the admin.</param>
public sealed record RatingRun(IReadOnlyList<RatedMap> RatedMaps, IReadOnlyList<SkippedMap> Skipped, IReadOnlyList<RosterEvent> Events)
{
    public int Rated => RatedMaps.Count;
}

/// <summary>
/// One rated map and how it moved each team's rating (the map's own update, before the series
/// bonus and re-centring), for the team view's recent results.
/// </summary>
public sealed record RatedMap(MapRecord Map, double RatingChangeA, double RatingChangeB);

public sealed record SkippedMap(string MapId, string Reason);

public enum RosterEventKind
{
    /// <summary>A new team got its starting offset from its players.</summary>
    Seeded,
    /// <summary>Fewer than the core size stayed, so the team was seeded again as a new core.</summary>
    NewCore,
    /// <summary>The core stayed and new players joined: offset pulled, boost started.</summary>
    RosterChange,
    /// <summary>A frozen team played again and resumed its rating.</summary>
    Returned,
    /// <summary>A newly seeded lineup holds the core of <see cref="RosterEvent.OtherTeamId"/>.</summary>
    SharesCore,
}

/// <param name="NewPlayers">Players new to the core, on a roster change.</param>
/// <param name="OtherTeamId">The team whose core it shares.</param>
public sealed record RosterEvent(DateTimeOffset At, int TeamId, RosterEventKind Kind, int NewPlayers = 0, int? OtherTeamId = null);
