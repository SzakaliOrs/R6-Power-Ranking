// The local admin dashboard: forms over the /api endpoints, one section per hash route.
"use strict";

const view = document.getElementById("view");
const message = document.getElementById("message");
const today = () => new Date().toISOString().slice(0, 10);

function escapeHtml(text) {
  return String(text ?? "").replace(/[&<>"']/g, character => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;",
  })[character]);
}

async function api(path, options = {}) {
  // The server only accepts changes that carry this header, which other sites cannot send here.
  const headers = { "X-Rankings-Admin": "1" };
  if (options.body) headers["Content-Type"] = "application/json";
  const response = await fetch(`/api/${path}`, {
    ...options,
    headers,
    body: options.body ? JSON.stringify(options.body) : undefined,
  });
  if (response.status === 204) return null;
  const content = await response.json().catch(() => null);
  if (!response.ok) throw new Error(content?.error ?? `${response.status} ${response.statusText}`);
  return content;
}

function show(text, kind) {
  message.textContent = text;
  message.className = kind;
}

/** Runs a change, reports it, refreshes the section. */
async function change(action, done) {
  try {
    await action();
    show(done, "ok");
    await route(false);
  } catch (error) {
    show(error.message, "error");
  }
}

function formValues(form) {
  return Object.fromEntries(new FormData(form).entries());
}

async function leagueOptions(selected) {
  return leagueOptionsFrom(await api("regions"), selected);
}

/** The leagues a team can be in, from an already loaded regions list. */
function leagueOptionsFrom(regions, selected) {
  return regions.filter(region => region.rating !== null)
    .map(region => `<option value="${region.regionId}" ${region.regionId === selected ? "selected" : ""}>${escapeHtml(region.name)}</option>`)
    .join("");
}

function teamLabel(team) {
  return `${team.name ?? `Team ${team.teamId}`} (${team.league}, #${team.teamId})`;
}

function teamOptions(teams) {
  return teams.map(team => `<option value="${team.teamId}">${escapeHtml(teamLabel(team))}</option>`).join("");
}

// Sortable tables: every column with a title in a table's header sorts on click, by the cell's
// data-sort-value or else its text. The order survives re-renders until the page is reloaded.

const tableSorts = {};

function cellSortValue(row, column) {
  const cell = row.cells[column];
  return (cell?.dataset.sortValue ?? cell?.textContent ?? "").trim();
}

function applyTableSort(table) {
  const sort = tableSorts[table.dataset.sortKey];
  [...table.tHead.rows[0].cells].forEach((header, column) => {
    const button = header.querySelector("button.sort");
    if (!button) return;
    const active = sort?.column === column;
    button.textContent = button.dataset.title + (active ? (sort.ascending ? " ▲" : " ▼") : "");
    button.classList.toggle("active", active);
    header.setAttribute("aria-sort", active ? (sort.ascending ? "ascending" : "descending") : "none");
  });
  const body = table.tBodies[0];
  if (!sort || !body) return;
  const rows = [...body.rows];
  const values = rows.map(row => cellSortValue(row, sort.column));
  const numeric = values.every(value => value === "" || value === "–" || Number.isFinite(Number(value)));
  const isEmpty = value => value === "" || value === "–";
  const order = rows.map((row, position) => ({ row, value: values[position], firstPosition: Number(row.dataset.firstPosition) }));
  order.sort((first, second) => {
    // Empty cells go last whichever way the column runs.
    if (isEmpty(first.value) !== isEmpty(second.value)) return isEmpty(first.value) ? 1 : -1;
    const comparison = numeric
      ? Number(first.value) - Number(second.value)
      : first.value.localeCompare(second.value, undefined, { sensitivity: "base", numeric: true });
    return (sort.ascending ? comparison : -comparison) || first.firstPosition - second.firstPosition;
  });
  body.append(...order.map(entry => entry.row));
}

