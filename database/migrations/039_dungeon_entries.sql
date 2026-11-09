-- Daily dungeon entry counts (Aion:Dungeons:EntryLimits, OURS; unused while the limits are off): entries per character and
-- dungeon (Dungeon.dat id) in a counting day ('yyyy-MM-dd' from Aion:Dungeons:ResetHour, server local time). Only the
-- current day is kept: a save drops the character's rows of other days.
CREATE TABLE game.character_dungeon_entries (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    dungeon_id integer NOT NULL,
    period text NOT NULL CHECK (period ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'),
    count integer NOT NULL CHECK (count > 0),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, dungeon_id, period)
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_dungeon_entries TO aion_app;
