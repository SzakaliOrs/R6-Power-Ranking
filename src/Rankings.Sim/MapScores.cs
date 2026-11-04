namespace Rankings.Sim;

/// <summary>
/// Map scores played round by round: first to 7, or first to 8 once the map reaches 6–6,
/// so overtime ends 8–6 or 8–7. A stronger team wins more rounds, so margins carry information.
/// </summary>
public static class MapScores
{
    public static bool IsFinished(int roundsA, int roundsB) =>
        (roundsA == 7 && roundsB <= 5) || (roundsB == 7 && roundsA <= 5) || roundsA == 8 || roundsB == 8;

    /// <summary>Chance of winning the map when each round is won with <paramref name="roundWinChance"/>.</summary>
    public static double MapWinChance(double roundWinChance)
    {
        var fromScore = new double?[9, 9];
        double WinFrom(int roundsA, int roundsB)
        {
            if (IsFinished(roundsA, roundsB))
                return roundsA > roundsB ? 1 : 0;
            return fromScore[roundsA, roundsB] ??=
                roundWinChance * WinFrom(roundsA + 1, roundsB) + (1 - roundWinChance) * WinFrom(roundsA, roundsB + 1);
        }
        return WinFrom(0, 0);
    }

    /// <summary>The round win chance that gives a map win chance of <paramref name="mapWinChance"/>.</summary>
    public static double RoundWinChanceFor(double mapWinChance)
    {
        double low = 0, high = 1;
        for (var step = 0; step < 50; step++)
        {
            var middle = (low + high) / 2;
            if (MapWinChance(middle) < mapWinChance)
                low = middle;
            else
                high = middle;
        }
        return (low + high) / 2;
    }

    /// <summary>Plays one map round by round.</summary>
    public static (int RoundsA, int RoundsB) Play(double mapWinChance, Random random)
    {
        var roundWinChance = RoundWinChanceFor(mapWinChance);
        var (roundsA, roundsB) = (0, 0);
        while (!IsFinished(roundsA, roundsB))
        {
            if (random.NextDouble() < roundWinChance)
                roundsA++;
            else
                roundsB++;
        }
        return (roundsA, roundsB);
    }
}
