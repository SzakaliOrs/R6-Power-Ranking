# Rankings.Sim

Synthetic top-level seasons where every team has a hidden "true" strength. With them, the engine
can be tested and roughly tuned before real data exists, by checking how closely its ranking
follows strengths that are known.

## The world

- **7 leagues of 10 teams:** EU, NA, SA, CN, and the three APAC leagues (ASIA, OCE, APAC-N).
- **Each season:** two league stages (a round robin, one round a week, then playoffs for the top
  four), two majors and a world championship.
- **International spots** follow the real split: EU, NA and SA send 4 each. APAC sends 4 through a
  BO1 qualifier between the top two of each subregion, so a subregion can send two teams, one or
  none. China (`LateLeague`, default `CN`) only plays from the latest season, as it did in 2026,
  and gets its 4 spots then.
- **Strength** is the league's average, plus the organisation's own share, plus the players'
  average skill.
- **Map scores** are played round by round: first to 7, with overtime to 8 from 6–6. A stronger team
  wins more rounds, so the margin carries information.
- **Between seasons**, players' skill drifts, rosters change, the last team in a league may be
  relegated, and teams may disband or be founded.

The same seed always gives the same world.

## Main types

| Type | Purpose |
|---|---|
| `SimulationOptions` | Seed, number of seasons, late league and the spreads of league, organisation and player strength |
| `Simulator` | Builds a `SimulatedWorld` from the options |
| `SimulatedWorld` | The seasons' series, the `History` to replay, and `TrueStrengths` to compare against |
| `SimulatedMatchSource` | The world as an `IMatchSource`. Its raw responses are shaped like LPDB match2 pages, so they run through the real parser and archive path without calling Liquipedia |
| `MapScores` | Round-by-round map play and conversion between round and map win chances |

## Used by

- `Rankings.Job backtest --synthetic`: tuning and the sensitivity check
- `Rankings.Job sample-site`: the frontend's sample JSON
- The tests, for whole-pipeline checks on known data