function makeTablesSortable(sectionName) {
  view.querySelectorAll("table").forEach((table, tableNumber) => {
    if (!table.tHead) return;
    table.dataset.sortKey = `${sectionName}:${tableNumber}`;
    [...(table.tBodies[0]?.rows ?? [])].forEach((row, position) => { row.dataset.firstPosition = position; });
    [...table.tHead.rows[0].cells].forEach((header, column) => {
      const title = header.textContent.trim();
      if (title === "" || header.querySelector("input, select, button")) return;
      const button = document.createElement("button");
      button.type = "button";
      button.className = "sort";
      button.dataset.title = title;
      header.replaceChildren(button);
      button.addEventListener("click", () => {
        const current = tableSorts[table.dataset.sortKey];
        tableSorts[table.dataset.sortKey] = { column, ascending: current?.column === column ? !current.ascending : true };
        applyTableSort(table);
      });
    });
    applyTableSort(table);
  });
}

// Sections

async function showAssign() {
  const [unassigned, teams, leagues] = await Promise.all([api("unassigned-teams"), api("teams"), leagueOptions()]);
  view.innerHTML = `
    <h1>Assign teams</h1>
    <p class="lead">Team keys from Liquipedia not linked to a team yet; their maps are skipped until they are.
      A new roster core gets a new team; a rebrand or a core that moved to another organisation links to its existing team.
      Afterwards, run the job's reparse command for the days they played so the skipped maps are stored.</p>
    ${unassigned.length === 0 ? '<p class="muted">Every team key is assigned.</p>' : unassigned.map(team => `
      <fieldset>
        <legend>${escapeHtml(team.name)} <code>${escapeHtml(team.providerKey)}</code></legend>
        <div class="muted">Last seen ${escapeHtml(new Date(team.lastSeenAt).toISOString().slice(0, 10))}
          in ${escapeHtml(team.lastTournament ?? "an unknown event")}</div>
        <form class="inline" data-create="${escapeHtml(team.providerKey)}">
          <strong>New team:</strong>
          <select name="league" required>${leagues}</select>
          <input name="name" value="${escapeHtml(team.name)}" required aria-label="Name">
          <input name="shortName" placeholder="Short name (blank: full name)" aria-label="Short name">
          <input name="createdOn" type="date" value="${today()}" required aria-label="Start date">
          <button class="primary">Create and link</button>
        </form>
        ${teams.length === 0 ? "" : `<form class="inline" data-link="${escapeHtml(team.providerKey)}">
          <strong>Or existing team:</strong>
          <select name="teamId">${teamOptions(teams)}</select>
          <button>Link</button>
        </form>`}
      </fieldset>`).join("")}`;

  view.querySelectorAll("[data-create]").forEach(form => form.addEventListener("submit", event => {
    event.preventDefault();
    const values = formValues(form);
    change(() => api("teams", { method: "POST", body: { ...values, providerKey: form.dataset.create } }),
      `Created ${values.name} and linked ${form.dataset.create}.`);
  }));
  view.querySelectorAll("[data-link]").forEach(form => form.addEventListener("submit", event => {
    event.preventDefault();
    change(() => api("provider-teams/link", { method: "POST", body: { providerKey: form.dataset.link, targetId: Number(formValues(form).teamId) } }),
      `Linked ${form.dataset.link}.`);
  }));
}

let teamsRegionFilter = "";
let teamsStatusFilter = "";

