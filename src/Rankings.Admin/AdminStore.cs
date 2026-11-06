using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Rankings.Job.Storage;

namespace Rankings.Admin;

public sealed record AdminTeam(
    int TeamId, string League, string Status, string CreatedOn,
    string? Name, string? ShortName, string? Logo, IReadOnlyList<string> ProviderKeys, string? LogoDark = null);

public sealed record AdminPlayer(int PlayerId, string Name, IReadOnlyList<string> ProviderKeys);

public sealed record AdminSeason(string StartsOn, string Name);

public sealed record AdminRegion(string RegionId, string Name, string? ParentId, double? Rating, double? EntryLevel);

public sealed record AdminRankedTeam(
    int? Rank, int TeamId, string? Name, string? ShortName, string League, string Status,
    double Rating, double Offset, double LeagueRating, double? WeekChange, int MapsPlayed, bool Provisional);

public sealed record AdminLeagueRating(string RegionId, string Name, string? ParentId, double Rating, double? WeekChange);

/// <summary>The ratings a day's run saved; Date is null when nothing is rated yet.</summary>
public sealed record AdminRankings(
    string? Date, string? FirstDate, string? LastDate, IReadOnlyList<AdminRankedTeam> Teams, IReadOnlyList<AdminLeagueRating> Leagues);

public sealed record AdminSkippedMap(string MapId, string? SeriesId, DateTimeOffset? PlayedAt, string Reason, DateTimeOffset LoggedAt);

/// <summary>A refused admin change, shown to the admin as it is.</summary>
public sealed class AdminException(string message) : Exception(message);

/// <summary>
/// The manual inputs the admin dashboard edits in the local rankings.db: team assignments,
/// display names and logos, team status, player relinks, seasons and league entry levels.
/// Opens the file for each change and closes it again, so it never holds the file locked.
/// </summary>
public sealed class AdminStore(string databasePath)
{
    public const string StatusOverride = "status";

    public string DatabasePath { get; } = databasePath;

    public bool DatabaseExists => File.Exists(DatabasePath);

    RankingsDb Open() =>
        DatabaseExists
            ? RankingsDb.Open(DatabasePath)
            : throw new AdminException($"There is no rankings.db at {DatabasePath}. Pull it from R2 first.");

    // Teams

    public List<UnassignedTeam> UnassignedTeams()
    {
        using var db = Open();
        return new ProviderIds(db).UnassignedTeams();
    }

    public List<AdminTeam> Teams()
    {
        using var db = Open();
        var keys = Rows(db, "SELECT team_id, provider_key FROM provider_teams WHERE team_id IS NOT NULL ORDER BY provider_key",
                reader => (TeamId: reader.GetInt32(0), Key: reader.GetString(1)))
            .ToLookup(row => row.TeamId, row => row.Key);
        // The display row in effect today: the latest one that has started.
        return Rows(db, """
            SELECT t.team_id, t.region_id, t.status, t.created_on, d.name, d.short_name, d.logo, d.logo_dark
            FROM teams t
            LEFT JOIN team_display d ON d.team_id = t.team_id AND d.valid_from = (
                SELECT MAX(valid_from) FROM team_display WHERE team_id = t.team_id AND valid_from <= date('now'))
            ORDER BY t.region_id, d.name, t.team_id
            """,
            reader => new AdminTeam(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                NullableString(reader, 4), NullableString(reader, 5), NullableString(reader, 6), [.. keys[reader.GetInt32(0)]],
                NullableString(reader, 7)));
    }

