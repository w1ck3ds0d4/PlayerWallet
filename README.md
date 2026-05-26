# GrainWallet

Comparison harness for the GrainWallet per-player wallet microservice across
versions. `main` is a thin orchestration hub: it carries only the comparison
dashboard, the workspace tooling, and this README. Each numbered version of
the service lives on its own branch (`v1`, `v2`, ...) and is checked out as a
sibling worktree under `vN/` on disk.

## Repo layout

```
GrainWallet/                                  (main branch: hub-only)
  src/PlayerWallet.Dashboard/                 NBomber-driven side-by-side comparison UI
  v1/                                         git worktree -> v1 branch (full v1 source)
  v2/                                         git worktree -> v2 branch (full v2 source)
  vN/                                         add more versions the same way
  .vscode/                                    Compare compound + per-version build tasks
  PlayerWallet.slnx                           Hub solution (Dashboard only)
```

`v1/`, `v2/`, ... are ignored on `main` (see `.gitignore`), so the hub commit
graph stays free of version source.

## What each branch is

| Branch | What lives there |
|---|---|
| `main` | This hub: Dashboard, workspace config, no service code. |
| `v1`   | Original PlayerWallet submission: in-memory LRU, basic outbox drainer, original event names. |
| `v2`   | Hardened revision: `FOR UPDATE SKIP LOCKED` outbox, real LRU idempotency, back-pressure gate (HTTP 503), endpoint pre-grain validation, `synchronous_commit=on`, currency `VARCHAR(3)` + CHECK, event rename (`DeductionRejected` -> `OperationRejected`). |

Each version branch carries its own `README.md`, `ENGINEERING_JOURNAL.md`,
`DEMO.md`, tests, and load harness.

## Set up worktrees

After cloning this repo, materialize the version subfolders:

```powershell
cd C:\Users\danie\Documents\GitHub\repos\GrainWallet
git worktree add v1 v1
git worktree add v2 v2
```

Each `git worktree add` creates `vN/` with the branch checked out. To add a
future v3, you create branch `v3` (typically from `v2`) and run
`git worktree add v3 v3`.

## Run the comparison dashboard

VS Code: Run and Debug -> `Compare: v1 + v2 + Dashboard`. This compound
builds both AppHosts in parallel, starts them on the ports the dashboard
expects, then launches the dashboard UI.

From the command line:

```powershell
# Terminal A: v1 on default ports (API 5000, Kafka 19092)
cd v1
dotnet run --project src/PlayerWallet.AppHost

# Terminal B: v2 on overridden ports (API 5001, Kafka 19093)
cd v2
$env:WALLET_API_HOST_PORT = "5001"
$env:WALLET_KAFKA_HOST_PORT = "19093"
dotnet run --project src/PlayerWallet.AppHost

# Terminal C: dashboard at http://localhost:5100
cd ..
dotnet run --project src/PlayerWallet.Dashboard
```

Dashboard config (`src/PlayerWallet.Dashboard/appsettings.json`) maps each
project name to a base URL:

```json
"Projects": [
  { "Name": "v1", "Url": "http://localhost:5000" },
  { "Name": "v2", "Url": "http://localhost:5001" }
]
```

Adding a third version means starting v3 on its own port and adding a third
entry to that list.

## Local CI gates

```powershell
dotnet format PlayerWallet.slnx --verify-no-changes --severity error
dotnet build  PlayerWallet.slnx -c Release -warnaserror
```

These also run on every push and PR to `main` via `.github/workflows/ci.yml`.
Per-version branches keep their own test workflows.

## Prerequisites

- .NET 10 SDK
- Docker Desktop running (for each version's Aspire AppHost; Postgres + Kafka)
- `gh` CLI (optional; used to drive CI and merge PRs)