async function showTeams() {
  const [teams, regions] = await Promise.all([api("teams"), api("regions")]);
  // A region with subregions (APAC) shows the teams of all its leagues.
  const inRegion = team => teamsRegionFilter === "" || team.league === teamsRegionFilter
    || regions.find(region => region.regionId === team.league)?.parentId === teamsRegionFilter;
  const inFilter = team => inRegion(team) && (teamsStatusFilter === "" || team.status === teamsStatusFilter);
  const statusFilterOptions = [["", "Any status"], ["active", "Active"], ["relegated", "Relegated"], ["disbanded", "Disbanded"]]
    .map(([status, label]) => `<option value="${status}" ${status === teamsStatusFilter ? "selected" : ""}>${label}</option>`)
    .join("");
  const shownTeams = teams.filter(inFilter);
  const regionFilterOptions = [{ regionId: "", name: "All regions" }, ...regions]
    .map(region => `<option value="${region.regionId}" ${region.regionId === teamsRegionFilter ? "selected" : ""}>${region.parentId ? "— " : ""}${escapeHtml(region.name)}</option>`)
    .join("");
  view.innerHTML = `
    <h1>Teams</h1>
    <p class="lead">Each team is a roster core in one league. A display change applies from its date, so history keeps the old name.
      Marking a team relegated or disbanded stops it being ranked from that date, in daily runs and full replays alike.
      Change league only to fix a wrong assignment; an organisation that really moves region gets a new team.
      Tick several teams to give them the same status at once.</p>
    ${teams.length === 0 ? "" : `<fieldset><legend>Selected teams</legend>
      <form class="inline" id="bulk-status">
        <span id="selected-count" class="muted">None selected</span>
        <select name="status" aria-label="Status">${["relegated", "disbanded", "active"].map(status => `<option>${status}</option>`).join("")}</select>
        <label><input type="checkbox" name="afterLastMap" checked> from the day after each team's last map</label>
        <input name="effectiveOn" type="date" value="${today()}" aria-label="From">
        <input name="note" placeholder="Note" aria-label="Note">
        <button class="primary">Set status for selected</button>
      </form>
    </fieldset>`}
    ${teams.length === 0 ? "" : `<form class="inline" id="region-filter">
      <select name="region" aria-label="Show teams from">${regionFilterOptions}</select>
      <select name="status" aria-label="Show teams with status">${statusFilterOptions}</select>
      <span class="muted">${shownTeams.length} of ${teams.length} teams</span>
    </form>`}
    <table>
      <thead><tr><th><input type="checkbox" id="select-all" aria-label="Select every team shown"></th><th>#</th><th>League</th><th>Name</th><th>Logo</th><th>Status</th><th>Liquipedia keys</th><th></th></tr></thead>
      <tbody>${shownTeams.map(team => `<tr>
        <td><input type="checkbox" data-select="${team.teamId}" aria-label="Select ${escapeHtml(team.name ?? `team ${team.teamId}`)}"></td>
        <td class="num">${team.teamId}</td>
        <td>${team.league}</td>
        <td data-sort-value="${escapeHtml(team.name ?? "")}">${escapeHtml(team.name ?? "–")} <span class="muted">${escapeHtml(team.shortName ?? "")}</span></td>
        <td data-sort-value="${escapeHtml(team.logo ?? "")}">${team.logo ?`<code>${escapeHtml(team.logo)}</code>` : '<span class="muted">fallback</span>'}</td>
        <td>${team.status}</td>
        <td data-sort-value="${escapeHtml(team.providerKeys[0] ?? "")}">${team.providerKeys.map(key => `<code>${escapeHtml(key)}</code>`).join("<br>") || '<span class="muted">none</span>'}</td>
        <td><details><summary>Edit</summary>
          <form class="inline" data-league="${team.teamId}">
            <select name="league" aria-label="League">${leagueOptionsFrom(regions, team.league)}</select>
            <button>Change league</button>
          </form>
          ${teams.length < 2 ? "" : `<form class="inline" data-merge="${team.teamId}">
            <input name="intoTeam" list="merge-targets" placeholder="Type a team name" required autocomplete="off" aria-label="Team to merge into">
            <button>Merge into</button>
          </form>`}
          <form class="inline" data-display="${team.teamId}">
            <input name="validFrom" type="date" value="${today()}" required aria-label="From">
            <input name="name" value="${escapeHtml(team.name ?? "")}" required aria-label="Name">
            <input name="shortName" value="${escapeHtml(team.shortName ?? "")}" placeholder="Short name (blank: full name)" aria-label="Short name">
            <input name="logo" value="${escapeHtml(team.logo ?? "")}" placeholder="logo file, e.g. 12.svg" aria-label="Logo">
            <button>Save display</button>
          </form>
          <form class="inline" data-status="${team.teamId}">
            <select name="status" aria-label="Status">${["active", "relegated", "disbanded"].map(status =>
              `<option ${status === team.status ? "selected" : ""}>${status}</option>`).join("")}</select>
            <label><input type="checkbox" name="afterLastMap" checked> from the day after its last map</label>
            <input name="effectiveOn" type="date" value="${today()}" aria-label="From">
            <input name="note" placeholder="Note" aria-label="Note">
            <button>Set status</button>
          </form>
        </details></td>
      </tr>`).join("")}</tbody>
    </table>
    <datalist id="merge-targets">${teams.map(team => `<option value="${escapeHtml(teamLabel(team))}"></option>`).join("")}</datalist>
    ${teams.length === 0 ? '<p class="muted">No teams yet; create them from Assign teams.</p>'
      : shownTeams.length === 0 ? '<p class="muted">No teams match the filters.</p>' : ""}`;

  view.querySelector("#region-filter")?.addEventListener("change", () => {
    const values = formValues(view.querySelector("#region-filter"));
    teamsRegionFilter = values.region;
    teamsStatusFilter = values.status;
    route(false);
  });

  view.querySelectorAll("[data-league]").forEach(form => form.addEventListener("submit", event => {
    event.preventDefault();
    const { league } = formValues(form);
    const team = teams.find(candidate => candidate.teamId === Number(form.dataset.league));
    if (league === team.league) return show(`${team.name ?? `Team ${team.teamId}`} is already in ${league}.`, "error");
    let fullRecalculationNeeded = false;
    change(async () => {
      ({ fullRecalculationNeeded } = await api(`teams/${team.teamId}/league`, { method: "POST", body: { league } }));
    }, `Moved ${team.name ?? `team ${team.teamId}`} from ${team.league} to ${league}.`).then(() => {
      if (fullRecalculationNeeded)
        show(`Moved ${team.name ?? `team ${team.teamId}`} to ${league}. It has been rated already, so push to R2 and run a full recalculation (the Daily run workflow's option, or run --full) to re-rate its history in ${league}.`, "error");
    });
  }));
  view.querySelectorAll("[data-merge]").forEach(form => form.addEventListener("submit", event => {
    event.preventDefault();
    const label = teamLabel;
    const from = teams.find(candidate => candidate.teamId === Number(form.dataset.merge));
    const typed = formValues(form).intoTeam.trim();
    const into = teams.find(candidate => teamLabel(candidate) === typed);
    if (!into) return show(`Pick the team to merge into from the suggestions; "${typed}" is not one of them.`, "error");
    if (into.teamId === from.teamId) return show(`${label(from)} cannot be merged into itself.`, "error");
    if (!confirm(`Merge ${label(from)} into ${label(into)}?\n\nIts Liquipedia keys and maps move to ${label(into)}, which keeps its own `
      + `league, names and status. ${label(from)} is then deleted; the dashboard cannot undo this.`)) return;
    let fullRecalculationNeeded = false;
    change(async () => {
      ({ fullRecalculationNeeded } = await api(`teams/${from.teamId}/merge`, { method: "POST", body: { intoTeamId: into.teamId } }));
    }, `Merged ${label(from)} into ${label(into)}.`).then(() => {
      if (fullRecalculationNeeded)
        show(`Merged ${label(from)} into ${label(into)}. It had been rated already, so push to R2 and run a full recalculation (the Daily run workflow's option, or run --full) to re-rate the joined history.`, "error");
    });
  }));
  view.querySelectorAll("[data-display]").forEach(form => form.addEventListener("submit", event => {
    event.preventDefault();
    change(() => api(`teams/${form.dataset.display}/display`, { method: "POST", body: formValues(form) }), "Display saved.");
  }));
  view.querySelectorAll("[data-status]").forEach(form => form.addEventListener("submit", event => {
    event.preventDefault();
    const values = formValues(form);
    const afterLastMap = values.afterLastMap === "on" && values.status !== "active";
    if (!afterLastMap && !values.effectiveOn) return show("Pick the date the status starts.", "error");
    change(() => api("teams/status", {
      method: "POST",
      body: { teamIds: [Number(form.dataset.status)], status: values.status, effectiveOn: afterLastMap ? null : values.effectiveOn, note: values.note, afterLastMap },
    }), "Status saved. Run a full recalculation to see it in the rankings.");
  }));

  const selectBoxes = [...view.querySelectorAll("[data-select]")];
  const selectedTeamIds = () => selectBoxes.filter(box => box.checked).map(box => Number(box.dataset.select));
  const selectAll = view.querySelector("#select-all");
  const updateSelection = () => {
    const selectedCount = selectedTeamIds().length;
    const countLabel = view.querySelector("#selected-count");
    if (!countLabel) return;
    countLabel.textContent = selectedCount === 0 ? "None selected" : `${selectedCount} selected`;
    selectAll.checked = selectBoxes.length > 0 && selectedCount === selectBoxes.length;
    selectAll.indeterminate = selectedCount > 0 && selectedCount < selectBoxes.length;
  };
  selectBoxes.forEach(box => box.addEventListener("change", updateSelection));
  selectAll?.addEventListener("change", () => {
    selectBoxes.forEach(box => { box.checked = selectAll.checked; });
    updateSelection();
  });
  view.querySelector("#bulk-status")?.addEventListener("submit", event => {
    event.preventDefault();
    const teamIds = selectedTeamIds();
    if (teamIds.length === 0) return show("Tick the teams to change first.", "error");
    const values = formValues(event.target);
    const afterLastMap = values.afterLastMap === "on" && values.status !== "active";
    if (!afterLastMap && !values.effectiveOn) return show("Pick the date the status starts.", "error");
    const from = afterLastMap ? "the day after each team's last map" : values.effectiveOn;
    const names = teams.filter(team => teamIds.includes(team.teamId)).map(team => team.name ?? `Team ${team.teamId}`);
    if (!confirm(`Mark ${teamIds.length} team(s) ${values.status} from ${from}?\n\n${names.join("\n")}`)) return;
    change(() => api("teams/status", {
      method: "POST",
      body: { teamIds, status: values.status, effectiveOn: afterLastMap ? null : values.effectiveOn, note: values.note, afterLastMap },
    }), `Marked ${teamIds.length} team(s) ${values.status} from ${from}. Run a full recalculation to see it in the rankings.`);
  });
}

