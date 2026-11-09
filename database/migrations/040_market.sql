-- Market (OURS: the "//ah" auction house and its market bot, docs/MARKET.md). The client's own Trade Post browse
-- protocol is not observed, so the market is server-side: a player listing holds the item in escrow (location 'market',
-- the row keeps its enchant/upgrades), a bot listing has no item row (the item is granted when bought).
ALTER TABLE game.item_instances DROP CONSTRAINT item_instances_location_check;
ALTER TABLE game.item_instances ADD CONSTRAINT item_instances_location_check
    CHECK (location IN ('bag','equipment','storage','buyback','market'));

CREATE TABLE game.market_listings (
    listing_id bigserial PRIMARY KEY,
    -- NULL: listed by the market bot (no escrowed item, no payout).
    seller_character uuid REFERENCES game.characters(character_id) ON DELETE CASCADE,
    seller_name text NOT NULL,
    -- The seller's world; NULL for the bot (its listings show in every world).
    world_id integer,
    build_id text NOT NULL,
    template_id integer NOT NULL,
    quantity bigint NOT NULL CHECK (quantity > 0),
    price_each bigint NOT NULL CHECK (price_each > 0),
    instance_id uuid UNIQUE REFERENCES game.item_instances(instance_id) ON DELETE CASCADE,
    listed_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    CHECK ((seller_character IS NULL) = (instance_id IS NULL)),
    FOREIGN KEY (build_id, template_id) REFERENCES catalog.items
);
CREATE INDEX market_listings_template_idx ON game.market_listings (template_id, price_each);
CREATE INDEX market_listings_seller_idx ON game.market_listings (seller_character);
CREATE INDEX market_listings_expiry_idx ON game.market_listings (expires_at);

-- Completed sales (price history for "//ah price" and the bots; buyer NULL = the market bot bought it).
CREATE TABLE game.market_sales (
    sale_id bigserial PRIMARY KEY,
    listing_id bigint NOT NULL,
    template_id integer NOT NULL,
    quantity bigint NOT NULL CHECK (quantity > 0),
    price_each bigint NOT NULL CHECK (price_each > 0),
    seller_character uuid,
    buyer_character uuid,
    sold_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX market_sales_template_idx ON game.market_sales (template_id, sold_at);
CREATE INDEX market_sales_bot_day_idx ON game.market_sales (sold_at) WHERE buyer_character IS NULL;

-- Takes Kinah (item 930100001) from a character's bag stacks, largest first; false (nothing taken) when short.
CREATE FUNCTION game.market_take_kinah(p_character uuid, p_amount bigint) RETURNS boolean
LANGUAGE plpgsql AS $$
DECLARE
    remaining bigint := p_amount;
    stack record;
    take bigint;
BEGIN
    IF p_amount IS NULL OR p_amount <= 0 THEN RETURN true; END IF;
    IF (SELECT COALESCE(sum(quantity), 0) FROM (SELECT quantity FROM game.item_instances
            WHERE character_id = p_character AND template_id = 930100001 AND location = 'bag' FOR UPDATE) held) < p_amount THEN
        RETURN false;
    END IF;
    FOR stack IN SELECT instance_id, quantity FROM game.item_instances
                 WHERE character_id = p_character AND template_id = 930100001 AND location = 'bag'
                 ORDER BY quantity DESC, instance_id
    LOOP
        EXIT WHEN remaining <= 0;
        take := LEAST(remaining, stack.quantity);
        IF take = stack.quantity THEN
            DELETE FROM game.item_instances WHERE instance_id = stack.instance_id;
        ELSE
            UPDATE game.item_instances SET quantity = quantity - take, revision = revision + 1 WHERE instance_id = stack.instance_id;
        END IF;
        remaining := remaining - take;
    END LOOP;
    RETURN true;
END $$;
REVOKE ALL ON FUNCTION game.market_take_kinah(uuid, bigint) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION game.market_take_kinah(uuid, bigint) TO aion_app;

GRANT SELECT, INSERT, UPDATE, DELETE ON game.market_listings, game.market_sales TO aion_app;
GRANT USAGE, SELECT ON SEQUENCE game.market_listings_listing_id_seq, game.market_sales_sale_id_seq TO aion_app;
