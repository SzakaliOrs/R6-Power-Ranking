# Rankings.Job

The command-line job: the daily run (fetch, rate, export), plus database setup, Liquipedia
imports, the backtest and the sample site.

## Commands

Run from the repository root:

```powershell
dotnet run --project src/Rankings.Job -- <command> [options]
```

| Command | What it does | Calls Liquipedia |
|---|---|---|
| `run [--source liquipedia\|synthetic] [--full]` | The daily run: fetches yesterday and the 2 days before, rates every day since the last settled one, writes the site's JSON (`--site-out`, default `web/public/data`). `--full` rebuilds every rating from the archive. | Yes, with `--source liquipedia` (the default) |
| `init-db <path>` | Creates an empty `rankings.db` with the current schema. Refuses to overwrite an existing file. | No |
| `fetch --from D --to D` | Fetches finished maps for those UTC days, archives each day, stores the maps | Yes |
| `backfill [--from D] [--to D] [--restart]` | Fetches a month at a time from 2023-01-01 (or `--from`) to yesterday, continuing where it stopped. It stops cleanly when the hourly budget is spent. | Yes |
| `reparse --from D --to D` | Reads archived days back through the parser, e.g. after a team is assigned, so nothing is fetched again | No |
| `backtest --synthetic [--seed N] [--seasons N]` | Replays synthetic seasons: the first is the warm-up, the middle ones tune the settings, the last checks them. Reads and writes nothing. | No |
| `sample-site [--seed N] [--out DIR]` | Writes the site's JSON from synthetic seasons (default `web/public/data`) | No |

Dates are `YYYY-MM-DD` UTC days, both ends included. Nothing before 2023-01-01 is ever requested.

Common options: `--db` (default `rankings.db`), `--archive-dir` (default `archive`), `--call-log`
(default `lpdb-calls.json`), `--b2` (also archive to B2; reparse and full recalculations read from
B2), and `--wait` (wait for the hourly budget instead of stopping; use it locally only, since waiting
is billed on Actions). For `run`, `--seed N` picks the synthetic world, and `--today YYYY-MM-DD`
acts as if the run happened at 04:17 UTC that day, so you can step through days locally.

Exit codes: `0` success, `1` refused (e.g. the database already exists, or it was built from the
other data source), `2` bad arguments, and `3` when the Liquipedia budget is spent or the API
answered with a rate limit.

## The daily run

The site shows ratings through yesterday, but the last two days aren't final yet: Liquipedia can
add a result a few hours late. So each run works like this:

1. **Fetch** yesterday and the 2 days before it, archive each day (to B2 with `--b2`), and store the
   maps. A settled day is never fetched again.
2. **Rate** from the day after the last settled day through yesterday, one UTC day at a time with
   `DailyRating`. Each day gets its snapshot, rated maps and roster events.
3. **Settle** the oldest day of the window (yesterday − 2): no later fetch can change it. The
   rating state at its end is saved exactly (`run_state.settled_state`), and its maps leave the
   `maps` table, because B2 holds them. The two newer days are rated again by the next run.
4. **Export** the site's JSON from the database.

A full recalculation (`--full`) clears everything it rebuilds, reads every archived day back (or,
for the synthetic source, asks the simulation again), and runs the same days through the same
code. It therefore reproduces the daily runs exactly; the tests check this on a week of
synthetic runs, including results that arrive a day late.

A few other behaviours:

- **Assignments made later.** When the admin assigns a team whose maps were skipped, the next run
  reads those archived days back by itself. If those days have already settled, the run warns
  that a full recalculation is needed to rate them.
- **One data source per database.** `run_state.data_source` records which source built the file,
  and the run refuses the other one. Switching from synthetic to Liquipedia needs an empty
  `rankings.db`.
- **Synthetic runs need no admin.** `SyntheticSetup` assigns every simulated team, adds the
  season starts and relegations, and archives under `synthetic/`, so synthetic days never mix
  with real ones in B2. A synthetic run on an empty database starts from the whole simulated
  history.
- **Freezes come from the admin's status changes.** A relegated or disbanded status freezes the
  team from its effective date, in daily runs and full recalculations alike. The team also stays
  unranked while that status is in effect, even if it plays again (its rating still moves); marking
  it active ranks it again. Statuses are usually set after the fact, so a team whose status is now
  relegated or disbanded is also frozen at the end of the day of its last map, whatever date was
  entered: it leaves the rankings, movers and its league's average from then on, keeps its rating,
  and its maps still count for the teams it played.

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
| `Storage/` | `schema.sql` (version 1) and `migrations/NNN-*.sql` (later versions, applied in order to older files), `RankingsDb` (open/create/migrate, maps, skipped-map log, run state), `ProviderIds` (provider keys → own team and player IDs), `RatingsStore` (checkpoint, snapshots, rated maps, roster events), `RatingStateJson` (the exact checkpoint format) |
| `Liquipedia/` | `LpdbMatchSource` (the `IMatchSource` adapter), `LpdbMatchParser`, `LpdbArchive` (one file per day), `HourlyCallLimiter` and `CallLogFile` (the 60-calls-an-hour budget, carried between runs) |
| `Archive/` | `IArchiveStore` with a local-folder store, an S3 store for B2, and a prefixed store for `synthetic/` |
| `Site/` | `SiteExport` (the site's JSON files), `SiteData` (that input read from the database) and `SampleSite` (that input built from synthetic seasons) |
| `DailyRun.cs` | The `run` command: fetch window, rating from the checkpoint, settling, export |
| `SyntheticSetup.cs` | The admin inputs a synthetic world needs, filled in automatically |
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
