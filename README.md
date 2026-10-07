# Insight Flow

Multi-tenant SaaS analytics: governed, drag-and-drop BI (semantic model, shelves, dashboards, roles) combined with
AI-first exploration (derived fields, branching Data Threads). Every chart is a canonical `VizSpec` compiled to
DuckDB SQL over Parquet extracts. The AI writes DuckDB SQL only, and it always runs in a locked-down sandbox.

> Status: **Milestone 1 — solution skeleton & orchestration.** Architecture docs, ADRs and the Developer 2 handoff
> land in later milestones (see `docs/`).

## Prerequisites

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | 10.0.401+ (pinned in `global.json`) | |
| Aspire CLI | 13.6+ | `aspire run` |
| Docker Desktop (or Podman) | running | Postgres, Redis and Azurite run as containers |
| Node.js | 22+ (npm 10+) | Builds Tailwind CSS and vendors Vega-Lite for `InsightFlow.Web` (runs automatically during `dotnet build`) |

## Run locally

```bash
aspire run
```

This starts PostgreSQL, Redis, the Azurite storage emulator, the migration step and all services. The dashboard URL
is printed in the console. The Web app is at http://localhost:5100.

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
