using Rankings.Core;

namespace Rankings.Tests;

/// <summary>
/// The rules applied around the per-map update: series bonus, roster core, seeding, season
/// start, freezing, inactivity decay, the provisional flag and the warm-up season. Most tests
/// compare a run with the same maps rated by hand from the state the rule should produce.
/// </summary>
public class RatingRulesTests
{
    static readonly DateTimeOffset Day = new(2026, 3, 1, 15, 0, 0, TimeSpan.Zero);

    static readonly RatingSettings Settings = new();

    static readonly RatingEngine Engine = new(Settings);

    static readonly int[] LineupOne = [11, 12, 13, 14, 15];

    static readonly int[] LineupTwo = [21, 22, 23, 24, 25];

    /// <summary>Teams 1–4 in EU and 5 in NA.</summary>
    static RatingState State()
    {
        var state = new RatingState();
        state.AddLeague("EU", 1500);
        state.AddLeague("NA", 1500);
        for (var teamId = 1; teamId <= 4; teamId++)
            state.AddTeam(teamId, "EU");
        state.AddTeam(5, "NA");
        return state;
    }

    static MapRecord Map(int teamA, int teamB, int roundsA, int roundsB, string series = "s1", int number = 1,
        DateTimeOffset? playedAt = null, int bestOf = 1, int[]? playersA = null, int[]? playersB = null) =>
        new($"{series}-{number}", series, playedAt ?? Day, number, bestOf, teamA, teamB, roundsA, roundsB,
            playersA ?? [], playersB ?? []);

    /// <summary>The per-map updates alone, then re-centring: what a run gives when no rule applies.</summary>
    static void RateByHand(RatingState state, params MapRecord[] maps)
    {
        foreach (var map in maps)
            Engine.RateMap(state, map);
        RatingEngine.Recentre(state);
    }

    static void AssertSameOffsets(RatingState expected, RatingState actual)
    {
        foreach (var (teamId, team) in expected.Teams)
            Assert.Equal(team.Offset, actual.Team(teamId).Offset, 9);
        foreach (var (leagueId, rating) in expected.Leagues)
            Assert.Equal(rating, actual.Leagues[leagueId], 9);
    }

    // Series bonus

    [Fact]
    public void SeriesBonus_EqualTeamsWinningTwoNilGetHalfOfKSeries()
    {
        MapRecord[] series = [Map(1, 2, 7, 5, bestOf: 3), Map(1, 2, 7, 5, number: 2, bestOf: 3)];
        var withBonus = State();
        var withoutBonus = State();

        Engine.Rate(withBonus, series, Day);
        RateByHand(withoutBonus, series);

        Assert.Equal(Settings.KSeries * 0.5, withBonus.Team(1).Offset - withoutBonus.Team(1).Offset, 9);
        Assert.Equal(-Settings.KSeries * 0.5, withBonus.Team(2).Offset - withoutBonus.Team(2).Offset, 9);
    }

    [Fact]
    public void SeriesBonus_ScalesWithHowUnlikelyTheSeriesWinWas()
    {
        MapRecord[] upsetSeries = [Map(1, 2, 5, 7, bestOf: 3), Map(1, 2, 5, 7, number: 2, bestOf: 3)];
        var withBonus = State();
        withBonus.Team(1).Offset = 100;
        var withoutBonus = State();
        withoutBonus.Team(1).Offset = 100;

        Engine.Rate(withBonus, upsetSeries, Day);
        RateByHand(withoutBonus, upsetSeries);

        var seriesWinChance = Elo.SeriesExpected(Elo.Expected(1500, 1600), 3);
        Assert.Equal(Settings.KSeries * (1 - seriesWinChance), withBonus.Team(2).Offset - withoutBonus.Team(2).Offset, 9);
        Assert.True(1 - seriesWinChance > 0.5);
    }

    [Fact]
    public void SeriesBonus_NoneForABestOfOne()
    {
        var map = Map(1, 2, 7, 5);
        var rated = State();
        var byHand = State();

        Engine.Rate(rated, [map], Day);
        RateByHand(byHand, map);

        AssertSameOffsets(byHand, rated);
    }

    [Fact]
    public void SeriesBonus_NoneWhenASkippedMapLeavesTheSeriesUndecided()
    {
        var first = Map(1, 2, 7, 5, bestOf: 3);
        var third = Map(1, 2, 5, 7, number: 3, bestOf: 3);
        var rated = State();
        var byHand = State();

        Engine.Rate(rated, [first, Map(1, 2, 6, 6, number: 2, bestOf: 3), third], Day);
        RateByHand(byHand, first, third);

        AssertSameOffsets(byHand, rated);
    }

    // Roster core

