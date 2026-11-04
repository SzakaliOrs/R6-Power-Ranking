using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Rankings.Core;

namespace Rankings.Job.Liquipedia;

/// <summary>
/// The raw archive: one file per UTC day, holding that day's series exactly as LPDB returned
/// them, in LPDB's own <c>{"result": [...]}</c> shape so a replay reads it through the same parser.
/// </summary>
public static class LpdbArchive
{
    public static string Key(DateOnly day) => $"matches/{day:yyyy}/{day:MM}/{day:dd}.json.gz";

    /// <summary>
    /// Regroups fetched pages by the day each series started. Every day in [from, to] gets a
    /// document, an empty one when nothing was played, which tells "fetched, nothing played"
    /// apart from "never fetched". A series repeated across pages is kept once. Days are taken
    /// from the "date" field the request filtered on, so no series lands outside the window;
    /// one that does anyway stops the run, since writing it would replace a complete day in B2
    /// with part of one.
    /// </summary>
    public static SortedDictionary<DateOnly, string> SplitByDay(
        IEnumerable<RawResponse> responses, DateOnly from, DateOnly to, DateTimeOffset fetchedAt)
    {
        var days = new SortedDictionary<DateOnly, OrderedDictionary<string, string>>();
        for (var day = from; day <= to; day = day.AddDays(1))
            days[day] = [];

        foreach (var response in responses)
        {
            using var document = JsonDocument.Parse(response.Body);
            foreach (var series in document.RootElement.GetProperty("result").EnumerateArray())
            {
                // The request filters by date, so every series has one.
                if (LpdbMatchParser.ListedDate(series) is not { } start)
                    continue;
                var day = DateOnly.FromDateTime(start.UtcDateTime);
                if (!days.TryGetValue(day, out var list))
                    throw new InvalidOperationException(
                        $"Series {SeriesKey(series, 0)} is dated {day:yyyy-MM-dd}, outside the fetched days "
                        + $"{from:yyyy-MM-dd}..{to:yyyy-MM-dd}; nothing was archived.");
                list[SeriesKey(series, list.Count)] = series.GetRawText();
            }
        }

        var stamp = JsonSerializer.Serialize(fetchedAt);
        return new SortedDictionary<DateOnly, string>(days.ToDictionary(
            d => d.Key,
            d => $$"""{"fetchedAt":{{stamp}},"result":[{{string.Join(",", d.Value.Values)}}]}"""));
    }

    /// <summary>Parses an archived day the same way as a fresh response.</summary>
    public static void Parse(string dayJson, List<SourceMap> maps, List<SkippedSourceMap> skipped)
    {
        using var document = JsonDocument.Parse(dayJson);
        foreach (var series in document.RootElement.GetProperty("result").EnumerateArray())
            LpdbMatchParser.Parse(series, maps, skipped);
    }

    public static byte[] Compress(string json)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize))
            gzip.Write(Encoding.UTF8.GetBytes(json));
        return output.ToArray();
    }

    public static string Decompress(byte[] content)
    {
        using var gzip = new GZipStream(new MemoryStream(content), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    static string SeriesKey(JsonElement series, int position) =>
        series.TryGetProperty("match2id", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()!
            : $"#{position}";
}