    /// <summary>
    /// The rankings as the local runs saved them for a day (default: the latest rated day), with
    /// the names in use that day and the change against the same day a week earlier.
    /// </summary>
    public AdminRankings Rankings(string? date)
    {
        if (date is not null)
            RequireDate(date, "The date");
        using var db = Open();
        var firstDate = Scalar(db, null, "SELECT MIN(date) FROM team_snapshots") as string;
        var lastDate = Scalar(db, null, "SELECT MAX(date) FROM team_snapshots") as string;
        if (lastDate is null)
            return new AdminRankings(null, null, null, [], []);
        // A day without a snapshot shows the latest rated day before it.
        var shownDate = date is null
            ? lastDate
            : Scalar(db, null, "SELECT MAX(date) FROM team_snapshots WHERE date <= $date", ("$date", date)) as string ?? firstDate!;

        var provisionalUntilMaps = new global::Rankings.Core.RatingSettings().ProvisionalUntilMaps;
        var teams = Rows(db, """
            SELECT s.rank, s.team_id, d.name, d.short_name, t.region_id, t.status,
                   s.rating, s.rating_offset, s.region_rating, s.rating - w.rating, s.maps_played
            FROM team_snapshots s
            JOIN teams t ON t.team_id = s.team_id
            -- The name in use that day; before a team's first name starts, that first name.
            LEFT JOIN team_display d ON d.team_id = s.team_id AND d.valid_from = COALESCE(
                (SELECT MAX(valid_from) FROM team_display WHERE team_id = s.team_id AND valid_from <= s.date),
                (SELECT MIN(valid_from) FROM team_display WHERE team_id = s.team_id))
            LEFT JOIN team_snapshots w ON w.team_id = s.team_id AND w.date = date(s.date, '-7 days')
            WHERE s.date = $date
            ORDER BY s.rank IS NULL, s.rank, s.rating DESC
            """,
            reader => new AdminRankedTeam(
                reader.IsDBNull(0) ? null : reader.GetInt32(0), reader.GetInt32(1), NullableString(reader, 2), NullableString(reader, 3),
                reader.GetString(4), reader.GetString(5), reader.GetDouble(6), reader.GetDouble(7), reader.GetDouble(8),
                reader.IsDBNull(9) ? null : reader.GetDouble(9), reader.GetInt32(10), reader.GetInt32(10) < provisionalUntilMaps),
            ("$date", shownDate));
        var leagues = Rows(db, """
            SELECT r.region_id, r.name, r.parent_id, s.rating, s.rating - w.rating
            FROM region_snapshots s
            JOIN regions r ON r.region_id = s.region_id
            LEFT JOIN region_snapshots w ON w.region_id = s.region_id AND w.date = date(s.date, '-7 days')
            WHERE s.date = $date
            ORDER BY s.rating DESC
            """,
            reader => new AdminLeagueRating(reader.GetString(0), reader.GetString(1), NullableString(reader, 2), reader.GetDouble(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4)),
            ("$date", shownDate));
        return new AdminRankings(shownDate, firstDate, lastDate, teams, leagues);
    }

    /// <summary>
    /// A new team (roster core) in a league, optionally linking a provider key to it at once.
    /// Also how an organisation moving its name to another region is handled: a new team there.
    /// </summary>
    public int CreateTeam(string league, string name, string? shortName, string createdOn, string? providerKey)
    {
        RequireDate(createdOn, "The start date");
        RequireText(name, "The team name");
        using var db = Open();
        if (!string.IsNullOrWhiteSpace(providerKey)
            && Scalar(db, null, "SELECT COUNT(*) FROM provider_teams WHERE provider_key = $key", ("$key", providerKey)) is 0L)
            throw new AdminException($"No team has been seen with provider key '{providerKey}'.");
        using var tx = db.Connection.BeginTransaction();
        int teamId;
        try
        {
            teamId = Convert.ToInt32(Scalar(db, tx, "INSERT INTO teams (region_id, created_on) VALUES ($league, $created) RETURNING team_id",
                ("$league", league), ("$created", createdOn)));
        }
        catch (SqliteException error) when (error.SqliteErrorCode is 19 or 1)
        {
            throw new AdminException($"'{league}' is not a league a team can be in (APAC teams go in Asia, Oceania or APAC North).");
        }
        Execute(db, tx, "INSERT INTO team_display (team_id, valid_from, name, short_name) VALUES ($team, $from, $name, $short)",
            ("$team", teamId), ("$from", createdOn), ("$name", name.Trim()), ("$short", Blank(shortName)));
        tx.Commit();
        if (!string.IsNullOrWhiteSpace(providerKey))
            new ProviderIds(db).LinkTeam(providerKey, teamId);
        return teamId;
    }

