using System.Text.Json;
using Rankings.Core;

namespace Rankings.Job.Site;

/// <param name="Logo">File under logos/; null shows the fallback logo.</param>
public sealed record SiteTeam(int TeamId, string Name, string? ShortName, string? Logo, string League);

/// <param name="ParentId">The display region a league belongs to (APAC for its subregions); null for a display region.</param>
public sealed record SiteRegion(string RegionId, string Name, string? ParentId);

public sealed record SiteMap(RatedMap Rated, string? Tournament);

/// <summary>Everything the site's JSON files are built from.</summary>
/// <param name="SeasonStart">Start of the current season; regions' international records count from it.</param>
/// <param name="Days">Daily snapshots, oldest first; the last one is what the site shows.</param>
/// <param name="Maps">Rated maps the team views and international records draw on.</param>
public sealed record SiteInput(
    DateTimeOffset UpdatedAt,
    string AlgorithmVersion,
    DateTimeOffset? SeasonStart,
    IReadOnlyList<SiteTeam> Teams,
    IReadOnlyList<SiteRegion> Regions,
    IReadOnlyList<DaySnapshot> Days,
    IReadOnlyList<SiteMap> Maps,
    IReadOnlyList<RosterEvent> RosterEvents);

/// <summary>
/// Writes the site's data: one JSON file per tab (rankings, regions, movers), one per team for
/// the team view and a small meta file. The page loads each file when its tab opens; the daily
/// job deploys them with the page to the Worker, so they replace the previous day's set.
/// </summary>
public static class SiteExport
{
    /// <summary>Region rows as the schema seeds them: five display regions, APAC split in three leagues.</summary>
    public static IReadOnlyList<SiteRegion> DefaultRegions { get; } =
    [
        new("EU", "Europe", null), new("NA", "North America", null), new("SA", "South America", null),
        new("CN", "China", null), new("APAC", "Asia-Pacific", null),
        new("ASIA", "Asia", "APAC"), new("OCE", "Oceania", "APAC"), new("APAC-N", "APAC North", "APAC"),
    ];

    const int MoversShown = 10;
    const int RecentMapsShown = 20;
    const int DailyHistoryDays = 365;

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static void Write(SiteInput input, string dataDirectory)
    {
        if (input.Days.Count == 0)
            throw new ArgumentException("There is no snapshot to export.", nameof(input));

        var teamsDirectory = Path.Combine(dataDirectory, "teams");
        if (Directory.Exists(teamsDirectory))
            Directory.Delete(teamsDirectory, recursive: true);
        Directory.CreateDirectory(teamsDirectory);

        var latest = input.Days[^1];
        var teams = input.Teams.ToDictionary(team => team.TeamId);
        var regions = input.Regions.ToDictionary(region => region.RegionId);

        WriteFile(Path.Combine(dataDirectory, "meta.json"), new
        {
            updatedAt = input.UpdatedAt,
            snapshotDate = latest.Date,
            algorithmVersion = input.AlgorithmVersion,
            seasonStart = input.SeasonStart,
            rankedTeams = latest.Teams.Count(team => team.Rank is not null),
        });
        WriteFile(Path.Combine(dataDirectory, "rankings.json"), new
        {
            teams = latest.Teams.Where(team => team.Rank is not null).Select(snapshot => new
            {
                rank = snapshot.Rank,
                rating = Round(snapshot.Rating),
                change = Round(snapshot.Change),
                maps = snapshot.RatedMaps,
                provisional = snapshot.Provisional,
                team = TeamCard(teams[snapshot.TeamId], regions),
            }),
        });
        WriteFile(Path.Combine(dataDirectory, "regions.json"), new { regions = RegionRows(input, latest, regions) });
        WriteFile(Path.Combine(dataDirectory, "movers.json"), new
        {
            periods = new[] { 7, 30 }.Select(days => Movers(input, latest, days, teams, regions)).Where(period => period is not null),
        });

        foreach (var snapshot in latest.Teams)
            WriteFile(Path.Combine(teamsDirectory, $"{snapshot.TeamId}.json"), TeamView(input, snapshot, teams, regions));
    }

