using System.Globalization;
using Microsoft.Data.Sqlite;
using Rankings.Core;

namespace Rankings.Job.Storage;

/// <summary>
/// The SQLite file holding everything the next run needs. Opening a path that
/// does not exist creates it with the current schema.
/// </summary>
public sealed class RankingsDb : IDisposable
{
    public const int SchemaVersion = 1;

    public SqliteConnection Connection { get; }

    RankingsDb(SqliteConnection connection) => Connection = connection;

    /// <param name="path">File path, or ":memory:" for a throwaway database.</param>
    public static RankingsDb Open(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            // Pooling would keep the file locked after Dispose, which blocks the R2 upload.
            Pooling = false,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        var db = new RankingsDb(connection);
        try
        {
            db.EnsureSchema();
        }
        catch
        {
            db.Dispose();
            throw;
        }
        return db;
    }

    void EnsureSchema()
    {
        var version = Convert.ToInt32(Scalar("PRAGMA user_version"));
        if (version == SchemaVersion)
            return;
        if (version != 0)
            throw new InvalidOperationException(
                $"Database schema version is {version}, this build expects {SchemaVersion}.");

        using var tx = Connection.BeginTransaction();
        Execute(ReadSchema(), tx);
        Execute($"PRAGMA user_version = {SchemaVersion}", tx);
        tx.Commit();
    }