let playerSearch = "";

async function showPlayers() {
  const players = await api(`players?search=${encodeURIComponent(playerSearch)}`);
  view.innerHTML = `
    <h1>Players</h1>
    <p class="lead">A renamed player arrives from Liquipedia under a new key and becomes a new player.
      Link the new key to the old player so the roster core rules still recognise them.</p>
    <form class="inline" id="search">
      <input name="search" value="${escapeHtml(playerSearch)}" placeholder="Name or Liquipedia key" aria-label="Search">
      <button>Search</button>
      ${playerSearch ? '<button type="button" id="clear-search">Show all</button>' : ""}
      <span class="muted">${players.length} player(s)</span>
    </form>
    ${players.length === 0 ? `<p class="muted">${playerSearch ? "No players found." : "No players yet."}</p>` : `<table>
      <thead><tr><th>#</th><th>Name</th><th>Liquipedia keys</th></tr></thead>
      <tbody>${players.map(player => `<tr><td class="num">${player.playerId}</td><td>${escapeHtml(player.name)}</td>
        <td>${player.providerKeys.map(key => `<code>${escapeHtml(key)}</code>`).join(", ")}</td></tr>`).join("")}</tbody>
    </table>`}
    <fieldset><legend>Link a key to a player</legend>
      <form class="inline" id="link">
        <input name="providerKey" placeholder="Liquipedia key (the new name)" required aria-label="Key">
        <input name="targetId" type="number" placeholder="Player #" required aria-label="Player">
        <button class="primary">Link</button>
      </form>
    </fieldset>`;

  view.querySelector("#search").addEventListener("submit", event => {
    event.preventDefault();
    playerSearch = formValues(event.target).search;
    route(false);
  });
  view.querySelector("#clear-search")?.addEventListener("click", () => {
    playerSearch = "";
    route(false);
  });
  view.querySelector("#link").addEventListener("submit", event => {
    event.preventDefault();
    const values = formValues(event.target);
    change(() => api("provider-players/link", { method: "POST", body: { providerKey: values.providerKey, targetId: Number(values.targetId) } }),
      `Linked ${values.providerKey} to player ${values.targetId}.`);
  });
}