    [Fact]
    public void RosterChange_OneNewPlayerPullsTheOffsetAndStartsTheBoost()
    {
        var map = Map(1, 2, 7, 5, playersA: [11, 12, 13, 14, 16], playersB: LineupTwo);
        var rated = State();
        rated.Team(1).Offset = 100;
        rated.Team(1).Lineup = LineupOne;
        rated.Team(2).Lineup = LineupTwo;
        var byHand = State();
        byHand.Team(1).Offset = 100 * (1 - Settings.NewPlayerPull);
        byHand.Team(1).BoostMapsLeft = Settings.RosterChangeBoostMaps;

        var run = Engine.Rate(rated, [map], Day);
        RateByHand(byHand, map);

        AssertSameOffsets(byHand, rated);
        Assert.Equal(new RosterEvent(Day, 1, RosterEventKind.RosterChange, NewPlayers: 1), Assert.Single(run.Events));
        Assert.Equal(Settings.RosterChangeBoostMaps - 1, rated.Team(1).BoostMapsLeft);
        Assert.Equal([11, 12, 13, 14, 16], rated.Team(1).Lineup);
    }

    [Fact]
    public void RosterChange_SameLineupChangesNothing()
    {
        var map = Map(1, 2, 7, 5, playersA: LineupOne, playersB: LineupTwo);
        var rated = State();
        rated.Team(1).Lineup = LineupOne;
        rated.Team(2).Lineup = LineupTwo;
        var byHand = State();

        var run = Engine.Rate(rated, [map], Day);
        RateByHand(byHand, map);

        AssertSameOffsets(byHand, rated);
        Assert.Empty(run.Events);
    }

    [Fact]
    public void Boost_RaisesTheTeamsKUntilItsMapsAreUsed()
    {
        var state = State();
        state.Team(1).BoostMapsLeft = 1;
        var evenTwoRoundFactor = (1 + 0.5 * Math.Log(2)) * 0.5;

        Engine.RateMap(state, Map(1, 2, 7, 5));
        Assert.Equal(Settings.KRegional * Settings.RosterChangeBoost * evenTwoRoundFactor, state.Team(1).Offset, 9);
        Assert.Equal(-Settings.KRegional * evenTwoRoundFactor, state.Team(2).Offset, 9);
        Assert.Equal(0, state.Team(1).BoostMapsLeft);
    }

    [Fact]
    public void NewCore_KeepingFewerThanThreePlayersSeedsTheTeamAgain()
    {
        int[] newLineup = [11, 12, 31, 32, 33];
        var map = Map(1, 2, 7, 5, playersA: newLineup, playersB: LineupTwo);
        var rated = State();
        rated.Team(1).Offset = 100;
        rated.Team(1).RatedMaps = 40;
        rated.Team(1).Lineup = LineupOne;
        rated.Team(2).Lineup = LineupTwo;
        rated.SetPlayer(11, new PlayerRating(1, 100));
        rated.SetPlayer(12, new PlayerRating(1, 100));
        // Two players carry half of 100, three newcomers count as the newcomer gap: (2·50 − 3·50) / 5.
        var byHand = State();
        byHand.Team(1).Offset = -10;

        var run = Engine.Rate(rated, [map], Day);
        RateByHand(byHand, map);

        AssertSameOffsets(byHand, rated);
        Assert.Equal(RosterEventKind.NewCore, Assert.Single(run.Events).Kind);
        Assert.Equal(1, rated.Team(1).RatedMaps);
        Assert.True(rated.Team(1).IsProvisional(Settings));
    }

    // Seeding

    [Fact]
    public void Seeding_ANewTeamStartsFromItsPlayersPreviousOffsets()
    {
        int[] newLineup = [11, 12, 13, 41, 42];
        var map = Map(9, 2, 7, 5, playersA: newLineup);
        var rated = State();
        rated.AddNewTeam(9, "EU");
        foreach (var playerId in (int[])[11, 12, 13])
            rated.SetPlayer(playerId, new PlayerRating(1, 80));
        // Three players carry half of 80, two newcomers count as the newcomer gap: (3·40 − 2·50) / 5.
        var byHand = State();
        byHand.AddTeam(9, "EU", 4);

        var run = Engine.Rate(rated, [map], Day);
        RateByHand(byHand, map);

        AssertSameOffsets(byHand, rated);
        Assert.Equal(new RosterEvent(Day, 9, RosterEventKind.Seeded), Assert.Single(run.Events));
        Assert.Equal(newLineup, rated.Team(9).Lineup);
    }

    [Fact]
    public void Seeding_ATeamOfNewcomersStartsAtTheNewcomerGap()
    {
        var map = Map(9, 2, 7, 5, playersA: [41, 42, 43, 44, 45]);
        var rated = State();
        rated.AddNewTeam(9, "EU");
        var byHand = State();
        byHand.AddTeam(9, "EU", -Settings.NewcomerGap);

        Engine.Rate(rated, [map], Day);
        RateByHand(byHand, map);

        AssertSameOffsets(byHand, rated);
    }

