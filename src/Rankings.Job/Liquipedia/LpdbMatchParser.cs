using System.Globalization;
using System.Text.Json;
using Rankings.Core;

namespace Rankings.Job.Liquipedia;

/// <summary>
/// Reads one LPDB match2 record (a series) into source maps. LPDB is PHP-backed, so an empty
/// object can arrive as [] and a number as a string; the readers below accept both.
/// </summary>
public static class LpdbMatchParser
{
    public static void Parse(JsonElement match, List<SourceMap> maps, List<SkippedSourceMap> skipped)
    {
        var seriesId = Str(match, "match2id");
        if (string.IsNullOrEmpty(seriesId))
        {
            skipped.Add(new(Str(match, "objectname") ?? "unknown", null, null, "series has no match2id"));
            return;
        }
        // Not over yet: a later fetch picks it up.
        if (Int(match, "finished") != 1)
            return;

        var playedAt = Timestamp(match);
        void SkipSeries(string reason) => skipped.Add(new(seriesId, seriesId, playedAt, reason));

        if (playedAt is null)
        {
            SkipSeries("series has no date");
            return;
        }
        var walkover = Str(match, "walkover");
        var resultType = Str(match, "resulttype");
        if (!string.IsNullOrEmpty(walkover) || !string.IsNullOrEmpty(resultType))
        {
            SkipSeries($"series was not played out (walkover '{walkover}', result type '{resultType}')");
            return;
        }
        if (!match.TryGetProperty("match2opponents", out var opponents)
            || opponents.ValueKind != JsonValueKind.Array || opponents.GetArrayLength() != 2)
        {
            SkipSeries("series does not have two opponents");
            return;
        }
        var teamA = Team(opponents[0]);
        var teamB = Team(opponents[1]);
        if (teamA is null || teamB is null)
        {
            SkipSeries("an opponent is not a team with a template");
            return;
        }
        if (teamA.Key == teamB.Key)
        {
            SkipSeries("both opponents are the same team");
            return;
        }
        var bestOf = Int(match, "bestof") ?? 0;
        if (bestOf < 1)
        {
            SkipSeries("series has no best-of");
            return;
        }
        if (!match.TryGetProperty("match2games", out var games) || games.ValueKind != JsonValueKind.Array)
        {
            SkipSeries("series has no games");
            return;
        }

        var tier = Str(match, "liquipediatier") ?? "";
        var tournament = Str(match, "tournament") ?? "";
        foreach (var game in games.EnumerateArray())
        {
            if (Int(game, "match2gameid") is not { } number)
            {
                SkipSeries("a game has no match2gameid");
                continue;
            }
            var mapId = $"{seriesId}/{number}";
            var winner = Str(game, "winner");
            var gameResultType = Str(game, "resulttype");
            var scores = Scores(game);

            // Never played, e.g. the third map of a 2–0.
            if (gameResultType == "np" || (string.IsNullOrEmpty(winner) && scores is null or (0, 0)))
                continue;

            string? reason = null;
            if (!string.IsNullOrEmpty(Str(game, "walkover")) || !string.IsNullOrEmpty(gameResultType))
                reason = $"map was not played out (result type '{gameResultType}')";
            else if (winner is not ("1" or "2"))
                reason = "map has no winner";
            else if (scores is not { } rounds)
                reason = "map has no round scores";
            else if (rounds.A == rounds.B || rounds.A > rounds.B != (winner == "1"))
                reason = $"winner {winner} disagrees with the score {rounds.A}–{rounds.B}";
            else if (number > bestOf)
                reason = $"map {number} is past the best-of-{bestOf}";

            if (reason is not null)
            {
                skipped.Add(new(mapId, seriesId, playedAt, reason));
                continue;
            }
            var (roundsA, roundsB) = scores!.Value;
            // Every map carries the series start, so a series' maps stay together in play order.
            maps.Add(new SourceMap(mapId, seriesId, playedAt.Value, number, bestOf,
                teamA, teamB, roundsA, roundsB, tier, tournament));
        }
    }

    static SourceTeam? Team(JsonElement opponent)
    {
        var template = Str(opponent, "template");
        if (Str(opponent, "type") != "team" || string.IsNullOrEmpty(template))
            return null;

        var players = new List<SourcePlayer>();
        if (opponent.TryGetProperty("match2players", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var player in list.EnumerateArray())
            {
                var key = Str(player, "name");
                if (!string.IsNullOrEmpty(key) && players.All(p => p.Key != key))
                    players.Add(new SourcePlayer(key, Str(player, "displayname") is { Length: > 0 } shown ? shown : key));
            }
        }
        return new SourceTeam(template, Str(opponent, "name") is { Length: > 0 } name ? name : template, players);
    }

    /// <summary>Rounds per side, from the per-opponent scores or else the scores array.</summary>
    static (int A, int B)? Scores(JsonElement game)
    {
        if (game.TryGetProperty("opponents", out var opponents) && opponents.ValueKind == JsonValueKind.Array
            && opponents.GetArrayLength() == 2
            && Int(opponents[0], "score") is { } a && Int(opponents[1], "score") is { } b && a >= 0 && b >= 0)
            return (a, b);
        if (game.TryGetProperty("scores", out var scores) && scores.ValueKind == JsonValueKind.Array
            && scores.GetArrayLength() == 2
            && AsInt(scores[0]) is { } sa && AsInt(scores[1]) is { } sb && sa >= 0 && sb >= 0)
            return (sa, sb);
        return null;
    }

    /// <summary>When a series or game started: extradata.timestamp, else the UTC "date" field.</summary>
    public static DateTimeOffset? Timestamp(JsonElement element)
    {
        if (element.TryGetProperty("extradata", out var extra) && Int(extra, "timestamp") is { } seconds and > 0)
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        return DateTimeOffset.TryParseExact(Str(element, "date"), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var date) ? date : null;
    }

    /// <summary>The UTC "date" field that LPDB filters requests on, else <see cref="Timestamp"/>.</summary>
    public static DateTimeOffset? ListedDate(JsonElement element) =>
        DateTimeOffset.TryParseExact(Str(element, "date"), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var date) ? date : Timestamp(element);

    static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    static int? Int(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? AsInt(value) : null;

    static int? AsInt(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt32(out var number) => number,
        JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
        _ => null,
    };
}
