# CLAUDE.md — rules for working in this repository

Insight Flow is a multi-tenant SaaS analytics product: governed BI (semantic model, `VizSpec` charts) plus AI-first
exploration (derived fields, Data Threads). Every chart is a `VizSpec` compiled to DuckDB SQL over Parquet extracts.
The AI writes DuckDB SQL only, and it runs only in a locked-down sandbox.

Start here: [docs/architecture.md](docs/architecture.md) · [docs/adr/](docs/adr/README.md) ·
[docs/handoff-dev2.md](docs/handoff-dev2.md) · [docs/deploy.md](docs/deploy.md) · original brief:
[docs/prompts/01-foundation.md](docs/prompts/01-foundation.md) (read its errata: the UI is **Tailwind**, not Telerik).

## Locked decisions (details in the ADRs)

| # | Decision |
|---|---|
| D1 | Multi-tenant SaaS; `TenantId` on every tenant-owned row; per-tenant blob prefixes |
| D2 | .NET 10, C# 14, nullable, warnings as errors |
| D3 | Aspire 13.x AppHost + ServiceDefaults |
| D4 | Azure Container Apps, PostgreSQL Flexible Server, Azure Managed Redis, Blob, Key Vault; containers/Azurite locally |
| D5 | One versioned `VizSpec` for every chart |
| D6 | DuckDB.NET in-process over Parquet extracts; dialect seam for future pushdown |
| D7 | AI writes DuckDB SQL only; single SELECT via DuckDB's parser; sandbox with external access off, config locked, timeout, row cap |
| D8 | Microsoft Agent Framework on `IChatClient`; Azure OpenAI + Anthropic via a model router; no local LLMs |
| D9 | Blazor Web App (Interactive Server) + **Tailwind CSS v4**; charts with **Apache ECharts** (ADR 0028; no Telerik) |
| D10 | PostgreSQL + EF Core 10, JSONB for specs/schemas |
| D11 | Redis query cache keyed by tenant + version + model + spec |
| D12 | Data Threads = DAG of immutable `DatasetVersion`s |
| D13 | v1 sources all ingest to Parquet (CSV, Parquet, SQL Server done; others stubbed) |
| D14 | Entra External ID in the cloud; development auth handler locally |
| D15 | Quartz.NET with the clustered PostgreSQL job store |

## Projects and dependency rules (enforced by `tests/InsightFlow.Architecture.Tests`)

- `Domain` → nothing (BCL only). `Contracts` → Domain. `Persistence` → Domain.
- `Query` → Domain, Contracts (never Agents, MVC, UI). `Agents` → Domain, Contracts, Query. `Connectors` → Domain, Contracts.
- Hosts (`QueryService`, `AgentService`, `Api`, `Worker`, `MigrationService`) → their library + Persistence + ServiceDefaults.
- `Web` → Contracts + ServiceDefaults only, and from Domain only `InsightFlow.Domain.Viz`. It talks to services over HTTP.
- Packages: AI SDKs only in Agents; DB drivers only in Connectors; DuckDB only in Query/Connectors/Agents;
  `Microsoft.AspNetCore.Components.*`, npm and JS/CSS only in Web.
- New project or package? Update the rule tables in the architecture tests, plus Central Package Management (`Directory.Packages.props`).

## Commands

```bash
dotnet build InsightFlow.slnx
```

```bash
dotnet test --solution InsightFlow.slnx
```

```bash
dotnet run --project src/InsightFlow.AppHost
```

Run a single test project: `dotnet test --project tests/InsightFlow.Query.Tests`. The integration tests need Docker.

