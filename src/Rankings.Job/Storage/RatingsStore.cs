using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Rankings.Core;
using Rankings.Job.Site;

namespace Rankings.Job.Storage;

/// <summary>A run refused before it changed anything, e.g. a database built from another data source.</summary>
public sealed class DailyRunException(string message) : Exception(message);

/// <summary>
/// What the daily run keeps in rankings.db besides the maps: the settled checkpoint it carries on
/// from, each day's snapshots, rated maps and roster events, and the inputs it rates with.
/// </summary>
public sealed class RatingsStore(RankingsDb db)
{
    /// <summary>The last day that is final: rated from maps no later fetch will change.</summary>
    public const string SettledThroughKey = "settled_through";

    /// <summary>The rating state at the end of the settled day, as <see cref="RatingStateJson"/>.</summary>
    public const string SettledStateKey = "settled_state";

    /// <summary>The last day rated, settled or not.</summary>
    public const string RatedThroughKey = "rated_through";

    /// <summary>Where the maps came from (liquipedia or synthetic), so the two are never mixed.</summary>
    public const string DataSourceKey = "data_source";

    public DateOnly? SettledThrough =>
        db.GetState(SettledThroughKey) is { } day ? ParseDay(day) : null;

    public RatingState LoadSettledState() =>
        RatingStateJson.Read(db.GetState(SettledStateKey)
            ?? throw new InvalidDataException($"run_state has {SettledThroughKey} but no {SettledStateKey}."));

    /// <summary>Records the data source on the first run and refuses a different one afterwards.</summary>
    public void CheckDataSource(string dataSource)
    {
        if (db.GetState(DataSourceKey) is { } existing && existing != dataSource)
            throw new DailyRunException(
                $"This rankings.db was built from {existing} data, not {dataSource}. Start from an empty database to switch.");
        db.SetState(DataSourceKey, dataSource);
    }

    /// <summary>Clears everything a full recalculation rebuilds; teams, players, seasons and overrides stay.</summary>
    public void ResetForFullRecalculation()
    {
        using var tx = db.Connection.BeginTransaction();
        foreach (var sql in (string[])[
            "DELETE FROM maps",
            "DELETE FROM skipped_maps",
            "DELETE FROM team_snapshots",
            "DELETE FROM region_snapshots",
            "DELETE FROM rated_maps",
            "DELETE FROM roster_events",
            "DELETE FROM team_ratings",
            "UPDATE regions SET rating = 1500 WHERE rating IS NOT NULL",
            $"DELETE FROM run_state WHERE key IN ('{SettledThroughKey}', '{SettledStateKey}', '{RatedThroughKey}')",
        ])
            db.Execute(sql, tx);
        tx.Commit();
    }

    // Inputs

    /// <summary>The rated leagues: every region that is not only a grouping of subregions.</summary>
    public List<string> Leagues() =>
        Rows("SELECT region_id FROM regions r WHERE NOT EXISTS (SELECT 1 FROM regions s WHERE s.parent_id = r.region_id) ORDER BY region_id",
            reader => reader.GetString(0));

    public Dictionary<int, string> TeamLeagues() =>
        Rows("SELECT team_id, region_id FROM teams", reader => (reader.GetInt32(0), reader.GetString(1)))
            .ToDictionary(row => row.Item1, row => row.Item2);

    /// <summary>Teams relegated or disbanded, by the day the admin set it to take effect.</summary>
    public ILookup<DateOnly, int> Freezes() =>
        Rows("SELECT team_id, effective_on, payload FROM overrides WHERE kind = 'status' AND team_id IS NOT NULL ORDER BY override_id",
                reader => (TeamId: reader.GetInt32(0), Day: ParseDay(reader.GetString(1)), Payload: reader.GetString(2)))
            .Where(row => FreezesTeam(row.Payload))
            .ToLookup(row => row.Day, row => row.TeamId);

    static bool FreezesTeam(string statusPayload)
    {
        using var payload = JsonDocument.Parse(statusPayload);
        return payload.RootElement.TryGetProperty("status", out var status) && status.GetString() is "relegated" or "disbanded";
    }

    /// <summary>
    /// Days with maps skipped only because a team was unassigned, where every such team is
    /// assigned now: reading those days back from the archive stores the maps.
    /// </summary>
    public List<DateOnly> DaysWithNewlyAssignedTeams()
    {
        var assigned = Rows("SELECT provider_key FROM provider_teams WHERE team_id IS NOT NULL", reader => reader.GetString(0))
            .ToHashSet(StringComparer.Ordinal);
        return [.. Rows($"SELECT played_at, reason FROM skipped_maps WHERE played_at IS NOT NULL AND reason LIKE '{ProviderIds.UnassignedReason}%'",
                reader => (PlayedAt: reader.GetInt64(0), Reason: reader.GetString(1)))
            .Where(row => row.Reason[ProviderIds.UnassignedReason.Length..].Split(", ").All(assigned.Contains))
            .Select(row => DayOf(DateTimeOffset.FromUnixTimeSeconds(row.PlayedAt)))
            .Distinct()
            .Order()];
    }

