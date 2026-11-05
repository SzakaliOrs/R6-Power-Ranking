using Rankings.Core;

namespace Rankings.Sim;

/// <summary>
/// Builds synthetic top-level seasons: 7 leagues of 10 teams, each league with two stages
/// (a round robin, then playoffs), two majors and a world championship. International spots
/// follow the real split: EU, NA and SA 4 each and 4 shared by the APAC subregions through a
/// qualifier, plus 4 for the late league (China) once it exists. Between seasons players change
/// skill, rosters change, the last team in a league may be relegated and teams may disband.
/// The same seed always gives the same world.
/// </summary>
public sealed class Simulator
{
    static readonly (string Id, string Name)[] Leagues =
    [
        ("EU", "Europe"), ("NA", "North America"), ("SA", "South America"), ("CN", "China"),
        ("ASIA", "Asia"), ("OCE", "Oceania"), ("APAC-N", "APAC North"),
    ];

    static readonly string[] ApacSubregions = ["ASIA", "OCE", "APAC-N"];

    const int RosterSize = 5;

    const int SpotsPerRegion = 4;

    readonly SimulationOptions _options;
    readonly Random _random;
    readonly Dictionary<string, double> _leagueStrength = [];
    readonly List<Team> _teams = [];
    readonly Dictionary<int, Player> _players = [];
    readonly Dictionary<string, List<int>> _freeAgents = [];
    readonly List<SimulatedSeries> _series = [];
    readonly List<MapRecord> _maps = [];
    readonly Dictionary<string, double> _trueChances = [];
    readonly List<TeamFreeze> _freezes = [];
    readonly List<DateTimeOffset> _seasonStarts = [];
    int _nextTeamId = 1;
    int _nextPlayerId = 1;
    int _nextSeriesNumber = 1;

    sealed class Team(int id, string league, double organisation)
    {
        public int Id { get; } = id;
        public string League { get; } = league;
        public double Organisation { get; } = organisation;
        public List<int> Roster { get; } = [];
        public bool Active { get; set; } = true;
        public bool Relegated { get; set; }
    }

    sealed class Player(int id, double skill)
    {
        public int Id { get; } = id;
        public double Skill { get; set; } = skill;
    }

    Simulator(SimulationOptions options)
    {
        _options = options;
        _random = new Random(options.Seed);
    }

    public static SimulatedWorld Run(SimulationOptions? options = null) => new Simulator(options ?? new()).Build();

    SimulatedWorld Build()
    {
        foreach (var (leagueId, _) in Leagues)
        {
            _leagueStrength[leagueId] = Normal(0, _options.LeagueSpread);
            _freeAgents[leagueId] = [];
            if (leagueId == _options.LateLeague)
                continue;
            for (var slot = 0; slot < _options.TeamsPerLeague; slot++)
                NewTeam(leagueId, Normal(0, _options.OrganisationSpread), veterans: RosterSize, skillMean: 0);
        }

        for (var season = 0; season < _options.Seasons; season++)
        {
            var year = _options.FirstYear + season;
            if (season > 0)
                OffSeason(At(year - 1, 12, 1));
            // The late league starts in the latest season: established players, new to the data.
            if (season == _options.Seasons - 1 && _options.LateLeague is { } lateLeague)
                for (var slot = 0; slot < _options.TeamsPerLeague; slot++)
                    NewTeam(lateLeague, Normal(0, _options.OrganisationSpread), veterans: 0, skillMean: 0);
            PlaySeason(year);
        }

        var teamLeagues = _teams.ToDictionary(team => team.Id, team => team.League);
        var history = new History(teamLeagues, _maps, _freezes, _seasonStarts);
        var trueStrengths = _teams.Where(team => team.Active).ToDictionary(team => team.Id, Strength);
        return new SimulatedWorld(history, trueStrengths, _trueChances, _series);
    }

