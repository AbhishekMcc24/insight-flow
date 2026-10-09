# Insight Flow

Multi-tenant SaaS analytics: governed, drag-and-drop BI (semantic model, shelves, dashboards, roles) combined with
AI-first exploration (derived fields, branching Data Threads). Every chart is a canonical `VizSpec` compiled to
DuckDB SQL over Parquet extracts. The AI writes DuckDB SQL only, and it always runs in a locked-down sandbox.

> Status: **foundation (milestones 1–10)**: orchestration, domain, persistence, query engine, connectors, AI sandbox
> and agents, evals, the Web workspace, CI and Azure deployment config. Start with [CLAUDE.md](CLAUDE.md), [docs/architecture.md](docs/architecture.md) and [docs/handoff-dev2.md](docs/handoff-dev2.md).

## Prerequisites

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | 10.0.401+ (pinned in `global.json`) | |
| Aspire CLI | 13.6+ | `aspire run` |
| PostgreSQL | 17 | Local server on port 5432. Not required when `InsightFlow:EphemeralInfrastructure=true` (integration tests) |
| Garnet | current | Redis-compatible server on port 6379 (`dotnet tool install -g Microsoft.Garnet`) |
| Node.js | 22+ (npm 10+) | Builds Tailwind CSS and vendors Vega-Lite for `InsightFlow.Web` (runs automatically during `dotnet build`) |

## Run locally

```bash
aspire run
```

This starts migrations and all services against PostgreSQL and Garnet already listening on localhost
(connection strings in AppHost `appsettings.Development.json` and user-secrets). Uploaded files and Parquet
extracts are stored under `%LOCALAPPDATA%\InsightFlow\storage`. The dashboard URL is printed in the console.
The Web app is at http://localhost:5100.

Integration tests still start throwaway containers. That path needs Docker and is selected with
`InsightFlow:EphemeralInfrastructure=true`.

### AI provider keys (optional)

Without keys the app starts normally and AI features are disabled. To enable providers, set AppHost user-secrets
(never commit keys; `.env.example` lists every setting):

```bash
dotnet user-secrets --project src/InsightFlow.AppHost set "Parameters:anthropic-api-key" "<key>"
```

```bash
dotnet user-secrets --project src/InsightFlow.AppHost set "Parameters:azure-openai-endpoint" "https://<resource>.openai.azure.com/openai/v1/"
```

```bash
dotnet user-secrets --project src/InsightFlow.AppHost set "Parameters:azure-openai-api-key" "<key>"
```

## Build and test

```bash
dotnet build InsightFlow.slnx
```

```bash
dotnet test --solution InsightFlow.slnx
```

Tests run on Microsoft.Testing.Platform (opted in via `global.json`). `InsightFlow.IntegrationTests` starts the whole
AppHost and is skipped automatically when Docker is not running. Build with `-p:SkipNpm=true` to skip the front-end
step (generated CSS/JS must then already exist).

### Run the benchmarks

Compile latency and DuckDB group-bys over a deterministic 10M-row retail dataset (generated once into
`%TEMP%/insightflow-bench/10m`). Results are recorded in [docs/benchmarks.md](docs/benchmarks.md).

```bash
dotnet run -c Release --project tests/InsightFlow.Benchmarks -- --filter "*"
```

### Run the evals

AI accuracy against the 20-question retail set (see [evals/README.md](evals/README.md)). `--provider reference` self-tests the harness without keys; `anthropic` / `azure-openai` need a provider key:

```bash
dotnet run --project evals/InsightFlow.Evals -- --provider anthropic
```

### Continuous integration

`.github/workflows/ci.yml` runs on every push to `main` and every pull request. It does a Release build (warnings are
errors), runs the unit and architecture tests and the eval harness self-test, and in a second job runs the integration
tests against throwaway containers. CI uses no secrets and never deploys.

### Golden files

SQL and JSON snapshots live in `Golden/*.verified.*` next to the tests. On a mismatch the test writes a
`.received.*` file — review it, then rename it to `.verified.*` (or rerun with `INSIGHTFLOW_ACCEPT_GOLDEN=1`).

## Deploy to Azure

Manual and deliberate: see [docs/deploy.md](docs/deploy.md) (`aspire publish` to review the Bicep, then `aspire deploy`).

## Repository layout

| Path | Purpose |
|---|---|
| `src/InsightFlow.AppHost` | Aspire orchestration (local containers, Azure resources when published) |
| `src/InsightFlow.ServiceDefaults` | OpenTelemetry, health checks, resilience, service discovery |
| `src/InsightFlow.Domain` | Pure model: `VizSpec`, semantic model, Data Thread DAG, workspace tree |
| `src/InsightFlow.Contracts` | DTOs shared by Web, Api and services |
| `src/InsightFlow.Persistence` | EF Core 10 / PostgreSQL metadata store |
| `src/InsightFlow.MigrationService` | One-shot migration and seed step |
| `src/InsightFlow.Query` / `QueryService` | VizSpec → SQL compiler, DuckDB execution, extract store, cache |
| `src/InsightFlow.Agents` / `AgentService` | Agents, tools, model router, AI-SQL sandbox (SSE) |
| `src/InsightFlow.Connectors` | Data source connectors and the extract pipeline |
| `src/InsightFlow.Api` | Public REST API (workspace explorer, connections, datasets) |
| `src/InsightFlow.Worker` | Quartz.NET background jobs |
| `src/InsightFlow.Web` | Blazor Web App + Tailwind CSS v4 + Vega-Lite |
| `tests/` | Unit, architecture, integration tests and benchmarks |
| `evals/InsightFlow.Evals` | AI accuracy harness |
| `docs/` | Architecture, ADRs, deploy guide, Developer 2 handoff, benchmarks, original prompt |