    static object TeamCard(SiteTeam team, Dictionary<string, SiteRegion> regions) => new
    {
        id = team.TeamId,
        name = team.Name,
        shortName = team.ShortName,
        logo = team.Logo,
        league = team.League,
        leagueName = regions[team.League].Name,
        region = DisplayRegion(team.League, regions),
    };

    /// <summary>Display regions by rating; one with subregions (APAC) shows their average and lists them beneath it.</summary>
    static IEnumerable<object> RegionRows(SiteInput input, DaySnapshot latest, Dictionary<string, SiteRegion> regions)
    {
        var leagueRatings = latest.Leagues.ToDictionary(league => league.LeagueId);
        var teamLeagues = input.Teams.ToDictionary(team => team.TeamId, team => team.League);
        var rankedPerLeague = latest.Teams.Where(team => team.Rank is not null).ToLookup(team => teamLeagues[team.TeamId]);
        var seasonMaps = input.Maps.Where(map => input.SeasonStart is not { } start || map.Rated.Map.PlayedAt >= start).ToList();

        (int Won, int Lost) Record(Func<string, bool> inside)
        {
            var (won, lost) = (0, 0);
            foreach (var map in seasonMaps)
            {
                var (leagueA, leagueB) = (teamLeagues[map.Rated.Map.TeamA], teamLeagues[map.Rated.Map.TeamB]);
                if (inside(leagueA) == inside(leagueB))
                    continue;
                var insideWon = inside(leagueA) == map.Rated.Map.RoundsA > map.Rated.Map.RoundsB;
                if (insideWon)
                    won++;
                else
                    lost++;
            }
            return (won, lost);
        }

        object LeagueRow(SiteRegion league)
        {
            var (won, lost) = Record(leagueId => leagueId == league.RegionId);
            return new
            {
                id = league.RegionId,
                name = league.Name,
                rating = Round(leagueRatings[league.RegionId].Rating),
                change = Round(leagueRatings[league.RegionId].Change),
                teams = rankedPerLeague[league.RegionId].Count(),
                international = new { won, lost },
                subregions = Array.Empty<object>(),
            };
        }

        var rows = new List<(double Rating, object Row)>();
        foreach (var region in input.Regions.Where(region => region.ParentId is null))
        {
            var subregions = input.Regions.Where(league => league.ParentId == region.RegionId && leagueRatings.ContainsKey(league.RegionId)).ToList();
            if (subregions.Count == 0)
            {
                if (leagueRatings.ContainsKey(region.RegionId) && rankedPerLeague[region.RegionId].Any())
                    rows.Add((leagueRatings[region.RegionId].Rating, LeagueRow(region)));
                continue;
            }
            var rating = subregions.Average(league => leagueRatings[league.RegionId].Rating);
            var (won, lost) = Record(leagueId => subregions.Any(league => league.RegionId == leagueId));
            rows.Add((rating, new
            {
                id = region.RegionId,
                name = region.Name,
                rating = Round(rating),
                change = Round(subregions.Average(league => leagueRatings[league.RegionId].Change)),
                teams = subregions.Sum(league => rankedPerLeague[league.RegionId].Count()),
                international = new { won, lost },
                subregions = subregions.OrderByDescending(league => leagueRatings[league.RegionId].Rating).Select(LeagueRow),
            }));
        }
        return rows.OrderByDescending(row => row.Rating).Select(row => row.Row);
    }

