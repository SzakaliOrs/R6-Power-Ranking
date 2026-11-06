namespace Rankings.Core;

/// <summary>
/// Everything the engine carries from one run to the next: league ratings, team offsets and
/// the players' last offsets. A team's rating is its league's rating plus its offset. Every
/// value is plain data, so the daily job can save it to rankings.db and load it on the next runner.
/// </summary>
public sealed class RatingState
{
    readonly Dictionary<string, double> _leagues = new(StringComparer.Ordinal);
    readonly Dictionary<string, double> _leagueEntryLevels = new(StringComparer.Ordinal);
    readonly Dictionary<string, DateTimeOffset> _leagueEnteredAt = new(StringComparer.Ordinal);
    readonly Dictionary<int, TeamRating> _teams = [];
    readonly Dictionary<int, PlayerRating> _players = [];

    public IReadOnlyDictionary<string, double> Leagues => _leagues;

    /// <summary>
    /// Admin input for a league that enters later (China in 2026): points above or below the
    /// average established league that it starts at. A league without one starts at the average.
    /// </summary>
    public IReadOnlyDictionary<string, double> LeagueEntryLevels => _leagueEntryLevels;

    /// <summary>When each league that entered after the start had its first team rated.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> LeagueEnteredAt => _leagueEnteredAt;

    public IReadOnlyDictionary<int, TeamRating> Teams => _teams;

    /// <summary>Each player's last rated team and its offset, for seeding the player's next team.</summary>
    public IReadOnlyDictionary<int, PlayerRating> Players => _players;

    /// <summary>The latest season start already applied (its offset pull done).</summary>
    public DateTimeOffset? SeasonStartedAt { get; set; }

    /// <summary>Maps before this instant use the warm-up K multiplier; set for the first replayed season.</summary>
    public DateTimeOffset? WarmUpUntil { get; set; }

    /// <summary>When the first rated day's first map was played; null until then. Set by <see cref="DailyRating"/>.</summary>
    public DateTimeOffset? FirstMapAt { get; set; }

    public void AddLeague(string leagueId, double rating) => _leagues.Add(leagueId, rating);

    public void SetLeagueEntryLevel(string leagueId, double level) => _leagueEntryLevels[leagueId] = level;

    /// <summary>Records when a league entered, e.g. when loading the state from the database.</summary>
    public void SetLeagueEnteredAt(string leagueId, DateTimeOffset enteredAt) => _leagueEnteredAt[leagueId] = enteredAt;

    /// <summary>Adds a team that already has a rating, e.g. loaded from the database or at the replay start.</summary>
    public TeamRating AddTeam(int teamId, string leagueId, double offset = 0) =>
        Add(new TeamRating(teamId, leagueId) { Offset = offset, Seeded = true });

    /// <summary>Adds a team with no rating yet; it is seeded from its players on its first map.</summary>
    public TeamRating AddNewTeam(int teamId, string leagueId) => Add(new TeamRating(teamId, leagueId));

    public void SetPlayer(int playerId, PlayerRating rating) => _players[playerId] = rating;

    public double Rating(int teamId) => Rating(Team(teamId));

    /// <summary>A frozen team keeps the rating it was frozen at, whatever its league does since.</summary>
    public double Rating(TeamRating team) => team.FrozenRating ?? _leagues[team.League] + team.Offset;

    public TeamRating Team(int teamId) =>
        _teams.TryGetValue(teamId, out var team)
            ? team
            : throw new KeyNotFoundException($"Team {teamId} is not in the rating state.");

    internal void MoveLeague(string leagueId, double change) => _leagues[leagueId] += change;

    internal void SetLeagueRating(string leagueId, double rating) => _leagues[leagueId] = rating;

    TeamRating Add(TeamRating team)
    {
        if (!_leagues.ContainsKey(team.League))
            throw new KeyNotFoundException($"League '{team.League}' is not in the rating state.");
        _teams.Add(team.TeamId, team);
        return team;
    }
}

/// <summary>
/// One team's place in the ratings. The rating belongs to its current roster core: a new core
/// starts a new rating under the same ID, and a core another team signs takes the rating along.
/// Its league never changes.
/// </summary>
public sealed class TeamRating(int teamId, string league)
{
    public int TeamId { get; } = teamId;

    public string League { get; } = league;

    public double Offset { get; set; }

    /// <summary>False until the team's first map gives it a starting offset from its players.</summary>
    public bool Seeded { get; set; }

    public int RatedMaps { get; set; }

    /// <summary>Maps left on the roster-change boost.</summary>
    public int BoostMapsLeft { get; set; }

    public DateTimeOffset? LastPlayedAt { get; set; }

    /// <summary>Inactivity decay periods already applied since <see cref="LastPlayedAt"/>.</summary>
    public int DecayPeriods { get; set; }

    /// <summary>Total rating kept while relegated or disbanded; null while the team is rated.</summary>
    public double? FrozenRating { get; set; }

    /// <summary>Players listed on the team's last rated map: the core the next lineup is compared with.</summary>
    public IReadOnlyList<int> Lineup { get; set; } = [];

    /// <summary>
    /// Counts the cores the team has had: it goes up whenever the team starts a new one (seeded, seeded
    /// again, or taking over another team's core) and when another team takes its core. A player whose
    /// record holds an older number played in a core the team has since left behind.
    /// </summary>
    public int CoreGeneration { get; set; }

    public bool IsFrozen => FrozenRating is not null;

    public bool IsProvisional(RatingSettings settings) => RatedMaps < settings.ProvisionalUntilMaps;
}

/// <param name="TeamId">The player's last rated team.</param>
/// <param name="Offset">That team's offset at the end of the run the player last played in.</param>
/// <param name="CoreGeneration">That team's <see cref="TeamRating.CoreGeneration"/> then: which of its cores the player was in.</param>
/// <param name="RatedMaps">That team's rated maps then, carried over when the core moves to another team.</param>
/// <param name="LastPlayedAt">That team's last rated map then, so a core that moves after time away decays for it.</param>
public sealed record PlayerRating(int TeamId, double Offset, int CoreGeneration = 0, int RatedMaps = 0, DateTimeOffset? LastPlayedAt = null);
