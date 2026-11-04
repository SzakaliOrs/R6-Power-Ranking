namespace Rankings.Core;

/// <summary>The per-map Elo formulas: Δ = K · M · (S − E).</summary>
public static class Elo
{
    /// <summary>Rating points per factor of 10 in the odds.</summary>
    const double Scale = 400;

    /// <summary>Converts the winner's lead in rating points into the favourite correction's units.</summary>
    const double LeadFactor = 0.001;

    /// <summary>Chance that a team rated <paramref name="rating"/> wins a map against <paramref name="opponent"/>.</summary>
    public static double Expected(double rating, double opponent) =>
        1 / (1 + Math.Pow(10, (opponent - rating) / Scale));

    /// <summary>
    /// M = (1 + w · ln(margin)) · c / (0.001 · lead + c): a bigger round margin counts more,
    /// a favourite's win counts less and an upset more.
    /// </summary>
    /// <param name="margin">Winner's rounds minus loser's, at least 1.</param>
    /// <param name="lead">Winner's rating minus the loser's before the map; negative on an upset.</param>
    public static double MarginMultiplier(int margin, double lead, RatingSettings settings)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(margin, 1);
        var correction = settings.FavouriteCorrection;
        return (1 + settings.MarginWeight * Math.Log(margin)) * correction / (LeadFactor * lead + correction);
    }

    /// <summary>Chance of winning a best-of-<paramref name="bestOf"/> series when each map is won with chance <paramref name="mapWinChance"/>.</summary>
    public static double SeriesExpected(double mapWinChance, int bestOf)
    {
        if (bestOf < 1 || bestOf % 2 == 0)
            throw new ArgumentOutOfRangeException(nameof(bestOf), bestOf, "A series is best of an odd number of maps.");
        var mapsToWin = bestOf / 2 + 1;
        // Winning the last map after some losses: C(mapsToWin − 1 + losses, losses) · p^mapsToWin · (1 − p)^losses.
        double seriesWinChance = 0, orderings = 1;
        for (var losses = 0; losses < mapsToWin; losses++)
        {
            if (losses > 0)
                orderings = orderings * (mapsToWin - 1 + losses) / losses;
            seriesWinChance += orderings * Math.Pow(mapWinChance, mapsToWin) * Math.Pow(1 - mapWinChance, losses);
        }
        return seriesWinChance;
    }

    /// <summary>Change for the side scoring <paramref name="score"/> (1 win, 0 loss) against an expectation.</summary>
    public static double Delta(double kFactor, double marginMultiplier, double score, double expected) =>
        kFactor * marginMultiplier * (score - expected);
}