    /// <summary>Biggest rises and falls since the last snapshot at least <paramref name="days"/> days before the latest.</summary>
    static object? Movers(SiteInput input, DaySnapshot latest, int days, Dictionary<int, SiteTeam> teams, Dictionary<string, SiteRegion> regions)
    {
        var earlier = input.Days.LastOrDefault(day => day.Date <= latest.Date.AddDays(-days));
        if (earlier is null)
            return null;
        var ratingsThen = earlier.Teams.ToDictionary(team => team.TeamId, team => team.Rating);
        var changes = latest.Teams
            .Where(team => team.Rank is not null && ratingsThen.ContainsKey(team.TeamId))
            .Select(team => (Snapshot: team, Change: team.Rating - ratingsThen[team.TeamId]))
            .ToList();
        object Row((TeamSnapshot Snapshot, double Change) mover) => new
        {
            rank = mover.Snapshot.Rank,
            rating = Round(mover.Snapshot.Rating),
            change = Round(mover.Change),
            team = TeamCard(teams[mover.Snapshot.TeamId], regions),
        };
        return new
        {
            days,
            since = earlier.Date,
            rises = changes.Where(mover => mover.Change > 0).OrderByDescending(mover => mover.Change).Take(MoversShown).Select(Row),
            falls = changes.Where(mover => mover.Change < 0).OrderBy(mover => mover.Change).Take(MoversShown).Select(Row),
        };
    }

    static object TeamView(SiteInput input, TeamSnapshot snapshot, Dictionary<int, SiteTeam> teams, Dictionary<string, SiteRegion> regions)
    {
        var teamId = snapshot.TeamId;
        var latestDate = input.Days[^1].Date;
        // Daily for the last year, weekly before that, to keep the file small.
        var history = input.Days
            .Where(day => day.Date > latestDate.AddDays(-DailyHistoryDays) || day.Date.DayOfWeek == DayOfWeek.Monday)
            .SelectMany(day => day.Teams.Where(team => team.TeamId == teamId))
            .Select(team => new { date = team.Date, rating = Round(team.Rating), rank = team.Rank });
        var recent = input.Maps
            .Where(map => map.Rated.Map.TeamA == teamId || map.Rated.Map.TeamB == teamId)
            .OrderByDescending(map => map.Rated.Map.PlayedAt)
            .ThenByDescending(map => map.Rated.Map.MapNumber)
            .Take(RecentMapsShown)
            .Select(map =>
            {
                var isTeamA = map.Rated.Map.TeamA == teamId;
                var (roundsFor, roundsAgainst) = isTeamA
                    ? (map.Rated.Map.RoundsA, map.Rated.Map.RoundsB)
                    : (map.Rated.Map.RoundsB, map.Rated.Map.RoundsA);
                return new
                {
                    playedAt = map.Rated.Map.PlayedAt,
                    tournament = map.Tournament,
                    mapNumber = map.Rated.Map.MapNumber,
                    opponent = TeamCard(teams[isTeamA ? map.Rated.Map.TeamB : map.Rated.Map.TeamA], regions),
                    roundsFor,
                    roundsAgainst,
                    won = roundsFor > roundsAgainst,
                    change = Round(isTeamA ? map.Rated.RatingChangeA : map.Rated.RatingChangeB),
                };
            });
        var roster = input.RosterEvents
            .Where(rosterEvent => rosterEvent.TeamId == teamId && rosterEvent.Kind != RosterEventKind.SharesCore)
            .OrderByDescending(rosterEvent => rosterEvent.At)
            .Select(rosterEvent => new { at = rosterEvent.At, kind = rosterEvent.Kind.ToString(), newPlayers = rosterEvent.NewPlayers });

        return new
        {
            team = TeamCard(teams[teamId], regions),
            rank = snapshot.Rank,
            rating = Round(snapshot.Rating),
            change = Round(snapshot.Change),
            maps = snapshot.RatedMaps,
            provisional = snapshot.Provisional,
            history,
            recent,
            roster,
        };
    }

    static string DisplayRegion(string leagueId, Dictionary<string, SiteRegion> regions) =>
        regions[leagueId].ParentId ?? leagueId;

    static double Round(double value) => Math.Round(value, 1);

    static void WriteFile(string path, object content) =>
        File.WriteAllText(path, JsonSerializer.Serialize(content, Json));
}
