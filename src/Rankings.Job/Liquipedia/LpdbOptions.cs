namespace Rankings.Job.Liquipedia;

public sealed class LpdbOptions
{
    public required string ApiKey { get; init; }

    /// <summary>Liquipedia asks for a user agent naming the project and a contact.</summary>
    public required string UserAgent { get; init; }

    public Uri BaseUri { get; init; } = new("https://api.liquipedia.net/api/v3/");

    public string Wiki { get; init; } = "rainbowsix";

    /// <summary>Liquipedia tiers rated as top level.</summary>
    public IReadOnlyList<int> Tiers { get; init; } = [1, 2];

    /// <summary>No request ever reaches back before this; LPDB itself goes back to 2007.</summary>
    public DateTimeOffset EarliestDate { get; init; } = DefaultEarliestDate;

    public static readonly DateTimeOffset DefaultEarliestDate = new(2023, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Liquipedia tier types left out even within those tiers.</summary>
    public IReadOnlyList<string> ExcludedTierTypes { get; init; } = ["Qualifier", "Showmatch"];

    /// <summary>Matches per call; Liquipedia advises against more than 100.</summary>
    public int PageSize { get; init; } = 100;

    /// <summary>Liquipedia allows at most 60 calls an hour.</summary>
    public int MaxCallsPerHour { get; init; } = 60;
}