async function showSeasons() {
  const seasons = await api("seasons");
  view.innerHTML = `
    <h1>Seasons</h1>
    <p class="lead">When each top-level season starts. Each start pulls every team's offset part-way toward its league's
      average, and the first season of a replay is its warm-up. Set the start after the Six Invitational if rosters
      change after it.</p>
    <table>
      <thead><tr><th>Starts on</th><th>Name</th><th></th></tr></thead>
      <tbody>${seasons.map(season => `<tr><td>${season.startsOn}</td><td>${escapeHtml(season.name)}</td>
        <td><button data-delete="${season.startsOn}">Delete</button></td></tr>`).join("")}</tbody>
    </table>
    <fieldset><legend>Add or rename a season</legend>
      <form class="inline" id="season">
        <input name="startsOn" type="date" required aria-label="Starts on">
        <input name="name" placeholder="e.g. 2027" required aria-label="Name">
        <button class="primary">Save</button>
      </form>
    </fieldset>`;

  view.querySelector("#season").addEventListener("submit", event => {
    event.preventDefault();
    change(() => api("seasons", { method: "POST", body: formValues(event.target) }), "Season saved.");
  });
  view.querySelectorAll("[data-delete]").forEach(button => button.addEventListener("click", () => {
    if (confirm(`Delete the season starting ${button.dataset.delete}?`))
      change(() => api(`seasons/${button.dataset.delete}`, { method: "DELETE" }), "Season deleted.");
  }));
}