    // Writing a rated day

    /// <summary>Removes snapshots, rated maps and roster events from <paramref name="day"/> on, before they are rated again.</summary>
    public void ClearFrom(DateOnly day, SqliteTransaction tx)
    {
        var date = Text(day);
        var start = StartOf(day).ToUnixTimeSeconds();
        Execute(tx, "DELETE FROM team_snapshots WHERE date >= $date", ("$date", date));
        Execute(tx, "DELETE FROM region_snapshots WHERE date >= $date", ("$date", date));
        Execute(tx, "DELETE FROM roster_events WHERE date >= $date", ("$date", date));
        Execute(tx, "DELETE FROM rated_maps WHERE played_at >= $start", ("$start", start));
    }

    public void SaveDay(DaySnapshot snapshot, RatingRun run, string algorithmVersion, SqliteTransaction tx)
    {
        var date = Text(snapshot.Date);
        foreach (var team in snapshot.Teams)
            Execute(tx, """
                INSERT INTO team_snapshots (date, team_id, rank, rating, rating_offset, region_rating, change, maps_played, algorithm_version)
                VALUES ($date, $team, $rank, $rating, $offset, $league, $change, $maps, $version)
                """,
                ("$date", date), ("$team", team.TeamId), ("$rank", team.Rank), ("$rating", team.Rating), ("$offset", team.Offset),
                ("$league", team.LeagueRating), ("$change", team.Change), ("$maps", team.RatedMaps), ("$version", algorithmVersion));
        foreach (var league in snapshot.Leagues)
            Execute(tx, "INSERT INTO region_snapshots (date, region_id, rating, algorithm_version) VALUES ($date, $league, $rating, $version)",
                ("$date", date), ("$league", league.LeagueId), ("$rating", league.Rating), ("$version", algorithmVersion));
        foreach (var rated in run.RatedMaps)
        {
            var map = rated.Map;
            Execute(tx, """
                INSERT INTO rated_maps (map_id, series_id, played_at, map_number, maps_in_series, team_a, team_b, rounds_a, rounds_b, change_a, change_b)
                VALUES ($map, $series, $played, $number, $bestOf, $teamA, $teamB, $roundsA, $roundsB, $changeA, $changeB)
                """,
                ("$map", map.MapId), ("$series", map.SeriesId), ("$played", map.PlayedAt.ToUnixTimeSeconds()), ("$number", map.MapNumber),
                ("$bestOf", map.MapsInSeries), ("$teamA", map.TeamA), ("$teamB", map.TeamB), ("$roundsA", map.RoundsA),
                ("$roundsB", map.RoundsB), ("$changeA", rated.RatingChangeA), ("$changeB", rated.RatingChangeB));
        }
        foreach (var rosterEvent in run.Events)
            Execute(tx, "INSERT INTO roster_events (date, at, team_id, kind, new_players, other_team_id) VALUES ($date, $at, $team, $kind, $new, $other)",
                ("$date", date), ("$at", rosterEvent.At.ToUnixTimeSeconds()), ("$team", rosterEvent.TeamId),
                ("$kind", rosterEvent.Kind.ToString()), ("$new", rosterEvent.NewPlayers), ("$other", rosterEvent.OtherTeamId));
    }

    /// <summary>
    /// Makes <paramref name="day"/> the new checkpoint and drops the maps rated from
    /// <paramref name="ratedFrom"/> through it: they are in the archive and will not change again.
    /// </summary>
    public void Settle(DateOnly day, DateOnly ratedFrom, RatingState state, SqliteTransaction tx)
    {
        SetState(tx, SettledStateKey, RatingStateJson.Write(state));
        SetState(tx, SettledThroughKey, Text(day));
        Execute(tx, "DELETE FROM maps WHERE played_at >= $start AND played_at < $end",
            ("$start", StartOf(ratedFrom).ToUnixTimeSeconds()), ("$end", StartOf(day.AddDays(1)).ToUnixTimeSeconds()));
    }

    /// <summary>The latest ratings in team_ratings and regions, for the admin dashboard and anyone reading the file.</summary>
    public void SaveCurrentRatings(RatingState state, DateOnly ratedThrough, SqliteTransaction tx)
    {
        Execute(tx, "DELETE FROM team_ratings");
        foreach (var team in state.Teams.Values.Where(team => team.Seeded))
            Execute(tx, "INSERT INTO team_ratings (team_id, rating_offset, rated_maps, boost_maps_left, last_played_at) VALUES ($team, $offset, $maps, $boost, $last)",
                ("$team", team.TeamId), ("$offset", team.Offset), ("$maps", team.RatedMaps), ("$boost", team.BoostMapsLeft),
                ("$last", team.LastPlayedAt?.ToUnixTimeSeconds()));
        foreach (var (league, rating) in state.Leagues)
            Execute(tx, "UPDATE regions SET rating = $rating WHERE region_id = $league", ("$rating", rating), ("$league", league));
        SetState(tx, RatedThroughKey, Text(ratedThrough));
    }

