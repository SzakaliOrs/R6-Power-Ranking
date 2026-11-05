# Rankings.Tests

xUnit tests for every project in `src/`. They need no credentials or network access: Liquipedia
responses come from fixtures and synthetic seasons, and storage uses temporary SQLite files and
local folders.

```powershell
dotnet test                                              # everything
dotnet test --filter "FullyQualifiedName~RatingRules"    # one class
```

## What each file covers

| File | Covers |
|---|---|
| `EloTests` | Expectation, margin multiplier with the favourite correction, series win chance |
| `RatingEngineTests` | The per-map update, re-centring, play order, and identical results on replay |
| `RatingRulesTests` | Each rule on its own: series bonus, roster core, seeding, freezes, season starts, decay, provisional flag, warm-up K, late leagues |
| `BacktestTests` | Log loss, the settings search, and rank correlation on synthetic worlds |
| `SimulatorTests` | The synthetic world: league structure, international spots, map scores, determinism |
| `RankingsDbTests` | Every table and its constraints, and map upserts by map ID |
| `ProviderIdsTests` | Provider keys → own IDs, unassigned teams, relinks |
| `LpdbMatchParserTests` | Reading LPDB match2 records, including PHP-style empty values and walkovers |
| `LpdbMatchSourceTests` | Request building, paging, and stopping on a 429 response |
| `LpdbArchiveTests` | Splitting responses into complete UTC days, and reading them back |
| `HourlyCallLimiterTests` | The 60-calls-an-hour budget and the saved call log |
| `MatchImportTests` | Fetch, backfill progress, and reparse after an assignment |
| `SiteExportTests` | The site's JSON files |
| `DailyRunTests` | A week of daily runs equals a full recalculation from the archive, late results inside the lookback, settling, the site from the database, the data-source guard, late team assignments, the exact checkpoint |
| `AdminTests` | Admin store changes, R2 sync refusals, local-only checks |

`Fixtures/` holds a real LPDB response (APAC North playoffs), which is copied to the output folder
for the parser tests.

## Conventions

- **Names say what they hold.** Variables and loop counters are named for their content (`teamA`,
  `kFactor`, `losses`), never single letters or formula symbols.
- **One test per rule.** Each rating rule gets its own test before it is combined with others.
- **Invariants stay tested.** Re-centring never changes a total rating, and a replay is
  deterministic.