async function showLeagues() {
  const regions = await api("regions");
  view.innerHTML = `
    <h1>Leagues</h1>
    <p class="lead">The entry level only matters for a league new to the ratings, as China was in 2026: it starts that many
      points above (+) or below (−) the average established league when its first team is rated. Empty means level.</p>
    <table>
      <thead><tr><th>League</th><th>Region</th><th class="num">Rating</th><th>Entry level</th></tr></thead>
      <tbody>${regions.filter(region => region.rating !== null).map(region => `<tr>
        <td>${escapeHtml(region.name)} <code>${region.regionId}</code></td>
        <td>${region.parentId ?? region.regionId}</td>
        <td class="num">${region.rating.toFixed(1)}</td>
        <td data-sort-value="${region.entryLevel ?? ""}"><form class="inline" data-entry="${region.regionId}">
          <input name="level" type="number" step="1" value="${region.entryLevel ?? ""}" aria-label="Entry level">
          <button>Save</button>
        </form></td>
      </tr>`).join("")}</tbody>
    </table>`;

  view.querySelectorAll("[data-entry]").forEach(form => form.addEventListener("submit", event => {
    event.preventDefault();
    const level = formValues(form).level;
    change(() => api(`regions/${form.dataset.entry}/entry-level`, { method: "POST", body: { level: level === "" ? null : Number(level) } }),
      "Entry level saved.");
  }));
}

async function showSkipped() {
  const skipped = await api("skipped-maps");
  view.innerHTML = `
    <h1>Skipped maps</h1>
    <p class="lead">The latest 200 maps the job could not store or rate, with the reason. Maps waiting for a team assignment
      are stored once the team is assigned and the days are reparsed.</p>
    ${skipped.length === 0 ? '<p class="muted">Nothing skipped.</p>' : `<table>
      <thead><tr><th>Map</th><th>Played</th><th>Reason</th><th>Logged</th></tr></thead>
      <tbody>${skipped.map(map => `<tr><td><code>${escapeHtml(map.mapId)}</code></td>
        <td>${map.playedAt ? map.playedAt.slice(0, 10) : "–"}</td><td>${escapeHtml(map.reason)}</td>
        <td>${map.loggedAt.slice(0, 10)}</td></tr>`).join("")}</tbody>
    </table>`}`;
}

let rankingsDate = "";
let rankingsRegionFilter = "";
let rankingsShowUnranked = false;

function signedChange(change) {
  if (change === null || change === undefined) return "–";
  const rounded = change.toFixed(1);
  return change > 0 ? `+${rounded}` : rounded === "-0.0" ? "0.0" : rounded.replace("-", "−");
}

