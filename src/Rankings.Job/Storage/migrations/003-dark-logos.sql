-- Schema version 3: a team's logo can have a separate version for dark backgrounds.
-- logo is used in light mode, and in dark mode too when logo_dark is NULL.
ALTER TABLE team_display ADD COLUMN logo_dark TEXT;     -- file under logos/ for the dark theme
