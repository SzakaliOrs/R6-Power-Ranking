using Rankings.Core;

namespace Rankings.Tests;

public class RatingEngineTests
{
    static readonly DateTimeOffset Day = new(2026, 3, 1, 15, 0, 0, TimeSpan.Zero);

    // Δ for equal teams on a 7–5 map: K · (1 + 0.5 ln 2) · 0.5.
    static readonly double EvenTwoRoundFactor = (1 + 0.5 * Math.Log(2)) * 0.5;

    static readonly RatingEngine Engine = new(new RatingSettings());

    /// <summary>Teams 1–4 in EU, 5–6 in NA, 7 in ASIA and 8 in OCE.</summary>
    static RatingState State()
    {
        var state = new RatingState();
        foreach (var league in (string[])["EU", "NA", "ASIA", "OCE"])
            state.AddLeague(league, 1500);
        foreach (var (team, league) in (ReadOnlySpan<(int, string)>)[(1, "EU"), (2, "EU"), (3, "EU"), (4, "EU"),
                     (5, "NA"), (6, "NA"), (7, "ASIA"), (8, "OCE")])
            state.AddTeam(team, league);
        return state;
    }

    static MapRecord Map(int teamA, int teamB, int roundsA, int roundsB, string series = "s1", int number = 1,
        DateTimeOffset? playedAt = null, int bestOf = 3, int[]? playersA = null, int[]? playersB = null) =>
        new($"{series}-{number}", series, playedAt ?? Day, number, bestOf, teamA, teamB, roundsA, roundsB,
            playersA ?? [], playersB ?? []);

    [Fact]
    public void RateMap_EqualTeamsMoveByHalfOfK()
    {
        var state = State();

        Engine.RateMap(state, Map(1, 2, 7, 5));

        Assert.Equal(16 * EvenTwoRoundFactor, state.Team(1).Offset, 9);
        Assert.Equal(-16 * EvenTwoRoundFactor, state.Team(2).Offset, 9);
    }

    [Fact]
    public void RateMap_TeamBWinningMovesTheOtherWay()
    {
        var state = State();

        Engine.RateMap(state, Map(1, 2, 5, 7));

        Assert.Equal(-16 * EvenTwoRoundFactor, state.Team(1).Offset, 9);
        Assert.Equal(16 * EvenTwoRoundFactor, state.Team(2).Offset, 9);
    }

    [Fact]
    public void RateMap_AnUpsetMovesMoreThanTheFavouriteWinning()
    {
        var upset = State();
        upset.Team(1).Offset = 100;
        Engine.RateMap(upset, Map(1, 2, 5, 7));

        var favourite = State();
        favourite.Team(1).Offset = 100;
        Engine.RateMap(favourite, Map(1, 2, 7, 5));

        // 100 points apart, 7–5: 16 · M · (S − E) with E = 0.360 or 0.640.
        Assert.Equal(14.447, upset.Team(2).Offset, 3);
        Assert.Equal(7.418, favourite.Team(1).Offset - 100, 3);
    }

    [Fact]
    public void RateMap_OvertimeMarginIsTheActualRoundDifference()
    {
        var eightSix = State();
        Engine.RateMap(eightSix, Map(1, 2, 8, 6));
        var sevenFive = State();
        Engine.RateMap(sevenFive, Map(1, 2, 7, 5));
        var eightSeven = State();
        Engine.RateMap(eightSeven, Map(1, 2, 8, 7));

        Assert.Equal(sevenFive.Team(1).Offset, eightSix.Team(1).Offset, 12);
        Assert.Equal(8, eightSeven.Team(1).Offset, 12);
    }

    [Fact]
    public void RateMap_RegionalMapLeavesLeagueRatingsUnchanged()
    {
        var state = State();

        Engine.RateMap(state, Map(1, 2, 7, 0));

        Assert.All(state.Leagues.Values, rating => Assert.Equal(1500, rating));
    }

