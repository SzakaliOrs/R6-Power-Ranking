# Rankings.Job

The command-line job. Today it creates the database, imports matches from Liquipedia into it,
backtests the engine and writes the site's JSON. The daily rating run (Phase 5) will be added here
as well.

## Commands

Run from the repository root:

```powershell
dotnet run --project src/Rankings.Job -- <command> [options]
```

| Command | What it does | Calls Liquipedia |
|---|---|---|
| `init-db <path>` | Creates an empty `rankings.db` with the current schema. Refuses to overwrite an existing file. | No |
| `fetch --from D --to D` | Fetches finished maps for those UTC days, archives each day, stores the maps | Yes |
| `backfill [--from D] [--to D] [--restart]` | Fetches a month at a time from 2023-01-01 (or `--from`) to yesterday, continuing where it stopped. It stops cleanly when the hourly budget is spent. | Yes |
| `reparse --from D --to D` | Reads archived days back through the parser, e.g. after a team is assigned, so nothing is fetched again | No |
| `backtest --synthetic [--seed N] [--seasons N]` | Replays synthetic seasons: the first is the warm-up, the middle ones tune the settings, the last checks them. Reads and writes nothing. | No |
| `sample-site [--seed N] [--out DIR]` | Writes the site's JSON from synthetic seasons (default `web/public/data`) | No |

Dates are `YYYY-MM-DD` UTC days, both ends included. Nothing before 2023-01-01 is ever requested.

Common options: `--db` (default `rankings.db`), `--archive-dir` (default `archive`), `--call-log`
(default `lpdb-calls.json`), `--b2` (also archive to B2; reparse reads from B2), and `--wait` (wait
for the hourly budget instead of stopping; use it locally only, since waiting is billed on Actions).

Exit codes: `0` success, `1` refused (e.g. the database already exists), `2` bad arguments, and `3`
when the Liquipedia budget is spent or the API answered with a rate limit.

## Environment

| Variable | Needed for |
|---|---|
| `LIQUIPEDIA_API_KEY`, `LIQUIPEDIA_USER_AGENT` | `fetch`, `backfill` |
| `B2_KEY_ID`, `B2_APPLICATION_KEY`, `B2_ENDPOINT`, `B2_BUCKET` | any command with `--b2` |

On GitHub Actions, `fetch` and `backfill` refuse to run without `--b2`, because the local archive
is deleted along with the runner.

## Layout

| Folder | Contents |
|---|---|
| `Storage/` | `schema.sql` (embedded), `RankingsDb` (open/create, save and load maps, skipped-map log, run state), `ProviderIds` (provider keys → own team and player IDs) |
| `Liquipedia/` | `LpdbMatchSource` (the `IMatchSource` adapter), `LpdbMatchParser`, `LpdbArchive` (one file per day), `HourlyCallLimiter` and `CallLogFile` (the 60-calls-an-hour budget, carried between runs) |
| `Archive/` | `IArchiveStore` with a local-folder store and an S3 store for B2 |
| `Site/` | `SiteExport` (the site's JSON files) and `SampleSite` (that input built from synthetic seasons) |
| `MatchImport.cs` | Fetch, backfill and reparse on whole UTC days. The archive is written before the database changes. |
| `SyntheticBacktest.cs` | The `backtest --synthetic` report |

## Rules the import keeps

- **The archive comes first.** Every fetched day is written to the archive before the database
  changes, and each archive file holds a complete day, including an empty one when nothing was
  played.
- **Reruns overwrite by map ID**, so they never duplicate a map.
- **Teams need an explicit assignment** from the admin dashboard, because the assignment fixes the
  team's league. Maps with an unassigned team are skipped and logged until it is assigned. Then
  `reparse` recovers them.
- **Players are created on first sight.** A renamed player can be relinked from the admin dashboard.
- **No retries after a 429 response.** The run stops, and the call log counts the hour as full.

## Site files

`SiteExport` writes these files:

- `rankings.json`, `regions.json` and `movers.json`, one per tab
- `teams/<id>.json`, one per team
- `meta.json`, with the last-updated time and the algorithm version