    /// <summary>
    /// Corrects the league a team was assigned to by mistake. (An organisation that really moves
    /// region fields a new team instead.) Returns true when the team has been rated already: its
    /// history was rated in the old league, so only a full recalculation puts it right.
    /// </summary>
    public bool SetLeague(int teamId, string league)
    {
        using var db = Open();
        RequireTeam(db, teamId);
        try
        {
            Execute(db, null, "UPDATE teams SET region_id = $league WHERE team_id = $team", ("$league", league), ("$team", teamId));
        }
        catch (SqliteException error) when (error.SqliteErrorCode is 19 or 1)
        {
            throw new AdminException($"'{league}' is not a league a team can be in (APAC teams go in Asia, Oceania or APAC North).");
        }
        return Scalar(db, null, """
            SELECT EXISTS (SELECT 1 FROM team_snapshots WHERE team_id = $team)
                OR EXISTS (SELECT 1 FROM team_ratings WHERE team_id = $team)
            """, ("$team", teamId)) is 1L;
    }

    /// <summary>
    /// Folds a team that was assigned separately by mistake into the team it should be part of:
    /// its Liquipedia keys and maps move over, and the empty team is deleted. The kept team keeps
    /// its own league, names and status. Returns true when the merged team had been rated already,
    /// so only a full recalculation re-rates the joined history.
    /// </summary>
    public bool MergeTeams(int fromTeamId, int intoTeamId)
    {
        if (fromTeamId == intoTeamId)
            throw new AdminException("A team cannot be merged into itself.");
        using var db = Open();
        RequireTeam(db, fromTeamId);
        RequireTeam(db, intoTeamId);
        foreach (var table in (string[])["maps", "rated_maps"])
            if (Scalar(db, null, $"""
                    SELECT MIN(played_at) FROM {table}
                    WHERE (team_a = $from AND team_b = $into) OR (team_a = $into AND team_b = $from)
                    """, ("$from", fromTeamId), ("$into", intoTeamId)) is long playedAt)
                throw new AdminException($"Teams {fromTeamId} and {intoTeamId} played each other on "
                    + $"{DateTimeOffset.FromUnixTimeSeconds(playedAt):yyyy-MM-dd}, so they are two different teams.");

        var wasRated = Scalar(db, null, """
            SELECT EXISTS (SELECT 1 FROM team_snapshots WHERE team_id = $from)
                OR EXISTS (SELECT 1 FROM team_ratings WHERE team_id = $from)
                OR EXISTS (SELECT 1 FROM rated_maps WHERE team_a = $from OR team_b = $from)
            """, ("$from", fromTeamId)) is 1L;

        using var tx = db.Connection.BeginTransaction();
        foreach (var sql in (string[])[
            "UPDATE provider_teams SET team_id = $into WHERE team_id = $from",
            "UPDATE maps SET team_a = $into WHERE team_a = $from",
            "UPDATE maps SET team_b = $into WHERE team_b = $from",
            "UPDATE rated_maps SET team_a = $into WHERE team_a = $from",
            "UPDATE rated_maps SET team_b = $into WHERE team_b = $from",
            "UPDATE roster_events SET team_id = $into WHERE team_id = $from",
            "UPDATE roster_events SET other_team_id = $into WHERE other_team_id = $from",
            "DELETE FROM roster_events WHERE team_id = other_team_id",
            "UPDATE OR IGNORE player_teams SET team_id = $into WHERE team_id = $from",
            // What is left of the merged team: its own snapshots, ratings, status, names and the team itself.
            "DELETE FROM player_teams WHERE team_id = $from",
            "DELETE FROM team_snapshots WHERE team_id = $from",
            "DELETE FROM team_ratings WHERE team_id = $from",
            "DELETE FROM overrides WHERE team_id = $from",
            "DELETE FROM team_display WHERE team_id = $from",
            "DELETE FROM teams WHERE team_id = $from",
        ])
            Execute(db, tx, sql, ("$from", fromTeamId), ("$into", intoTeamId));
        tx.Commit();
        return wasRated;
    }

    /// <summary>
    /// Points a provider key at a team, e.g. after a rebrand. Not for a core that moved to another
    /// organisation: the link has no date, so a full recalculation would move the key's whole
    /// history; the engine carries the core's rating to its new team by itself.
    /// </summary>
    public void LinkTeam(string providerKey, int teamId)
    {
        using var db = Open();
        RequireTeam(db, teamId);
        try
        {
            new ProviderIds(db).LinkTeam(providerKey, teamId);
        }
        catch (KeyNotFoundException error)
        {
            throw new AdminException(error.Message);
        }
    }

