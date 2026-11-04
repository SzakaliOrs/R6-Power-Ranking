using Rankings.Core;

namespace Rankings.Tests;

public class EloTests
{
    static readonly RatingSettings Settings = new();

    [Fact]
    public void Expected_IsEvenForEqualRatingsAndSumsToOne()
    {
        Assert.Equal(0.5, Elo.Expected(1500, 1500), 12);
        Assert.Equal(1, Elo.Expected(1600, 1500) + Elo.Expected(1500, 1600), 12);
    }

    [Fact]
    public void Expected_FourHundredPointsIsTenToOne()
    {
        Assert.Equal(10.0 / 11, Elo.Expected(1900, 1500), 12);
    }

    [Fact]
    public void MarginMultiplier_IsOneForAOneRoundWinBetweenEqualTeams()
    {
        Assert.Equal(1, Elo.MarginMultiplier(1, 0, Settings), 12);
    }

    [Fact]
    public void MarginMultiplier_GrowsWithTheLogOfTheMargin()
    {
        Assert.Equal(1 + 0.5 * Math.Log(2), Elo.MarginMultiplier(2, 0, Settings), 12);
        Assert.Equal(1 + 0.5 * Math.Log(7), Elo.MarginMultiplier(7, 0, Settings), 12);
    }

    [Fact]
    public void MarginMultiplier_DampsAFavouritesWinAndRaisesAnUpset()
    {
        Assert.Equal(2.2 / 2.3, Elo.MarginMultiplier(1, 100, Settings), 12);
        Assert.Equal(2.2 / 2.1, Elo.MarginMultiplier(1, -100, Settings), 12);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void SeriesExpected_IsEvenForEvenMaps(int bestOf)
    {
        Assert.Equal(0.5, Elo.SeriesExpected(0.5, bestOf), 12);
    }

    [Fact]
    public void SeriesExpected_LongerSeriesFavourTheBetterTeamMore()
    {
        Assert.Equal(0.6, Elo.SeriesExpected(0.6, 1), 12);
        Assert.Equal(0.648, Elo.SeriesExpected(0.6, 3), 12);
        Assert.Equal(0.68256, Elo.SeriesExpected(0.6, 5), 12);
    }

    [Fact]
    public void SeriesExpected_RefusesAnEvenBestOf()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Elo.SeriesExpected(0.5, 2));
    }

    [Fact]
    public void MarginMultiplier_RefusesAMarginBelowOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Elo.MarginMultiplier(0, 0, Settings));
    }
}
