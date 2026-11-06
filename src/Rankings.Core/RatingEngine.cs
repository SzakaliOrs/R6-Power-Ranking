namespace Rankings.Core;

/// <summary>
/// Rates maps in play order. A map within one league moves the two teams' offsets with
/// K_reg; a map between two leagues moves their offsets with K_int and both league
/// ratings with K_league. A BO3 or BO5 winner gets one extra update. Before a team's map
/// the engine seeds it, brings it back from a freeze, decays it for inactivity and checks
/// its lineup against its roster core. A rating belongs to a core: a team that signs another
/// team's core takes over its rating. After each run every league's offsets are re-centred on zero.
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
                state.SetPlayer(playerId, new PlayerRating(team.TeamId, team.Offset, team.CoreGeneration, team.RatedMaps, team.LastPlayedAt));

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
    /// players carry their last offsets into their next teams (a team that signs its core takes
    /// over the rating). It comes back if it plays again.
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
            // A team that has played but has no rating lost its core to another team.
            var kind = team.LastPlayedAt is null ? RosterEventKind.Seeded : RosterEventKind.NewCore;
            // Asked even when the team takes over a core: a new league's first team sets the league's rating.
            var inNewLeague = InNewLeague(state, team.League, playedAt);
            if (!TakeOverCore(state, team, lineup, playedAt, events))
            {
                if (inNewLeague)
                    StartLevel(team, lineup);
                else
                    Seed(state, team, lineup, playedAt);
                events.Add(new RosterEvent(playedAt, team.TeamId, kind));
            }
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
            // Too few of the core stayed: a new team under the same ID, with the rating of the core
            // it signed, if any, else seeded from its players.
            if (!TakeOverCore(state, team, lineup, playedAt, events))
            {
                Seed(state, team, lineup, playedAt);
                events.Add(new RosterEvent(playedAt, team.TeamId, RosterEventKind.NewCore));
            }
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
    /// Starting offset from the players: a share of each one's last offset, decayed for the time
    /// since that team last played as inactivity decay would have, or the newcomer gap below the
    /// league rating for a player with no rated history.
    /// </summary>
    void Seed(RatingState state, TeamRating team, IReadOnlyList<int> lineup, DateTimeOffset playedAt)
    {
        team.Offset = lineup.Count == 0
            ? -Settings.NewcomerGap
            : lineup.Average(playerId => state.Players.TryGetValue(playerId, out var lastRating)
                ? Settings.SeedingShare * (lastRating.LastPlayedAt is { } lastPlayed
                    ? Decayed(lastRating.Offset, DecayPeriodsAway(lastPlayed, playedAt))
                    : lastRating.Offset)
                : -Settings.NewcomerGap);
        team.Seeded = true;
        team.RatedMaps = 0;
        team.BoostMapsLeft = 0;
        team.DecayPeriods = 0;
        team.FrozenRating = null;
        team.Lineup = lineup;
        team.CoreGeneration++;
    }

    /// <summary>
    /// When at least the core size of the lineup last played together in another team's core, the
    /// team takes over that core's rating instead of being seeded: the rating follows the players
    /// when an organisation signs another team's core. Each player not from that core pulls the
    /// offset part-way toward the league average, as on a roster change, and the boost starts. A
    /// team still holding the core loses it and has no rating until it plays again. A team that
    /// carried on with a core of other players keeps its rating, and the lineup is seeded as usual.
    /// </summary>
    /// <returns>Whether the team took over a core.</returns>
    bool TakeOverCore(RatingState state, TeamRating team, IReadOnlyList<int> lineup, DateTimeOffset playedAt, List<RosterEvent> events)
    {
        var movedCore = lineup
            .Where(state.Players.ContainsKey)
            .Select(playerId => (PlayerId: playerId, Record: state.Players[playerId]))
            .Where(player => player.Record.TeamId != team.TeamId && state.Teams.ContainsKey(player.Record.TeamId))
            .GroupBy(player => (player.Record.TeamId, player.Record.CoreGeneration))
            .Where(group => group.Count() >= Settings.RosterCoreSize)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key.TeamId)
            .ThenByDescending(group => group.Key.CoreGeneration)
            .FirstOrDefault();
        if (movedCore is null)
            return false;

        var formerTeam = state.Team(movedCore.Key.TeamId);
        double coreRating;
        int coreRatedMaps;
        if (movedCore.Key.CoreGeneration == formerTeam.CoreGeneration)
        {
            // Still the former team's core, unless it carried on without these players.
            var corePlayers = movedCore.Select(player => player.PlayerId).ToHashSet();
            if (formerTeam.Lineup.Count(corePlayers.Contains) < Settings.RosterCoreSize)
                return false;
            // A frozen core decays for the time away, as it would on returning to its own team.
            if (formerTeam.IsFrozen)
            {
                formerTeam.Offset = formerTeam.FrozenRating!.Value - state.Leagues[formerTeam.League];
                formerTeam.FrozenRating = null;
            }
            Decay(formerTeam, playedAt);
            coreRating = state.Rating(formerTeam);
            coreRatedMaps = formerTeam.RatedMaps;
            LoseCore(formerTeam);
            events.Add(new RosterEvent(playedAt, formerTeam.TeamId, RosterEventKind.CoreLost, OtherTeamId: team.TeamId));
        }
        else
        {
            // The former team has started a new core since: the players bring the rating they left
            // with, decayed for the time since the core last played as a team would be.
            var coreOffset = movedCore.Average(player => player.Record.Offset);
            if (movedCore.Max(player => player.Record.LastPlayedAt) is { } corePlayedAt)
                coreOffset = Decayed(coreOffset, DecayPeriodsAway(corePlayedAt, playedAt));
            coreRating = state.Leagues[formerTeam.League] + coreOffset;
            coreRatedMaps = movedCore.Max(player => player.Record.RatedMaps);
        }

        // Rating, not offset, so a core that moves to another league keeps its strength.
        var newPlayers = lineup.Count - movedCore.Count();
        team.Offset = (coreRating - state.Leagues[team.League]) * Math.Pow(1 - Settings.NewPlayerPull, newPlayers);
        team.Seeded = true;
        team.RatedMaps = coreRatedMaps;
        team.BoostMapsLeft = Settings.RosterChangeBoostMaps;
        team.DecayPeriods = 0;
        team.FrozenRating = null;
        team.Lineup = lineup;
        team.CoreGeneration++;

        // The core's rating lives here now. Its other players (a provider can list six) point here
        // too, so no second team that signs some of them takes the same rating over again.
        var holder = new PlayerRating(team.TeamId, team.Offset, team.CoreGeneration, team.RatedMaps, playedAt);
        var formerCorePlayers = state.Players
            .Where(player => player.Value.TeamId == formerTeam.TeamId && player.Value.CoreGeneration == movedCore.Key.CoreGeneration)
            .Select(player => player.Key)
            .ToList();
        foreach (var playerId in formerCorePlayers)
            state.SetPlayer(playerId, holder);

        events.Add(new RosterEvent(playedAt, team.TeamId, RosterEventKind.TookOverCore, newPlayers, formerTeam.TeamId));
        return true;
    }

    /// <summary>
    /// The team's core went to another team, which took the rating: the team leaves the ranking
    /// and its league's average, and is seeded from its players when it plays again.
    /// </summary>
    static void LoseCore(TeamRating team)
    {
        team.Seeded = false;
        team.FrozenRating = null;
        team.BoostMapsLeft = 0;
        team.Lineup = [];
        team.CoreGeneration++;
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
        team.CoreGeneration++;
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
        var periodsAway = DecayPeriodsAway(lastPlayed, asOf);
        var periodsToApply = periodsAway - team.DecayPeriods;
        if (periodsToApply <= 0)
            return;
        team.Offset = Decayed(team.Offset, periodsToApply);
        team.DecayPeriods = periodsAway;
    }

    /// <summary>Decay periods started between <paramref name="lastPlayed"/> and <paramref name="asOf"/>, past the threshold.</summary>
    int DecayPeriodsAway(DateTimeOffset lastPlayed, DateTimeOffset asOf)
    {
        var daysPastThreshold = (asOf - lastPlayed).TotalDays - Settings.InactivityThresholdDays;
        return daysPastThreshold <= 0 ? 0 : (int)Math.Ceiling(daysPastThreshold / Settings.InactivityDecayPeriodDays);
    }

    /// <summary>The offset after <paramref name="periods"/> decay periods, each closing a share of the gap to the newcomer level.</summary>
    double Decayed(double offset, int periods)
    {
        var newcomerOffset = -Settings.NewcomerGap;
        return newcomerOffset + (offset - newcomerOffset) * Math.Pow(1 - Settings.InactivityDecay, periods);
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
    /// <summary>
    /// Fewer than the core size stayed, so the team was seeded again as a new core; or a team that
    /// lost its core to another team was seeded from its new players.
    /// </summary>
    NewCore,
    /// <summary>The core stayed and new players joined: offset pulled, boost started.</summary>
    RosterChange,
    /// <summary>A frozen team played again and resumed its rating.</summary>
    Returned,
    /// <summary>A newly seeded lineup holds the core of <see cref="RosterEvent.OtherTeamId"/>.</summary>
    SharesCore,
    /// <summary>The team signed the core of <see cref="RosterEvent.OtherTeamId"/> and took over its rating.</summary>
    TookOverCore,
    /// <summary>
    /// <see cref="RosterEvent.OtherTeamId"/> took over the team's core and rating; the team is
    /// unrated until it plays again.
    /// </summary>
    CoreLost,
}

/// <param name="NewPlayers">Players new to the core, on a roster change or a core takeover.</param>
/// <param name="OtherTeamId">The team whose core it shares, took over, or that took its core.</param>
public sealed record RosterEvent(DateTimeOffset At, int TeamId, RosterEventKind Kind, int NewPlayers = 0, int? OtherTeamId = null);
