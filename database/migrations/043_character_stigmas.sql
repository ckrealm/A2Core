-- Stigmas (docs/STIGMAS.md): the stigma skills a character bought with Stigma Shards and their stigma levels. Kept apart
-- from game.character_skills, which the level trigger (migration 008) manages: a stigma is never unlocked by level.
-- Shards are not stored: a character has had a fixed number at its level (tools/data/stigmas.json) and has spent what its
-- stigma levels cost. Stigma slots are quick slots (game.quick_slots) holding a stigma skill.
CREATE TABLE game.character_stigmas (
    character_id uuid NOT NULL REFERENCES game.characters(character_id) ON DELETE CASCADE,
    skill_id integer NOT NULL CHECK (skill_id > 0),
    level integer NOT NULL CHECK (level BETWEEN 1 AND 40),
    PRIMARY KEY (character_id, skill_id)
);
GRANT SELECT, INSERT, UPDATE, DELETE ON game.character_stigmas TO aion_app;
