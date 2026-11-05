using System.Globalization;
using System.Text.Json.Nodes;
using Rankings.Core;

namespace Rankings.Sim;

/// <summary>
/// The synthetic seasons as a match source. Its raw responses are shaped like LPDB match2
/// pages, so they run through the real parser and the B2 archive just as Liquipedia's would,
/// with no call to Liquipedia.
/// </summary>
public sealed class SimulatedMatchSource(SimulatedWorld world, int pageSize = 100) : IMatchSource
{
    public Task<SourceBatch> FetchAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        var series = world.Series
            .Where(series => series.PlayedAt >= from && series.PlayedAt < to)
            .OrderBy(series => series.PlayedAt)
            .ThenBy(series => series.SeriesId, StringComparer.Ordinal)
            .ToList();

        var maps = new List<SourceMap>();
        foreach (var played in series)
        {
            for (var number = 1; number <= played.Maps.Count; number++)
            {
                var (roundsA, roundsB) = played.Maps[number - 1];
                maps.Add(new SourceMap($"{played.SeriesId}/{number}", played.SeriesId, played.PlayedAt, number,
                    played.BestOf, SourceTeam(played.TeamA), SourceTeam(played.TeamB), roundsA, roundsB,
                    played.Tier, played.Tournament));
            }
        }

        var responses = new List<RawResponse>();
        for (var offset = 0; offset < series.Count; offset += pageSize)
        {
            var page = new JsonArray([.. series.Skip(offset).Take(pageSize).Select(LpdbSeries)]);
            var request = $"simulated?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&offset={offset}";
            responses.Add(new RawResponse(request, to, new JsonObject { ["result"] = page }.ToJsonString()));
        }
        return Task.FromResult(new SourceBatch(maps, [], responses));
    }

    /// <summary>One series as an LPDB match2 record, with the maps a decided series never played marked "np".</summary>
    public static JsonObject LpdbSeries(SimulatedSeries series)
    {
        var date = series.PlayedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var timestamp = series.PlayedAt.ToUnixTimeSeconds();
        var games = new JsonArray();
        for (var number = 1; number <= series.BestOf; number++)
        {
            var played = number <= series.Maps.Count;
            var (roundsA, roundsB) = played ? series.Maps[number - 1] : (0, 0);
            games.Add(new JsonObject
            {
                ["match2gameid"] = number,
                ["scores"] = played ? new JsonArray(roundsA, roundsB) : new JsonArray(),
                ["opponents"] = played
                    ? new JsonArray(new JsonObject { ["score"] = roundsA }, new JsonObject { ["score"] = roundsB })
                    : new JsonArray(),
                ["winner"] = played ? (roundsA > roundsB ? "1" : "2") : "",
                ["walkover"] = "",
                ["resulttype"] = played ? "" : "np",
                ["date"] = date,
                ["extradata"] = new JsonObject { ["timestamp"] = timestamp },
            });
        }

        var winsA = series.Maps.Count(map => map.RoundsA > map.RoundsB);
        return new JsonObject
        {
            ["match2id"] = series.SeriesId,
            ["objectname"] = $"sim_{series.SeriesId}",
            ["winner"] = winsA > series.Maps.Count - winsA ? "1" : "2",
            ["walkover"] = "",
            ["resulttype"] = "",
            ["finished"] = 1,
            ["game"] = "siegex",
            ["date"] = date,
            ["bestof"] = series.BestOf,
            ["tournament"] = series.Tournament,
            ["liquipediatier"] = series.Tier,
            ["liquipediatiertype"] = "General",
            ["extradata"] = new JsonObject { ["timestamp"] = timestamp },
            ["match2opponents"] = new JsonArray(Opponent(series.TeamA), Opponent(series.TeamB)),
            ["match2games"] = games,
            ["wiki"] = "rainbowsix",
        };
    }

    static JsonObject Opponent(SimulatedSide side) => new()
    {
        ["type"] = "team",
        ["name"] = side.Name,
        ["template"] = side.Key,
        ["match2players"] = new JsonArray([.. side.PlayerIds.Select(playerId => (JsonNode)new JsonObject
        {
            ["name"] = PlayerKey(playerId),
            ["displayname"] = PlayerKey(playerId),
        })]),
    };

    static SourceTeam SourceTeam(SimulatedSide side) =>
        new(side.Key, side.Name, [.. side.PlayerIds.Select(playerId => new SourcePlayer(PlayerKey(playerId), PlayerKey(playerId)))]);

    static string PlayerKey(int playerId) => $"SimPlayer{playerId}";
}
