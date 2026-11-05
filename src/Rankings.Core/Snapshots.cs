namespace Rankings.Core;

/// <summary>
/// A day's snapshot of every team and league, as the team_snapshots and region_snapshots tables
/// keep them. Rated teams are ranked by rating; frozen teams keep a row without a rank.
/// </summary>
public static class Snapshots
{
    /// <param name="unrankedTeams">
    /// Teams the admin marked relegated or disbanded as of <paramref name="date"/>: they keep a row
    /// and their rating, but no rank, even after playing again (which unfreezes their rating).
    /// </param>
    public static DaySnapshot Take(RatingState state, DateOnly date, RatingSettings settings, DaySnapshot? previous,
        IReadOnlySet<int>? unrankedTeams = null)
    {
        var previousTeams = previous?.Teams.ToDictionary(team => team.TeamId, team => team.Rating) ?? [];
        var previousLeagues = previous?.Leagues.ToDictionary(league => league.LeagueId, league => league.Rating) ?? [];

        var ranks = Replay.Ranking(state)
            .Where(teamId => unrankedTeams is null || !unrankedTeams.Contains(teamId))
            .Select((teamId, index) => (teamId, rank: index + 1))
            .ToDictionary(pair => pair.teamId, pair => pair.rank);
        var teams = state.Teams.Values
            .Where(team => team.Seeded)
            .OrderBy(team => ranks.GetValueOrDefault(team.TeamId, int.MaxValue))
            .ThenBy(team => team.TeamId)
            .Select(team =>
            {
                var rating = state.Rating(team);
                return new TeamSnapshot(date, team.TeamId, ranks.TryGetValue(team.TeamId, out var rank) ? rank : null,
                    rating, team.Offset, state.Leagues[team.League],
                    previousTeams.TryGetValue(team.TeamId, out var before) ? rating - before : 0,
                    team.RatedMaps, team.IsProvisional(settings));
            })
            .ToList();
        var leagues = state.Leagues
            .OrderBy(league => league.Key, StringComparer.Ordinal)
            .Select(league => new LeagueSnapshot(date, league.Key, league.Value,
                previousLeagues.TryGetValue(league.Key, out var before) ? league.Value - before : 0))
            .ToList();
        return new DaySnapshot(date, teams, leagues);
    }
}

public sealed record DaySnapshot(DateOnly Date, IReadOnlyList<TeamSnapshot> Teams, IReadOnlyList<LeagueSnapshot> Leagues);

/// <param name="Rank">Place among rated teams; null while relegated or disbanded.</param>
/// <param name="Change">Rating change since the previous snapshot; 0 on a team's first.</param>
public sealed record TeamSnapshot(
    DateOnly Date, int TeamId, int? Rank, double Rating, double Offset, double LeagueRating, double Change,
    int RatedMaps, bool Provisional);

public sealed record LeagueSnapshot(DateOnly Date, string LeagueId, double Rating, double Change);
