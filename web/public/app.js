// R6 Power Ranking: a static page with one JSON file per tab, loaded when the tab opens.
// Routes: #/rankings, #/regions, #/movers, #/method, #/team/<id>.
"use strict";

const view = document.getElementById("view");
const cache = new Map();

const subregionsOf = { APAC: ["ASIA", "OCE", "APAC-N"] };
const filterLabels = [
  ["all", "All"], ["EU", "EU"], ["NA", "NA"], ["SA", "SA"], ["CN", "CN"], ["APAC", "APAC"],
  ["ASIA", "Asia"], ["OCE", "Oceania"], ["APAC-N", "APAC North"],
];
let rankingsFilter = "all";

function loadJson(path) {
  if (!cache.has(path)) {
    cache.set(path, fetch(`data/${path}`).then(response => {
      if (!response.ok) throw new Error(`${path}: ${response.status}`);
      return response.json();
    }));
  }
  return cache.get(path);
}

function escapeHtml(text) {
  return String(text ?? "").replace(/[&<>"']/g, character => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;",
  })[character]);
}

function logoUrl(team) {
  return team.logo ? `logos/${encodeURIComponent(team.logo)}` : "logos/fallback.svg";
}

function teamLink(team, provisional = false) {
  const badge = provisional ? '<span class="badge" title="Fewer than 10 rated maps">Provisional</span>' : "";
  return `<a class="team" href="#/team/${team.id}"><img src="${logoUrl(team)}" alt="" loading="lazy"
    onerror="this.onerror=null;this.src='logos/fallback.svg'">${escapeHtml(team.name)}</a>${badge}`;
}

function formatRating(value) {
  return Number(value).toFixed(1);
}

function formatChange(value) {
  if (value > 0.05) return `<span class="up">▲ ${value.toFixed(1)}</span>`;
  if (value < -0.05) return `<span class="down">▼ ${Math.abs(value).toFixed(1)}</span>`;
  return '<span class="muted">–</span>';
}

function formatDate(value) {
  return new Date(value).toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric", timeZone: "UTC" });
}

function regionLabel(team) {
  return team.region === team.league ? team.leagueName : `${team.region} · ${team.leagueName}`;
}

async function showRankings() {
  const { teams } = await loadJson("rankings.json");
  const matches = row => rankingsFilter === "all"
    || row.team.region === rankingsFilter || row.team.league === rankingsFilter;
  const rows = teams.filter(matches);
  const buttons = filterLabels.map(([id, label]) => {
    const isSub = Object.values(subregionsOf).some(list => list.includes(id));
    return `<button type="button" class="${isSub ? "sub" : ""}" data-filter="${id}"
      aria-pressed="${rankingsFilter === id}">${label}</button>`;
  }).join("");

  view.innerHTML = `
    <h1>Rankings</h1>
    <p class="lead">Every active top-level team by rating, with the change since yesterday.</p>
    <div class="filters" role="group" aria-label="Region">${buttons}</div>
    <div class="table-scroll"><table>
      <thead><tr><th class="num">#</th><th>Team</th><th class="hide-narrow">Region</th>
        <th class="num">Rating</th><th class="num">Change</th><th class="num hide-narrow">Maps</th></tr></thead>
      <tbody>${rows.map(row => `<tr>
        <td class="num">${row.rank}</td>
        <td>${teamLink(row.team, row.provisional)}</td>
        <td class="hide-narrow">${escapeHtml(regionLabel(row.team))}</td>
        <td class="num">${formatRating(row.rating)}</td>
        <td class="num">${formatChange(row.change)}</td>
        <td class="num hide-narrow">${row.maps}</td>
      </tr>`).join("")}</tbody>
    </table></div>
    ${rows.length === 0 ? '<p class="muted">No ranked teams in this region.</p>' : ""}`;

  view.querySelectorAll("[data-filter]").forEach(button => button.addEventListener("click", () => {
    rankingsFilter = button.dataset.filter;
    showRankings();
  }));
}