async function showRankings() {
  const [rankings, regions] = await Promise.all([api(`rankings?date=${encodeURIComponent(rankingsDate)}`), api("regions")]);
  if (rankings.date === null) {
    view.innerHTML = `<h1>Rankings</h1>
      <p class="muted">Nothing is rated in this rankings.db yet. Run a recalculation first (run --full on this file).</p>`;
    return;
  }
  // A region with subregions (APAC) shows the teams of all its leagues.
  const inFilter = league => rankingsRegionFilter === "" || league === rankingsRegionFilter
    || regions.find(region => region.regionId === league)?.parentId === rankingsRegionFilter;
  const unrankedCount = rankings.teams.filter(team => team.rank === null && inFilter(team.league)).length;
  const shownTeams = rankings.teams.filter(team => inFilter(team.league) && (rankingsShowUnranked || team.rank !== null));
  const regionFilterOptions = [{ regionId: "", name: "All regions" }, ...regions]
    .map(region => `<option value="${region.regionId}" ${region.regionId === rankingsRegionFilter ? "selected" : ""}>${region.parentId ? "— " : ""}${escapeHtml(region.name)}</option>`)
    .join("");
  view.innerHTML = `
    <h1>Rankings</h1>
    <p class="lead">The ratings your local runs saved in this rankings.db, as they stood at the end of the chosen day.
      Rating = league rating + the team's offset. Teams under 10 rated maps are provisional. Teams marked relegated or
      disbanded on that day have no rank and are hidden unless you tick the box, even if they still play; marking
      them active ranks them again. A status change shows here after the next recalculation. The live site changes only once this file is pushed and published.</p>
    <form class="inline" id="rankings-filter">
      <input name="date" type="date" value="${rankings.date}" min="${rankings.firstDate}" max="${rankings.lastDate}" aria-label="Day">
      <select name="region" aria-label="Show teams from">${regionFilterOptions}</select>
      <label><input type="checkbox" name="unranked" ${rankingsShowUnranked ? "checked" : ""}> Show ${unrankedCount} unranked (relegated, disbanded)</label>
      ${rankingsDate ? '<button type="button" id="latest-day">Latest day</button>' : ""}
      <span class="muted">${shownTeams.length} team(s) · rated ${rankings.firstDate} to ${rankings.lastDate}</span>
    </form>
    <h2>Leagues</h2>
    <table>
      <thead><tr><th>League</th><th>Region</th><th class="num">Rating</th><th class="num">7 days</th></tr></thead>
      <tbody>${rankings.leagues.map(league => `<tr>
        <td>${escapeHtml(league.name)} <code>${league.regionId}</code></td>
        <td>${league.parentId ?? league.regionId}</td>
        <td class="num">${league.rating.toFixed(1)}</td>
        <td class="num" data-sort-value="${league.weekChange ?? ""}">${signedChange(league.weekChange)}</td>
      </tr>`).join("")}</tbody>
    </table>
    <h2>Teams</h2>
    <table>
      <thead><tr><th class="num">Rank</th><th>Team</th><th>League</th><th class="num">Rating</th><th class="num">League rating</th>
        <th class="num">Offset</th><th class="num">7 days</th><th class="num">Maps</th><th>Status</th></tr></thead>
      <tbody>${shownTeams.map(team => `<tr>
        <td class="num">${team.rank ?? "–"}</td>
        <td data-sort-value="${escapeHtml(team.name ?? "")}">${escapeHtml(team.name ?? `Team ${team.teamId}`)} <span class="muted">${escapeHtml(team.shortName ?? "")} #${team.teamId}</span></td>
        <td>${team.league}</td>
        <td class="num">${team.rating.toFixed(1)}</td>
        <td class="num">${team.leagueRating.toFixed(1)}</td>
        <td class="num" data-sort-value="${team.offset}">${signedChange(team.offset)}</td>
        <td class="num" data-sort-value="${team.weekChange ?? ""}">${signedChange(team.weekChange)}</td>
        <td class="num">${team.mapsPlayed}</td>
        <td>${team.status}${team.provisional ? ' <span class="muted">provisional</span>' : ""}</td>
      </tr>`).join("")}</tbody>
    </table>
    ${shownTeams.length === 0 ? '<p class="muted">No rated teams in this region on this day.</p>' : ""}`;

  const filterForm = view.querySelector("#rankings-filter");
  filterForm.addEventListener("change", () => {
    const values = formValues(filterForm);
    rankingsDate = values.date === rankings.lastDate ? "" : values.date;
    rankingsRegionFilter = values.region;
    rankingsShowUnranked = values.unranked === "on";
    route(false);
  });
  filterForm.addEventListener("submit", event => event.preventDefault());
  view.querySelector("#latest-day")?.addEventListener("click", () => {
    rankingsDate = "";
    route(false);
  });
}

