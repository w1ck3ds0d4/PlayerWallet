# DEMO walkthrough

A timed sequence for a live review. Total runtime ~20 minutes plus
open-ended questions. Open this file alongside the repo in VS Code
so you can jump to the file references inline.

## Pre-flight (1 minute)

1. Open the repo in VS Code. Make sure Docker Desktop is running (whale
   icon solid in the system tray).
2. Run `dotnet run --project src/GrainWallet.AppHost` once before the
   call so the container images are already pulled. Stop the AppHost.
3. Have these tabs open in the browser:
    - This repo on GitHub
    - GitHub Actions tab showing the latest green run
4. Have one terminal ready in the repo root.

## Section 1: open the repo on GitHub (1 minute)

Talk through:

- The squash-merged PRs in order: scaffold -> contracts -> grain ->
  api -> apphost -> kafka -> load harness -> perf tuning (Postgres,
  Npgsql, Kafka buffer) -> collapse two `WriteStateAsync` into one
  -> custom `IWalletStateStore` with atomic state + outbox in one
  Postgres tx -> pre-warm phase + Testcontainers integration test ->
  small fix PRs (Kafka host port pinning, API HTTP port pinning) ->
  docs.
- The architectural arc is the interesting bit: three measured
  designs (in-grain outbox -> transactional outbox table -> atomic
  state + outbox in one transaction) with the journal documenting
  why each prior design lost the throughput + latency target on
  this hardware.
- Each PR is small and focused so a reviewer can read the diff in
  2 minutes. The `format + build + test` check gates every PR.

## Section 2: read `ENGINEERING_JOURNAL.md` together (5 minutes)

Highlight:

- Section 2.2 (the Orleans 10 schema gotcha): a real moment of catching
  the AI being confidently wrong because a library convention changed
  between major versions.
- Section 2.3 (the duplicate health check): why I keep
  WebApplicationFactory tests around even for "small" wiring changes.
- Section 2.4 (the response-disposal trap): why disposing
  HttpResponseMessage is a load-test correctness issue, not a memory
  hygiene one.
- **Section 7 (Benchmark Results) - the architecture journey: three
  measured designs.** This is the most interesting talking point.
  We tried three architectures (in-grain outbox -> transactional
  outbox table -> custom IWalletStateStore with atomic state + outbox
  in one Postgres tx), benched each, documented when the textbook
  pattern was the wrong call for our scale, and built the custom
  store that gets 1000 rps clean across a 5-minute sustained run on
  all three primary endpoints with deduct-funds at p95 47 ms / p99
  222 ms. The journal also documents reverted perf experiments
  (`ThreadPool.SetMinThreads`, fire-and-forget `Produce`,
  fire-and-forget drain) where measurement contradicted the prediction.
- Section 5 (Failure Scenarios): crash modes with their recovery
  semantics. At-least-once enforced by the `wallet_outbox` table
  (`pending = 0` after every bench).
- Section 6 (Security Considerations): explicit list of what is and is
  not protected, with the production-hardening order.

## Section 3: start the AppHost and tour the dashboard (5 minutes)

```bash
dotnet run --project src/GrainWallet.AppHost
```

Or hit `F5` in VS Code. The Aspire dashboard pops in the browser. Point at:

- Resource graph: api waits for postgres and kafka. KafkaUI hangs off
  the kafka resource.
- Open the Kafka UI in another tab. The `wallet.events` topic shows
  with 6 partitions (created by `KafkaTopicInitializer` on startup).
- Open the api logs tab; show the schema bootstrap message
  "Bootstrapping schema (orleans=..., wallet_state=...)" on first run,
  "Schema (Orleans + wallet_state + wallet_outbox) already present;
  skipping bootstrap" on subsequent runs. Then look for the
  "WalletOutboxDrainer started; poll 25ms, idle 100ms, batch 200" log
  line - that is the background service that takes Kafka publishing
  off the request path.
- Open the Scalar API explorer at the `/scalar/v1` URL listed on the
  api resource; show the example request bodies pre-filled with valid
  Guid + numeric amount thanks to the schema transformer.

## Section 4: drive the endpoints from `.http` files (5 minutes)

Open `requests/scenarios.http`. The scenarios are pre-numbered for the demo:

1. Add 100 EUR. Show the response and the dashboard updating
   `wallet.requests` (`result=accepted`) and `wallet.balance_after_op`.
2. Deduct 30 EUR. Same pattern. Note the balance reflects 70 EUR.
3. Balance check. Same player, 70 EUR.
4. Same `operationId` repeated. Idempotent: response is the prior
   balance, no new mutation. `wallet.idempotency_hits` ticks up.