    /// <summary>
    /// Name, short name and logos shown from <paramref name="validFrom"/> on; earlier days keep their
    /// names. <paramref name="logo"/> serves both themes unless <paramref name="logoDark"/> is given for dark.
    /// </summary>
    public void SetDisplay(int teamId, string validFrom, string name, string? shortName, string? logo, string? logoDark = null)
    {
        RequireDate(validFrom, "The date");
        RequireText(name, "The team name");
        using var db = Open();
        RequireTeam(db, teamId);
        Execute(db, null, """
            INSERT INTO team_display (team_id, valid_from, name, short_name, logo, logo_dark) VALUES ($team, $from, $name, $short, $logo, $logoDark)
            ON CONFLICT (team_id, valid_from) DO UPDATE SET
                name = excluded.name, short_name = excluded.short_name, logo = excluded.logo, logo_dark = excluded.logo_dark
            """, ("$team", teamId), ("$from", validFrom), ("$name", name.Trim()), ("$short", Blank(shortName)),
            ("$logo", Blank(logo)), ("$logoDark", Blank(logoDark)));
    }

    /// <summary>
    /// Marks a team active, relegated or disbanded from a date, and records it as an override so
    /// full replays apply it on the same day.
    /// </summary>
    public void SetStatus(int teamId, string status, string effectiveOn, string? note) =>
        SetStatuses([teamId], status, effectiveOn, note);

    /// <summary>
    /// Gives several teams the same status, all or none: an unknown team leaves every team as it
    /// was. With <paramref name="afterLastMap"/>, each team's status starts the day after its last
    /// map (a team that never played: <paramref name="effectiveOn"/>, else the day it was created),
    /// so a disbanded team leaves the rankings when it stopped, without changing any rating.
    /// </summary>
    public void SetStatuses(IReadOnlyCollection<int> teamIds, string status, string? effectiveOn, string? note, bool afterLastMap = false)
    {
        if (status is not ("active" or "relegated" or "disbanded"))
            throw new AdminException($"'{status}' is not a status; use active, relegated or disbanded.");
        if (afterLastMap && status == "active")
            throw new AdminException("Pick the date a team becomes active again; 'after its last map' is for relegated or disbanded.");
        if (!afterLastMap || effectiveOn is not null)
            RequireDate(effectiveOn ?? "", "The date");
        if (teamIds.Count == 0)
            throw new AdminException("Select at least one team.");
        using var db = Open();
        var distinctTeamIds = teamIds.Distinct().ToList();
        foreach (var teamId in distinctTeamIds)
            RequireTeam(db, teamId);
        var effectiveDates = distinctTeamIds.ToDictionary(teamId => teamId, teamId => afterLastMap
            ? Scalar(db, null, """
                SELECT date(MAX(played_at), 'unixepoch', '+1 day') FROM (
                    SELECT played_at FROM rated_maps WHERE team_a = $team OR team_b = $team
                    UNION ALL SELECT played_at FROM maps WHERE team_a = $team OR team_b = $team)
                """, ("$team", teamId)) as string
                ?? effectiveOn
                ?? (string)Scalar(db, null, "SELECT created_on FROM teams WHERE team_id = $team", ("$team", teamId))!
            : effectiveOn!);
        using var tx = db.Connection.BeginTransaction();
        foreach (var teamId in distinctTeamIds)
        {
            Execute(db, tx, "UPDATE teams SET status = $status WHERE team_id = $team", ("$status", status), ("$team", teamId));
            Execute(db, tx, "INSERT INTO overrides (kind, team_id, effective_on, payload, note) VALUES ($kind, $team, $on, $payload, $note)",
                ("$kind", StatusOverride), ("$team", teamId), ("$on", effectiveDates[teamId]),
                ("$payload", JsonSerializer.Serialize(new { status })), ("$note", Blank(note)));
        }
        tx.Commit();
    }

    // Players