    void PlaySeason(int year)
    {
        _seasonStarts.Add(At(year, 1, 15));
        var seasonMapWins = _teams.Where(team => team.Active).ToDictionary(team => team.Id, _ => 0);

        var stageOne = PlayStage(year, "Stage 1", At(year, 2, 1), At(year, 4, 12), seasonMapWins);
        PlayInternational($"Major {year} - Spring", At(year, 5, 10), stageOne);

        MidSeasonChanges();
        var stageTwo = PlayStage(year, "Stage 2", At(year, 6, 8), At(year, 8, 16), seasonMapWins);
        PlayInternational($"Major {year} - Autumn", At(year, 9, 10), stageTwo);

        var seasonStandings = Leagues.ToDictionary(league => league.Id, league => _teams
            .Where(team => team.Active && team.League == league.Id)
            .OrderByDescending(team => seasonMapWins.GetValueOrDefault(team.Id))
            .ThenBy(team => team.Id)
            .ToList());
        PlayInternational($"World Championship {year}", At(year, 11, 10), seasonStandings);
    }

    /// <summary>A round robin in every league, one round a week, then semi-finals and a final for the top four.</summary>
    Dictionary<string, List<Team>> PlayStage(int year, string stage, DateTimeOffset start, DateTimeOffset playoffs,
        Dictionary<int, int> seasonMapWins)
    {
        var standings = new Dictionary<string, List<Team>>();
        for (var leagueIndex = 0; leagueIndex < Leagues.Length; leagueIndex++)
        {
            var (leagueId, leagueName) = Leagues[leagueIndex];
            var tournament = $"{leagueName} League {year} - {stage}";
            var teams = _teams.Where(team => team.Active && team.League == leagueId).ToList();
            if (teams.Count == 0)
                continue;
            // Half the leagues play their round robin as BO1, the others as BO3.
            var roundRobinBestOf = leagueIndex % 2 == 0 ? 1 : 3;
            var mapWins = teams.ToDictionary(team => team.Id, _ => 0);

            var rounds = RoundRobin(teams);
            for (var round = 0; round < rounds.Count; round++)
            {
                var matchDay = start.AddDays(7 * round);
                for (var match = 0; match < rounds[round].Count; match++)
                {
                    var (home, away) = rounds[round][match];
                    var series = PlaySeries(home, away, matchDay.AddHours(10 + 2 * match), roundRobinBestOf, tournament, "2");
                    Count(series, mapWins, seasonMapWins);
                }
            }

            var ranked = teams.OrderByDescending(team => mapWins[team.Id]).ThenBy(team => team.Id).ToList();
            var semiFinalOne = PlaySeries(ranked[0], ranked[3], playoffs.AddHours(12), 3, tournament, "2");
            var semiFinalTwo = PlaySeries(ranked[1], ranked[2], playoffs.AddHours(16), 3, tournament, "2");
            Count(semiFinalOne, mapWins, seasonMapWins);
            Count(semiFinalTwo, mapWins, seasonMapWins);
            var final = PlaySeries(Winner(semiFinalOne), Winner(semiFinalTwo), playoffs.AddDays(1).AddHours(14), 5, tournament, "2");
            Count(final, mapWins, seasonMapWins);

            // The stage winner and runner-up head the standings that qualify teams for the next event.
            var finalists = new[] { Winner(final), Loser(final) };
            standings[leagueId] = [.. finalists, .. ranked.Where(team => !finalists.Contains(team))];
        }
        return standings;
    }

    /// <summary>
    /// The top four of EU, NA, SA and the late league once it exists, and four APAC teams: the
    /// top four of a BO1 round robin between the top two of each APAC subregion a week before the
    /// event, so a subregion can send two teams, one or none.
    /// </summary>
    List<Team> Qualified(Dictionary<string, List<Team>> standings, string tournament, DateTimeOffset eventStart)
    {
        var apacCandidates = ApacSubregions.SelectMany(leagueId => standings[leagueId].Take(2)).ToList();
        var mapWins = apacCandidates.ToDictionary(team => team.Id, _ => 0);
        var qualifierDay = eventStart.AddDays(-7);
        var pairing = 0;
        for (var first = 0; first < apacCandidates.Count; first++)
            for (var second = first + 1; second < apacCandidates.Count; second++)
            {
                var series = PlaySeries(apacCandidates[first], apacCandidates[second],
                    qualifierDay.AddDays(pairing / 5).AddHours(10 + pairing % 5), 1, $"{tournament} - APAC Qualifier", "2");
                Count(series, mapWins, null);
                pairing++;
            }

        var directRegions = Leagues.Select(league => league.Id)
            .Where(leagueId => !ApacSubregions.Contains(leagueId) && standings.ContainsKey(leagueId));
        return
        [
            .. directRegions.SelectMany(leagueId => standings[leagueId].Take(SpotsPerRegion)),
            .. apacCandidates.OrderByDescending(team => mapWins[team.Id]).ThenBy(team => team.Id).Take(SpotsPerRegion),
        ];
    }

