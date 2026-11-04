using Microsoft.Data.Sqlite;
using Rankings.Core;
using Rankings.Job.Storage;

namespace Rankings.Tests;

public class RankingsDbTests
{
    static readonly string[] AllTables =
    [
        "regions", "teams", "team_display", "players", "player_teams", "team_ratings",
        "team_snapshots", "region_snapshots", "run_state", "overrides", "seasons",
        "maps", "map_players", "provider_teams", "provider_players", "skipped_maps",
    ];

    static MapRecord SampleMap(string mapId = "XtD9IqfaeJ_R01-M001/1", int roundsA = 7, int roundsB = 8) => new(
        MapId: mapId,
        SeriesId: "XtD9IqfaeJ_R01-M001",
        PlayedAt: DateTimeOffset.FromUnixTimeSeconds(1783501200),
        MapNumber: 1,
        MapsInSeries: 3,
        TeamA: 1,
        TeamB: 2,
        RoundsA: roundsA,
        RoundsB: roundsB,
        PlayersA: [1, 2, 3, 4, 5],
        PlayersB: [6, 7, 8, 9, 10, 11]);

    /// Two APAC North teams and the eleven players SampleMap refers to.
    static void SeedTeamsAndPlayers(RankingsDb db) => db.Execute("""
        INSERT INTO teams (team_id, region_id, created_on) VALUES (1, 'APAC-N', '2026-01-01'), (2, 'APAC-N', '2026-01-01');
        WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 11)
        INSERT INTO players (player_id, name) SELECT i, 'player' || i FROM n;
        """);

