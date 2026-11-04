using System.Text.Json;

namespace Rankings.Job.Liquipedia;

/// <summary>
/// The limiter's recent calls in a small JSON file (lpdb-calls.json). The workflow carries it
/// between runs through R2 and uploads it even when the job fails, because a failed run never
/// uploads rankings.db and a rerun must still see the calls that run made.
/// </summary>
public sealed class CallLogFile(string path)
{
    public string Path { get; } = path;

    /// <summary>The saved calls; none when the file does not exist yet.</summary>
    public IReadOnlyList<DateTimeOffset> Load()
    {
        if (!File.Exists(Path))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<DateTimeOffset>>(File.ReadAllText(Path)) ?? [];
        }
        catch (JsonException e)
        {
            // Guessing could overspend the budget; a person decides whether to delete it.
            throw new InvalidDataException($"{Path} is not a valid call log; delete it only if no calls were made in the last hour.", e);
        }
    }

    /// <summary>Replaces the file in one step, so a crash never leaves half a log.</summary>
    public void Save(IReadOnlyList<DateTimeOffset> calls)
    {
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(calls));
        File.Move(temp, Path, overwrite: true);
    }

    /// <summary>A limiter that starts from this file and writes every change back to it.</summary>
    public HourlyCallLimiter CreateLimiter(int maxCallsPerHour, TimeProvider time) =>
        new(maxCallsPerHour, time, Load(), Save);
}