async function showSync() {
  const status = await api("status");
  const sync = status.sync;
  view.innerHTML = `
    <h1>Sync with R2</h1>
    <p class="lead">The dashboard edits a local copy of rankings.db. Pull it from R2 before editing and push it back when done.
      The daily job runs at 04:17 UTC, so pushing is blocked from 04:00 to 06:00 UTC, and refused if the job ran since your pull.</p>
    ${sync.configured ? "" : '<p class="warning">R2 is not set up: set R2_ACCESS_KEY_ID, R2_SECRET_ACCESS_KEY, R2_ENDPOINT and R2_BUCKET, then restart the dashboard.</p>'}
    ${sync.inJobWindow ? '<p class="warning">The daily job may be running now (04:00–06:00 UTC).</p>' : ""}
    <table><tbody>
      <tr><th>Local file</th><td><code>${escapeHtml(status.databasePath)}</code> ${status.databaseExists ? "" : '<span class="muted">(not there yet)</span>'}</td></tr>
      <tr><th>Last pulled</th><td>${sync.pulledAt ? new Date(sync.pulledAt).toLocaleString() : "never"}</td></tr>
      <tr><th>Local changes</th><td>${sync.localChanged ? "yes, not pushed" : "none"}</td></tr>
    </tbody></table>
    <p>
      <button class="primary" id="pull">Pull from R2</button>
      <button id="pull-overwrite">Pull and drop local changes</button>
      <button class="primary" id="push">Push to R2</button>
    </p>`;

  view.querySelector("#pull").addEventListener("click", () =>
    change(() => api("sync/pull", { method: "POST", body: { overwrite: false } }), "Pulled rankings.db from R2."));
  view.querySelector("#pull-overwrite").addEventListener("click", () => {
    if (confirm("Drop the local changes and pull R2's copy?"))
      change(() => api("sync/pull", { method: "POST", body: { overwrite: true } }), "Pulled rankings.db from R2.");
  });
  view.querySelector("#push").addEventListener("click", () => {
    if (confirm("Push the local rankings.db to R2? The next daily run will use it."))
      change(() => api("sync/push", { method: "POST" }), "Pushed rankings.db to R2.");
  });
}

const sections = {
  assign: showAssign, teams: showTeams, players: showPlayers, seasons: showSeasons,
  leagues: showLeagues, skipped: showSkipped, rankings: showRankings, sync: showSync,
};

async function showStatus() {
  try {
    const status = await api("status");
    const sync = status.sync;
    document.getElementById("status").textContent = status.databaseExists
      ? `${status.databasePath}${sync.localChanged ? " · local changes not pushed" : ""}`
      : "No rankings.db yet: pull it from R2 under Sync";
  } catch (error) {
    document.getElementById("status").textContent = error.message;
  }
}

async function route(clearMessage = true) {
  const name = location.hash.slice(1) || "assign";
  document.querySelectorAll(".tabs a").forEach(link => link.classList.toggle("active", link.getAttribute("href") === `#${name}`));
  if (clearMessage) show("", "");
  try {
    await (sections[name] ?? showAssign)();
    makeTablesSortable(sections[name] ? name : "assign");
  } catch (error) {
    view.innerHTML = "";
    show(error.message, "error");
  }
  showStatus();
}

window.addEventListener("hashchange", () => route());
route();
