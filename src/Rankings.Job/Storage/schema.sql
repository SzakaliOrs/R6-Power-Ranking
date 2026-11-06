-- Schema for rankings.db. RankingsDb applies it to an empty file and stamps
-- PRAGMA user_version with RankingsDb.SchemaVersion; bump both together.
-- Dates are 'YYYY-MM-DD' text, instants are Unix seconds (UTC).

-- Leagues are the regions without subregions; they hold the ratings and the teams.
-- A region with subregions (APAC) only groups them for display.
CREATE TABLE regions (
    region_id   TEXT PRIMARY KEY,               -- short code, e.g. 'EU'
    name        TEXT NOT NULL,
    parent_id   TEXT REFERENCES regions,        -- set on subregions
    rating      REAL DEFAULT 1500,              -- current rating; NULL on a grouping region
    -- Admin input for a league that enters later (China in 2026): points above (+) or below (−)
    -- the average established league that it starts at when its first team is rated; NULL = level.
    entry_level REAL,
    CHECK (parent_id IS NULL OR parent_id <> region_id)
) STRICT;

INSERT INTO regions (region_id, name, parent_id, rating) VALUES
    ('EU',     'Europe',        NULL,   1500),
    ('NA',     'North America', NULL,   1500),
    ('SA',     'South America', NULL,   1500),
    ('CN',     'China',         NULL,   1500),
    ('APAC',   'Asia-Pacific',  NULL,   NULL),
    ('ASIA',   'Asia',          'APAC', 1500),
    ('OCE',    'Oceania',       'APAC', 1500),
    ('APAC-N', 'APAC North',    'APAC', 1500);