    static string ReadSchema()
    {
        using var stream = typeof(RankingsDb).Assembly.GetManifestResourceStream("schema.sql")
            ?? throw new InvalidOperationException("schema.sql is not embedded in the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Inserts maps, or replaces them when their map ID is already stored.</summary>
    public void SaveMaps(IEnumerable<MapRecord> maps)
    {
        using var tx = Connection.BeginTransaction();

        using var upsert = Connection.CreateCommand();
        upsert.Transaction = tx;
        upsert.CommandText = """
            INSERT INTO maps (map_id, series_id, played_at, map_number, maps_in_series,
                              team_a, team_b, rounds_a, rounds_b)
            VALUES ($map_id, $series_id, $played_at, $map_number, $maps_in_series,
                    $team_a, $team_b, $rounds_a, $rounds_b)
            ON CONFLICT (map_id) DO UPDATE SET
                series_id = excluded.series_id,
                played_at = excluded.played_at,
                map_number = excluded.map_number,
                maps_in_series = excluded.maps_in_series,
                team_a = excluded.team_a,
                team_b = excluded.team_b,
                rounds_a = excluded.rounds_a,
                rounds_b = excluded.rounds_b
            """;
        var mapId = upsert.Parameters.Add("$map_id", SqliteType.Text);
        var seriesId = upsert.Parameters.Add("$series_id", SqliteType.Text);
        var playedAt = upsert.Parameters.Add("$played_at", SqliteType.Integer);
        var mapNumber = upsert.Parameters.Add("$map_number", SqliteType.Integer);
        var mapsInSeries = upsert.Parameters.Add("$maps_in_series", SqliteType.Integer);
        var teamA = upsert.Parameters.Add("$team_a", SqliteType.Integer);
        var teamB = upsert.Parameters.Add("$team_b", SqliteType.Integer);
        var roundsA = upsert.Parameters.Add("$rounds_a", SqliteType.Integer);
        var roundsB = upsert.Parameters.Add("$rounds_b", SqliteType.Integer);

        using var clearPlayers = Connection.CreateCommand();
        clearPlayers.Transaction = tx;
        clearPlayers.CommandText = "DELETE FROM map_players WHERE map_id = $map_id";
        var clearMapId = clearPlayers.Parameters.Add("$map_id", SqliteType.Text);

        using var insertPlayer = Connection.CreateCommand();
        insertPlayer.Transaction = tx;
        insertPlayer.CommandText =
            "INSERT INTO map_players (map_id, side, player_id) VALUES ($map_id, $side, $player_id)";
        var playerMapId = insertPlayer.Parameters.Add("$map_id", SqliteType.Text);
        var side = insertPlayer.Parameters.Add("$side", SqliteType.Text);
        var playerId = insertPlayer.Parameters.Add("$player_id", SqliteType.Integer);

        foreach (var map in maps)
        {
            mapId.Value = map.MapId;
            seriesId.Value = map.SeriesId;
            playedAt.Value = map.PlayedAt.ToUnixTimeSeconds();
            mapNumber.Value = map.MapNumber;
            mapsInSeries.Value = map.MapsInSeries;
            teamA.Value = map.TeamA;
            teamB.Value = map.TeamB;
            roundsA.Value = map.RoundsA;
            roundsB.Value = map.RoundsB;
            upsert.ExecuteNonQuery();

            clearMapId.Value = map.MapId;
            clearPlayers.ExecuteNonQuery();

            playerMapId.Value = map.MapId;
            foreach (var (sideCode, players) in new[] { ("A", map.PlayersA), ("B", map.PlayersB) })
            {
                side.Value = sideCode;
                foreach (var id in players)
                {
                    playerId.Value = id;
                    insertPlayer.ExecuteNonQuery();
                }
            }
        }

        tx.Commit();
    }

    /// <summary>All stored maps in rating order; player lists are sorted by ID.</summary>
    public List<MapRecord> LoadMaps()
    {
        var players = new Dictionary<(string MapId, string Side), List<int>>();
        using (var cmd = Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT map_id, side, player_id FROM map_players ORDER BY player_id";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var key = (reader.GetString(0), reader.GetString(1));
                if (!players.TryGetValue(key, out var list))
                    players[key] = list = [];
                list.Add(reader.GetInt32(2));
            }
        }

        var maps = new List<MapRecord>();
        using (var cmd = Connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT map_id, series_id, played_at, map_number, maps_in_series,
                       team_a, team_b, rounds_a, rounds_b
                FROM maps
                ORDER BY played_at, series_id, map_number
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                maps.Add(new MapRecord(
                    MapId: id,
                    SeriesId: reader.GetString(1),
                    PlayedAt: DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)),
                    MapNumber: reader.GetInt32(3),
                    MapsInSeries: reader.GetInt32(4),
                    TeamA: reader.GetInt32(5),
                    TeamB: reader.GetInt32(6),
                    RoundsA: reader.GetInt32(7),
                    RoundsB: reader.GetInt32(8),
                    PlayersA: players.GetValueOrDefault((id, "A")) ?? [],
                    PlayersB: players.GetValueOrDefault((id, "B")) ?? []));
            }
        }
        return maps;
    }

    /// <summary>Logs maps that could not be rated; a map skipped again keeps one row with the latest reason.</summary>
    public void SaveSkipped(IEnumerable<SkippedSourceMap> skipped, DateTimeOffset loggedAt)
    {
        using var tx = Connection.BeginTransaction();
        using var cmd = Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO skipped_maps (map_id, series_id, played_at, reason, logged_at)
            VALUES ($map_id, $series_id, $played_at, $reason, $logged_at)
            ON CONFLICT (map_id) DO UPDATE SET
                series_id = excluded.series_id,
                played_at = excluded.played_at,
                reason = excluded.reason,
                logged_at = excluded.logged_at
            """;
        var mapId = cmd.Parameters.Add("$map_id", SqliteType.Text);
        var seriesId = cmd.Parameters.Add("$series_id", SqliteType.Text);
        var playedAt = cmd.Parameters.Add("$played_at", SqliteType.Integer);
        var reason = cmd.Parameters.Add("$reason", SqliteType.Text);
        cmd.Parameters.AddWithValue("$logged_at", loggedAt.ToUnixTimeSeconds());

        foreach (var skip in skipped)
        {
            mapId.Value = skip.MapId;
            seriesId.Value = (object?)skip.SeriesId ?? DBNull.Value;
            playedAt.Value = (object?)skip.PlayedAt?.ToUnixTimeSeconds() ?? DBNull.Value;
            reason.Value = skip.Reason;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Removes skip log rows by map or series ID, once those maps were stored after all.</summary>
    public void ClearSkipped(IEnumerable<string> ids)
    {
        using var tx = Connection.BeginTransaction();
        using var cmd = Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM skipped_maps WHERE map_id = $id";
        var id = cmd.Parameters.Add("$id", SqliteType.Text);
        foreach (var value in ids.Distinct())
        {
            id.Value = value;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Season starts from the admin-maintained seasons table, earliest first, at 00:00 UTC.</summary>
    public List<DateTimeOffset> LoadSeasonStarts()
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = "SELECT starts_on FROM seasons ORDER BY starts_on";
        using var reader = cmd.ExecuteReader();
        var seasonStarts = new List<DateTimeOffset>();
        while (reader.Read())
        {
            var startsOn = DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            seasonStarts.Add(new DateTimeOffset(startsOn.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        }
        return seasonStarts;
    }

    /// <summary>Entry levels the admin set for leagues that enter later, by league.</summary>
    public Dictionary<string, double> LoadLeagueEntryLevels()
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = "SELECT region_id, entry_level FROM regions WHERE entry_level IS NOT NULL";
        using var reader = cmd.ExecuteReader();
        var entryLevels = new Dictionary<string, double>(StringComparer.Ordinal);
        while (reader.Read())
            entryLevels[reader.GetString(0)] = reader.GetDouble(1);
        return entryLevels;
    }

    public string? GetState(string key)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM run_state WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetState(string key, string value)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO run_state (key, value) VALUES ($key, $value)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    public int Execute(string sql, SqliteTransaction? tx = null)
    {
        using var cmd = Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }

    public object? Scalar(string sql)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    public void Dispose() => Connection.Dispose();
}