function regionRow(region, isSubregion) {
  const played = region.international.won + region.international.lost;
  const share = played > 0 ? `${Math.round(100 * region.international.won / played)}%` : "–";
  const name = isSubregion ? region.name : `<strong>${escapeHtml(region.name)}</strong>`;
  return `<tr class="${isSubregion ? "subregion" : ""}">
    <td>${isSubregion ? escapeHtml(name) : name}</td>
    <td class="num">${formatRating(region.rating)}</td>
    <td class="num">${formatChange(region.change)}</td>
    <td class="num hide-narrow">${region.teams}</td>
    <td class="num">${region.international.won}–${region.international.lost}</td>
    <td class="num hide-narrow">${share}</td>
  </tr>`;
}

async function showRegions() {
  const { regions } = await loadJson("regions.json");
  const rows = regions.map(region =>
    regionRow(region, false) + region.subregions.map(subregion => regionRow(subregion, true)).join("")).join("");

  view.innerHTML = `
    <h1>Regions</h1>
    <p class="lead">Each region's rating is its teams' average strength. APAC shows the average of its three subregions,
      which are rated separately; maps between them count as international.</p>
    <div class="table-scroll"><table>
      <thead><tr><th>Region</th><th class="num">Rating</th><th class="num">Change</th><th class="num hide-narrow">Teams</th>
        <th class="num">International maps this season</th><th class="num hide-narrow">Won</th></tr></thead>
      <tbody>${rows}</tbody>
    </table></div>`;
}

function moverList(movers) {
  if (movers.length === 0) return '<p class="muted">None.</p>';
  return `<table><tbody>${movers.map(mover => `<tr>
    <td>${teamLink(mover.team)}</td>
    <td class="num">${formatRating(mover.rating)}</td>
    <td class="num">${formatChange(mover.change)}</td>
  </tr>`).join("")}</tbody></table>`;
}

async function showMovers() {
  const { periods } = await loadJson("movers.json");
  view.innerHTML = `
    <h1>Movers</h1>
    <p class="lead">Biggest rises and falls in rating among ranked teams.</p>
    ${periods.map(period => `
      <h2>Last ${period.days} days <span class="muted">(since ${formatDate(period.since)})</span></h2>
      <div class="movers">
        <section class="card"><h3>Rises</h3>${moverList(period.rises)}</section>
        <section class="card"><h3>Falls</h3>${moverList(period.falls)}</section>
      </div>`).join("")}
    ${periods.length === 0 ? '<p class="muted">Not enough history yet.</p>' : ""}`;
}

function historyChart(history) {
  if (history.length < 2) return '<p class="muted">Not enough history yet.</p>';
  const width = 720, height = 220, padLeft = 44, padRight = 8, padTop = 10, padBottom = 24;
  const times = history.map(point => Date.parse(point.date));
  const ratings = history.map(point => point.rating);
  const [minTime, maxTime] = [Math.min(...times), Math.max(...times)];
  const [minRating, maxRating] = [Math.min(...ratings), Math.max(...ratings)];
  const ratingSpan = Math.max(maxRating - minRating, 10);
  const x = time => padLeft + (width - padLeft - padRight) * (time - minTime) / Math.max(maxTime - minTime, 1);
  const y = rating => padTop + (height - padTop - padBottom) * (1 - (rating - minRating) / ratingSpan);
  const points = history.map((point, index) => `${x(times[index]).toFixed(1)},${y(point.rating).toFixed(1)}`).join(" ");
  const gridValues = [minRating, minRating + ratingSpan / 2, minRating + ratingSpan];
  return `<svg class="chart" viewBox="0 0 ${width} ${height}" role="img"
      aria-label="Rating from ${formatRating(ratings[0])} to ${formatRating(ratings[ratings.length - 1])}">
    ${gridValues.map(value => `<line class="grid" x1="${padLeft}" x2="${width - padRight}" y1="${y(value)}" y2="${y(value)}"/>
      <text x="${padLeft - 6}" y="${y(value) + 4}" text-anchor="end">${Math.round(value)}</text>`).join("")}
    <polyline points="${points}" fill="none" stroke="currentColor" stroke-width="2" stroke-linejoin="round"/>
    <text x="${padLeft}" y="${height - 6}">${formatDate(history[0].date)}</text>
    <text x="${width - padRight}" y="${height - 6}" text-anchor="end">${formatDate(history[history.length - 1].date)}</text>
  </svg>`;
}