    [Fact]
    public void Open_CreatesEveryTableInANewFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rankings-{Guid.NewGuid():N}.db");
        try
        {
            using (var db = RankingsDb.Open(path))
            {
                Assert.Equal(RankingsDb.SchemaVersion, Convert.ToInt32(db.Scalar("PRAGMA user_version")));
                var tables = Query(db, "SELECT name FROM sqlite_schema WHERE type = 'table' ORDER BY name");
                Assert.Equal(AllTables.Order(), tables);
            }

            Assert.True(File.Exists(path));
            // Reopening an existing file keeps it as it is.
            using (var db = RankingsDb.Open(path))
                Assert.Equal(0L, db.Scalar("SELECT COUNT(*) FROM teams"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Open_SeedsFiveRegionsAndSevenLeagues()
    {
        using var db = RankingsDb.Open(":memory:");

        Assert.Equal(["APAC", "CN", "EU", "NA", "SA"],
            Query(db, "SELECT region_id FROM regions WHERE parent_id IS NULL ORDER BY region_id"));
        Assert.Equal(["APAC-N", "ASIA", "OCE"],
            Query(db, "SELECT region_id FROM regions WHERE parent_id = 'APAC' ORDER BY region_id"));
        // Leagues are the regions without subregions, and each starts at 1,500.
        Assert.Equal(["APAC-N", "ASIA", "CN", "EU", "NA", "OCE", "SA"], Query(db, """
            SELECT region_id FROM regions r
            WHERE NOT EXISTS (SELECT 1 FROM regions c WHERE c.parent_id = r.region_id) AND rating = 1500
            ORDER BY region_id
            """));
        Assert.Null(db.Scalar("SELECT rating FROM regions WHERE region_id = 'APAC'") as double?);
    }

    [Fact]
    public void Open_RejectsAnUnknownSchemaVersion()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rankings-{Guid.NewGuid():N}.db");
        try
        {
            using (var db = RankingsDb.Open(path))
                db.Execute("PRAGMA user_version = 99");

            Assert.Throws<InvalidOperationException>(() => RankingsDb.Open(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Maps_RoundTripThroughTheDatabase()
    {
        using var db = RankingsDb.Open(":memory:");
        SeedTeamsAndPlayers(db);
        var map = SampleMap();

        db.SaveMaps([map]);

        var loaded = Assert.Single(db.LoadMaps());
        Assert.Equal(map with { PlayersA = [], PlayersB = [] }, loaded with { PlayersA = [], PlayersB = [] });
        Assert.Equal(map.PlayersA, loaded.PlayersA);
        Assert.Equal(map.PlayersB, loaded.PlayersB);
    }

    [Fact]
    public void Maps_RerunOverwritesInsteadOfDuplicating()
    {
        using var db = RankingsDb.Open(":memory:");
        SeedTeamsAndPlayers(db);
        db.SaveMaps([SampleMap()]);

        db.SaveMaps([SampleMap(roundsA: 8, roundsB: 6) with { PlayersB = [6, 7, 8, 9, 10] }]);

        var loaded = Assert.Single(db.LoadMaps());
        Assert.Equal((8, 6), (loaded.RoundsA, loaded.RoundsB));
        Assert.Equal([6, 7, 8, 9, 10], loaded.PlayersB);
    }

    [Fact]
    public void Maps_LoadInPlayOrder()
    {
        using var db = RankingsDb.Open(":memory:");
        SeedTeamsAndPlayers(db);
        var first = SampleMap("s/1");
        var second = SampleMap("s/2") with { MapNumber = 2 };
        var nextDay = SampleMap("t/1") with { SeriesId = "t", PlayedAt = first.PlayedAt.AddDays(1) };

        db.SaveMaps([nextDay, second, first]);

        Assert.Equal(["s/1", "s/2", "t/1"], db.LoadMaps().Select(m => m.MapId));
    }

    [Theory]
    [InlineData("SELECT rating FROM regions WHERE region_id = 'APAC-N'", 1500.0)]
    [InlineData("SELECT status FROM teams WHERE team_id = 1", "active")]
    [InlineData("SELECT name FROM team_display", "SCARZ")]
    [InlineData("SELECT name FROM players WHERE player_id = 1", "player1")]
    [InlineData("SELECT left_on FROM player_teams", "2026-06-01")]
    [InlineData("SELECT rating_offset FROM team_ratings", 42.5)]
    [InlineData("SELECT extra FROM team_snapshots", """{"sigma":80}""")]
    [InlineData("SELECT rating FROM region_snapshots", 1512.0)]
    [InlineData("SELECT value FROM run_state WHERE key = 'last_run'", "2026-11-02")]
    [InlineData("SELECT payload FROM overrides", """{"name":"SCARZ"}""")]
    [InlineData("SELECT name FROM seasons", "2026")]
    [InlineData("SELECT rounds_b FROM maps", 8L)]
    [InlineData("SELECT COUNT(*) FROM map_players", 11L)]
    [InlineData("SELECT reason FROM skipped_maps", "no winner")]
    [InlineData("SELECT team_id FROM provider_teams", 2L)]
    [InlineData("SELECT player_id FROM provider_players", 1L)]
    public void EveryTable_CanBeWrittenAndRead(string query, object expected)
    {
        using var db = RankingsDb.Open(":memory:");
        SeedTeamsAndPlayers(db);
        db.SaveMaps([SampleMap()]);
        db.Execute("""
            INSERT INTO team_display (team_id, valid_from, name, short_name) VALUES (2, '2026-01-01', 'SCARZ', 'SZ');
            INSERT INTO player_teams (player_id, team_id, joined_on, left_on) VALUES (1, 1, '2025-01-01', '2026-06-01');
            INSERT INTO team_ratings (team_id, rating_offset, rated_maps) VALUES (1, 42.5, 3);
            INSERT INTO team_snapshots (date, team_id, rank, rating, rating_offset, region_rating, change, maps_played, algorithm_version, extra)
                VALUES ('2026-07-08', 1, 1, 1542.5, 42.5, 1500, 12.5, 3, 'v1', '{"sigma":80}');
            INSERT INTO region_snapshots (date, region_id, rating, algorithm_version) VALUES ('2026-07-08', 'APAC', 1512, 'v1');
            INSERT INTO run_state (key, value) VALUES ('last_run', '2026-11-02');
            INSERT INTO overrides (kind, team_id, effective_on, payload) VALUES ('display', 2, '2026-07-01', '{"name":"SCARZ"}');
            INSERT INTO seasons (starts_on, name) VALUES ('2026-01-15', '2026');
            INSERT INTO skipped_maps (map_id, reason, logged_at) VALUES ('x/1', 'no winner', 1783501200);
            INSERT INTO provider_teams (provider_key, team_id, name, first_seen_at, last_seen_at) VALUES ('scarz 2022', 2, 'SCARZ', 0, 0);
            INSERT INTO provider_players (provider_key, player_id) VALUES ('Zaka', 1);
            """);

        Assert.Equal(expected, db.Scalar(query));
    }

    [Theory]
    [InlineData("INSERT INTO teams (team_id, region_id, created_on) VALUES (9, 'NOPE', '2026-01-01')")]
    [InlineData("INSERT INTO teams (team_id, region_id, status, created_on) VALUES (9, 'EU', 'retired', '2026-01-01')")]
    [InlineData("INSERT INTO teams (team_id, region_id, created_on) VALUES (9, 'APAC', '2026-01-01')")]
    [InlineData("UPDATE teams SET region_id = 'APAC' WHERE team_id = 1")]
    [InlineData("INSERT INTO maps VALUES ('m', 's', 0, 1, 1, 1, 1, 7, 3)")]
    [InlineData("INSERT INTO maps VALUES ('m', 's', 0, 4, 3, 1, 2, 7, 3)")]
    [InlineData("INSERT INTO map_players VALUES ('missing-map', 'A', 1)")]
    [InlineData("INSERT INTO seasons VALUES ('15/01/2026', '2026')")]
    [InlineData("INSERT INTO seasons VALUES ('2026-01-15 00:00', '2026')")]
    public void Constraints_RejectInvalidRows(string sql)
    {
        using var db = RankingsDb.Open(":memory:");
        SeedTeamsAndPlayers(db);

        Assert.Throws<SqliteException>(() => db.Execute(sql));
    }

    [Fact]
    public void SeasonStarts_LoadEarliestFirstAtMidnightUtc()
    {
        using var db = RankingsDb.Open(":memory:");
        db.Execute("INSERT INTO seasons (starts_on, name) VALUES ('2026-01-15', '2026'), ('2025-02-01', '2025')");

        Assert.Equal(
            [DateTimeOffset.Parse("2025-02-01T00:00:00Z"), DateTimeOffset.Parse("2026-01-15T00:00:00Z")],
            db.LoadSeasonStarts());
    }

    static List<string> Query(RankingsDb db, string sql)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(reader.GetString(0));
        return rows;
    }
}
