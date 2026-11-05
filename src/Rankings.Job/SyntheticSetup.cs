using System.Globalization;
using Rankings.Job.Site;
using Rankings.Job.Storage;
using Rankings.Sim;

namespace Rankings.Job;

/// <summary>
/// The admin inputs a synthetic world needs, filled in automatically: every team assigned to its
/// league with a made-up name, the season starts, and relegations as status overrides. Existing
/// rows are left alone, so it runs before every synthetic run without undoing a manual change.
/// </summary>
public static class SyntheticSetup
{
    public const string DataSource = "synthetic";

    /// <summary>Archive keys for synthetic days start with this, apart from the real ones.</summary>
    public const string ArchivePrefix = "synthetic/";

    public static void Ensure(RankingsDb db, SimulatedWorld world)
    {
        var history = world.History;
        var createdOn = Day(history.SeasonStarts.Min());
        var providerKeys = world.Series
            .SelectMany(series => new[] { series.TeamA, series.TeamB })
            .DistinctBy(side => side.TeamId)
            .ToDictionary(side => side.TeamId, side => side.Key);

        using var tx = db.Connection.BeginTransaction();
        void Execute(string sql, params (string Name, object Value)[] parameters)
        {
            using var cmd = db.Connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters)
                cmd.Parameters.AddWithValue(name, value);
            cmd.ExecuteNonQuery();
        }

        foreach (var (teamId, league) in history.TeamLeagues.OrderBy(team => team.Key))
        {
            Execute("INSERT INTO teams (team_id, region_id, created_on) VALUES ($team, $league, $on) ON CONFLICT DO NOTHING",
                ("$team", teamId), ("$league", league), ("$on", createdOn));
            Execute("INSERT INTO team_display (team_id, valid_from, name, short_name) VALUES ($team, $on, $name, $short) ON CONFLICT DO NOTHING",
                ("$team", teamId), ("$on", createdOn), ("$name", SampleSite.TeamName(teamId)), ("$short", SampleSite.ShortTeamName(teamId)));
            if (providerKeys.TryGetValue(teamId, out var key))
                Execute("""
                    INSERT INTO provider_teams (provider_key, team_id, name, first_seen_at, last_seen_at)
                    VALUES ($key, $team, $name, 0, 0) ON CONFLICT DO NOTHING
                    """, ("$key", key), ("$team", teamId), ("$name", SampleSite.TeamName(teamId)));
        }
        foreach (var start in history.SeasonStarts)
            Execute("INSERT INTO seasons (starts_on, name) VALUES ($on, $name) ON CONFLICT DO NOTHING",
                ("$on", Day(start)), ("$name", start.Year.ToString(CultureInfo.InvariantCulture)));
        foreach (var freeze in history.Freezes)
            Execute("""
                INSERT INTO overrides (kind, team_id, effective_on, payload, note)
                SELECT 'status', $team, $on, '{"status":"relegated"}', 'synthetic'
                WHERE NOT EXISTS (SELECT 1 FROM overrides WHERE kind = 'status' AND team_id = $team AND effective_on = $on)
                """, ("$team", freeze.TeamId), ("$on", Day(freeze.At)));
        tx.Commit();
    }

    static string Day(DateTimeOffset instant) => instant.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
