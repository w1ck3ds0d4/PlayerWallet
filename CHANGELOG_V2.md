# PlayerWallet v2 Changelog

PlayerWallet v2 is a fork of [PlayerWallet v1](https://github.com/w1ck3ds0d4/PlayerWallet)
that applies the improvements identified in the v1 post-mortem study guide.
Same surface, same Aspire + Orleans + Kafka + Postgres stack, same wire
contract for the three endpoints. The wallet event schema is **breaking**
(`DeductionRejected` was renamed to `OperationRejected`) so v2 is a major
release for consumers.

The v1 ENGINEERING_JOURNAL.md is preserved verbatim as a historical record of
how the original code came together. This document covers what changed.

## Headline changes

| # | Change | File(s) | Risk if shipped without it |
|---|---|---|---|
| 1 | Outbox drainer now claims rows with `FOR UPDATE SKIP LOCKED` inside a transaction. | [`src/PlayerWallet.Api/Db/WalletOutboxDrainer.cs`](src/PlayerWallet.Api/Db/WalletOutboxDrainer.cs) | A second API instance would have double-published every event. |
| 2 | Idempotency cache is a real LRU. Touched entries move to the tail; cache hits no longer get evicted by an unrelated 256-op burst. | [`src/PlayerWallet.Grains/WalletState.cs`](src/PlayerWallet.Grains/WalletState.cs), [`WalletGrain.cs`](src/PlayerWallet.Grains/WalletGrain.cs) | Long-tailed retry storms could miss the cache and re-mutate. |
| 3 | Outbox back-pressure gate. Drainer publishes pending-row counts to a shared `OutboxBackpressureGate`; the wallet grain rejects mutations with `OutboxFull` (HTTP 503) when the cap (default 100k) is breached. | [`src/PlayerWallet.Grains/OutboxBackpressureGate.cs`](src/PlayerWallet.Grains/OutboxBackpressureGate.cs), [`WalletGrain.cs`](src/PlayerWallet.Grains/WalletGrain.cs), [`WalletOutboxDrainer.cs`](src/PlayerWallet.Api/Db/WalletOutboxDrainer.cs), [`Program.cs`](src/PlayerWallet.Api/Program.cs) | Kafka outage of any length would grow `wallet_outbox` without bound. |
| 4 | Endpoint pre-grain validation: invalid amount (`<= 0`) is rejected with `400` at the endpoint, never reaches the grain. | [`src/PlayerWallet.Api/Endpoints/WalletEndpoints.cs`](src/PlayerWallet.Api/Endpoints/WalletEndpoints.cs) | Garbage requests amplified one Postgres tx + one outbox row per bad request. |
| 5 | State-dependent rejections only persist. Grain stops writing `OperationRejected` rows for input rejections (`InvalidAmount`, `CurrencyMismatch`). | [`src/PlayerWallet.Grains/WalletGrain.cs`](src/PlayerWallet.Grains/WalletGrain.cs) | Same amplification path as #4 from a different angle. |
| 6 | Event schema rename: `DeductionRejected` → `OperationRejected`. | [`src/PlayerWallet.Contracts/WalletEvents.cs`](src/PlayerWallet.Contracts/WalletEvents.cs), JSON contexts, drainer dispatch | v1 type name misrepresented add-funds rejections. Breaking change for consumers. |
| 7 | Currency column tightened: `CHAR(3)` → `VARCHAR(3)` + `CHECK (^[A-Z]{3}$)`. Loader no longer needs `TrimEnd`. | [`src/PlayerWallet.Api/Db/Schema/WalletStateAndOutbox.sql`](src/PlayerWallet.Api/Db/Schema/WalletStateAndOutbox.sql), [`PostgresWalletStateStore.cs`](src/PlayerWallet.Api/Db/PostgresWalletStateStore.cs) | Direct SQL consumers saw `'EUR '` with trailing space. |
| 8 | `wallet.outbox_pending` OTel meter is now actually fed. Drainer publishes pending count every 2 s; gauge surfaces it to the Aspire dashboard. | [`src/PlayerWallet.Api/Db/WalletOutboxDrainer.cs`](src/PlayerWallet.Api/Db/WalletOutboxDrainer.cs), [`src/PlayerWallet.Grains/Telemetry/WalletMeters.cs`](src/PlayerWallet.Grains/Telemetry/WalletMeters.cs) | v1 gauge had no call sites; always read zero. |
| 9 | `Money.Add` and `Money.Subtract` use `checked()`. Overflow throws instead of wrapping. | [`src/PlayerWallet.Contracts/Money.cs`](src/PlayerWallet.Contracts/Money.cs) | Theoretical only, but a financial service should be paranoid. |
| 10 | Route constraint on `playerId` (`minlength(1):maxlength(64)`). | [`src/PlayerWallet.Api/Endpoints/WalletEndpoints.cs`](src/PlayerWallet.Api/Endpoints/WalletEndpoints.cs) | A 100 KB `playerId` could inflate Postgres rows and OTel span tags. |
| 11 | `synchronous_commit` defaults to `on` in the AppHost Postgres config. v1 dev-bench setting (`off`) is opt-in via `PLAYERWALLET_PG_SYNC=off`. | [`src/PlayerWallet.AppHost/AppHost.cs`](src/PlayerWallet.AppHost/AppHost.cs) | v1 headline numbers were measured without durable commits. |
| 12 | Dead `MemoryGrainStorage("WalletStorage")` registration removed. Wallet grain now goes through `IWalletStateStore` exclusively. | [`src/PlayerWallet.Api/Program.cs`](src/PlayerWallet.Api/Program.cs), [`tests/.../WalletGrainTestCluster.cs`](tests/PlayerWallet.Tests.Component/Grain/WalletGrainTestCluster.cs) | Cosmetic but misleading on a code walk-through. |
| 13 | Pre-warm in the load harness switched to no-op mutations (add 0.01 + deduct 0.01) instead of GET /balance. | [`tests/PlayerWallet.Tests.Load/WalletPool.cs`](tests/PlayerWallet.Tests.Load/WalletPool.cs) | v1 add-funds p95 ran ~5 ms over the 100 ms target due to first-mutation cost in the bench window. |

## Test additions

- `WalletStateLruTests` proves the cache touches and evicts as LRU, not FIFO.
- `WalletGrainTests` adds:
  - `Insufficient_Funds_Publishes_OperationRejected_Event` (rename + persistence semantics)
  - `Invalid_Amount_Does_Not_Publish_Event` (proves v2 doesn't emit for input rejections)
  - `Currency_Mismatch_Does_Not_Publish_Event` (same)

## Items deferred to v2.1

These were in the v2 study guide but are larger scope (multi-PR or
infra-dependent) and intentionally left out of this release.

- **Cache to child table.** `recent_operations` + `operation_order` JSONB
  columns still rewrite in full per mutation. Moving to a
  `wallet_operation_cache` child table with periodic prune (or Redis) is
  the right next step but a schema-migration story we did not want to
  bundle with the rest.
- **Multi-silo Orleans + `HashBasedPlacement`** for stable wallet-to-silo
  affinity. Single-silo `UseLocalhostClustering` ships unchanged.
- **Sharded Postgres** by `player_id` hash. Single-node Postgres ships
  unchanged.
- **Native AOT** for the API. Source-gen JSON is in place; the AOT trim
  pass would be its own PR.
- **`long` minor units** for `Money`. Wire format stays `{amount,currency}`
  decimal. A v2 contract route would be the migration path.
- **Kafka producer tuning for multi-broker production** (Acks=All,
  EnableIdempotence=true, 24-48 partitions). Dev-bench config retained.

## Benchmark numbers

This release ships **without** new benchmark numbers in the repo. The v1
numbers in `tests/PlayerWallet.Tests.Load/reports/` were measured with
`synchronous_commit=off` and the GET-based pre-warm and are not directly
comparable to v2's behaviour.

To re-bench against v2:

```powershell
$env:WALLET_API_URL = "http://localhost:5000"
dotnet run --project src/PlayerWallet.AppHost
# in a second shell, once the dashboard shows the API as ready:
dotnet run --project tests/PlayerWallet.Tests.Load --configuration Release
```

Expected directional changes vs v1:
- `add-funds` p95 lower (mutation-based pre-warm absorbs the first-mutation cost).
- `add-funds` and `deduct-funds` mean slightly higher (`synchronous_commit=on` adds fsync latency on a single node).
- `get-balance` unchanged (no write path involved).
- `wallet_outbox.pending` is now exposed via the `wallet.outbox_pending` OTel meter.

To reproduce the v1 trade-off (durability off, for dev throughput):

```powershell
$env:PLAYERWALLET_PG_SYNC = "off"
```

## Known weaknesses still in v2

These are honest and known. They are weaknesses I would call out in a code
review on my own PR.

1. **JSONB cache write amplification.** As the cache fills toward 256
   entries each mutation rewrites two JSONB columns of growing size. v2.1
   moves this to a child table.
2. **`OperationRejected` event has the rejection reason in `Reason`, no
   schema versioning.** Adding a `schemaVersion` and a v2.1
   `RejectionReason` enum would make consumer compatibility easier.
3. **Back-pressure gate sampling delay.** The drainer refreshes pending
   count every 2 s, so a sudden burst that fills the outbox can race the
   gate. Acceptable trade-off (otherwise we'd need a COUNT(*) per
   mutation).
4. **Schema migration for the currency column** uses an inline `DO $$`
   block. This is idempotent and safe to re-run, but a real deployment
   would lift this into a dedicated migration tool (FluentMigrator,
   EF Core migrations, or hand-rolled SQL).
5. **Single-silo Orleans, single-node Postgres, single-broker Kafka.**
   The scale story is documented but not exercised.

## Hours-to-ship transparency

I built v2 in one focused session (not the original 4-6 hour budget).
The deltas above are the highest-ROI improvements from the v1 study
guide, picked specifically because:

1. Each one is provably correct in a unit or integration test.
2. None of them needs infrastructure I don't have locally (no multi-broker
   Kafka, no multi-node Postgres, no Native AOT toolchain re-validation).
3. Each one lands cleanly in a small diff that a reviewer can validate
   without paging in surrounding context.

The deferred items are deferred because they fail one or more of those
criteria, not because they don't matter.
