using Rankings.Core;
using Rankings.Sim;

namespace Rankings.Job.Site;

/// <summary>
/// Site data from synthetic seasons, for building and checking the frontend before real data:
/// the seasons are replayed day by day as the daily job would rate them, with a snapshot each day.
/// Team names are made up from two word lists.
/// </summary>
public static class SampleSite
{
    static readonly string[] FirstWords =
    [
        "Crimson", "Silent", "Iron", "Golden", "Frost", "Shadow", "Storm", "Ember", "Azure", "Obsidian",
        "Velvet", "Scarlet", "Polar", "Lunar", "Solar", "Thunder", "Jade", "Onyx", "Cobalt", "Ivory",
    ];

    static readonly string[] SecondWords =
    [
        "Wolves", "Ravens", "Lynx", "Vipers", "Falcons", "Titans", "Foxes", "Owls", "Sharks", "Bears",
        "Hornets", "Cobras", "Stags", "Hawks", "Rhinos", "Panthers", "Orcas", "Bison", "Herons", "Jackals",
    ];

    public static SiteInput Build(SimulationOptions options)
    {
        var world = Simulator.Run(options);
        var history = world.History;
        var settings = new RatingSettings();
        var tournaments = world.Series.ToDictionary(series => series.SeriesId, series => series.Tournament);

        var days = new List<DaySnapshot>();
        var maps = new List<SiteMap>();
        var rosterEvents = new List<RosterEvent>();
        Replay.Run(history, settings, onDayRated: (day, state, run) =>
        {
            days.Add(Snapshots.Take(state, day, settings, days.LastOrDefault()));
            maps.AddRange(run.RatedMaps.Select(rated => new SiteMap(rated, tournaments[rated.Map.SeriesId])));
            rosterEvents.AddRange(run.Events);
        });

        var teams = history.TeamLeagues
            .OrderBy(team => team.Key)
            .Select(team => new SiteTeam(team.Key, TeamName(team.Key), ShortTeamName(team.Key), Logo: null, team.Value))
            .ToList();
        var lastDay = days[^1].Date;
        return new SiteInput(
            UpdatedAt: new DateTimeOffset(lastDay.AddDays(1).ToDateTime(new TimeOnly(4, 17)), TimeSpan.Zero),
            AlgorithmVersion: "sample",
            SeasonStart: history.SeasonStarts.Order().Last(),
            teams, SiteExport.DefaultRegions, days, maps, rosterEvents, SyntheticSetup.DataSource);
    }

    /// <summary>A distinct two-word name for each of the first 400 team IDs.</summary>
    public static string TeamName(int teamId)
    {
        var index = teamId - 1;
        var first = index % FirstWords.Length;
        var second = (index / FirstWords.Length + index) % SecondWords.Length;
        return $"{FirstWords[first]} {SecondWords[second]}";
    }

    public static string ShortTeamName(int teamId) =>
        string.Concat(TeamName(teamId).Split(' ').Select(word => word[..2].ToUpperInvariant()));
}
