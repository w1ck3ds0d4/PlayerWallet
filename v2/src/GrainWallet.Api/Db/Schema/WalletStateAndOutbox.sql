-- v2 schema for the wallet grain. State + outbox commit in ONE transaction
-- (one commit) via PostgresWalletStateStore. Compact operation receipts provide
-- durable idempotency without rewriting the legacy JSON cache columns.
-- wallet_outbox is drained out-of-band by WalletOutboxDrainer
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
    initialized        BOOLEAN        NOT NULL DEFAULT TRUE,
    version            BIGINT         NOT NULL DEFAULT 0,
    updated_at         TIMESTAMPTZ    NOT NULL DEFAULT NOW()
);

ALTER TABLE wallet_state ADD COLUMN IF NOT EXISTS initialized BOOLEAN NOT NULL DEFAULT TRUE;
ALTER TABLE wallet_state ADD COLUMN IF NOT EXISTS version BIGINT NOT NULL DEFAULT 0;

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

-- State and outbox must have the same crash durability. They still share one
-- transaction and one commit; logging the outbox adds WAL volume, not another
-- round trip or another transaction commit.
ALTER TABLE wallet_outbox SET LOGGED;

-- Fixed-size receipts replace the v2.1 best-effort JSON cache flush. They are
-- part of the same statement as state + outbox, so an operation remains
-- idempotent across process crashes and after the in-memory LRU evicts it.
CREATE TABLE IF NOT EXISTS wallet_operations
(
    player_id      TEXT          NOT NULL,
    operation_id   UUID          NOT NULL,
    operation_type TEXT          NOT NULL,
    amount         NUMERIC(20,4) NOT NULL,
    currency       VARCHAR(3)    NOT NULL,
    result         JSONB         NOT NULL,
    created_at     TIMESTAMPTZ   NOT NULL DEFAULT NOW(),
    PRIMARY KEY (player_id, operation_id)
);

-- v2.3: per-table autovacuum + fillfactor tuning. Root cause of the recurring
-- "one bench duration is much slower than its siblings" anomaly was Postgres
-- autovacuum firing mid-bench on the wallet_outbox table. Every drainer
-- UPDATE of published_at creates a dead tuple (the partial index on
-- WHERE published_at IS NULL prevents HOT updates), so after ~50k mutations
-- autovacuum's 20% dead-tuple threshold is crossed and it starts a vacuum
-- run that contends for I/O with the in-flight bench writes.
--
-- Fix: keep autovacuum on (dead-tuple cleanup IS needed for an append+update
-- workload like the outbox), but raise the dead-tuple threshold (20% -> 60%)
-- so it fires less often, AND cap its I/O cost (cost_limit 200 vs 200 default
-- and cost_delay 20ms vs 2ms default) so when it does fire it doesn't slam
-- the disk. fillfactor=70 leaves ~30% page space for HOT-updates where the
-- partial index doesn't block them.
ALTER TABLE wallet_outbox SET (
    autovacuum_vacuum_scale_factor    = 0.6,
    autovacuum_analyze_scale_factor   = 0.6,
    autovacuum_vacuum_cost_delay      = 20,
    autovacuum_vacuum_cost_limit      = 200,
    fillfactor                        = 70
);

ALTER TABLE wallet_state SET (
    autovacuum_vacuum_scale_factor    = 0.5,
    autovacuum_analyze_scale_factor   = 0.5,
    autovacuum_vacuum_cost_delay      = 20,
    fillfactor                        = 80
);

-- One-shot analyze after CREATE so the planner has stats for the optimiser
-- even on a fresh volume before the first INSERT.
ANALYZE wallet_outbox;
ANALYZE wallet_state;
ANALYZE wallet_operations;
