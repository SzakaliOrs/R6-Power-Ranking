using Microsoft.Data.Sqlite;
using Rankings.Core;

namespace Rankings.Job.Storage;

public sealed record ResolvedMaps(IReadOnlyList<MapRecord> Maps, IReadOnlyList<SkippedSourceMap> Skipped);

public sealed record UnassignedTeam(string ProviderKey, string Name, string? LastTournament, DateTimeOffset LastSeenAt);

/// <summary>
/// Turns provider keys into own team and player IDs. Teams need an explicit assignment
/// (it fixes their league); players are created on first sight.
/// </summary>
public sealed class ProviderIds(RankingsDb db)
{
    /// <summary>Starts the skip reason of a map whose team is unassigned; the keys follow, comma-separated.</summary>
    public const string UnassignedReason = "team not assigned yet: ";

    /// <summary>
    /// Resolves each map's teams and players. Maps with a team not yet assigned are skipped,
    /// and every team key seen is recorded so the admin endpoint can list it.
    /// </summary>
    public ResolvedMaps Resolve(IEnumerable<SourceMap> maps)
    {
        var resolved = new List<MapRecord>();
        var skipped = new List<SkippedSourceMap>();
        using var tx = db.Connection.BeginTransaction();

        foreach (var map in maps)
        {
            var teamA = SeeTeam(map.TeamA, map, tx);
            var teamB = SeeTeam(map.TeamB, map, tx);
            if (teamA is null || teamB is null)
            {
                var keys = new[] { (map.TeamA.Key, teamA), (map.TeamB.Key, teamB) }
                    .Where(t => t.Item2 is null).Select(t => t.Key);
                skipped.Add(new(map.MapId, map.SeriesId, map.PlayedAt, UnassignedReason + string.Join(", ", keys)));
                continue;
            }
            if (teamA == teamB)
            {
                skipped.Add(new(map.MapId, map.SeriesId, map.PlayedAt,
                    $"{map.TeamA.Key} and {map.TeamB.Key} are linked to the same team"));
                continue;
            }

            resolved.Add(new MapRecord(
                MapId: map.MapId,
                SeriesId: map.SeriesId,
                PlayedAt: map.PlayedAt,
                MapNumber: map.MapNumber,
                MapsInSeries: map.MapsInSeries,
                TeamA: teamA.Value,
                TeamB: teamB.Value,
                RoundsA: map.RoundsA,
                RoundsB: map.RoundsB,
                PlayersA: [.. map.TeamA.Players.Select(p => Player(p, tx)).Distinct()],
                PlayersB: [.. map.TeamB.Players.Select(p => Player(p, tx)).Distinct()]));
        }

        tx.Commit();
        return new ResolvedMaps(resolved, skipped);
    }

    /// <summary>Team keys waiting for an assignment, most recently seen first.</summary>
    public List<UnassignedTeam> UnassignedTeams()
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT provider_key, name, last_tournament, last_seen_at
            FROM provider_teams WHERE team_id IS NULL
            ORDER BY last_seen_at DESC, provider_key
            """;
        using var reader = cmd.ExecuteReader();
        var teams = new List<UnassignedTeam>();
        while (reader.Read())
            teams.Add(new UnassignedTeam(reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3))));
        return teams;
    }

    /// <summary>Points a seen team key at an own team, e.g. a new team or the old one after a rebrand.</summary>
    public void LinkTeam(string providerKey, int teamId)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE provider_teams SET team_id = $team WHERE provider_key = $key";
        cmd.Parameters.AddWithValue("$team", teamId);
        cmd.Parameters.AddWithValue("$key", providerKey);
        if (cmd.ExecuteNonQuery() == 0)
            throw new KeyNotFoundException($"No team has been seen with provider key '{providerKey}'.");
    }

    /// <summary>Points a player key at an own player, e.g. the old player after a rename.</summary>
    public void LinkPlayer(string providerKey, int playerId)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO provider_players (provider_key, player_id) VALUES ($key, $player)
            ON CONFLICT (provider_key) DO UPDATE SET player_id = excluded.player_id
            """;
        cmd.Parameters.AddWithValue("$key", providerKey);
        cmd.Parameters.AddWithValue("$player", playerId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Records the key as seen and returns its team, or null while unassigned.</summary>
    int? SeeTeam(SourceTeam team, SourceMap map, SqliteTransaction tx)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.Transaction = tx;
        // Backfills arrive out of order, so name and tournament follow the latest map only.
        cmd.CommandText = """
            INSERT INTO provider_teams (provider_key, name, last_tournament, first_seen_at, last_seen_at)
            VALUES ($key, $name, $tournament, $seen, $seen)
            ON CONFLICT (provider_key) DO UPDATE SET
                name = CASE WHEN excluded.last_seen_at >= last_seen_at THEN excluded.name ELSE name END,
                last_tournament = CASE WHEN excluded.last_seen_at >= last_seen_at
                                       THEN excluded.last_tournament ELSE last_tournament END,
                first_seen_at = min(first_seen_at, excluded.first_seen_at),
                last_seen_at = max(last_seen_at, excluded.last_seen_at)
            RETURNING team_id
            """;
        cmd.Parameters.AddWithValue("$key", team.Key);
        cmd.Parameters.AddWithValue("$name", team.Name);
        cmd.Parameters.AddWithValue("$tournament", map.Tournament);
        cmd.Parameters.AddWithValue("$seen", map.PlayedAt.ToUnixTimeSeconds());
        return cmd.ExecuteScalar() is long id ? (int)id : null;
    }

    int Player(SourcePlayer player, SqliteTransaction tx)
    {
        using (var find = db.Connection.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText = "SELECT player_id FROM provider_players WHERE provider_key = $key";
            find.Parameters.AddWithValue("$key", player.Key);
            if (find.ExecuteScalar() is long existing)
                return (int)existing;
        }

        int id;
        using (var create = db.Connection.CreateCommand())
        {
            create.Transaction = tx;
            create.CommandText = "INSERT INTO players (name) VALUES ($name) RETURNING player_id";
            create.Parameters.AddWithValue("$name", player.Name);
            id = (int)(long)create.ExecuteScalar()!;
        }

        using var link = db.Connection.CreateCommand();
        link.Transaction = tx;
        link.CommandText = "INSERT INTO provider_players (provider_key, player_id) VALUES ($key, $player)";
        link.Parameters.AddWithValue("$key", player.Key);
        link.Parameters.AddWithValue("$player", id);
        link.ExecuteNonQuery();
        return id;
    }
}
