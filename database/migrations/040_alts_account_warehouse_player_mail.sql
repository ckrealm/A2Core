-- Aion:Alts (OURS, docs/friends-server/ALTS.md).
-- Account warehouse (SharedWarehouse): an item in location 'account_storage' belongs to the warehouse of an account on a world
-- (warehouse_account, warehouse_world); character_id is the last character that held it, and any of the account's characters on
-- that world may take it out. Slots are unique per warehouse. Keeping the account apart from character_id lets a deleted
-- character's deposits stay in the warehouse (deletion moves the character, not its items, to the graveyard account).
-- Player mail (PlayerMail): an item sent with //senditem waits in location 'mail' under the recipient until the post is taken
-- (character_posts.attached_items); Kinah travels as an ordinary attachment. sender_character is who sent it (audit).
ALTER TABLE game.item_instances DROP CONSTRAINT item_instances_location_check;
ALTER TABLE game.item_instances ADD CONSTRAINT item_instances_location_check
    CHECK (location IN ('bag','equipment','storage','buyback','account_storage','mail'));
ALTER TABLE game.item_instances ADD COLUMN warehouse_account uuid, ADD COLUMN warehouse_world integer;
ALTER TABLE game.item_instances ADD CONSTRAINT item_instances_warehouse_check
    CHECK ((location = 'account_storage') = (warehouse_account IS NOT NULL AND warehouse_world IS NOT NULL));
CREATE UNIQUE INDEX item_account_warehouse_slot_idx ON game.item_instances(warehouse_account, warehouse_world, slot)
    WHERE location = 'account_storage' AND slot IS NOT NULL;
CREATE INDEX item_account_warehouse_idx ON game.item_instances(warehouse_account, warehouse_world) WHERE location = 'account_storage';

ALTER TABLE game.character_posts ADD COLUMN attached_items uuid[] NOT NULL DEFAULT '{}',
    ADD COLUMN sender_character uuid REFERENCES game.characters(character_id) ON DELETE SET NULL;