5. Insufficient funds (9999 EUR deduct). 402 ProblemDetails with
   `rejectionCode: InsufficientFunds`. Note the `DeductionRejected`
   event on the topic (rejected operations still publish for audit).
6. Currency mismatch (USD into an EUR wallet). 400 with
   `rejectionCode: CurrencyMismatch`.
7. Invalid amount (0). 400.

Switch to the Kafka UI tab and scroll through `wallet.events`. Point at:

- One partition per playerId hash (6 partitions total).
- `traceparent` header on every message.
- The `$type` discriminator distinguishes `FundsAdded`,
  `FundsDeducted`, `DeductionRejected`.

## Section 5: race tests (2 minutes)

Open `tests/GrainWallet.Tests.Component/Grain/WalletGrainConcurrencyTests.cs`.

Walk through the three tests. Spend the time on
`Parallel_Deductions_Beyond_Balance_Reject_Without_OverDraw`:

```csharp
await wallet.AddFundsAsync(Guid.NewGuid(), new Money(50m, "EUR"));

var tasks = Enumerable.Range(0, 100)
    .Select(_ => wallet.DeductFundsAsync(Guid.NewGuid(), new Money(1m, "EUR")))
    .ToArray();

var results = await Task.WhenAll(tasks);

Assert.Equal(50, results.Count(r => r.Succeeded));
Assert.Equal(50, results.Count(r => !r.Succeeded));

Assert.Equal(new Money(0m, "EUR"), await wallet.GetBalanceAsync());
```

This is the financial-consistency proof. 100 parallel deductions of 1
EUR each from a wallet seeded with 50 EUR produce exactly 50 successes
and exactly 50 `InsufficientFunds` rejections. No double-spend. Then
run them:

```bash
dotnet test tests/GrainWallet.Tests.Component --filter "FullyQualifiedName~WalletGrainConcurrencyTests"
```

3 tests, ~300 ms.

## Section 6: load benchmark (2 minutes if reports are pre-baked)

The reports are pre-baked under `tests/GrainWallet.Tests.Load/reports/`.

- Open `reports/add-funds/add-funds-<timestamp>.html`. Point at the
  latency distribution and the percentile timeline. Read mean / p95
  / p99 / stddev off the table.
- Do the same for `reports/deduct-funds/` and `reports/get-balance/`.
- For hot-wallet: open the journal's "Hot-wallet appendix" section
  and walk through why the scenario is skipped on Windows (TCP
  ephemeral-port exhaustion) but the per-grain ceiling is implicitly
  proven by the pool-wide deduct-funds run.

The pool-wide 5-minute scenarios are intentionally not run live
during the interview; pre-bake them so the report HTMLs are ready
to scroll through.

## Section 7: take questions

Anchor points for likely questions:

- "Why decimal not long minor units?" -> Journal section 3.2.
- "Why a custom IWalletStateStore instead of Orleans's
  IPersistentState?" -> Journal section 7 ("The architecture
  journey: three measured designs"). The textbook transactional
  outbox table doubled the Postgres write rate per mutation
  (separate state save + separate outbox INSERT in two transactions)
  and lost the throughput ceiling; folding state + outbox INSERT
  into one Npgsql transaction keeps the per-mutation IO cost at
  one fsync while still putting Kafka publish off the request path.
- "What happens if Kafka is down?" -> Journal section 5.5. Drainer
  retries rows where `published_at IS NULL`; the wallet_outbox
  table is the durable queue.
- "What if the API crashes mid-transaction?" -> Journal section 5.1 to
  5.3. The single Postgres transaction in
  `PostgresWalletStateStore.SaveAsync` either commits both
  wallet_state + wallet_outbox or neither, so the system never sees
  "balance changed but no event emitted" or "event emitted but
  balance unchanged."
- "What about security?" -> Journal section 6. List of what is
  protected (currency validation, idempotency, source-gen JSON, no SQL
  injection, ProblemDetails not stack traces) and what is intentionally
  out of scope (auth, rate limiting, HTTPS enforcement, PII
  tokenization) with production-add order.
- "How do you scale to 20k rps?" -> Journal section 7 subsection
  "What still needs to change..." (multi-silo Orleans with
  hash-based placement, replicated Postgres or sharding by player_id,
  multi-broker Kafka with RF=3 re-enabling Acks=All + idempotent
  producer).
- "Show me the trace context propagation." -> Open
  `src/GrainWallet.Api/Kafka/KafkaWalletEventPublisher.cs`, point at
  `BuildHeaders`. Then open the propagation tests in
  `tests/GrainWallet.Tests.Component/Kafka/`.
