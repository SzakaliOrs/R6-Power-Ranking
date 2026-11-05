using System.Text.Json;
using Rankings.Core;

namespace Rankings.Job.Storage;

/// <summary>
/// The whole <see cref="RatingState"/> as JSON, kept in run_state as the checkpoint the next run
/// carries on from. Doubles are written so they read back bit for bit, and teams, players and
/// leagues keep their order, so a run that loads the checkpoint rates exactly as one that never stopped.
/// </summary>
public static class RatingStateJson
{
    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    sealed record StateDocument(
        DateTimeOffset? FirstMapAt,
        DateTimeOffset? SeasonStartedAt,
        DateTimeOffset? WarmUpUntil,
        List<LeagueDocument> Leagues,
        List<TeamDocument> Teams,
        List<PlayerDocument> Players);

    sealed record LeagueDocument(string Id, double Rating, double? EntryLevel, DateTimeOffset? EnteredAt);

    sealed record TeamDocument(
        int Id, string League, double Offset, bool Seeded, int RatedMaps, int BoostMapsLeft,
        DateTimeOffset? LastPlayedAt, int DecayPeriods, double? FrozenRating, List<int> Lineup);

    sealed record PlayerDocument(int Id, int TeamId, double Offset);

    public static string Write(RatingState state) => JsonSerializer.Serialize(new StateDocument(
        state.FirstMapAt,
        state.SeasonStartedAt,
        state.WarmUpUntil,
        [.. state.Leagues.Select(league => new LeagueDocument(league.Key, league.Value,
            state.LeagueEntryLevels.TryGetValue(league.Key, out var level) ? level : null,
            state.LeagueEnteredAt.TryGetValue(league.Key, out var enteredAt) ? enteredAt : null))],
        [.. state.Teams.Values.Select(team => new TeamDocument(team.TeamId, team.League, team.Offset, team.Seeded,
            team.RatedMaps, team.BoostMapsLeft, team.LastPlayedAt, team.DecayPeriods, team.FrozenRating, [.. team.Lineup]))],
        [.. state.Players.Select(player => new PlayerDocument(player.Key, player.Value.TeamId, player.Value.Offset))]),
        Options);

    public static RatingState Read(string json)
    {
        var document = JsonSerializer.Deserialize<StateDocument>(json, Options)
            ?? throw new InvalidDataException("The saved rating state is empty.");
        var state = new RatingState
        {
            FirstMapAt = document.FirstMapAt,
            SeasonStartedAt = document.SeasonStartedAt,
            WarmUpUntil = document.WarmUpUntil,
        };
        foreach (var league in document.Leagues)
        {
            state.AddLeague(league.Id, league.Rating);
            if (league.EntryLevel is { } level)
                state.SetLeagueEntryLevel(league.Id, level);
            if (league.EnteredAt is { } enteredAt)
                state.SetLeagueEnteredAt(league.Id, enteredAt);
        }
        foreach (var saved in document.Teams)
        {
            var team = state.AddNewTeam(saved.Id, saved.League);
            team.Offset = saved.Offset;
            team.Seeded = saved.Seeded;
            team.RatedMaps = saved.RatedMaps;
            team.BoostMapsLeft = saved.BoostMapsLeft;
            team.LastPlayedAt = saved.LastPlayedAt;
            team.DecayPeriods = saved.DecayPeriods;
            team.FrozenRating = saved.FrozenRating;
            team.Lineup = saved.Lineup;
        }
        foreach (var player in document.Players)
            state.SetPlayer(player.Id, new PlayerRating(player.TeamId, player.Offset));
        return state;
    }
}