    [Fact]
    public void RateMap_InternationalMapMovesOffsetsWithKIntAndLeaguesWithKLeague()
    {
        var state = State();

        Engine.RateMap(state, Map(1, 5, 7, 5));

        Assert.Equal(32 * EvenTwoRoundFactor, state.Team(1).Offset, 9);
        Assert.Equal(-32 * EvenTwoRoundFactor, state.Team(5).Offset, 9);
        Assert.Equal(1500 + 3 * EvenTwoRoundFactor, state.Leagues["EU"], 9);
        Assert.Equal(1500 - 3 * EvenTwoRoundFactor, state.Leagues["NA"], 9);
        Assert.Equal(1500, state.Leagues["ASIA"]);
    }

    [Fact]
    public void RateMap_TwoApacLeaguesCountAsInternational()
    {
        var state = State();

        Engine.RateMap(state, Map(7, 8, 7, 5));

        Assert.Equal(32 * EvenTwoRoundFactor, state.Team(7).Offset, 9);
        Assert.Equal(1500 + 3 * EvenTwoRoundFactor, state.Leagues["ASIA"], 9);
        Assert.Equal(1500 - 3 * EvenTwoRoundFactor, state.Leagues["OCE"], 9);
    }

    [Fact]
    public void RateMap_CountsTheMapAndRecordsWhenEachTeamPlayed()
    {
        var state = State();

        Engine.RateMap(state, Map(1, 2, 7, 5));

        Assert.Equal(1, state.Team(1).RatedMaps);
        Assert.Equal(Day, state.Team(2).LastPlayedAt);
        Assert.Equal(0, state.Team(3).RatedMaps);
    }

    [Fact]
    public void RateMap_RefusesAnUnknownTeam()
    {
        Assert.Throws<KeyNotFoundException>(() => Engine.RateMap(State(), Map(1, 99, 7, 5)));
    }

    [Fact]
    public void Rate_SkipsAMapWithNoWinner()
    {
        var state = State();

        var run = Engine.Rate(state, [Map(1, 2, 6, 6), Map(1, 2, 7, 5, number: 2)], Day);

        Assert.Equal(1, run.Rated);
        Assert.Equal(new SkippedMap("s1-1", RatingEngine.NoWinner), Assert.Single(run.Skipped));
        Assert.Equal(1, state.Team(1).RatedMaps);
    }

    [Fact]
    public void Rate_RecentresEachLeagueOnZero()
    {
        var state = State();

        Engine.Rate(state, [Map(1, 5, 7, 5), Map(2, 3, 7, 1, "s2")], Day);

        foreach (var league in state.Teams.Values.GroupBy(t => t.League))
            Assert.Equal(0, league.Sum(t => t.Offset), 9);
        // EU took the international win, so its rating now carries the gain.
        Assert.True(state.Leagues["EU"] > 1500);
    }

    [Fact]
    public void Recentre_NeverChangesATeamsRating()
    {
        var state = State();
        var random = new Random(7);
        foreach (var team in state.Teams.Values)
            team.Offset = random.Next(-200, 200);
        var before = state.Teams.Keys.ToDictionary(id => id, state.Rating);

        RatingEngine.Recentre(state);

        foreach (var (id, rating) in before)
            Assert.Equal(rating, state.Rating(id), 9);
    }

    [Fact]
    public void Rate_ReplayingTheSameMapsGivesIdenticalRatings()
    {
        List<MapRecord> maps =
        [
            Map(1, 5, 7, 5, "a", 1, Day), Map(1, 5, 3, 7, "a", 2, Day), Map(1, 5, 8, 7, "a", 3, Day),
            Map(2, 3, 7, 2, "b", 1, Day), Map(7, 8, 7, 6, "c", 1, Day.AddDays(1)),
            Map(4, 6, 2, 7, "d", 1, Day.AddDays(2)), Map(1, 2, 7, 4, "e", 1, Day.AddDays(3)),
        ];
        var first = State();
        Engine.Rate(first, maps, Day.AddDays(3));
        var second = State();
        Engine.Rate(second, Enumerable.Reverse(maps), Day.AddDays(3));

        Assert.Equal(first.Leagues, second.Leagues);
        Assert.Equal(first.Teams.Values.Select(t => t.Offset), second.Teams.Values.Select(t => t.Offset));
    }
}