    /// <summary>Players whose name or provider key contains the search; an empty search lists every player.</summary>
    public List<AdminPlayer> FindPlayers(string search)
    {
        using var db = Open();
        var pattern = $"%{search.Trim()}%";
        var players = Rows(db, """
            SELECT p.player_id, p.name FROM players p
            WHERE p.name LIKE $pattern OR EXISTS (SELECT 1 FROM provider_players k WHERE k.player_id = p.player_id AND k.provider_key LIKE $pattern)
            ORDER BY p.name COLLATE NOCASE
            """, reader => (Id: reader.GetInt32(0), Name: reader.GetString(1)), ("$pattern", pattern));
        var keys = Rows(db, "SELECT player_id, provider_key FROM provider_players ORDER BY provider_key",
                reader => (Id: reader.GetInt32(0), Key: reader.GetString(1)))
            .ToLookup(row => row.Id, row => row.Key);
        return [.. players.Select(player => new AdminPlayer(player.Id, player.Name, [.. keys[player.Id]]))];
    }

    /// <summary>Points a player's provider key at another player, e.g. the old one after a rename.</summary>
    public void LinkPlayer(string providerKey, int playerId)
    {
        using var db = Open();
        if (Scalar(db, null, "SELECT COUNT(*) FROM players WHERE player_id = $player", ("$player", playerId)) is 0L)
            throw new AdminException($"There is no player {playerId}.");
        if (Scalar(db, null, "SELECT COUNT(*) FROM provider_players WHERE provider_key = $key", ("$key", providerKey)) is 0L)
            throw new AdminException($"No player has been seen with provider key '{providerKey}'.");
        new ProviderIds(db).LinkPlayer(providerKey, playerId);
    }

    // Seasons and leagues

    public List<AdminSeason> Seasons()
    {
        using var db = Open();
        return Rows(db, "SELECT starts_on, name FROM seasons ORDER BY starts_on", reader => new AdminSeason(reader.GetString(0), reader.GetString(1)));
    }

    public void SetSeason(string startsOn, string name)
    {
        RequireDate(startsOn, "The season start");
        RequireText(name, "The season name");
        using var db = Open();
        Execute(db, null, "INSERT INTO seasons (starts_on, name) VALUES ($on, $name) ON CONFLICT (starts_on) DO UPDATE SET name = excluded.name",
            ("$on", startsOn), ("$name", name.Trim()));
    }

    public void DeleteSeason(string startsOn)
    {
        using var db = Open();
        Execute(db, null, "DELETE FROM seasons WHERE starts_on = $on", ("$on", startsOn));
    }

    public List<AdminRegion> Regions()
    {
        using var db = Open();
        return Rows(db, "SELECT region_id, name, parent_id, rating, entry_level FROM regions ORDER BY parent_id IS NOT NULL, parent_id, region_id",
            reader => new AdminRegion(reader.GetString(0), reader.GetString(1), NullableString(reader, 2),
                reader.IsDBNull(3) ? null : reader.GetDouble(3), reader.IsDBNull(4) ? null : reader.GetDouble(4)));
    }

    /// <summary>Points above or below the average league a league new to the ratings starts at; null for level.</summary>
    public void SetEntryLevel(string regionId, double? entryLevel)
    {
        using var db = Open();
        var changed = Execute(db, null, "UPDATE regions SET entry_level = $level WHERE region_id = $region AND rating IS NOT NULL",
            ("$level", entryLevel), ("$region", regionId));
        if (changed == 0)
            throw new AdminException($"'{regionId}' is not a rated league.");
    }

    public List<AdminSkippedMap> SkippedMaps()
    {
        using var db = Open();
        return Rows(db, "SELECT map_id, series_id, played_at, reason, logged_at FROM skipped_maps ORDER BY logged_at DESC, map_id LIMIT 200",
            reader => new AdminSkippedMap(reader.GetString(0), NullableString(reader, 1),
                reader.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)),
                reader.GetString(3), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4))));
    }

    // Helpers

    static void RequireTeam(RankingsDb db, int teamId)
    {
        if (Scalar(db, null, "SELECT COUNT(*) FROM teams WHERE team_id = $team", ("$team", teamId)) is 0L)
            throw new AdminException($"There is no team {teamId}.");
    }

    static void RequireDate(string value, string what)
    {
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new AdminException($"{what} must be a date like 2026-01-15, not '{value}'.");
    }

    static void RequireText(string value, string what)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new AdminException($"{what} is empty.");
    }

    static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    static string? NullableString(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetString(column);

    static List<T> Rows<T>(RankingsDb db, string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] parameters)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
            rows.Add(read(reader));
        return rows;
    }

    static int Execute(RankingsDb db, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    static object? Scalar(RankingsDb db, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteScalar();
    }
}