    /// <summary>
    /// Groups of four playing BO1, then the eight best (every group winner, then the best
    /// runners-up) in BO3 quarter-finals and semi-finals and a BO5 final.
    /// </summary>
    void PlayInternational(string tournament, DateTimeOffset start, Dictionary<string, List<Team>> standings)
    {
        var drawn = Qualified(standings, tournament, start).OrderBy(_ => _random.Next()).ToList();
        var groupWinners = new List<(Team First, Team Second)>();
        var groupMapWins = new Dictionary<int, int>();
        for (var group = 0; group < drawn.Count / 4; group++)
        {
            var members = drawn.Skip(4 * group).Take(4).ToList();
            var mapWins = members.ToDictionary(team => team.Id, _ => 0);
            var pairing = 0;
            for (var first = 0; first < members.Count; first++)
                for (var second = first + 1; second < members.Count; second++)
                {
                    var series = PlaySeries(members[first], members[second],
                        start.AddDays(pairing / 2).AddHours(10 + 3 * group + pairing % 2), 1, tournament, "1");
                    Count(series, mapWins, null);
                    pairing++;
                }
            var ranked = members.OrderByDescending(team => mapWins[team.Id]).ThenBy(team => team.Id).ToList();
            groupWinners.Add((ranked[0], ranked[1]));
            foreach (var member in members)
                groupMapWins[member.Id] = mapWins[member.Id];
        }

        // Seeded 1 v 8, 4 v 5, 2 v 7, 3 v 6, so the top two seeds can only meet in the final.
        var seeds = groupWinners.Select(pair => pair.First).OrderByDescending(team => groupMapWins[team.Id])
            .Concat(groupWinners.Select(pair => pair.Second).OrderByDescending(team => groupMapWins[team.Id]))
            .Take(8)
            .ToList();
        var knockoutDay = start.AddDays(4);
        Team[] quarterFinalWinners =
        [
            Winner(PlaySeries(seeds[0], seeds[7], knockoutDay.AddHours(10), 3, tournament, "1")),
            Winner(PlaySeries(seeds[3], seeds[4], knockoutDay.AddHours(14), 3, tournament, "1")),
            Winner(PlaySeries(seeds[1], seeds[6], knockoutDay.AddHours(18), 3, tournament, "1")),
            Winner(PlaySeries(seeds[2], seeds[5], knockoutDay.AddHours(22), 3, tournament, "1")),
        ];
        var semiFinalDay = knockoutDay.AddDays(1);
        var finalistOne = Winner(PlaySeries(quarterFinalWinners[0], quarterFinalWinners[1], semiFinalDay.AddHours(12), 3, tournament, "1"));
        var finalistTwo = Winner(PlaySeries(quarterFinalWinners[2], quarterFinalWinners[3], semiFinalDay.AddHours(16), 3, tournament, "1"));
        PlaySeries(finalistOne, finalistTwo, semiFinalDay.AddDays(1).AddHours(14), 5, tournament, "1");
    }

    SimulatedSeries PlaySeries(Team teamA, Team teamB, DateTimeOffset playedAt, int bestOf, string tournament, string tier)
    {
        var seriesId = $"SIM{_nextSeriesNumber++:D6}";
        var chanceTeamAWins = Elo.Expected(Strength(teamA), Strength(teamB));
        var mapsToWin = bestOf / 2 + 1;
        var maps = new List<(int RoundsA, int RoundsB)>();
        var (winsA, winsB) = (0, 0);
        while (winsA < mapsToWin && winsB < mapsToWin)
        {
            var score = MapScores.Play(chanceTeamAWins, _random);
            maps.Add(score);
            if (score.RoundsA > score.RoundsB)
                winsA++;
            else
                winsB++;
        }

        var series = new SimulatedSeries(seriesId, playedAt, bestOf, tournament, tier, Side(teamA), Side(teamB), maps);
        _series.Add(series);
        for (var number = 1; number <= maps.Count; number++)
        {
            var mapId = $"{seriesId}/{number}";
            _maps.Add(new MapRecord(mapId, seriesId, playedAt, number, bestOf, teamA.Id, teamB.Id,
                maps[number - 1].RoundsA, maps[number - 1].RoundsB, series.TeamA.PlayerIds, series.TeamB.PlayerIds));
            _trueChances[mapId] = chanceTeamAWins;
        }
        return series;
    }

