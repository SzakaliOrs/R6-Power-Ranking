-- Schema version 2: what the daily run keeps for the site once a day's maps leave the maps table.

-- Tournament and tier per series, as the provider named them, for the team views.
CREATE TABLE series (
    series_id   TEXT PRIMARY KEY,
    tournament  TEXT,
    tier        TEXT
) STRICT;

-- Every rated map with what its own update did to both teams (before the series bonus and
-- re-centring). The team views' recent results and the regions' international records read it.
CREATE TABLE rated_maps (
    map_id          TEXT PRIMARY KEY,
    series_id       TEXT NOT NULL,
    played_at       INTEGER NOT NULL,
    map_number      INTEGER NOT NULL,
    maps_in_series  INTEGER NOT NULL,
    team_a          INTEGER NOT NULL REFERENCES teams,
    team_b          INTEGER NOT NULL REFERENCES teams,
    rounds_a        INTEGER NOT NULL,
    rounds_b        INTEGER NOT NULL,
    change_a        REAL NOT NULL,
    change_b        REAL NOT NULL
) STRICT;
CREATE INDEX rated_maps_played_at ON rated_maps (played_at);

-- Roster changes the engine found, by the day it rated them, for the team views.
CREATE TABLE roster_events (
    event_id        INTEGER PRIMARY KEY,
    date            TEXT NOT NULL,
    at              INTEGER NOT NULL,
    team_id         INTEGER NOT NULL REFERENCES teams,
    kind            TEXT NOT NULL,              -- RosterEventKind: Seeded, NewCore, RosterChange, Returned, SharesCore
    new_players     INTEGER NOT NULL DEFAULT 0,
    other_team_id   INTEGER REFERENCES teams
) STRICT;
CREATE INDEX roster_events_date ON roster_events (date);
