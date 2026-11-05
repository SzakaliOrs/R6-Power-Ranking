# R6 Power Ranking

Daily power rankings of top-level Rainbow Six Siege teams, rated map by map with a two-layer Elo:
each team's rating is its **league rating** plus its **offset** within that league. Matches come from
Liquipedia (LPDB match2), the ratings are recomputed once a day, and the result is published as a
static site.

## How it runs

Everything runs on free tiers, with no server of its own:

| Piece | Role |
|---|---|
| GitHub Actions runner | Runs the daily job: fetch, rate, export, deploy |
| Cloudflare R2 | Holds `rankings.db` (the state the next run needs), its backups and the Liquipedia call log |
| Backblaze B2 | Archives the raw Liquipedia responses, one gzipped file per UTC day: the source of truth for replays |
| Cloudflare Worker | Serves the static site from `web/public` on the custom domain |

The admin dashboard is the only other piece. It runs on the admin's own machine and syncs
`rankings.db` with R2 by hand.

## Repository layout

| Path | What it is |
|---|---|
| [src/Rankings.Core](src/Rankings.Core/README.md) | The rating engine: map records in, ratings and snapshots out. No I/O. |
| [src/Rankings.Sim](src/Rankings.Sim/README.md) | Synthetic seasons with hidden team strengths, for testing and tuning before real data |
| [src/Rankings.Job](src/Rankings.Job/README.md) | The command-line job: SQLite storage, Liquipedia adapter, archive, backtest, site export |
| [src/Rankings.Admin](src/Rankings.Admin/README.md) | Local-only admin dashboard for manual inputs (team assignments, seasons, names, logos) |
| [tests/Rankings.Tests](tests/Rankings.Tests/README.md) | xUnit tests for all of the above |
| [web](web/README.md) | The static frontend and its Worker config |
| [.github/workflows](.github/workflows) | GitHub Actions workflows |

Dependencies point one way: `Core` ← `Sim` ← `Job` ← `Admin`. `Core` depends on nothing.

## Getting started

Requirements: the [.NET 10 SDK](https://dotnet.microsoft.com/download), and Node.js 22 when you
deploy the site or preview it with wrangler.

```powershell
dotnet build
dotnet test

# An empty database with the current schema
dotnet run --project src/Rankings.Job -- init-db rankings.db

# Backtest the engine on synthetic seasons (reads and writes nothing)
dotnet run --project src/Rankings.Job -- backtest --synthetic

# Generate the site's sample JSON into web/public/data, then preview the site
dotnet run --project src/Rankings.Job -- sample-site
cd web; npx wrangler@4 dev
```

`web/public/data/`, `rankings.db`, `lpdb-calls.json` and `archive/` are generated locally and
ignored by git.

## Configuration

Credentials come from environment variables. Locally, keep them in a `.env` file at the repository
root. It is ignored by git, and its header shows how to load it into a PowerShell session. On
GitHub Actions the same values come from repository secrets and variables.

| Variable | Used by | GitHub name |
|---|---|---|
| `CLOUDFLARE_API_TOKEN`, `CLOUDFLARE_ACCOUNT_ID` | `wrangler deploy` | secret `CF_API_TOKEN`, variable `CF_ACCOUNT_ID` |
| `CF_WORKER_NAME` | `wrangler deploy --name` | variable `CF_WORKER_NAME` |
| `R2_ACCESS_KEY_ID`, `R2_SECRET_ACCESS_KEY` | Admin R2 sync, workflows | secrets, same names |
| `R2_ENDPOINT`, `R2_BUCKET` | Admin R2 sync, workflows | variables, same names |
| `B2_KEY_ID`, `B2_APPLICATION_KEY` | Job with `--b2` | secrets, same names |
| `B2_ENDPOINT`, `B2_BUCKET` | Job with `--b2` | variables, same names |
| `LIQUIPEDIA_API_KEY` | Job `fetch` and `backfill` | secret, same name |
| `LIQUIPEDIA_USER_AGENT` | Job `fetch` and `backfill` | variable, same name |

## Workflows

- **Daily run** ([daily.yml](.github/workflows/daily.yml)): every day at 04:17 UTC, and by hand with
  an optional **full recalculation**. It downloads `rankings.db` and the call log from R2, runs
  `Rankings.Job run --b2`, deploys the site, backs up the previous database to `db-backups/` (keeping
  the latest 14), and uploads the new one. Any failed step stops the run before the live database
  is replaced. The call log is uploaded even after a failure.
- **Setup check** ([setup-check.yml](.github/workflows/setup-check.yml)): a manual run that lists
  both buckets and deploys `web/public` to the Worker. Because `web/public/data/` is not in git, the
  deployed page has no data unless a step generates it first.

The daily run reads two optional repository variables:

| Variable | Default | Meaning |
|---|---|---|
| `MATCH_SOURCE` | `synthetic` | `synthetic` or `liquipedia`. Switching needs an empty `rankings.db` in R2. |
| `BACKUPS_KEPT` | `14` | How many `db-backups/` copies to keep |

## Status

Phases 0 to 4 are done: setup, data model, rating engine, synthetic data and backtest, and the
frontend on the custom domain. The daily pipeline (Phase 5) is built and tested locally on
synthetic data; it is done once a week of scheduled runs has worked. Manual team changes in the
rating runs (Phase 6) come next.

## License

[MIT](LICENSE)
