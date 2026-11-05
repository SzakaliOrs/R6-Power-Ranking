# web

The public site: one static page with no build step and no framework, served by a Cloudflare
Worker from `public/`.

## Files

| Path | Contents |
|---|---|
| `public/index.html` | Page shell: header tabs, a view container, and the footer with the last-updated time |
| `public/app.js` | Hash router and rendering. Each tab loads its JSON file the first time it opens. |
| `public/styles.css` | Styles |
| `public/logos/` | Team logos; `fallback.svg` is used for teams without one |
| `public/data/` | The JSON the page reads. It is generated and ignored by git (see below). |
| `wrangler.jsonc` | Worker config: static assets from `public/`, production only (`workers_dev` and preview URLs off) |

## Routes

| Route | Shows | Data |
|---|---|---|
| `#/rankings` | Ranked teams with a region filter (APAC can be split into its subregions), provisional marks and logos | `data/rankings.json` |
| `#/regions` | League ratings, with APAC as the average of its subregions | `data/regions.json` |
| `#/movers` | The 10 biggest rises and falls over the last 7 and 30 days | `data/movers.json` |
| `#/method` | How the ranking works, in plain language. The text is the `method-template` in `index.html`. | none |
| `#/team/<id>` | One team's view | `data/teams/<id>.json` |

`data/meta.json` holds the last-updated time shown in the footer.

## Data

`Rankings.Job` writes `public/data/`. Run this from the repository root:

```powershell
dotnet run --project src/Rankings.Job -- sample-site     # synthetic sample data
```

The daily job will write the real data to the same place before each deploy.

## Preview and deploy

The commands below need Node.js 22.

```powershell
cd web
npx wrangler@4 dev                                      # local preview
npx wrangler@4 deploy --name $env:CF_WORKER_NAME        # production
```

Deploying needs `CLOUDFLARE_API_TOKEN` and `CLOUDFLARE_ACCOUNT_ID` in the environment. The custom
domain is attached to the Worker in the Cloudflare dashboard, and deploys leave it as it is.
Generate `public/data/` before deploying, or the page loads with no data.
