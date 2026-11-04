using System.Globalization;
using Rankings.Job;
using Rankings.Job.Archive;
using Rankings.Job.Liquipedia;
using Rankings.Job.Storage;

const string Usage = """
    Usage:
      Rankings.Job init-db <path>
      Rankings.Job fetch    --from YYYY-MM-DD --to YYYY-MM-DD [options] [--b2]
      Rankings.Job backfill [--from YYYY-MM-DD] [--to YYYY-MM-DD] [options] [--b2] [--restart]
      Rankings.Job reparse  --from YYYY-MM-DD --to YYYY-MM-DD [options] [--b2]

    Dates are whole UTC days, both included; nothing before 2023-01-01 is fetched.
    fetch and backfill call Liquipedia; reparse only reads the archive.

    Options:
      --db <path>            rankings.db to update (default rankings.db)
      --archive-dir <path>   local archive folder (default archive)
      --call-log <path>      Liquipedia call log carried between runs (default lpdb-calls.json)
      --b2                   fetch/backfill: also upload days to B2; reparse: read from B2
      --restart              backfill: ignore the saved progress and start at --from

    Environment:
      LIQUIPEDIA_API_KEY, LIQUIPEDIA_USER_AGENT                  for fetch and backfill
      B2_KEY_ID, B2_APPLICATION_KEY, B2_ENDPOINT, B2_BUCKET      with --b2
    """;

var floor = DateOnly.FromDateTime(LpdbOptions.DefaultEarliestDate.UtcDateTime);

try
{
    return args switch
    {
        ["init-db", var path] => InitDb(path),
        ["fetch", .. var rest] => await Fetch(Cli.Parse(rest)),
        ["backfill", .. var rest] => await Backfill(Cli.Parse(rest)),
        ["reparse", .. var rest] => await Reparse(Cli.Parse(rest)),
        _ => Fail(Usage, 2),
    };
}
catch (LpdbRateLimitedException e)
{
    return Fail(e.Message, 3);
}
catch (CliException e)
{
    return Fail($"{e.Message}\n\n{Usage}", 2);
}

int InitDb(string path)
{
    if (File.Exists(path))
        return Fail($"{path} already exists; delete it first to start over.", 1);
    using (RankingsDb.Open(path)) { }
    Console.WriteLine($"Created {path} (schema version {RankingsDb.SchemaVersion}).");
    return 0;
}

async Task<int> Fetch(Cli cli)
{
    var (from, to) = (cli.Date("from"), cli.Date("to"));
    CheckRange(from, to);
    using var db = RankingsDb.Open(cli.Value("db") ?? "rankings.db");
    var (source, limiter) = Liquipedia(cli);

    var summary = await new MatchImport(db, Archives(cli)).FetchAsync(source, from, to);
    Report("Fetched", summary, limiter);
    return 0;
}

async Task<int> Backfill(Cli cli)
{
    var from = cli.Date("from", floor);
    var to = cli.Date("to", DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1));
    CheckRange(from, to);
    using var db = RankingsDb.Open(cli.Value("db") ?? "rankings.db");
    var (source, limiter) = Liquipedia(cli);

    var summary = await new MatchImport(db, Archives(cli))
        .BackfillAsync(source, () => limiter.Remaining, from, to, cli.Flag("restart"));
    Report("Backfilled", summary, limiter);
    var through = db.GetState(MatchImport.BackfillStateKey);
    Console.WriteLine(summary.StoppedForBudget
        ? $"Stopped: this hour's Liquipedia budget is spent. Done through {through}; run again in an hour to continue."
        : $"Done through {through}.");
    return 0;
}

async Task<int> Reparse(Cli cli)
{
    var (from, to) = (cli.Date("from"), cli.Date("to"));
    CheckRange(from, to);
    using var db = RankingsDb.Open(cli.Value("db") ?? "rankings.db");
    var archive = cli.Flag("b2") ? B2() : new LocalArchiveStore(cli.Value("archive-dir") ?? "archive");

    var summary = await new MatchImport(db, []).ReparseAsync(archive, from, to);
    Report($"Reparsed from {archive.Name}", summary, null);
    if (summary.MissingDays is { Count: > 0 } missing)
        Console.WriteLine($"No archive file for {missing.Count} day(s), first {missing[0]:yyyy-MM-dd}.");
    return 0;
}

void CheckRange(DateOnly from, DateOnly to)
{
    if (from < floor)
        throw new CliException($"--from is before {floor:yyyy-MM-dd}, the oldest date this project uses.");
    if (to < from)
        throw new CliException("--to is before --from.");
}

(LpdbMatchSource, HourlyCallLimiter) Liquipedia(Cli cli)
{
    var options = new LpdbOptions
    {
        ApiKey = Env("LIQUIPEDIA_API_KEY"),
        UserAgent = Env("LIQUIPEDIA_USER_AGENT"),
    };
    var limiter = new CallLogFile(cli.Value("call-log") ?? "lpdb-calls.json")
        .CreateLimiter(options.MaxCallsPerHour, TimeProvider.System);
    return (new LpdbMatchSource(LpdbMatchSource.CreateHttpClient(), options, limiter), limiter);
}

List<IArchiveStore> Archives(Cli cli)
{
    List<IArchiveStore> stores = [new LocalArchiveStore(cli.Value("archive-dir") ?? "archive")];
    if (cli.Flag("b2"))
        stores.Add(B2());
    return stores;
}

IArchiveStore B2() =>
    S3ArchiveStore.ForB2(Env("B2_KEY_ID"), Env("B2_APPLICATION_KEY"), Env("B2_ENDPOINT"), Env("B2_BUCKET"));

string Env(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new CliException($"Environment variable {name} is not set.");

void Report(string what, ImportSummary summary, HourlyCallLimiter? limiter)
{
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"{what} {summary.Days} day(s): {summary.Maps} map(s) stored, {summary.Skipped} skipped, "
        + $"{summary.UnassignedTeams} team(s) waiting for an assignment."));
    if (limiter is not null)
        Console.WriteLine($"Liquipedia calls in the last hour: {limiter.RecentCalls.Count}, {limiter.Remaining} left.");
}

int Fail(string message, int code)
{
    Console.Error.WriteLine(message);
    return code;
}

sealed class CliException(string message) : Exception(message);

/// <summary>"--name value" options and "--name" flags.</summary>
sealed class Cli
{
    readonly Dictionary<string, string?> _values = [];

    public static Cli Parse(string[] args)
    {
        var cli = new Cli();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                throw new CliException($"Unexpected argument '{args[i]}'.");
            var name = args[i][2..];
            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            cli._values[name] = hasValue ? args[++i] : null;
        }
        return cli;
    }

    public string? Value(string name) => _values.GetValueOrDefault(name);

    public bool Flag(string name) => _values.ContainsKey(name);

    public DateOnly Date(string name, DateOnly? fallback = null)
    {
        if (Value(name) is not { } text)
            return fallback ?? throw new CliException($"--{name} is required.");
        return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new CliException($"--{name} must be a date like 2026-07-08, not '{text}'.");
    }
}
