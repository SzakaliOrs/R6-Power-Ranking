# Rankings.Admin

A local admin dashboard for the manual inputs the rating needs. It edits a local `rankings.db`
directly and moves the file to and from R2 when asked. It is never deployed: the Worker serves
`web/public` only, and the daily job does not reference this project.

## Running it

```powershell
dotnet run --project src/Rankings.Admin -- [--db rankings.db] [--port 5199]
```

Then open <http://localhost:5199>. `--db` defaults to `rankings.db` in the folder it starts in. If
the file does not exist, pull it from R2 or create it with `Rankings.Job init-db`.

To use the R2 sync, load `R2_ACCESS_KEY_ID`, `R2_SECRET_ACCESS_KEY`, `R2_ENDPOINT` and `R2_BUCKET`
into the session first, e.g. from the root `.env`. Without them, the dashboard still edits the local
file and shows the sync as unconfigured.

## Sections

| Section | What you do there |
|---|---|
| Assign teams | Link provider team keys the job has seen to a new or existing team. This fixes the team's league. |
| Teams | Create teams and set display names and logos by date. Mark a team active, relegated or disbanded. |
| Players | Find players and relink a renamed player's new provider key to the existing player |
| Seasons | Add or remove top-level season starts. Each start pulls offsets toward zero, and the first season is the warm-up. |
| Leagues | Set the entry level of a league that enters later (CN) |
| Skipped maps | See maps the job could not rate, and why |
| Sync with R2 | Pull `rankings.db` from R2, or push your edits back |

An organisation that moves its name to another region is entered as a new team in that region.

## Sync rules

The daily job downloads `rankings.db` at 04:17 UTC and uploads it when it is done, so:

- **Pushing is blocked from 04:00 to 06:00 UTC.**
- **A push is refused if R2's copy has changed since your pull**, because pushing would overwrite a
  day's ratings.
- **A pull is refused while you have local edits that are not pushed**, unless you choose to
  overwrite them.

What was pulled is recorded next to the database in `<db>.r2.json`, which is ignored by git.

## Local-only safeguards

- **No CI:** it refuses to start when `CI` or `GITHUB_ACTIONS` is set.
- **Loopback only:** Kestrel listens on localhost, whatever `ASPNETCORE_URLS` says.
- **Host filtering:** the Host header must be `localhost`, `127.0.0.1` or `[::1]`, which stops DNS
  rebinding.
- **Change header:** every change request must carry the `X-Rankings-Admin: 1` header, which only
  the dashboard's own page sends.
