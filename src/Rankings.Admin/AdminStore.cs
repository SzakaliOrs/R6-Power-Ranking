using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Rankings.Job.Storage;

namespace Rankings.Admin;

public sealed record AdminTeam(
    int TeamId, string League, string Status, string CreatedOn,
    string? Name, string? ShortName, string? Logo, IReadOnlyList<string> ProviderKeys);

public sealed record AdminPlayer(int PlayerId, string Name, IReadOnlyList<string> ProviderKeys);

public sealed record AdminSeason(string StartsOn, string Name);

public sealed record AdminRegion(string RegionId, string Name, string? ParentId, double? Rating, double? EntryLevel);

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
            SELECT t.team_id, t.region_id, t.status, t.created_on, d.name, d.short_name, d.logo
            FROM teams t
            LEFT JOIN team_display d ON d.team_id = t.team_id AND d.valid_from = (
                SELECT MAX(valid_from) FROM team_display WHERE team_id = t.team_id AND valid_from <= date('now'))
            ORDER BY t.region_id, d.name, t.team_id
            """,
            reader => new AdminTeam(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                NullableString(reader, 4), NullableString(reader, 5), NullableString(reader, 6), [.. keys[reader.GetInt32(0)]]));
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

    /// <summary>Points a provider key at a team, e.g. a rebrand or a core that moved to another organisation.</summary>
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

    /// <summary>Name, short name and logo shown from <paramref name="validFrom"/> on; earlier days keep their names.</summary>
    public void SetDisplay(int teamId, string validFrom, string name, string? shortName, string? logo)
    {
        RequireDate(validFrom, "The date");
        RequireText(name, "The team name");
        using var db = Open();
        RequireTeam(db, teamId);
        Execute(db, null, """
            INSERT INTO team_display (team_id, valid_from, name, short_name, logo) VALUES ($team, $from, $name, $short, $logo)
            ON CONFLICT (team_id, valid_from) DO UPDATE SET name = excluded.name, short_name = excluded.short_name, logo = excluded.logo
            """, ("$team", teamId), ("$from", validFrom), ("$name", name.Trim()), ("$short", Blank(shortName)), ("$logo", Blank(logo)));
    }

    /// <summary>
    /// Marks a team active, relegated or disbanded from a date, and records it as an override so
    /// full replays apply it on the same day.
    /// </summary>
    public void SetStatus(int teamId, string status, string effectiveOn, string? note)
    {
        RequireDate(effectiveOn, "The date");
        if (status is not ("active" or "relegated" or "disbanded"))
            throw new AdminException($"'{status}' is not a status; use active, relegated or disbanded.");
        using var db = Open();
        RequireTeam(db, teamId);
        using var tx = db.Connection.BeginTransaction();
        Execute(db, tx, "UPDATE teams SET status = $status WHERE team_id = $team", ("$status", status), ("$team", teamId));
        Execute(db, tx, "INSERT INTO overrides (kind, team_id, effective_on, payload, note) VALUES ($kind, $team, $on, $payload, $note)",
            ("$kind", StatusOverride), ("$team", teamId), ("$on", effectiveOn),
            ("$payload", JsonSerializer.Serialize(new { status })), ("$note", Blank(note)));
        tx.Commit();
    }

    // Players

    public List<AdminPlayer> FindPlayers(string search)
    {
        using var db = Open();
        var pattern = $"%{search.Trim()}%";
        var players = Rows(db, """
            SELECT p.player_id, p.name FROM players p
            WHERE p.name LIKE $pattern OR EXISTS (SELECT 1 FROM provider_players k WHERE k.player_id = p.player_id AND k.provider_key LIKE $pattern)
            ORDER BY p.name LIMIT 50
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