    /// <summary>
    /// Between seasons: skills drift, each league's last team may be relegated (replaced by a
    /// promoted or returning team), teams may disband, rosters change and unsigned players may retire.
    /// </summary>
    void OffSeason(DateTimeOffset at)
    {
        foreach (var player in _players.Values)
            player.Skill += Normal(0, _options.SkillDriftPerSeason);

        var lastSeasonMaps = _maps.Where(map => map.PlayedAt.Year == at.Year).ToList();
        var newTeams = new List<Team>();
        foreach (var (leagueId, _) in Leagues)
        {
            var active = _teams.Where(team => team.Active && team.League == leagueId).ToList();
            if (active.Count == 0)
                continue;
            var last = active.OrderBy(team => MapWins(lastSeasonMaps, team.Id)).ThenBy(team => team.Id).First();
            if (_random.NextDouble() < _options.RelegationChance)
            {
                Freeze(last, at, relegated: true);
                newTeams.Add(Promote(leagueId));
            }

            // The relegated team is no longer active, so it cannot also disband.
            foreach (var team in active.Where(team => team.Active))
            {
                if (_random.NextDouble() >= _options.DisbandChance)
                    continue;
                Freeze(team, at, relegated: false);
                _freeAgents[leagueId].AddRange(team.Roster);
                team.Roster.Clear();
                newTeams.Add(NewTeam(leagueId, Normal(-20, _options.OrganisationSpread), veterans: 2, skillMean: _options.NewcomerSkill));
            }
        }

        foreach (var team in _teams.Where(team => team.Active && !newTeams.Contains(team)))
        {
            var roll = _random.NextDouble();
            var changes = roll < _options.NewCoreChance ? 3
                : roll < _options.NewCoreChance + _options.TwoChangesChance ? 2
                : roll < _options.NewCoreChance + _options.TwoChangesChance + _options.OneChangeChance ? 1
                : 0;
            ChangePlayers(team, changes);
        }

        // Players nobody signed may retire.
        foreach (var pool in _freeAgents.Values)
            pool.RemoveAll(_ => _random.NextDouble() < 0.5);
    }

    void MidSeasonChanges()
    {
        foreach (var team in _teams.Where(team => team.Active))
            if (_random.NextDouble() < _options.MidSeasonChangeChance)
                ChangePlayers(team, 1);
    }

    /// <summary>The relegation spot goes to an earlier relegated team or to a newly promoted one.</summary>
    Team Promote(string leagueId)
    {
        var relegated = _teams.Where(team => team.Relegated && team.League == leagueId && team.Roster.Count == RosterSize).ToList();
        if (relegated.Count > 0 && _random.NextDouble() < _options.ReturnChance)
        {
            var returning = relegated[_random.Next(relegated.Count)];
            returning.Active = true;
            returning.Relegated = false;
            ChangePlayers(returning, _random.NextDouble() < 0.5 ? 1 : 0);
            return returning;
        }
        return NewTeam(leagueId, Normal(-20, _options.OrganisationSpread), veterans: _random.Next(3), skillMean: _options.NewcomerSkill);
    }

    void Freeze(Team team, DateTimeOffset at, bool relegated)
    {
        team.Active = false;
        team.Relegated = relegated;
        _freezes.Add(new TeamFreeze(at, team.Id));
    }

