-- Custom-store schema for the wallet grain. Replaces Orleans's AdoNet grain
-- storage so balance + outbox event commit in ONE transaction (one fsync).
-- Idempotency cache split into recent_operations (lookup) + operation_order
-- (LRU queue) JSONB columns rather than a child table; cache is per-player
-- and bounded at 256 entries, so a child table would only multiply write cost.
-- wallet_outbox is read out-of-band by WalletOutboxDrainer.

CREATE TABLE IF NOT EXISTS wallet_state
(
    player_id          TEXT PRIMARY KEY,
    balance_amount     NUMERIC(20, 4) NOT NULL,
    balance_currency   CHAR(3)        NOT NULL,
    recent_operations  JSONB          NOT NULL DEFAULT '{}'::jsonb,
    operation_order    JSONB          NOT NULL DEFAULT '[]'::jsonb,
    updated_at         TIMESTAMPTZ    NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS wallet_outbox
(
    id            BIGSERIAL PRIMARY KEY,
    event_id      UUID        NOT NULL UNIQUE,
    event_type    TEXT        NOT NULL,
    player_id     TEXT        NOT NULL,
    payload       JSONB       NOT NULL,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    published_at  TIMESTAMPTZ NULL
);

CREATE INDEX IF NOT EXISTS idx_wallet_outbox_unpublished
    ON wallet_outbox (id)
    WHERE published_at IS NULL;
