# Rankings.Core

The rating engine as a self-contained library. A list of map records goes in, ratings and daily
snapshots come out. There is no database, file or network code here, so the daily job, full
replays, the backtest and the tests all run the same code.

## The model

A team's rating is **its league's rating plus its own offset**. EU, NA, SA and CN are one league
each. APAC is three leagues (ASIA, OCE, APAC-N) grouped for display.

- **Per map:** `Δ = K · M · (S − E)`. `E` is the Elo expectation (400 points = 10 to 1 odds), and
  `M` is the round-margin multiplier with a favourite correction: a big margin counts more, a
  favourite's win counts less and an upset counts more.
- **Within one league:** only the two teams' offsets move, with `KRegional`.
- **Between two leagues** (including two APAC leagues): the offsets move with `KInternational`, and
  both league ratings move with `KLeague`.
- **Series bonus:** a BO3 or BO5 winner gets one extra update with `KSeries`.
- **Re-centring:** after each run every league's offsets are shifted to average zero, and the
  difference moves into the league rating. No team's total rating changes.

Before a team plays a map, the engine also handles its roster, its history and the calendar:

- **Seeding.** A new team starts from a share of its players' previous offsets. A player with no
  rated history counts as `NewcomerGap` below the league.
- **Roster changes.** A lineup must keep `RosterCoreSize` (3) players from the last one to stay the
  same team. Each new player pulls the offset toward zero, and K is boosted for the next few maps.
  Fewer than 3 kept means the team is reseeded under the same ID.
- **Freezing.** A relegated or disbanded team keeps its rating and is not ranked. It comes back if
  it plays again.
- **Season starts** pull every offset part-way toward zero.
- **Inactivity decay** starts after 90 days without a map.
- **Provisional flag:** a team is provisional until it has 10 rated maps.
- **Warm-up season:** K is doubled during the first replayed season.
- **Late leagues:** a league that enters later (China in 2026) starts at the average established
  league plus an admin-set entry level.

## Main types

| Type | Purpose |
|---|---|
| `MapRecord` | One played map in the project's own format, with own team and player IDs |
| `IMatchSource`, `SourceMap`, `SourceBatch` | Where maps come from, before provider keys are resolved to own IDs |
| `RatingSettings` | Every tunable value with its starting default, so the backtest can change them |
| `RatingState` | Everything carried between runs: league ratings, team offsets, players' last offsets |
| `RatingEngine` | One run: rates maps series by series, then applies seasons, decay and re-centring |
| `Elo` | The formulas: expectation, margin multiplier, series win chance, update |
| `DailyRating` | Rates one UTC day at a time and keeps all it needs in the `RatingState`, so the daily job can stop after any day and carry on next run with identical results |
| `Replay` | Rates a whole `History` one UTC day at a time, for the backtest and the sample site; it knows every team in advance |
| `Snapshots` | A day's ranked team rows and league rows |
| `Backtest` | Log-loss scoring, a simple settings search, and rank correlation against known strengths |

## Usage

```csharp
var settings = new RatingSettings();                 // the plan's starting values
var state = Replay.Run(history, settings);           // history: teams, maps, freezes, season starts
var ranking = Replay.Ranking(state);                 // team IDs, highest rating first
var snapshot = Snapshots.Take(state, day, settings, previousSnapshot);
```

Replaying the same maps twice always gives identical ratings. The tests check this, together with
re-centring never changing a total rating.