    // Reading back

    /// <summary>One day's snapshot, e.g. the settled day's, which the next day's changes are measured from.</summary>
    public DaySnapshot? LoadSnapshot(DateOnly day, RatingSettings settings) =>
        LoadSnapshots(settings, day).SingleOrDefault();

    /// <summary>Every day's snapshot, oldest first, in the order <see cref="Snapshots.Take"/> builds them.</summary>
    public List<DaySnapshot> LoadSnapshots(RatingSettings settings, DateOnly? onlyDay = null)
    {
        var filter = onlyDay is { } day ? $"WHERE date = '{Text(day)}'" : "";
        var teams = Rows($"""
                SELECT date, team_id, rank, rating, rating_offset, region_rating, change, maps_played
                FROM team_snapshots {filter}
                ORDER BY date, COALESCE(rank, 2147483647), team_id
                """,
                reader => new TeamSnapshot(ParseDay(reader.GetString(0)), reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetInt32(2), reader.GetDouble(3), reader.GetDouble(4), reader.GetDouble(5),
                    reader.GetDouble(6), reader.GetInt32(7), reader.GetInt32(7) < settings.ProvisionalUntilMaps))
            .ToLookup(team => team.Date);
        var leagues = Rows($"SELECT date, region_id, rating FROM region_snapshots {filter} ORDER BY date, region_id",
                reader => (Day: ParseDay(reader.GetString(0)), League: reader.GetString(1), Rating: reader.GetDouble(2)))
            .ToLookup(row => row.Day);

        var days = new List<DaySnapshot>();
        Dictionary<string, double> previousLeagues = [];
        foreach (var date in leagues.Select(group => group.Key).Union(teams.Select(group => group.Key)).Order())
        {
            var leagueSnapshots = leagues[date]
                .Select(row => new LeagueSnapshot(date, row.League, row.Rating,
                    previousLeagues.TryGetValue(row.League, out var before) ? row.Rating - before : 0))
                .ToList();
            days.Add(new DaySnapshot(date, [.. teams[date]], leagueSnapshots));
            previousLeagues = leagueSnapshots.ToDictionary(league => league.LeagueId, league => league.Rating);
        }
        return days;
    }

    public List<SiteMap> LoadRatedMaps() =>
        Rows("""
            SELECT m.map_id, m.series_id, m.played_at, m.map_number, m.maps_in_series, m.team_a, m.team_b,
                   m.rounds_a, m.rounds_b, m.change_a, m.change_b, s.tournament
            FROM rated_maps m LEFT JOIN series s ON s.series_id = m.series_id
            ORDER BY m.played_at, m.series_id, m.map_number
            """,
            reader => new SiteMap(new RatedMap(
                new MapRecord(reader.GetString(0), reader.GetString(1), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)),
                    reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8), [], []),
                reader.GetDouble(9), reader.GetDouble(10)),
                reader.IsDBNull(11) ? null : reader.GetString(11)));

    public List<RosterEvent> LoadRosterEvents() =>
        Rows("SELECT at, team_id, kind, new_players, other_team_id FROM roster_events ORDER BY event_id",
            reader => new RosterEvent(DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)), reader.GetInt32(1),
                Enum.Parse<RosterEventKind>(reader.GetString(2)), reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetInt32(4)));

    /// <summary>Every team with the name and logo in use on <paramref name="day"/>.</summary>
    public List<SiteTeam> SiteTeams(DateOnly day) =>
        Rows("""
            SELECT t.team_id, t.region_id, d.name, d.short_name, d.logo
            FROM teams t
            LEFT JOIN team_display d ON d.team_id = t.team_id AND d.valid_from = (
                SELECT MAX(valid_from) FROM team_display WHERE team_id = t.team_id AND valid_from <= $day)
            ORDER BY t.team_id
            """,
            reader => new SiteTeam(reader.GetInt32(0), reader.IsDBNull(2) ? $"Team {reader.GetInt32(0)}" : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(1)),
            ("$day", Text(day)));

    public List<SiteRegion> SiteRegions() =>
        Rows("SELECT region_id, name, parent_id FROM regions ORDER BY rowid",
            reader => new SiteRegion(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));

    // Helpers

    public static DateOnly DayOf(DateTimeOffset instant) => DateOnly.FromDateTime(instant.UtcDateTime);

    public static DateTimeOffset StartOf(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    static string Text(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    static DateOnly ParseDay(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    void SetState(SqliteTransaction tx, string key, string value) =>
        Execute(tx, "INSERT INTO run_state (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
            ("$key", key), ("$value", value));

    void Execute(SqliteTransaction tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    List<T> Rows<T>(string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] parameters)
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
}
