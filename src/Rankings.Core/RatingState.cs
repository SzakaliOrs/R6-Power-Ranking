namespace Rankings.Core;

/// <summary>
/// Everything the engine carries from one run to the next: league ratings, team offsets and
/// the players' last offsets. A team's rating is its league's rating plus its offset. Every
/// value is plain data, so the daily job can save it to rankings.db and load it on the next runner.
/// </summary>
public sealed class RatingState
{
    readonly Dictionary<string, double> _leagues = new(StringComparer.Ordinal);
    readonly Dictionary<int, TeamRating> _teams = [];
    readonly Dictionary<int, PlayerRating> _players = [];

    public IReadOnlyDictionary<string, double> Leagues => _leagues;

    public IReadOnlyDictionary<int, TeamRating> Teams => _teams;

    /// <summary>Each player's last rated team and its offset, for seeding the player's next team.</summary>
    public IReadOnlyDictionary<int, PlayerRating> Players => _players;

    /// <summary>The latest season start already applied (its offset pull done).</summary>
    public DateTimeOffset? SeasonStartedAt { get; set; }

    /// <summary>Maps before this instant use the warm-up K multiplier; set for the first replayed season.</summary>
    public DateTimeOffset? WarmUpUntil { get; set; }

    public void AddLeague(string leagueId, double rating) => _leagues.Add(leagueId, rating);

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

    TeamRating Add(TeamRating team)
    {
        if (!_leagues.ContainsKey(team.League))
            throw new KeyNotFoundException($"League '{team.League}' is not in the rating state.");
        _teams.Add(team.TeamId, team);
        return team;
    }
}

/// <summary>One roster core's place in the ratings. Its league never changes.</summary>
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

    public bool IsFrozen => FrozenRating is not null;

    public bool IsProvisional(RatingSettings settings) => RatedMaps < settings.ProvisionalUntilMaps;
}

/// <param name="TeamId">The player's last rated team.</param>
/// <param name="Offset">That team's offset at the end of the run the player last played in.</param>
public sealed record PlayerRating(int TeamId, double Offset);
