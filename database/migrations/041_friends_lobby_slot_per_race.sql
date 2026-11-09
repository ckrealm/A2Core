-- Aion:Friends:BothFactions (OURS): an account may hold both races on this server, each race's world with its own lobby
-- slots (as 037). With the switch off the server still refuses the other race (GameSession.OtherRace), so one race per
-- account holds as before.
DROP INDEX game.character_lobby_slot_idx;
CREATE UNIQUE INDEX character_lobby_slot_idx ON game.characters(account_id,world_id,race,lobby_slot) WHERE lobby_slot IS NOT NULL;
