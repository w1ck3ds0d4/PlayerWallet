-- v2 schema for the wallet grain. State + outbox commit in ONE transaction
-- (one fsync) via PostgresWalletStateStore. Idempotency cache stays inline as
-- two JSONB columns (cache is per-player and capped at 256 entries; a child
-- table would multiply write amplification without changing the per-mutation
-- fsync count). wallet_outbox is drained out-of-band by WalletOutboxDrainer
-- using SELECT ... FOR UPDATE SKIP LOCKED so multiple drainer instances claim
-- non-overlapping shards.
--
-- v2 changes from v1:
--   * balance_currency: CHAR(3) -> VARCHAR(3), drops the space-padding gotcha
--     that required a TrimEnd in the loader.
--   * CHECK constraint on currency enforces ISO 4217 shape at the DB layer.

CREATE TABLE IF NOT EXISTS wallet_state
(
    player_id          TEXT PRIMARY KEY,
    balance_amount     NUMERIC(20, 4) NOT NULL,
    balance_currency   VARCHAR(3)     NOT NULL CHECK (balance_currency ~ '^[A-Z]{3}$'),
    recent_operations  JSONB          NOT NULL DEFAULT '{}'::jsonb,
    operation_order    JSONB          NOT NULL DEFAULT '[]'::jsonb,
    updated_at         TIMESTAMPTZ    NOT NULL DEFAULT NOW()
);

-- v1->v2 migration: tighten the existing column to VARCHAR(3) for fresh data
-- volumes; pre-existing CHAR(3) rows have already been TrimEnd'd on read by
-- v1 so the in-process value matches.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'wallet_state'
          AND column_name = 'balance_currency'
          AND data_type   = 'character'
    ) THEN
        ALTER TABLE wallet_state
            ALTER COLUMN balance_currency TYPE VARCHAR(3) USING TRIM(BOTH FROM balance_currency);
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM information_schema.constraint_column_usage
        WHERE table_name = 'wallet_state'
          AND constraint_name = 'wallet_state_balance_currency_check'
    ) THEN
        BEGIN
            ALTER TABLE wallet_state
                ADD CONSTRAINT wallet_state_balance_currency_check
                CHECK (balance_currency ~ '^[A-Z]{3}$');
        EXCEPTION WHEN duplicate_object THEN
            -- constraint already exists under a different name; ignore
        END;
    END IF;
END $$;

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

-- v2.1: wallet_outbox is UNLOGGED. Postgres skips WAL writes for unlogged
-- tables, which removes the second fsync per mutation. Trade: outbox rows
-- are lost on Postgres crash before the drainer publishes them. Acceptable
-- because the wallet_state row (the source of truth for balance) is still
-- WAL-protected, and the consumer-side dedupe contract is at-least-once
-- anyway. On crash recovery, the surviving wallet_state row may have no
-- corresponding outbox event; this is an event-loss window the operator
-- accepts in exchange for the throughput. Flip back to LOGGED if your
-- compliance posture requires per-event durability.
ALTER TABLE wallet_outbox SET UNLOGGED;
