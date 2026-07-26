# GrainWallet

Comparison harness for the GrainWallet per-player wallet microservice across
versions. The repo root is an orchestration hub carrying the comparison
dashboard, the workspace tooling, and this README. Each numbered version of
the service lives in its own committed subfolder (`v1/`, `v2/`, ...).

## Repo layout

```
GrainWallet/
  src/GrainWallet.Dashboard/                 NBomber-driven side-by-side comparison UI
  v1/                                         full v1 source (committed)
  v2/                                         full v2 source (committed)
  vN/                                         add more versions the same way
  .vscode/                                    Compare compound + per-version build tasks
  GrainWallet.slnx                           Hub solution (Dashboard only)
```

Clone-and-run: `git clone` is enough. No worktree or submodule setup.

## What each version is

| Folder | What lives there |
|---|---|
| `v1/` | Original GrainWallet submission: in-memory LRU, basic outbox drainer, original event names. |
| `v2/` | Hardened revision: `FOR UPDATE SKIP LOCKED` outbox, real LRU idempotency, back-pressure gate (HTTP 503), endpoint pre-grain validation, `synchronous_commit=on`, currency `VARCHAR(3)` + CHECK, event rename (`DeductionRejected` -> `OperationRejected`). |

Each version folder carries its own `README.md`, `ENGINEERING_JOURNAL.md`,
`DEMO.md`, tests, and load harness.

## Run the comparison dashboard

VS Code: Run and Debug -> `Compare: v1 + v2 + Dashboard`. This compound
builds both AppHosts in parallel, starts them on the ports the dashboard
expects, then launches the dashboard UI.

From the command line:

```powershell
# Terminal A: v1 on default ports (API 5000, Kafka 19092)
cd v1
dotnet run --project src/GrainWallet.AppHost

# Terminal B: v2 on overridden ports (API 5001, Kafka 19093)
cd ..\v2
$env:WALLET_API_HOST_PORT = "5001"
$env:WALLET_KAFKA_HOST_PORT = "19093"
dotnet run --project src/GrainWallet.AppHost

# Terminal C: dashboard at http://localhost:5100
cd ..
dotnet run --project src/GrainWallet.Dashboard
```

Dashboard config (`src/GrainWallet.Dashboard/appsettings.json`) maps each
project name to a base URL:

```json
"Projects": [
  { "Name": "v1", "Url": "http://localhost:5000" },
  { "Name": "v2", "Url": "http://localhost:5001" }
]
```

Adding a third version means dropping the new source under `v3/`, starting
v3 on its own port, adding a third `Projects` entry, and copying the
`v1 AppHost` launch config in `.vscode/launch.json` to a `v3 AppHost`
variant.

## Local CI gates

```powershell
dotnet format GrainWallet.slnx --verify-no-changes --severity error
dotnet build  GrainWallet.slnx -c Release -warnaserror
```

These also run on every push and PR to `main` via `.github/workflows/ci.yml`.
The hub's `GrainWallet.slnx` only references the Dashboard project; each
`vN/` folder ships its own solution if you want to build that version
in isolation.

## Prerequisites

- .NET 10 SDK
- Docker Desktop running (for each version's Aspire AppHost; Postgres + Kafka)
- `gh` CLI (optional; used to drive CI and merge PRs)

## License

This project is dual-licensed:

- [AGPL v3](LICENSE) - free for open-source use. Derivatives and SaaS deployments must release their source under AGPL.
- [Commercial license](COMMERCIAL.md) - for proprietary / closed-source use or hosted services that do not want to comply with AGPL source-disclosure requirements. Contact for terms.
