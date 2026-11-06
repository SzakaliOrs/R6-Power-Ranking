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

    // A core signed by another team

    /// <summary>
    /// Team 1 at +100 with 40 maps, last played a week ago, holds <see cref="LineupOne"/>, whose
    /// players remember it; team 3 has its own lineup.
    /// </summary>
    static RatingState StateWithCoreToSign()
    {
        var state = State();
        state.Team(1).Offset = 100;
        state.Team(1).RatedMaps = 40;
        state.Team(1).LastPlayedAt = Day.AddDays(-7);
        state.Team(1).Lineup = LineupOne;
        state.Team(2).Lineup = LineupTwo;
        state.Team(3).Lineup = [31, 32, 33, 34, 35];
        foreach (var playerId in LineupOne)
            state.SetPlayer(playerId, new PlayerRating(1, 100, 0, 40));
        return state;
    }

    [Fact]
    public void CoreTakeover_ATeamSigningAnotherTeamsCoreTakesOverItsRating()
    {
        var map = Map(3, 2, 7, 5, playersA: [11, 12, 13, 14, 36], playersB: LineupTwo);
        var rated = StateWithCoreToSign();
        // Team 3 continues team 1's rating with one new player's pull; team 1 drops out unrated.
        var byHand = State();
        byHand.Team(1).Offset = 100;
        byHand.Team(1).Seeded = false;
        byHand.Team(3).Offset = 100 * (1 - Settings.NewPlayerPull);
        byHand.Team(3).BoostMapsLeft = Settings.RosterChangeBoostMaps;

        var run = Engine.Rate(rated, [map], Day);
        RateByHand(byHand, map);

        AssertSameOffsets(byHand, rated);
        Assert.Equal(
            [
                new RosterEvent(Day, 1, RosterEventKind.CoreLost, OtherTeamId: 3),
                new RosterEvent(Day, 3, RosterEventKind.TookOverCore, NewPlayers: 1, OtherTeamId: 1),
            ],
            run.Events);
        Assert.False(rated.Team(1).Seeded);
        Assert.Empty(rated.Team(1).Lineup);
        Assert.Equal(41, rated.Team(3).RatedMaps);
        Assert.False(rated.Team(3).IsProvisional(Settings));
        Assert.DoesNotContain(1, Replay.Ranking(rated));
    }

    [Fact]
    public void CoreTakeover_TheTeamThatLostItsCoreIsSeededFromItsNewPlayers()
    {
        var state = StateWithCoreToSign();
        Engine.Rate(state, [Map(3, 2, 7, 5, playersA: LineupOne, playersB: LineupTwo)], Day);
        var teamFourOffset = state.Team(4).Offset;
        var predictions = new List<double>();

        var run = Engine.Rate(state, [Map(1, 4, 7, 5, "s2", playedAt: Day.AddDays(1), playersA: [41, 42, 43, 44, 45])],
            Day.AddDays(1), (_, chanceTeamAWins) => predictions.Add(chanceTeamAWins));

        Assert.Equal(new RosterEvent(Day.AddDays(1), 1, RosterEventKind.NewCore), Assert.Single(run.Events));
        Assert.Equal(Elo.Expected(-Settings.NewcomerGap, teamFourOffset), Assert.Single(predictions), 9);
        Assert.True(state.Team(1).IsProvisional(Settings));
    }

    [Fact]
    public void CoreTakeover_AfterTheFormerTeamFieldedANewCoreThePlayersBringTheRatingTheyLeftWith()
    {
        var state = StateWithCoreToSign();
        var predictions = new List<double>();

        // Team 1 plays first with five newcomers, then team 3 fields team 1's old core.
        var run = Engine.Rate(state,
            [
                Map(1, 2, 7, 5, "s1", playedAt: Day, playersA: [41, 42, 43, 44, 45], playersB: LineupTwo),
                Map(3, 4, 7, 5, "s2", playedAt: Day.AddHours(3), playersA: LineupOne),
            ],
            Day.AddDays(1), (_, chanceTeamAWins) => predictions.Add(chanceTeamAWins));

        Assert.Equal(Elo.Expected(1600, 1500), predictions[1], 9);
        Assert.Contains(new RosterEvent(Day.AddHours(3), 3, RosterEventKind.TookOverCore, NewPlayers: 0, OtherTeamId: 1), run.Events);
        Assert.DoesNotContain(run.Events, rosterEvent => rosterEvent.Kind == RosterEventKind.CoreLost);
        Assert.True(state.Team(1).Seeded);
        Assert.Equal(41, state.Team(3).RatedMaps);
    }

    [Fact]
    public void CoreTakeover_ADisbandedTeamsCoreKeepsItsRatingAtItsNewTeam()
    {
        var state = StateWithCoreToSign();
        RatingEngine.Freeze(state, 1);
        state.AddNewTeam(9, "EU");
        var predictions = new List<double>();

        var run = Engine.Rate(state, [Map(9, 2, 7, 5, playersA: LineupOne, playersB: LineupTwo)], Day,
            (_, chanceTeamAWins) => predictions.Add(chanceTeamAWins));

        Assert.Equal(Elo.Expected(1600, 1500), Assert.Single(predictions), 9);
        Assert.Equal(
            [
                new RosterEvent(Day, 1, RosterEventKind.CoreLost, OtherTeamId: 9),
                new RosterEvent(Day, 9, RosterEventKind.TookOverCore, NewPlayers: 0, OtherTeamId: 1),
            ],
            run.Events);
        Assert.False(state.Team(1).IsFrozen);
        Assert.False(state.Team(1).Seeded);
    }

    /// <summary>+100 after 200 days away: 110 days past the threshold start 4 periods, each closing 10% of the gap to −50.</summary>
    static readonly double HundredAfterTwoHundredDaysAway = -Settings.NewcomerGap + (100 + Settings.NewcomerGap) * Math.Pow(1 - Settings.InactivityDecay, 4);

    [Fact]
    public void CoreTakeover_ACoreBackAfterTimeAwayDecaysForIt()
    {
        var state = StateWithCoreToSign();
        // Team 1 has fielded a new core since; its old core last played 200 days ago.
        state.Team(1).CoreGeneration = 1;
        foreach (var playerId in LineupOne)
            state.SetPlayer(playerId, new PlayerRating(1, 100, 0, 40, Day.AddDays(-200)));
        var predictions = new List<double>();

        Engine.Rate(state, [Map(3, 4, 7, 5, playersA: LineupOne)], Day, (_, chanceTeamAWins) => predictions.Add(chanceTeamAWins));

        Assert.Equal(Elo.Expected(1500 + HundredAfterTwoHundredDaysAway, 1500), Assert.Single(predictions), 9);
    }

    [Fact]
    public void CoreTakeover_ADisbandedTeamsCoreDecaysForTheTimeAway()
    {
        var state = StateWithCoreToSign();
        state.Team(1).LastPlayedAt = Day.AddDays(-200);
        RatingEngine.Freeze(state, 1);
        state.AddNewTeam(9, "EU");
        var predictions = new List<double>();

        Engine.Rate(state, [Map(9, 2, 7, 5, playersA: LineupOne, playersB: LineupTwo)], Day,
            (_, chanceTeamAWins) => predictions.Add(chanceTeamAWins));

        Assert.Equal(Elo.Expected(1500 + HundredAfterTwoHundredDaysAway, 1500), Assert.Single(predictions), 9);
    }

    [Fact]
    public void CoreTakeover_ACoreMovingToAnotherLeagueKeepsItsRatingNotItsOffset()
    {
        var state = new RatingState();
        state.AddLeague("EU", 1500);
        state.AddLeague("NA", 1450);
        state.AddTeam(1, "EU", 100).Lineup = LineupOne;
        state.AddTeam(2, "EU").Lineup = LineupTwo;
        state.AddTeam(5, "NA").Lineup = [51, 52, 53, 54, 55];
        foreach (var playerId in LineupOne)
            state.SetPlayer(playerId, new PlayerRating(1, 100));
        var predictions = new List<double>();

        Engine.Rate(state, [Map(5, 2, 7, 5, playersA: LineupOne, playersB: LineupTwo)], Day,
            (_, chanceTeamAWins) => predictions.Add(chanceTeamAWins));

        // 1600 in EU is 150 above NA's 1450.
        Assert.Equal(Elo.Expected(1600, 1500), Assert.Single(predictions), 9);
    }

    [Fact]
    public void CoreTakeover_OnlyTheFirstOfTwoTeamsSplittingASixPlayerCoreTakesItOver()
    {
        var state = StateWithCoreToSign();
        state.Team(1).Lineup = [11, 12, 13, 14, 15, 16];
        state.SetPlayer(16, new PlayerRating(1, 100, 0, 40));
        state.Team(4).Lineup = [41, 42, 43, 44, 45];

        var run = Engine.Rate(state,
            [
                Map(3, 2, 7, 5, "s1", playedAt: Day, playersA: [11, 12, 13, 51, 52], playersB: LineupTwo),
                Map(4, 2, 7, 5, "s2", playedAt: Day.AddHours(3), playersA: [14, 15, 16, 61, 62], playersB: LineupTwo),
            ],
            Day.AddDays(1));

        Assert.Equal(3, Assert.Single(run.Events, rosterEvent => rosterEvent.Kind == RosterEventKind.TookOverCore).TeamId);
        Assert.Contains(new RosterEvent(Day.AddHours(3), 4, RosterEventKind.NewCore), run.Events);
    }

    [Fact]
    public void CoreTakeover_NoneWhenTheFormerTeamCarriedOnWithACoreOfOtherPlayers()
    {
        // Team 1 was listed with six; it keeps three of them, and team 3 signs the other three.
        int[] sixListed = [11, 12, 13, 14, 15, 16];
        var state = StateWithCoreToSign();
        state.Team(1).Lineup = sixListed;
        state.SetPlayer(16, new PlayerRating(1, 100, 0, 40));

        var run = Engine.Rate(state,
            [
                Map(1, 2, 7, 5, "s1", playedAt: Day, playersA: [11, 12, 13, 41, 42], playersB: LineupTwo),
                Map(3, 4, 7, 5, "s2", playedAt: Day.AddHours(3), playersA: [14, 15, 16, 51, 52]),
            ],
            Day.AddDays(1));

        Assert.DoesNotContain(run.Events, rosterEvent => rosterEvent.Kind is RosterEventKind.TookOverCore or RosterEventKind.CoreLost);
        Assert.Contains(new RosterEvent(Day.AddHours(3), 3, RosterEventKind.NewCore), run.Events);
        Assert.True(state.Team(1).Seeded);
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
    public void Seeding_PlayersBackAfterTimeAwayBringADecayedOffset()
    {
        int[] newLineup = [11, 12, 13, 41, 42];
        var map = Map(9, 2, 7, 5, playersA: newLineup);
        var rated = State();
        rated.AddNewTeam(9, "EU");
        // Their team last played 200 days ago: 110 days past the threshold start 4 decay periods.
        foreach (var playerId in (int[])[11, 12, 13])
            rated.SetPlayer(playerId, new PlayerRating(1, 80, 0, 40, Day.AddDays(-200)));
        var decayedOffset = -Settings.NewcomerGap + (80 + Settings.NewcomerGap) * Math.Pow(1 - Settings.InactivityDecay, 4);
        var byHand = State();
        byHand.AddTeam(9, "EU", (3 * Settings.SeedingShare * decayedOffset - 2 * Settings.NewcomerGap) / 5);

        Engine.Rate(rated, [map], Day);
        RateByHand(byHand, map);

        AssertSameOffsets(byHand, rated);
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

        Assert.Equal(new PlayerRating(1, state.Team(1).Offset, state.Team(1).CoreGeneration, 1, Day), state.Players[11]);
        Assert.Equal(new PlayerRating(2, state.Team(2).Offset, state.Team(2).CoreGeneration, 1, Day), state.Players[25]);
    }

    // A league new to the ratings (China in 2026)

    /// <summary>EU at <paramref name="euRating"/> and NA at 1500 with a rated team each; CN with teams 9 and 10 not yet rated.</summary>
    static RatingState StateWithNewLeague(double euRating = 1500, double? entryLevel = null)
    {
        var state = new RatingState();
        state.AddLeague("EU", euRating);
        state.AddLeague("NA", 1500);
        state.AddLeague("CN", 1500);
        state.AddTeam(1, "EU");
        state.AddTeam(5, "NA");
        state.AddNewTeam(9, "CN");
        state.AddNewTeam(10, "CN");
        if (entryLevel is { } level)
            state.SetLeagueEntryLevel("CN", level);
        return state;
    }

    [Fact]
    public void NewLeague_ItsFirstTeamsStartLevelWithItNotAsNewcomers()
    {
        var map = Map(9, 10, 7, 5, playersA: [91, 92, 93, 94, 95], playersB: [101, 102, 103, 104, 105]);
        var rated = StateWithNewLeague();
        var byHand = StateWithNewLeague();
        byHand.Team(9).Offset = 0;
        byHand.Team(9).Seeded = true;
        byHand.Team(10).Offset = 0;
        byHand.Team(10).Seeded = true;

        Engine.Rate(rated, [map], Day);
        RateByHand(byHand, map);

        AssertSameOffsets(byHand, rated);
        Assert.Equal(Day, rated.LeagueEnteredAt["CN"]);
    }

    [Fact]
    public void NewLeague_StartsAtTheAverageEstablishedLeague()
    {
        var state = StateWithNewLeague(euRating: 1540);

        Engine.Rate(state, [Map(9, 10, 7, 5)], Day);

        Assert.Equal(1520, state.Leagues["CN"], 9);
    }

    [Fact]
    public void NewLeague_StartsAtTheAdminsEntryLevelFromTheAverage()
    {
        var state = StateWithNewLeague(euRating: 1540, entryLevel: -40);

        Engine.Rate(state, [Map(9, 10, 7, 5)], Day);

        Assert.Equal(1480, state.Leagues["CN"], 9);
    }

    [Fact]
    public void NewLeague_TeamsJoiningLaterInItsFirstSeasonStillStartLevel()
    {
        var engine = new RatingEngine(Settings, [Day.AddDays(-30), Day.AddDays(200)]);
        var state = StateWithNewLeague();
        engine.Rate(state, [Map(9, 10, 8, 7)], Day);
        state.AddNewTeam(11, "CN");
        var predictions = new List<double>();

        engine.Rate(state, [Map(11, 9, 7, 5, "s2", playedAt: Day.AddDays(10), playersA: [111, 112, 113, 114, 115])],
            Day.AddDays(10), (_, chanceTeamAWins) => predictions.Add(chanceTeamAWins));

        // Level with the league, team 11 faces team 9 at +8: just under even, not a newcomer's 0.42.
        Assert.Equal(Elo.Expected(0, 8), Assert.Single(predictions), 9);
    }

    [Fact]
    public void NewLeague_AfterItsFirstSeasonNewTeamsAreSeededFromTheirPlayersAgain()
    {
        var engine = new RatingEngine(Settings, [Day.AddDays(100)]);
        var state = StateWithNewLeague();
        engine.Rate(state, [Map(9, 10, 8, 7)], Day);
        state.AddNewTeam(11, "CN");
        var predictions = new List<double>();

        engine.Rate(state, [Map(11, 9, 7, 5, "s2", playedAt: Day.AddDays(150), playersA: [111, 112, 113, 114, 115])],
            Day.AddDays(150), (_, chanceTeamAWins) => predictions.Add(chanceTeamAWins));

        // Five newcomers start at the newcomer gap below the league, like a promoted team.
        Assert.True(Assert.Single(predictions) < 0.45);
    }

    [Fact]
    public void NewLeague_AnExistingLeaguesNewTeamIsStillSeededFromItsPlayers()
    {
        var state = StateWithNewLeague();
        state.AddNewTeam(2, "EU");

        Engine.Rate(state, [Map(2, 1, 7, 5, playersA: [21, 22, 23, 24, 25])], Day);

        Assert.False(state.LeagueEnteredAt.ContainsKey("EU"));
        Assert.True(state.Team(2).Offset < state.Team(1).Offset);
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