    [Fact]
    public void Seeding_NotesANewTeamHoldingAnotherTeamsCore()
    {
        var rated = State();
        rated.Team(1).Lineup = LineupOne;
        rated.AddNewTeam(9, "EU");

        var run = Engine.Rate(rated, [Map(9, 2, 7, 5, playersA: [11, 12, 13, 41, 42])], Day);

        Assert.Contains(new RosterEvent(Day, 9, RosterEventKind.SharesCore, OtherTeamId: 1), run.Events);
    }

    [Fact]
    public void Rate_RemembersEachPlayersLastOffsetForTheirNextTeam()
    {
        var state = State();
        state.Team(1).Lineup = LineupOne;

        Engine.Rate(state, [Map(1, 2, 7, 5, playersA: LineupOne, playersB: LineupTwo)], Day);

        Assert.Equal(new PlayerRating(1, state.Team(1).Offset), state.Players[11]);
        Assert.Equal(new PlayerRating(2, state.Team(2).Offset), state.Players[25]);
    }

    // Season start

    [Fact]
    public void SeasonStart_PullsEveryOffsetTowardZeroOnce()
    {
        var engine = new RatingEngine(Settings, [Day]);
        var state = State();
        state.Team(1).Offset = 100;
        state.Team(2).Offset = -100;

        engine.Rate(state, [], Day.AddDays(1));
        engine.Rate(state, [], Day.AddDays(2));

        Assert.Equal(100 * (1 - Settings.SeasonStartPull), state.Team(1).Offset, 9);
        Assert.Equal(-100 * (1 - Settings.SeasonStartPull), state.Team(2).Offset, 9);
        Assert.Equal(Day, state.SeasonStartedAt);
        Assert.Equal(1500, state.Leagues["EU"], 9);
    }

    [Fact]
    public void SeasonStart_WaitsForItsDate()
    {
        var engine = new RatingEngine(Settings, [Day.AddDays(10)]);
        var state = State();
        state.Team(1).Offset = 100;
        state.Team(2).Offset = -100;

        engine.Rate(state, [], Day);

        Assert.Equal(100, state.Team(1).Offset, 9);
        Assert.Null(state.SeasonStartedAt);
    }

    // Freezing and returning

    [Fact]
    public void Freeze_KeepsTheRatingWhileItsLeagueMoves()
    {
        var state = State();
        state.Team(2).Offset = 50;
        RatingEngine.Freeze(state, 2);

        Engine.Rate(state, [Map(1, 5, 7, 0), Map(3, 5, 7, 0, "s2")], Day);

        Assert.True(state.Leagues["EU"] > 1500);
        Assert.Equal(1550, state.Rating(2), 9);
        Assert.True(state.Team(2).IsFrozen);
    }

    [Fact]
    public void Return_AFrozenTeamThatPlaysAgainResumesItsRatingWithTheBoost()
    {
        var state = State();
        state.Team(2).Offset = 50;
        state.Team(2).LastPlayedAt = Day.AddDays(-10);
        RatingEngine.Freeze(state, 2);
        Engine.Rate(state, [Map(1, 5, 7, 0)], Day);
        var frozenRating = state.Rating(2);

        var run = Engine.Rate(state, [Map(2, 3, 7, 5, "s2", playedAt: Day.AddDays(1))], Day.AddDays(1));

        Assert.Equal(new RosterEvent(Day.AddDays(1), 2, RosterEventKind.Returned), Assert.Single(run.Events));
        Assert.False(state.Team(2).IsFrozen);
        Assert.Equal(Settings.RosterChangeBoostMaps - 1, state.Team(2).BoostMapsLeft);
        Assert.True(state.Rating(2) > frozenRating);
    }

    // Inactivity decay

    /// <summary>Team 3 at +100 last played on <see cref="Day"/>; EU still averages zero.</summary>
    static RatingState StateWithIdleTeam()
    {
        var state = State();
        state.Team(3).Offset = 100;
        state.Team(3).LastPlayedAt = Day;
        state.Team(4).Offset = -100;
        return state;
    }

    [Fact]
    public void Decay_NoneUpToTheThreshold()
    {
        var state = StateWithIdleTeam();

        Engine.Rate(state, [], Day.AddDays(Settings.InactivityThresholdDays));

        Assert.Equal(1600, state.Rating(3), 9);
    }

    [Fact]
    public void Decay_PastTheThresholdPullsTowardTheNewcomerLevelOncePerPeriod()
    {
        var state = StateWithIdleTeam();

        Engine.Rate(state, [], Day.AddDays(100));
        Engine.Rate(state, [], Day.AddDays(100));

        // 10% of the 150-point gap to the newcomer level (−50).
        Assert.Equal(1585, state.Rating(3), 9);
        Assert.Equal(1, state.Team(3).DecayPeriods);
    }