Other commands:
- Add a migration: `dotnet ef migrations add <Name> --project src/InsightFlow.Persistence`. The migration service applies it.
- Evals: `dotnet run --project evals/InsightFlow.Evals -- --provider reference` (self-test, no keys) or `--provider anthropic` / `azure-openai`.
- Benchmarks: `dotnet run -c Release --project tests/InsightFlow.Benchmarks -- --filter "*"`.
- Accept golden files: review `Golden/*.received.*`, then rename to `.verified.*`, or rerun with `INSIGHTFLOW_ACCEPT_GOLDEN=1`.
- Web CSS while editing: `npm run watch:css` in `src/InsightFlow.Web`. Build without Node: `-p:SkipNpm=true`.
- Azure: `aspire publish` to review, `aspire deploy` to provision. **Only with Roy's explicit go-ahead** (docs/deploy.md).

The app runs at http://localhost:5100 as Dev User of the Contoso Retail tenant. If the AppHost times out starting DCP
on Windows, delete `%USERPROFILE%\.dcp\state.elevated`.

## Coding standards

- File-scoped namespaces, `sealed` by default, records for immutable data, primary constructors where they help.
- `CancellationToken` on every async public method, passed all the way down. No `.Result`, no `.Wait()`.
- Logging: `ILogger<T>` with `[LoggerMessage]` source generation. Log ids, counts, durations and token usage only.
- JSON: source-generated contexts (`ContractsJsonContext`, `VizJsonContext`). Add every new DTO to the context.
- APIs: Minimal APIs with `MapGroup` under `/api/v1`, `TypedResults`, ProblemDetails (`ApiProblemException`), policies
  from `InsightFlowPolicies`. OpenAPI + Scalar in Development only.
- Options: `AddOptions<T>().Bind(...).ValidateDataAnnotations().ValidateOnStart()`.
- Telemetry: an `ActivitySource`/`Meter` per area named `InsightFlow.*` (registered by ServiceDefaults).
- Public abstractions get an XML doc comment explaining **why** they exist. Match the surrounding comment density.
- Tests: xUnit v3 on Microsoft.Testing.Platform, Shouldly, golden files through `tests/InsightFlow.Testing/Golden.cs`.
  Name tests `Method_State_Expected`. Use a fake `IChatClient` for agent tests (`ScriptedChatClient`).
- Zero warnings (`TreatWarningsAsErrors`, analyzers at `latest-recommended`). Fix the cause; don't suppress
  without a comment that gives the reason.
- Verify package versions and API signatures against NuGet and official docs. Never guess.
- Work in Developer 2's areas: skeletons, contracts and `TODO(dev2)` markers only, unless asked otherwise.
  Roy's open items are marked `TODO(roy)`.

## Never do

- **No Python**, anywhere, for any reason.
- **No secrets in the repo.** Use user secrets, Aspire parameters or Key Vault. `.env.example` and appsettings hold placeholders only.
- **No row data, prompts containing data samples, or credentials in logs** (also not in exception messages that get logged).
- **AI-generated SQL runs only through `ISqlSandbox`.** Never execute model output any other way. Never let AI SQL write files.
- **Column names never come from user or model text.** They come from the semantic model, quoted by the dialect. Values are always parameters.
- **Never read the tenant from a request body or route.** Use `ITenantContext` / `ICurrentTenant`, and never bypass the
  `tenant` query filter (`IgnoreQueryFilters` only with the specific named filter and a reason).
- **No UI or JS outside `InsightFlow.Web`; no CDN scripts.** Front-end packages are vendored through npm at build. No Telerik.
- **No database migrations from services.** Only the migration service migrates.
- **No commit, push, deploy, provisioning or deletion without Roy's explicit approval each time.** A push may trigger a
  deploy. Show `git diff --stat` and ask "Ready to commit — want me to?" first.

## Ownership

| Area | Owner |
|---|---|
| Architecture, ADRs, `CLAUDE.md`, Domain, Query/QueryService, Agents/AgentService, evals, AppHost/ServiceDefaults | Roy (tech lead) |
| Web, Api, Connectors (beyond the reference ones), Worker, tenancy admin, roles, RLS | Developer 2 |

See `.github/CODEOWNERS`. Contract changes in Roy's areas need his review even when Developer 2 needs them.