    /// <summary>A team of up to <paramref name="veterans"/> free agents from its league, filled up with newcomers.</summary>
    Team NewTeam(string leagueId, double organisation, int veterans, double skillMean)
    {
        var team = new Team(_nextTeamId++, leagueId, organisation);
        var pool = _freeAgents[leagueId];
        for (var signed = 0; signed < veterans && pool.Count > 0; signed++)
            team.Roster.Add(TakeAt(pool, _random.Next(pool.Count)));
        while (team.Roster.Count < RosterSize)
            team.Roster.Add(NewPlayer(skillMean));
        _teams.Add(team);
        return team;
    }

    /// <summary>Replaces players: each leaves for the free-agent pool; a free agent or a newcomer joins.</summary>
    void ChangePlayers(Team team, int changes)
    {
        var pool = _freeAgents[team.League];
        for (var change = 0; change < changes; change++)
        {
            var leaving = TakeAt(team.Roster, _random.Next(team.Roster.Count));
            var joining = pool.Count > 0 && _random.NextDouble() < 0.6
                ? TakeAt(pool, _random.Next(pool.Count))
                : NewPlayer(_options.NewcomerSkill);
            pool.Add(leaving);
            team.Roster.Add(joining);
        }
    }

    int NewPlayer(double skillMean)
    {
        var player = new Player(_nextPlayerId++, Normal(skillMean, _options.PlayerSpread));
        _players.Add(player.Id, player);
        return player.Id;
    }

    double Strength(Team team) =>
        _leagueStrength[team.League] + team.Organisation + team.Roster.Average(playerId => _players[playerId].Skill);

    SimulatedSide Side(Team team) =>
        new(team.Id, $"sim team {team.Id}", $"{team.League} Team {team.Id}", [.. team.Roster]);

    Team Winner(SimulatedSeries series) => TeamById(WonByA(series) ? series.TeamA.TeamId : series.TeamB.TeamId);

    Team Loser(SimulatedSeries series) => TeamById(WonByA(series) ? series.TeamB.TeamId : series.TeamA.TeamId);

    Team TeamById(int teamId) => _teams.First(team => team.Id == teamId);

    static bool WonByA(SimulatedSeries series) =>
        series.Maps.Count(map => map.RoundsA > map.RoundsB) > series.Maps.Count(map => map.RoundsB > map.RoundsA);

    static void Count(SimulatedSeries series, Dictionary<int, int> mapWins, Dictionary<int, int>? seasonMapWins)
    {
        foreach (var (roundsA, roundsB) in series.Maps)
        {
            var winnerId = roundsA > roundsB ? series.TeamA.TeamId : series.TeamB.TeamId;
            mapWins[winnerId]++;
            if (seasonMapWins is not null)
                seasonMapWins[winnerId] = seasonMapWins.GetValueOrDefault(winnerId) + 1;
        }
    }

    static int MapWins(List<MapRecord> maps, int teamId) =>
        maps.Count(map => map.RoundsA > map.RoundsB ? map.TeamA == teamId : map.TeamB == teamId);

    /// <summary>Pairings for a single round robin by the circle method: n − 1 rounds of n / 2 matches.</summary>
    static List<List<(Team Home, Team Away)>> RoundRobin(List<Team> teams)
    {
        var circle = new List<Team?>(teams);
        if (circle.Count % 2 == 1)
            circle.Add(null);
        var rounds = new List<List<(Team, Team)>>();
        for (var round = 0; round < circle.Count - 1; round++)
        {
            var matches = new List<(Team, Team)>();
            for (var pair = 0; pair < circle.Count / 2; pair++)
                if (circle[pair] is { } home && circle[circle.Count - 1 - pair] is { } away)
                    matches.Add((home, away));
            rounds.Add(matches);
            var moved = circle[^1];
            circle.RemoveAt(circle.Count - 1);
            circle.Insert(1, moved);
        }
        return rounds;
    }

    static int TakeAt(List<int> list, int index)
    {
        var value = list[index];
        list.RemoveAt(index);
        return value;
    }

    /// <summary>A normally distributed value (Box–Muller).</summary>
    double Normal(double mean, double spread)
    {
        var uniform = 1 - _random.NextDouble();
        var angle = 2 * Math.PI * _random.NextDouble();
        return mean + spread * Math.Sqrt(-2 * Math.Log(uniform)) * Math.Cos(angle);
    }

    static DateTimeOffset At(int year, int month, int day) => new(year, month, day, 0, 0, 0, TimeSpan.Zero);
}