    [Fact]
    public void Decay_EachFurtherPeriodPullsAgain()
    {
        var state = StateWithIdleTeam();
        Engine.Rate(state, [], Day.AddDays(100));
        var offset = state.Team(3).Offset;
        var league = state.Leagues["EU"];

        Engine.Rate(state, [], Day.AddDays(121));

        var newcomerOffset = -Settings.NewcomerGap;
        var decayed = newcomerOffset + (offset - newcomerOffset) * (1 - Settings.InactivityDecay);
        Assert.Equal(league + decayed, state.Rating(3), 9);
        Assert.Equal(2, state.Team(3).DecayPeriods);
    }

    [Fact]
    public void Decay_AReturningTeamsFirstMapUsesItsDecayedRating()
    {
        var map = Map(3, 4, 7, 5, playedAt: Day.AddDays(100));
        var rated = StateWithIdleTeam();
        var byHand = StateWithIdleTeam();
        byHand.Team(3).Offset = 85;

        Engine.Rate(rated, [map], Day.AddDays(100));
        RateByHand(byHand, map);

        AssertSameOffsets(byHand, rated);
        Assert.Equal(0, rated.Team(3).DecayPeriods);
    }

    // Provisional flag

    [Theory]
    [InlineData(0, true)]
    [InlineData(9, true)]
    [InlineData(10, false)]
    public void Provisional_UntilTenRatedMaps(int ratedMaps, bool provisional)
    {
        var team = State().Team(1);
        team.RatedMaps = ratedMaps;

        Assert.Equal(provisional, team.IsProvisional(Settings));
    }

    // Warm-up season

    [Fact]
    public void WarmUp_DoublesKUntilItEnds()
    {
        var evenTwoRoundFactor = (1 + 0.5 * Math.Log(2)) * 0.5;
        var duringWarmUp = State();
        duringWarmUp.WarmUpUntil = Day.AddDays(1);
        var afterWarmUp = State();
        afterWarmUp.WarmUpUntil = Day;

        Engine.RateMap(duringWarmUp, Map(1, 2, 7, 5));
        Engine.RateMap(afterWarmUp, Map(1, 2, 7, 5));

        Assert.Equal(Settings.KRegional * Settings.WarmUpKMultiplier * evenTwoRoundFactor, duringWarmUp.Team(1).Offset, 9);
        Assert.Equal(Settings.KRegional * evenTwoRoundFactor, afterWarmUp.Team(1).Offset, 9);
    }

    // Invariants with every rule in play

    [Fact]
    public void Replay_DayByDayWithEveryRuleGivesIdenticalRatingsTwice()
    {
        var engine = new RatingEngine(Settings, [Day.AddDays(60)]);
        List<MapRecord> maps =
        [
            Map(1, 2, 7, 5, "a", 1, Day, 3, LineupOne, LineupTwo),
            Map(1, 2, 3, 7, "a", 2, Day, 3, LineupOne, LineupTwo),
            Map(1, 2, 8, 7, "a", 3, Day, 3, LineupOne, LineupTwo),
            Map(9, 5, 7, 2, "b", 1, Day.AddDays(1), 1, [11, 12, 41, 42, 43], [51, 52, 53, 54, 55]),
            Map(1, 3, 7, 4, "c", 1, Day.AddDays(70), 1, [11, 12, 13, 14, 16], [31, 32, 33, 34, 35]),
            Map(2, 4, 2, 7, "d", 1, Day.AddDays(200), 1, [21, 22, 61, 62, 63], [41, 42, 43, 44, 45]),
        ];

        RatingState Replay()
        {
            var state = State();
            state.AddNewTeam(9, "EU");
            state.WarmUpUntil = Day.AddDays(30);
            for (var day = Day.Date; day <= Day.AddDays(200).Date; day = day.AddDays(1))
            {
                if (day == Day.AddDays(80).Date)
                    RatingEngine.Freeze(state, 9);
                var asOf = new DateTimeOffset(day.AddDays(1), TimeSpan.Zero);
                engine.Rate(state, maps.Where(map => map.PlayedAt.UtcDateTime.Date == day), asOf);
            }
            return state;
        }

        var first = Replay();
        var second = Replay();

        AssertSameOffsets(first, second);
        Assert.Equal(first.Teams.Values.Select(team => team.Offset), second.Teams.Values.Select(team => team.Offset));
        // Re-centring never lets a league's rated teams drift off zero.
        foreach (var league in first.Teams.Values.Where(team => team.Seeded && !team.IsFrozen).GroupBy(team => team.League))
            Assert.Equal(0, league.Sum(team => team.Offset), 9);
    }
}