// The methodology is static text kept in index.html, so it shows even when no data has loaded.
function showMethod() {
  view.replaceChildren(document.getElementById("method-template").content.cloneNode(true));
}

const rosterLabels = {
  Seeded: "New team, rated from its players",
  NewCore: "Most of the roster changed: rated as a new team",
  RosterChange: "Roster change",
  Returned: "Back in the top level",
};

async function showTeam(teamId) {
  let data;
  try {
    data = await loadJson(`teams/${encodeURIComponent(teamId)}.json`);
  } catch {
    view.innerHTML = '<h1>Team not found</h1><p><a href="#/rankings">Back to the rankings</a></p>';
    return;
  }
  const { team } = data;
  const rank = data.rank ? `#${data.rank}` : "Not ranked";
  view.innerHTML = `
    <p><a href="#/rankings">← Rankings</a></p>
    <div class="team-head">
      <img src="${logoUrl(team)}" alt="" onerror="this.onerror=null;this.src='logos/fallback.svg'">
      <div>
        <h1>${escapeHtml(team.name)}${data.provisional ? '<span class="badge">Provisional</span>' : ""}</h1>
        <div class="muted">${escapeHtml(regionLabel(team))}</div>
      </div>
    </div>
    <div class="stats">
      <div class="stat"><div class="value">${rank}</div><div class="label">Rank</div></div>
      <div class="stat"><div class="value">${formatRating(data.rating)}</div><div class="label">Rating</div></div>
      <div class="stat"><div class="value">${formatChange(data.change)}</div><div class="label">Since yesterday</div></div>
      <div class="stat"><div class="value">${data.maps}</div><div class="label">Rated maps</div></div>
    </div>
    <h2>Rating history</h2>
    ${historyChart(data.history)}
    <h2>Recent maps</h2>
    ${data.recent.length === 0 ? '<p class="muted">No rated maps yet.</p>' : `<div class="table-scroll"><table>
      <thead><tr><th>Date</th><th>Opponent</th><th class="num">Score</th><th class="num">Rating</th>
        <th class="hide-narrow">Event</th></tr></thead>
      <tbody>${data.recent.map(map => `<tr>
        <td>${formatDate(map.playedAt)}</td>
        <td>${teamLink(map.opponent)}</td>
        <td class="num"><span class="${map.won ? "up" : "down"}">${map.won ? "W" : "L"}</span> ${map.roundsFor}–${map.roundsAgainst}</td>
        <td class="num">${formatChange(map.change)}</td>
        <td class="hide-narrow muted">${escapeHtml(map.tournament ?? "")} · map ${map.mapNumber}</td>
      </tr>`).join("")}</tbody>
    </table></div>`}
    <h2>Roster changes</h2>
    ${data.roster.length === 0 ? '<p class="muted">None recorded.</p>' : `<ul>${data.roster.map(change => `
      <li>${formatDate(change.at)}: ${escapeHtml(rosterLabels[change.kind] ?? change.kind)}${change.newPlayers > 0
        ? ` (${change.newPlayers} new ${change.newPlayers === 1 ? "player" : "players"})` : ""}</li>`).join("")}</ul>`}`;
}

async function route() {
  const [, section = "rankings", id] = location.hash.replace(/^#\/?/, "#/").split("/");
  const tab = section === "team" ? null : section;
  document.querySelectorAll("[data-tab]").forEach(link => link.classList.toggle("active", link.dataset.tab === tab));
  try {
    if (section === "team" && id) await showTeam(id);
    else if (section === "regions") await showRegions();
    else if (section === "movers") await showMovers();
    else if (section === "method") showMethod();
    else await showRankings();
  } catch (error) {
    view.innerHTML = `<h1>Something went wrong</h1><p class="muted">${escapeHtml(error.message)}</p>`;
  }
  window.scrollTo(0, 0);
}

async function showUpdated() {
  try {
    const meta = await loadJson("meta.json");
    const updated = new Date(meta.updatedAt).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
    const synthetic = meta.dataSource === "synthetic" || meta.algorithmVersion === "sample";
    document.getElementById("updated").textContent =
      `Last updated ${updated}${synthetic ? " · sample data from synthetic seasons" : ""}.`;
  } catch {
    // The page still works without the meta file.
  }
}

window.addEventListener("hashchange", route);
showUpdated();
route();
