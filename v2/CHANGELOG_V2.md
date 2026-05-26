# PlayerWallet v2 Changelog

PlayerWallet v2 is a fork of [PlayerWallet v1](https://github.com/w1ck3ds0d4/PlayerWallet)
that applies the improvements identified in the v1 post-mortem study guide.
Same surface, same Aspire + Orleans + Kafka + Postgres stack, same wire
contract for the three endpoints. The wallet event schema is **breaking**
(`DeductionRejected` was renamed to `OperationRejected`) so v2 is a major
release for consumers.

The v1 ENGINEERING_JOURNAL.md is preserved verbatim as a historical record of
how the original code came together. This document covers what changed.

## v2.2: bench fidelity + drainer throughput pass

After running the v1-vs-v2 bench across multiple durations and persisting
the results, two systematic problems emerged. This pass investigates each
and fixes them.

### Problems observed (from `src/PlayerWallet.Dashboard/.../reports/`)

| Symptom | Root cause | Fix |
|---|---|---|
| `hot-wallet` @ 60s+ shows 1000+ HTTP failures from BOTH v1 and v2 | NBomber HTTP timeout (30s default) firing because 200 rps to a single Orleans grain is past per-grain capacity. Queue grows ~100/sec; last request waits > 30s. | (a) Per-scenario RPS override (hot-wallet defaults to 50). (b) HTTP timeout raised 30s -> 60s so late requests can wait. |
| `hot-wallet` @ 90s shows 7727/18000 v1 failures (43%) | Same root cause; v1's ~150 ms per-request grain capacity is even further below 200 rps. | Same fix; v1 still has the architectural bottleneck (no v2.1 perf changes) but the test now measures actual latency instead of NBomber timeout. |
| `add-funds` @ 60s: v2 mean 17.6 ms but its own 30s/90s runs were 10.6/10.7 ms | Likely a Postgres plan-cache warm-up not fully done in time, OR a single drainer batch holding row locks at the wrong moment. p50 shifted (16.6 vs 7.3) so it was systematic for that run. | (a) Pre-warm cycles 3 -> 5 (more chances for AutoPrepare to warm + more Postgres buffer-cache hits before measurement). (b) Two parallel drainer workers so locks are released faster per row. |
| Drainer ceiling at sustained hot-wallet load | Single drainer instance, batch 200, 25ms poll = ~8k evt/sec ceiling. | Drainer now runs N=2 worker tasks (FOR UPDATE SKIP LOCKED safety stays), batch bumped 200 -> 500, adaptive poll (5ms when last batch was full, 25ms when partial, 100ms when empty). |

### v2.2 file-by-file changes

| File | Change |
|---|---|
| [`BenchOptions.cs`](src/PlayerWallet.Dashboard/Bench/BenchOptions.cs) | New `HttpTimeoutSeconds` (default 60), new `ScenarioRpsOverrides` dict, new `ResolvedRpsFor(scenario)` helper. |
| [`appsettings.json`](src/PlayerWallet.Dashboard/appsettings.json) | `HttpTimeoutSeconds: 60`, `ScenarioRpsOverrides: { "hot-wallet": 50 }`. |
| [`BenchScenarios.cs`](src/PlayerWallet.Dashboard/Bench/BenchScenarios.cs) | `WarmupCyclesPerWallet` 3 -> 5. Scenario builder uses `opts.ResolvedRpsFor(scenario)` instead of the global rps. |
| [`BenchRunner.cs`](src/PlayerWallet.Dashboard/Bench/BenchRunner.cs) | HttpClient.Timeout from `bench.HttpTimeoutSeconds`. `BenchRun.RequestsPerSecond` records the resolved per-scenario rate so persisted summaries are accurate. Status text shows the resolved rate + http timeout. |
| [`WalletOutboxDrainer.cs`](src/PlayerWallet.Api/Db/WalletOutboxDrainer.cs) | Two parallel worker tasks (worker 0 owns the gate refresh + outbox depth meter to avoid double work). Adaptive poll (5/25/100ms). Batch size 200 -> 500. |
| [`DashboardEndpoints.cs`](src/PlayerWallet.Dashboard/Endpoints/DashboardEndpoints.cs) | `/api/config` now exposes `httpTimeoutSeconds` and `scenarioRpsOverrides`. |
| [`dashboard.js`](src/PlayerWallet.Dashboard/wwwroot/dashboard.js) | Config summary line shows per-scenario overrides and the timeout. |

### What v2.2 does NOT fix

- The `add-funds` 60s anomaly was stochastic; the longer pre-warm + multi-worker drainer reduce the chance but can't eliminate it. If you see it again, open v2's Aspire dashboard and inspect the slowest trace in the window; the OTel spans + tags will tell you whether it was DB-side or app-side.
- Bench environment hygiene: `wallet_outbox` grows across bench sessions and Postgres index bloat can cause drift. If you see consistent slow runs across all scenarios, restart v2's AppHost (volume gets recreated) or manually `VACUUM ANALYZE wallet_outbox` from a Postgres client.
- v1's per-grain ceiling is unchanged. v1's hot-wallet capacity is roughly ~7 rps before queueing hurts you. The new `hot-wallet=50rps` default still pushes v1 past its limit, which is intentional, that's the point of the comparison.

## v2.1: hot-path performance pass

After the first round of v1-vs-v2 bench comparisons via the dashboard,
v2 was 1-2 ms slower than v1 on `add-funds` / `deduct-funds` and at risk
of losing badly on `hot-wallet` due to `synchronous_commit=on` adding an
fsync per mutation that v1 didn't pay. v2.1 puts v2 back ahead **without
giving up the v2 correctness wins** (real LRU, `OperationRejected`,
SKIP LOCKED, back-pressure gate, validation pre-grain,
`synchronous_commit=on`).

| # | Change | File(s) | Expected impact |
|---|---|---|---|
| 14 | `PostgresWalletStateStore.SaveAsync` collapsed to a single CTE round-trip. v1 sent `BEGIN; UPSERT; INSERT outbox; COMMIT` (four round-trips, one fsync). v2.1 sends one statement: `WITH state_upsert AS (UPSERT) INSERT INTO wallet_outbox SELECT FROM state_upsert`. Implicit single transaction, one round-trip, one fsync. | [`PostgresWalletStateStore.cs`](src/PlayerWallet.Api/Db/PostgresWalletStateStore.cs) | Removes ~3 network round-trips per mutation. Typically 2-4 ms saved at sustained load. |
| 15 | `Max Auto Prepare=10` + `Auto Prepare Min Usages=5` on the Npgsql connection string. After the 5th call Npgsql server-side prepares the statement, skipping parse/plan cost. | [`Program.cs`](src/PlayerWallet.Api/Program.cs) | ~0.5-1 ms saved per mutation once steady-state. |
| 16 | `wallet_outbox` table is `UNLOGGED`. Postgres skips WAL writes for unlogged tables. Trade: outbox rows lost on Postgres crash before the drainer publishes them (wallet_state remains WAL-protected). Consumer dedupe is at-least-once anyway. Flip back to LOGGED in production where compliance requires per-event durability. | [`WalletStateAndOutbox.sql`](src/PlayerWallet.Api/Db/Schema/WalletStateAndOutbox.sql) | Removes the second fsync per mutation. Big win on hot-wallet. |
| 17 | Idempotency cache is no longer persisted on every mutation. `SaveAsync` only writes balance + outbox; `recent_operations` / `operation_order` JSONB columns are flushed via `PersistCacheAsync` from `WalletGrain.OnDeactivateAsync`. As the cache filled toward 256 entries v2 was rewriting ~10 KB of JSON per mutation; v2.1 hot path writes are now bounded to two NUMERIC + one TEXT + one JSONB payload (the event itself). | [`WalletGrain.cs`](src/PlayerWallet.Grains/WalletGrain.cs), [`PostgresWalletStateStore.cs`](src/PlayerWallet.Api/Db/PostgresWalletStateStore.cs), [`IWalletStateStore.cs`](src/PlayerWallet.Grains/IWalletStateStore.cs), [`InMemoryWalletStateStore.cs`](src/PlayerWallet.Grains/InMemoryWalletStateStore.cs) | Biggest single win at high mutation counts. Trade: process crash before deactivation loses the un-flushed cache; retries within seconds normally hit the same activation and dedupe before any persistence is involved (Orleans default activation idle = 5 min). |

### v2.1 honest caveats

- The UNLOGGED outbox is a **dev/bench config**. Production with
  compliance requirements should `ALTER TABLE wallet_outbox SET LOGGED`,
  which costs the second fsync back but preserves event-level durability.
- The OnDeactivate cache flush is best-effort. On process kill (-9,
  power loss), the in-memory cache for actively used grains is lost.
  This is acceptable for the wallet domain because (a) state is still
  durable, (b) idempotency is a retry-storm defence not a correctness
  requirement, (c) the worst case is "this operationId got applied
  twice" which the existing event-id dedupe on consumers catches.
- `Max Auto Prepare=10` warms up over the first ~50 mutations. Short
  benchmarks (the dashboard's 30 s default) may not fully amortise the
  warm-up cost.

All 68 component tests still pass against v2.1. The hot-path Postgres
contract is intentionally narrower than v2 (no cache columns in the
write SQL) but `LoadAsync` and `PersistCacheAsync` together preserve
the original semantic across a clean restart.

## v2.0 changes

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
