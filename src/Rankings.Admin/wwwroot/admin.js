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
  const regions = await api("regions");
  return regions.filter(region => region.rating !== null)
    .map(region => `<option value="${region.regionId}" ${region.regionId === selected ? "selected" : ""}>${escapeHtml(region.name)}</option>`)
    .join("");
}

function teamOptions(teams) {
  return teams.map(team => `<option value="${team.teamId}">${escapeHtml(team.name ?? `Team ${team.teamId}`)} (${team.league}, #${team.teamId})</option>`).join("");
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
          <input name="shortName" placeholder="Short name" aria-label="Short name">
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

async function showTeams() {
  const teams = await api("teams");
  view.innerHTML = `
    <h1>Teams</h1>
    <p class="lead">Each team is a roster core in one league. A display change applies from its date, so history keeps the old name.
      Marking a team relegated or disbanded stops it being ranked from that date, in daily runs and full replays alike.</p>
    <table>
      <thead><tr><th>#</th><th>League</th><th>Name</th><th>Logo</th><th>Status</th><th>Liquipedia keys</th><th></th></tr></thead>
      <tbody>${teams.map(team => `<tr>
        <td class="num">${team.teamId}</td>
        <td>${team.league}</td>
        <td>${escapeHtml(team.name ?? "–")} <span class="muted">${escapeHtml(team.shortName ?? "")}</span></td>
        <td>${team.logo ? `<code>${escapeHtml(team.logo)}</code>` : '<span class="muted">fallback</span>'}</td>
        <td>${team.status}</td>
        <td>${team.providerKeys.map(key => `<code>${escapeHtml(key)}</code>`).join("<br>") || '<span class="muted">none</span>'}</td>
        <td><details><summary>Edit</summary>
          <form class="inline" data-display="${team.teamId}">
            <input name="validFrom" type="date" value="${today()}" required aria-label="From">
            <input name="name" value="${escapeHtml(team.name ?? "")}" required aria-label="Name">
            <input name="shortName" value="${escapeHtml(team.shortName ?? "")}" placeholder="Short name" aria-label="Short name">
            <input name="logo" value="${escapeHtml(team.logo ?? "")}" placeholder="logo file, e.g. 12.svg" aria-label="Logo">
            <button>Save display</button>
          </form>
          <form class="inline" data-status="${team.teamId}">
            <select name="status" aria-label="Status">${["active", "relegated", "disbanded"].map(status =>
              `<option ${status === team.status ? "selected" : ""}>${status}</option>`).join("")}</select>
            <input name="effectiveOn" type="date" value="${today()}" required aria-label="From">
            <input name="note" placeholder="Note" aria-label="Note">
            <button>Set status</button>
          </form>
        </details></td>
      </tr>`).join("")}</tbody>
    </table>
    ${teams.length === 0 ? '<p class="muted">No teams yet; create them from Assign teams.</p>' : ""}`;

  view.querySelectorAll("[data-display]").forEach(form => form.addEventListener("submit", event => {
    event.preventDefault();
    change(() => api(`teams/${form.dataset.display}/display`, { method: "POST", body: formValues(form) }), "Display saved.");
  }));
  view.querySelectorAll("[data-status]").forEach(form => form.addEventListener("submit", event => {
    event.preventDefault();
    change(() => api(`teams/${form.dataset.status}/status`, { method: "POST", body: formValues(form) }), "Status saved.");
  }));
}

let playerSearch = "";

async function showPlayers() {
  const players = playerSearch ? await api(`players?search=${encodeURIComponent(playerSearch)}`) : [];
  view.innerHTML = `
    <h1>Players</h1>
    <p class="lead">A renamed player arrives from Liquipedia under a new key and becomes a new player.
      Link the new key to the old player so the roster core rules still recognise them.</p>
    <form class="inline" id="search">
      <input name="search" value="${escapeHtml(playerSearch)}" placeholder="Name or Liquipedia key" aria-label="Search">
      <button>Search</button>
    </form>
    ${players.length === 0 ? (playerSearch ? '<p class="muted">No players found.</p>' : "") : `<table>
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
        <td><form class="inline" data-entry="${region.regionId}">
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
  leagues: showLeagues, skipped: showSkipped, sync: showSync,
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
  } catch (error) {
    view.innerHTML = "";
    show(error.message, "error");
  }
  showStatus();
}

window.addEventListener("hashchange", () => route());
route();
