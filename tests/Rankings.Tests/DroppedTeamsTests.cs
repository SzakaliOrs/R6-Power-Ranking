using Rankings.Core;

namespace Rankings.Tests;

/// <summary>A team marked relegated or disbanded after the fact drops out after its last map's day.</summary>
public sealed class DroppedTeamsTests
{
    static readonly DateOnly Day = new(2026, 9, 5);

    static MapRecord Map(string series, int teamA, int teamB, int roundsA, int roundsB, int hour) =>
        new($"{series}-1", series, new DateTimeOffset(Day.ToDateTime(new TimeOnly(hour, 0)), TimeSpan.Zero), 1, 1,
            teamA, teamB, roundsA, roundsB, [], []);

    static readonly IReadOnlyList<MapRecord> DaysMaps =
    [
        Map("first", 1, 3, 7, 5, hour: 12),
        Map("second", 2, 3, 5, 7, hour: 15),
        Map("third", 1, 2, 7, 2, hour: 18),
    ];

    static readonly Dictionary<int, string> TeamLeagues = new() { [1] = "EU", [2] = "EU", [3] = "EU" };

    static RatingState RateTheDay(IEnumerable<int> droppedTeams)
    {
        var state = new RatingState();
        new DailyRating(new RatingSettings(), []).RateDay(state, Day, DaysMaps, ["EU"], TeamLeagues,
            new Dictionary<string, double>(), frozenTeams: [], droppedTeams);
        return state;
    }

    [Fact]
    public void DroppedTeam_KeepsItsRatingAndTheOthersKeepWhatTheirMapsAgainstItGaveThem()
    {
        var kept = RateTheDay(droppedTeams: []);
        var dropped = RateTheDay(droppedTeams: [3]);

        Assert.True(dropped.Team(3).IsFrozen);
        Assert.False(kept.Team(3).IsFrozen);
        foreach (var teamId in (int[])[1, 2, 3])
            Assert.Equal(kept.Rating(teamId), dropped.Rating(teamId), 9);
    }

    [Fact]
    public void DroppedTeam_LeavesTheRankingAndItsLeaguesAverage()
    {
        var state = RateTheDay(droppedTeams: [3]);

        var snapshot = Snapshots.Take(state, Day, new RatingSettings(), previous: null);

        Assert.Null(snapshot.Teams.Single(team => team.TeamId == 3).Rank);
        Assert.Equal([1, 2], snapshot.Teams.Where(team => team.Rank is not null).OrderBy(team => team.Rank).Select(team => team.TeamId));
        Assert.Equal((state.Rating(1) + state.Rating(2)) / 2, state.Leagues["EU"], 9);
    }
}
