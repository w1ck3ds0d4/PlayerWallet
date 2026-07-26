# GrainWallet v1 Roadmap

## What v1 is

A side-by-side comparison harness for the per-player wallet microservice across
versions: `main` ships the Dashboard hub, each numbered version (`v1/`, `v2/`,
...) lives as a committed source tree, and the Dashboard runs NBomber against
all of them in parallel and renders the latency comparison.

## Current state

The hub layout landed: `main` carries the Dashboard, the v1 and v2 service
trees are committed under `v1/` and `v2/`, the Compare compound boots both
stacks plus the Dashboard end-to-end, and CI gates the hub (format + build).
Each version subfolder carries its own AppHost, Postgres + Kafka via Aspire,
test suite, and load harness.

## v1 acceptance criteria

- [x] Hub identity (Dashboard-only `main`, vN/ source trees, single clone)
- [x] `Compare: v1 + v2 + Dashboard` compound works end-to-end
- [x] Dashboard `appsettings.json` maps project names to URLs
- [x] CI gates on `main` (format + build + test)
- [x] v1 source committed and runnable (`v1/`)
- [x] v2 source committed and runnable (`v2/`)
- [ ] One signed release tag (`v1.0.0`) on `main` after the smoke test
- [ ] README walks a first-time cloner through to a green Compare run
- [ ] Dashboard surfaces a "diff summary" badge so reviewers see the v1 -> v2 delta at a glance

## Milestones to v1

### M1. Smoke test the Compare flow on a clean machine (S)

- [ ] Fresh clone into a scratch directory and run the Compare compound
- [ ] Confirm v1 binds `:5000`, v2 binds `:5001`, Dashboard binds `:5100`
- [ ] Run each benchmark (`add-funds`, `deduct-funds`, `get-balance`) and confirm side-by-side cards render
- [ ] Capture a screenshot for the README

**Acceptance:** zero manual steps beyond `git clone`, `dotnet run --project src/GrainWallet.Dashboard`, and the per-version AppHost launch.

### M2. Reviewer-facing diff badge (M)

- [ ] Add a "v1 vs v2 delta" tile that highlights p95 / p99 deltas as `+X% faster` / `-X% slower`
- [ ] Compute deltas from in-memory `NodeStats` so it doesn't depend on disk reports
- [ ] Surface the back-pressure 503 rate per project (v2's gate is invisible today)

**Acceptance:** a reviewer can open the dashboard, click Run, and articulate v2's wins without reading numbers manually.

### M3. Tag and document v1.0.0 (S)

- [ ] Bump the Dashboard `csproj` to a 1.0.0 marker (informational)
- [ ] Push tag `v1.0.0` after manual smoke test passes
- [ ] README links to the tagged release

**Acceptance:** `git tag --list` shows `v1.0.0` on the smoke-tested commit.

## Beyond v1 (post-1.0 polish)

- Adding v3 as a worktree pattern (`git worktree add v3 v3`) once a v3 branch exists
- Charts in the Dashboard (per-scenario latency lines over benchmark history)
- Dashboard config UI for adjusting bench knobs without restarting
- Per-version `README.md` linkbacks from the hub README

## Out of scope for v1

- Production hosting of the AppHosts (Aspire is dev-only by design)
- Multi-region or distributed Orleans clusters (each version stays single-silo)
- Anything that requires changes inside `v1/` or `v2/` source - versions stay frozen at their snapshot SHA
