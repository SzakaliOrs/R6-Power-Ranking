using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Rankings.Core;
using Rankings.Job;
using Rankings.Job.Archive;
using Rankings.Job.Liquipedia;
using Rankings.Job.Storage;

namespace Rankings.Tests;

public sealed class MatchImportTests : IDisposable
{
    static readonly DateOnly Day = new(2026, 7, 8);
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-02T04:17:00Z");

    readonly string _root = Path.Combine(Path.GetTempPath(), $"archive-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// Answers every fetch with the fixture's series, as LPDB would for a window holding them.
    sealed class FixtureSource : IMatchSource
    {
        public List<(DateTimeOffset From, DateTimeOffset To)> Windows { get; } = [];

        public Task<SourceBatch> FetchAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
        {
            Windows.Add((from, to));
            var maps = new List<SourceMap>();
            var skipped = new List<SkippedSourceMap>();
            var body = LpdbMatchParserTests.Fixture;
            LpdbArchive.Parse(body, maps, skipped);
            var inWindow = maps.Where(m => m.PlayedAt >= from && m.PlayedAt < to).ToList();
            var responses = inWindow.Count > 0 ? new[] { new RawResponse("match?test", Now, body) } : [];
            return Task.FromResult(new SourceBatch(inWindow, skipped, responses));
        }
    }

    static RankingsDb DbWithTeams()
    {
        var db = RankingsDb.Open(":memory:");
        db.Execute("""
            INSERT INTO teams (team_id, region_id, created_on) VALUES
                (1, 'APAC-N', '2026-01-01'), (2, 'APAC-N', '2026-01-01'),
                (3, 'APAC-N', '2026-01-01'), (4, 'APAC-N', '2026-01-01');
            """);
        return db;
    }

    static void LinkAll(RankingsDb db)
    {
        var ids = new ProviderIds(db);
        ids.LinkTeam("cag by varrel orig", 1);
        ids.LinkTeam("scarz 2022", 2);
        ids.LinkTeam("dplus orig", 3);
        ids.LinkTeam("kinotrope gaming", 4);
    }

    [Fact]
    public async Task Fetch_ArchivesEveryDayOfTheWindow_ThenLogsMapsOfUnassignedTeams()
    {
        using var db = DbWithTeams();
        var store = new LocalArchiveStore(_root);
        var import = new MatchImport(db, [store], new FakeTimeProvider(Now));

        var summary = await import.FetchAsync(new FixtureSource(), Day.AddDays(-1), Day.AddDays(1));

        Assert.Equal((3, 0, 6, 4), (summary.Days, summary.Maps, summary.Skipped, summary.UnassignedTeams));
        var archived = LpdbArchive.Decompress((await store.ReadAsync("matches/2026/07/08.json.gz"))!);
        Assert.Equal(2, JsonNode.Parse(archived)!["result"]!.AsArray().Count);
        var emptyDay = LpdbArchive.Decompress((await store.ReadAsync("matches/2026/07/09.json.gz"))!);
        Assert.Empty(JsonNode.Parse(emptyDay)!["result"]!.AsArray());
        Assert.Equal(6L, db.Scalar("SELECT COUNT(*) FROM skipped_maps WHERE reason LIKE 'team not assigned%'"));
    }

    [Fact]
    public async Task Fetch_AsksForWholeUtcDays()
    {
        using var db = DbWithTeams();
        var source = new FixtureSource();

        await new MatchImport(db, []).FetchAsync(source, Day, Day.AddDays(1));

        Assert.Equal((DateTimeOffset.Parse("2026-07-08T00:00:00Z"), DateTimeOffset.Parse("2026-07-10T00:00:00Z")), source.Windows.Single());
    }

    [Fact]
    public async Task Reparse_StoresMapsOnceTheirTeamsAreAssigned_AndClearsTheirSkips()
    {
        using var db = DbWithTeams();
        var store = new LocalArchiveStore(_root);
        var import = new MatchImport(db, [store], new FakeTimeProvider(Now));
        await import.FetchAsync(new FixtureSource(), Day, Day);
        LinkAll(db);

        var summary = await import.ReparseAsync(store, Day, Day.AddDays(1));

        Assert.Equal((1, 6, 0, 0), (summary.Days, summary.Maps, summary.Skipped, summary.UnassignedTeams));
        Assert.Equal([Day.AddDays(1)], summary.MissingDays);
        Assert.Equal(6, db.LoadMaps().Count);
        Assert.Equal(0L, db.Scalar("SELECT COUNT(*) FROM skipped_maps"));
    }

    [Fact]
    public async Task Backfill_GoesMonthByMonthAndRecordsProgress()
    {
        using var db = DbWithTeams();
        var source = new FixtureSource();

        var summary = await new MatchImport(db, [new LocalArchiveStore(_root)])
            .BackfillAsync(source, () => 60, new DateOnly(2026, 6, 15), new DateOnly(2026, 8, 10));

        Assert.Equal(
            ["2026-06-15..2026-07-01", "2026-07-01..2026-08-01", "2026-08-01..2026-08-11"],
            source.Windows.Select(w => $"{w.From:yyyy-MM-dd}..{w.To:yyyy-MM-dd}"));
        Assert.Equal(57, summary.Days);
        Assert.False(summary.StoppedForBudget);
        Assert.Equal("2026-08-10", db.GetState(MatchImport.BackfillStateKey));
    }

    [Fact]
    public async Task Backfill_StopsWhenTheBudgetIsSpent_AndCarriesOnFromThere()
    {
        using var db = DbWithTeams();
        var import = new MatchImport(db, [new LocalArchiveStore(_root)]);
        var budget = 1;

        var first = await import.BackfillAsync(new FixtureSource(), () => budget--, new DateOnly(2026, 6, 1), new DateOnly(2026, 8, 31));

        Assert.True(first.StoppedForBudget);
        Assert.Equal("2026-06-30", db.GetState(MatchImport.BackfillStateKey));

        var source = new FixtureSource();
        await import.BackfillAsync(source, () => 60, new DateOnly(2026, 6, 1), new DateOnly(2026, 8, 31));

        Assert.Equal(new DateOnly(2026, 7, 1), DateOnly.FromDateTime(source.Windows[0].From.UtcDateTime));
        Assert.Equal("2026-08-31", db.GetState(MatchImport.BackfillStateKey));
    }

    [Fact]
    public async Task Backfill_RestartIgnoresSavedProgress()
    {
        using var db = DbWithTeams();
        db.SetState(MatchImport.BackfillStateKey, "2026-07-31");
        var source = new FixtureSource();

        await new MatchImport(db, []).BackfillAsync(source, () => 60, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31), restart: true);

        Assert.Single(source.Windows);
    }
}