-- A team's rating belongs to its roster core: a new core is rated afresh under the same team, and a
-- core signed by another team takes its rating there. An organisation that moves region fields a new team; the region
-- only changes to correct a wrong assignment (the admin dashboard's Change league).
CREATE TABLE teams (
    team_id     INTEGER PRIMARY KEY,
    region_id   TEXT NOT NULL REFERENCES regions,
    status      TEXT NOT NULL DEFAULT 'active'
                CHECK (status IN ('active', 'relegated', 'disbanded')),
    created_on  TEXT NOT NULL
) STRICT;

CREATE TRIGGER teams_insert_in_league BEFORE INSERT ON teams
WHEN EXISTS (SELECT 1 FROM regions WHERE parent_id = NEW.region_id)
BEGIN
    SELECT RAISE(ABORT, 'a team must be in a league, not a region with subregions');
END;

CREATE TRIGGER teams_update_in_league BEFORE UPDATE OF region_id ON teams
WHEN EXISTS (SELECT 1 FROM regions WHERE parent_id = NEW.region_id)
BEGIN
    SELECT RAISE(ABORT, 'a team must be in a league, not a region with subregions');
END;

-- Name and logo as shown on the site, by date, so history shows the name in use at the time.
CREATE TABLE team_display (
    team_id     INTEGER NOT NULL REFERENCES teams,
    valid_from  TEXT NOT NULL,
    name        TEXT NOT NULL,
    short_name  TEXT,
    logo        TEXT,                           -- file under logos/; NULL uses the fallback
                                                -- (migration 003 adds logo_dark for the dark theme)
    PRIMARY KEY (team_id, valid_from)
) STRICT;

CREATE TABLE players (
    player_id   INTEGER PRIMARY KEY,
    name        TEXT NOT NULL
) STRICT;

CREATE TABLE player_teams (
    player_id   INTEGER NOT NULL REFERENCES players,
    team_id     INTEGER NOT NULL REFERENCES teams,
    joined_on   TEXT NOT NULL,
    left_on     TEXT,                           -- NULL while still on the team
    PRIMARY KEY (player_id, team_id, joined_on)
) STRICT;
CREATE INDEX player_teams_team ON player_teams (team_id);

-- Engine state per team; total rating = region rating + rating_offset.
CREATE TABLE team_ratings (
    team_id         INTEGER PRIMARY KEY REFERENCES teams,
    rating_offset   REAL NOT NULL DEFAULT 0,
    rated_maps      INTEGER NOT NULL DEFAULT 0,  -- drives the provisional flag
    boost_maps_left INTEGER NOT NULL DEFAULT 0,  -- maps left on the roster-change boost
    last_played_at  INTEGER                      -- drives inactivity decay
) STRICT;

CREATE TABLE team_snapshots (
    date                TEXT NOT NULL,
    team_id             INTEGER NOT NULL REFERENCES teams,
    rank                INTEGER,                -- NULL when not ranked that day
    rating              REAL NOT NULL,
    rating_offset       REAL NOT NULL,
    region_rating       REAL NOT NULL,
    change              REAL NOT NULL,          -- since the previous snapshot
    maps_played         INTEGER NOT NULL,
    algorithm_version   TEXT NOT NULL,
    extra               TEXT,                   -- JSON for values the final algorithm adds
    PRIMARY KEY (date, team_id)
) STRICT;
CREATE INDEX team_snapshots_team ON team_snapshots (team_id);

CREATE TABLE region_snapshots (
    date                TEXT NOT NULL,
    region_id           TEXT NOT NULL REFERENCES regions,
    rating              REAL NOT NULL,
    algorithm_version   TEXT NOT NULL,
    PRIMARY KEY (date, region_id)
) STRICT;

-- Small key/value state the next run needs, e.g. the last processed date.
CREATE TABLE run_state (
    key     TEXT PRIMARY KEY,
    value   TEXT NOT NULL
) STRICT;

-- Manual team changes (Phase 6), applied in daily runs and replays alike.
CREATE TABLE overrides (
    override_id     INTEGER PRIMARY KEY,
    kind            TEXT NOT NULL,              -- e.g. 'disband', 'display', 'correction'
    team_id         INTEGER REFERENCES teams,
    effective_on    TEXT NOT NULL,
    payload         TEXT NOT NULL,              -- JSON, shape depends on kind
    note            TEXT
) STRICT;

-- Top-level season starts, entered through the admin dashboard like the overrides. Each start
-- pulls every team offset part-way toward zero once; the first replayed season is the warm-up.
CREATE TABLE seasons (
    starts_on   TEXT PRIMARY KEY                -- the season begins at 00:00 UTC that day
                CHECK (starts_on GLOB '[0-9][0-9][0-9][0-9]-[0-1][0-9]-[0-3][0-9]'),
    name        TEXT NOT NULL                   -- e.g. '2026', for the admin dashboard
) STRICT;

-- Maps fetched but not yet rated; removed once rated and their raw response is in B2.
CREATE TABLE maps (
    map_id          TEXT PRIMARY KEY,
    series_id       TEXT NOT NULL,
    played_at       INTEGER NOT NULL,
    map_number      INTEGER NOT NULL CHECK (map_number >= 1),
    maps_in_series  INTEGER NOT NULL CHECK (maps_in_series >= map_number),
    team_a          INTEGER NOT NULL REFERENCES teams,
    team_b          INTEGER NOT NULL REFERENCES teams,
    rounds_a        INTEGER NOT NULL CHECK (rounds_a >= 0),
    rounds_b        INTEGER NOT NULL CHECK (rounds_b >= 0),
    CHECK (team_a <> team_b)
) STRICT;
CREATE INDEX maps_played_at ON maps (played_at);

CREATE TABLE map_players (
    map_id      TEXT NOT NULL REFERENCES maps ON DELETE CASCADE,
    side        TEXT NOT NULL CHECK (side IN ('A', 'B')),
    player_id   INTEGER NOT NULL REFERENCES players,
    PRIMARY KEY (map_id, side, player_id)
) STRICT;

-- Provider team keys (Liquipedia team templates). team_id stays NULL until the team is
-- assigned through the admin endpoint; maps with an unassigned team are skipped. A rebrand
-- arrives as a new key, which the admin endpoint links to the existing team.
CREATE TABLE provider_teams (
    provider_key    TEXT PRIMARY KEY,
    team_id         INTEGER REFERENCES teams,
    name            TEXT NOT NULL,              -- as the provider last showed it
    last_tournament TEXT,                       -- context for whoever assigns it
    first_seen_at   INTEGER NOT NULL,
    last_seen_at    INTEGER NOT NULL
) STRICT;
CREATE INDEX provider_teams_team ON provider_teams (team_id);

-- Provider player keys (Liquipedia page names). A new key creates a player; a renamed
-- player's new key can be relinked to the existing player through the admin endpoint.
CREATE TABLE provider_players (
    provider_key    TEXT PRIMARY KEY,
    player_id       INTEGER NOT NULL REFERENCES players
) STRICT;
CREATE INDEX provider_players_player ON provider_players (player_id);

-- Maps that could not be rated (no winner, walkover, unknown team), kept for the log.
CREATE TABLE skipped_maps (
    map_id      TEXT PRIMARY KEY,
    series_id   TEXT,
    played_at   INTEGER,
    reason      TEXT NOT NULL,
    logged_at   INTEGER NOT NULL
) STRICT;
