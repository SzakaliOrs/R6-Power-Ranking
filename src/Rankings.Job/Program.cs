using System.Globalization;
using Rankings.Job;
using Rankings.Job.Archive;
using Rankings.Job.Liquipedia;
using Rankings.Job.Storage;

const string Usage = """
    Usage:
      Rankings.Job run      [--source liquipedia|synthetic] [--seed N] [--full] [--site-out DIR] [options] [--b2]
      Rankings.Job init-db <path>
      Rankings.Job fetch    --from YYYY-MM-DD --to YYYY-MM-DD [options] [--b2]
      Rankings.Job backfill [--from YYYY-MM-DD] [--to YYYY-MM-DD] [options] [--b2] [--restart]
      Rankings.Job reparse  --from YYYY-MM-DD --to YYYY-MM-DD [options] [--b2]
      Rankings.Job backtest --synthetic [--seed N] [--seasons N]
      Rankings.Job sample-site [--seed N] [--out web/public/data]

    Dates are whole UTC days, both included; nothing before 2023-01-01 is fetched.
    run is the daily run: it fetches yesterday and the 2 days before, rates every day since the
    last settled one and writes the site's JSON; --full rebuilds every rating from the archive.
    fetch and backfill call Liquipedia; reparse only reads the archive; backtest --synthetic
    simulates its seasons and touches nothing; sample-site writes the site's JSON from synthetic seasons.

    Options:
      --source <name>        run: liquipedia (default) or synthetic, whose archive keys start with synthetic/
      --seed <N>             run --source synthetic, backtest, sample-site: which synthetic world (default 1)
      --full                 run: rebuild every rating from the archive (synthetic: from the simulation)
      --site-out <dir>       run: where the site's JSON goes (default web/public/data)
      --today YYYY-MM-DD     run: act as if run on that day, e.g. to step through synthetic days locally
      --db <path>            rankings.db to update (default rankings.db)
      --archive-dir <path>   local archive folder (default archive)
      --call-log <path>      Liquipedia call log carried between runs (default lpdb-calls.json)
      --b2                   fetch/backfill: also upload days to B2 (required on GitHub Actions); reparse: read from B2
      --restart              backfill: ignore the saved progress and start at --from
      --wait                 fetch/backfill: wait for the hourly Liquipedia budget instead of stopping;
                             for local runs only, since waiting on GitHub Actions spends billed minutes

    Environment:
      LIQUIPEDIA_API_KEY, LIQUIPEDIA_USER_AGENT                  for fetch and backfill
      B2_KEY_ID, B2_APPLICATION_KEY, B2_ENDPOINT, B2_BUCKET      with --b2
    """;

var floor = DateOnly.FromDateTime(LpdbOptions.DefaultEarliestDate.UtcDateTime);

try
{
    return args switch
    {
        ["run", .. var rest] => await RunDaily(Cli.Parse(rest)),
        ["init-db", var path] => InitDb(path),
        ["fetch", .. var rest] => await Fetch(Cli.Parse(rest)),
        ["backfill", .. var rest] => await Backfill(Cli.Parse(rest)),
        ["reparse", .. var rest] => await Reparse(Cli.Parse(rest)),
        ["backtest", .. var rest] => RunBacktest(Cli.Parse(rest)),
        ["sample-site", .. var rest] => WriteSampleSite(Cli.Parse(rest)),
        _ => Fail(Usage, 2),
    };
}
catch (LpdbRateLimitedException e)
{
    return Fail(e.Message, 3);
}
catch (LpdbBudgetSpentException e)
{
    return Fail(e.Message, 3);
}
catch (CliException e)
{
    return Fail($"{e.Message}\n\n{Usage}", 2);
}
catch (DailyRunException e)
{
    return Fail(e.Message, 1);
}

async Task<int> RunDaily(Cli cli)
{
    var sourceName = cli.Value("source") ?? "liquipedia";
    TimeProvider time = cli.Value("today") is null
        ? TimeProvider.System
        : new FixedClock(new DateTimeOffset(cli.Date("today").ToDateTime(new TimeOnly(4, 17)), TimeSpan.Zero));
    using var db = RankingsDb.Open(cli.Value("db") ?? "rankings.db");

    Rankings.Core.IMatchSource source;
    HourlyCallLimiter? limiter = null;
    var archives = Archives(cli);
    switch (sourceName)
    {
        case "liquipedia":
            (source, limiter) = Liquipedia(cli);
            break;
        case SyntheticSetup.DataSource:
            var world = Rankings.Sim.Simulator.Run(new Rankings.Sim.SimulationOptions { Seed = cli.Number("seed", 1) });
            SyntheticSetup.Ensure(db, world);
            source = new Rankings.Sim.SimulatedMatchSource(world);
            archives = [.. archives.Select(store => (IArchiveStore)new PrefixedArchiveStore(store, SyntheticSetup.ArchivePrefix))];
            break;
        default:
            throw new CliException($"--source must be liquipedia or synthetic, not '{sourceName}'.");
    }

    // A synthetic database with nothing rated starts from the whole simulated history, which
    // costs nothing; a Liquipedia one starts from what a backfill stored.
    var synthetic = sourceName == SyntheticSetup.DataSource;
    var full = cli.Flag("full") || (synthetic && db.GetState(RatingsStore.RatedThroughKey) is null);

    // The last store is B2 with --b2, else the local folder: a full recalculation reads from it.
    var dailyRun = new DailyRun(db, source, archives, archives[^1], new Rankings.Core.RatingSettings(), time);
    var summary = await dailyRun.RunAsync(new DailyRunOptions
    {
        DataSource = sourceName,
        Full = full,
        FetchWholeHistory = full && synthetic,
    });

    Console.WriteLine(summary.RatedFrom is { } ratedFrom
        ? $"Rated {ratedFrom:yyyy-MM-dd} through {summary.RatedThrough:yyyy-MM-dd}: {summary.RatedMaps} map(s)."
        : $"Nothing new to rate through {summary.RatedThrough:yyyy-MM-dd}.");
    Console.WriteLine(summary.SettledThrough is { } settledThrough
        ? $"Settled through {settledThrough:yyyy-MM-dd}; later days are rated again in the next run."
        : "No day has settled yet.");
    Console.WriteLine($"Fetched {summary.FetchedMaps} map(s), {summary.Skipped} skipped, {summary.UnassignedTeams} team(s) waiting for an assignment.");
    if (summary.ReparsedDays > 0)
        Console.WriteLine($"Read {summary.ReparsedDays} archived day(s) back.");
    if (summary.MissingDays.Count > 0)
        Console.WriteLine($"No archive file for {summary.MissingDays.Count} day(s), first {summary.MissingDays[0]:yyyy-MM-dd}.");
    if (limiter is not null)
        Console.WriteLine($"Liquipedia calls in the last hour: {limiter.RecentCalls.Count}, {limiter.Remaining} left.");
    if (summary.UnassignedTeams > 0)
        Warn($"{summary.UnassignedTeams} team(s) wait for an assignment in the admin dashboard; their maps are skipped until then.");
    if (summary.LateMaps > 0)
        Warn($"{summary.LateMaps} map(s) were stored for days already settled. Run a full recalculation to rate them.");

    var siteOut = cli.Value("site-out") ?? Path.Combine("web", "public", "data");
    if (dailyRun.ExportSite(siteOut, sourceName))
        Console.WriteLine($"Wrote the site's data to {siteOut}.");
    else
        Warn("Nothing is rated yet, so no site data was written.");
    return 0;
}

void Warn(string message) =>
    Console.WriteLine(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" ? $"::warning::{message}" : $"Warning: {message}");

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

int RunBacktest(Cli cli)
{
    if (!cli.Flag("synthetic"))
        throw new CliException("Only --synthetic is available until the real history is backfilled.");
    var options = new Rankings.Sim.SimulationOptions
    {
        Seed = cli.Number("seed", 1),
        Seasons = cli.Number("seasons", 4),
    };
    if (options.Seasons < 3)
        throw new CliException("--seasons must be at least 3: warm-up, tuning and check.");
    Console.Write(SyntheticBacktest.Report(options));
    return 0;
}

int WriteSampleSite(Cli cli)
{
    var output = cli.Value("out") ?? Path.Combine("web", "public", "data");
    var input = Rankings.Job.Site.SampleSite.Build(new Rankings.Sim.SimulationOptions { Seed = cli.Number("seed", 1) });
    Rankings.Job.Site.SiteExport.Write(input, output);
    Console.WriteLine($"Wrote sample site data for {input.Days[^1].Teams.Count} teams to {output}.");
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
        .CreateLimiter(options.MaxCallsPerHour, TimeProvider.System, cli.Flag("wait") ? null : TimeSpan.Zero);
    return (new LpdbMatchSource(LpdbMatchSource.CreateHttpClient(), options, limiter), limiter);
}

List<IArchiveStore> Archives(Cli cli)
{
    if (!cli.Flag("b2") && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
        throw new CliException("On GitHub Actions the local archive is deleted with the runner; pass --b2.");
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

/// <summary>A clock stopped at one instant, for --today.</summary>
sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

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

    public int Number(string name, int fallback)
    {
        if (Value(name) is not { } text)
            return fallback;
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new CliException($"--{name} must be a whole number, not '{text}'.");
    }

    public DateOnly Date(string name, DateOnly? fallback = null)
    {
        if (Value(name) is not { } text)
            return fallback ?? throw new CliException($"--{name} is required.");
        return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new CliException($"--{name} must be a date like 2026-07-08, not '{text}'.");
    }
}
