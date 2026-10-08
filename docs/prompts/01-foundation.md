> **Errata and amendments (recorded 2026-10-08).** This is the original foundation prompt, kept verbatim for traceability.
> Where it conflicts with the notes below, the notes and the ADRs in `docs/adr/` win.
>
> 1. **Telerik → Tailwind (D9, M8, §3–§4, M0, M9).** "Telerik" was a mistake by Roy. The UI stack is **Tailwind CSS v4**,
>    built by npm + an MSBuild target, and **Vega-Lite is the only chart renderer** (not a fallback). There is no Telerik
>    feed, licence or `TELERIK_NUGET_KEY`, and no `TelerikRootComponent` or `TelerikDockManager`: the workspace is a
>    Tailwind CSS-grid layout. "Only Web references Telerik" became "only Web contains UI/JS assets and Blazor
>    packages". See ADR 0009.
> 2. **Workspace Explorer (added requirement).** An in-app folder tree (My Workspace + Shared) with drag-and-drop of
>    local files and folders. It replaces the standalone "upload a CSV" endpoint of M5. See ADR 0016.
> 3. **Verify → in-repo golden-file helper** (ADR 0018). **FluentAssertions → Shouldly** (ADR 0017). **NetArchTest →
>    ArchUnitNET** (ADR 0024).
> 4. **`Encoding` → `VizEncoding`** (§6.1) to avoid clashing with `System.Text.Encoding` (ADR 0005).
> 5. **`Azure.AI.OpenAI` → `OpenAI` SDK** against the Azure `/openai/v1/` endpoint; the Anthropic SDK is the package
>    `Anthropic` (ADR 0025).
> 6. **Interfaces refined** (§6.4): executor sources, `IExtractStore.SaveAsync(tenant, versionId, …)`,
>    `DateTrunc(…, dateOnly)` and `ApplyLimit` on dialects, and `IModelRouter.Resolve`/`GetClient` (ADR 0026).
> 7. **Sandbox process model:** in-process cancellation is proven, so `ProcessIsolatedSqlSandbox` was not built (ADR 0022).
> 8. **Commits:** per Roy's global rule, every commit and push needed his explicit approval. Milestones were committed
>    only after he said yes.

---

# Insight Flow — Master Prompt 01: Foundation & Architecture

---

## 0. Your role and how to work

You are the founding engineer for **Insight Flow**, a multi-tenant SaaS analytics product. You are working with **Roy (tech lead)**. Your job in this session is to build the **foundation and architecture** of the repository so that a second developer can clone it and start on product features immediately.

Working rules:

1. **Start in plan mode.** Read this entire prompt, check the environment (Milestone 0), then present a written plan and wait for my approval before writing code.
2. **Never guess package names, versions or API signatures.** Verify against NuGet and official docs (learn.microsoft.com, aspire.dev, telerik.com, duckdb.org, github.com/microsoft/agent-framework) before using them. Use the latest **stable** versions unless this prompt says otherwise. If an API named in this prompt has changed, use the current one and note it in the ADR.
3. **Work milestone by milestone.** At the end of each milestone: `dotnet build` (zero warnings), `dotnet test` (green), commit with a Conventional Commit message, then print a short summary (what was built, what was verified, anything deferred). **Pause for my review after Milestone 1 and after the final milestone**; otherwise continue to the next milestone.
4. **No secrets in the repo.** Use user-secrets locally, Aspire parameters, and Azure Key Vault in the cloud. Add a `.env.example` / `appsettings.Development.json` with placeholders only.
5. **Ask before** anything destructive or anything that touches a real cloud subscription (provisioning, deploying, deleting).
6. Prefer small, readable code with tests over clever code. Every public abstraction gets an XML doc comment explaining *why* it exists.

---

## 1. Product in one paragraph

Insight Flow combines Tableau's governed, drag-and-drop BI (semantic model, shelves, dashboards, roles) with Microsoft Data Formulator's AI-first exploration (drop a field that doesn't exist yet and AI derives it; branching "Data Threads" of exploration history). Users connect data, model it once, explore by dragging fields or asking in plain English, and pin results to dashboards. Every chart is a canonical **`VizSpec`** compiled to SQL by our own query engine ("VizQL-lite"). Extracts are Parquet files queried by **DuckDB**. AI writes **DuckDB SQL only** (never Python), executed in a locked-down sandbox, and every AI answer shows its SQL and a data preview.

---

## 2. Locked decisions (do not re-litigate; record each as an ADR)

| # | Area | Decision |
|---|---|---|
| D1 | Delivery model | Multi-tenant **SaaS**. One deployment, many tenants. `TenantId` on every metadata row; per-tenant blob prefix for extracts. |
| D2 | Runtime | **.NET 10** (LTS), **C# 14**, nullable enabled, `TreatWarningsAsErrors=true`. |
| D3 | Orchestration | **Aspire 13.x** AppHost (`Aspire.AppHost.Sdk`), ServiceDefaults for OpenTelemetry, health checks, resilience, service discovery. |
| D4 | Hosting | **Azure Container Apps** (via Aspire's ACA environment), Azure Database for PostgreSQL Flexible Server, Azure Cache for Redis / Azure Managed Redis, Azure Blob Storage, Azure Key Vault. Locally: containers + Azurite emulator. |
| D5 | Chart contract | One versioned **`VizSpec`** record (grammar of graphics) for every chart. UI, AI and saved workbooks all produce/consume it. |
| D6 | Analytical engine | **DuckDB.NET** (`DuckDB.NET.Data.Full`) in-process over **Parquet** extracts. Live pushdown to SQL sources comes later via a dialect interface. |
| D7 | AI output | AI writes **DuckDB SQL only**, validated (single `SELECT`, parsed by DuckDB itself) and run in a sandbox (`enable_external_access=false`, `lock_configuration=true`, timeout, row cap). No Python execution anywhere. |
| D8 | AI runtime | **Microsoft Agent Framework 1.x** (`Microsoft.Agents.AI`) on **`Microsoft.Extensions.AI.IChatClient`**. Providers: **Azure OpenAI and Anthropic**, both behind `IChatClient`, chosen per tenant/per task by a model router. **No on-premises / local LLMs.** |
| D9 | UI | **Blazor Web App**, Interactive Server render mode for v1, **Telerik UI for Blazor** (licences are available). Telerik Chart is the primary renderer; a Vega-Lite JS-interop renderer is the fallback for marks Telerik lacks (facets, box plot, density). |
| D10 | Metadata store | **PostgreSQL** via **EF Core 10** + Npgsql; JSONB for specs and schemas. |
| D11 | Cache | **Redis** for query results, keyed by hash(tenant, dataset version, normalized `VizSpec`). |
| D12 | History | **Data Threads** = a DAG of immutable `DatasetVersion` nodes in PostgreSQL. Branch from any node. |
| D13 | Data sources (v1) | Files: **CSV, Excel, Parquet**. Databases: **SQL Server, PostgreSQL, MySQL, Oracle, MongoDB, Azure Cosmos DB (NoSQL API)**. All ingest to Parquet extracts in v1. Document stores (Mongo, Cosmos) are flattened by sampled schema inference. |
| D14 | Identity | **Microsoft Entra External ID** (OIDC) for customer sign-in in the cloud; a development auth handler locally. Tenant resolved from a token claim. |
| D15 | Background jobs | **Quartz.NET** with the PostgreSQL ADO job store in the Worker service. |

---

## 3. Team and ownership (write this into `CLAUDE.md` and `CODEOWNERS`)

There are **two developers**.

**Roy — Tech lead / backend (.NET, SQL)** owns:
- Overall architecture, solution structure, ADRs, `CLAUDE.md`
- `InsightFlow.Domain` (`VizSpec`, semantic model, Data Thread DAG)
- `InsightFlow.Query` + `InsightFlow.QueryService` (compiler, dialects, DuckDB, extract store, cache)
- `InsightFlow.Agents` + `InsightFlow.AgentService` (agents, tools, model router, AI-SQL sandbox)
- `evals/InsightFlow.Evals` (AI accuracy eval set)

**Developer 2** owns (after handoff):
- `InsightFlow.Web` (all Blazor/Telerik UI: workspace, viz builder, dashboards, renderer adapter)
- `InsightFlow.Api` (CRUD endpoints, sharing, embedding)
- `InsightFlow.Connectors` (all connector implementations beyond the reference ones)
- `InsightFlow.Worker` (schedules, extract refresh, subscriptions, alerts, exports)
- Tenancy administration, roles (Creator / Explorer / Viewer), row-level security rules

**In this session you build the foundation for everything**, but for Developer 2's areas build only: the project skeleton, the abstractions/contracts, one working reference path, and clearly marked `// TODO(dev2):` stubs. Do **not** build full product features in Developer 2's areas.

---

## 4. Repository layout (create exactly this, adjust only with a stated reason)

```
/
├─ InsightFlow.slnx                     # .NET 10 SLNX solution format
├─ global.json                          # pin .NET 10 SDK (rollForward: latestFeature)
├─ Directory.Build.props                # LangVersion, Nullable, TreatWarningsAsErrors, analyzers
├─ Directory.Packages.props             # Central Package Management — all versions live here
├─ nuget.config                         # nuget.org + Telerik feed (credentials from env vars)
├─ .editorconfig  .gitignore  .gitattributes
├─ CLAUDE.md                            # rules for Claude Code sessions (see Milestone 10)
├─ README.md
├─ docs/
│  ├─ architecture.md                   # Mermaid diagrams + component responsibilities
│  ├─ adr/0001-... .md                  # one ADR per locked decision D1–D15
│  ├─ handoff-dev2.md                   # what Developer 2 picks up, where, and how
│  └─ prompts/01-foundation.md          # copy of this prompt
├─ src/
│  ├─ InsightFlow.AppHost/              # Aspire orchestration
│  ├─ InsightFlow.ServiceDefaults/      # OTel, health, resilience, service discovery
│  ├─ InsightFlow.Domain/               # pure model: NO references to EF, ASP.NET, Telerik, DuckDB
│  ├─ InsightFlow.Contracts/            # DTOs shared by Web ⇄ Api ⇄ services (System.Text.Json)
│  ├─ InsightFlow.Persistence/          # EF Core 10 DbContext, configurations, migrations
│  ├─ InsightFlow.Query/                # compiler, dialects, DuckDB execution, extract store, cache
│  ├─ InsightFlow.QueryService/         # ASP.NET Core host exposing the query engine
│  ├─ InsightFlow.Agents/               # agents, tools, model router, SQL sandbox
│  ├─ InsightFlow.AgentService/         # ASP.NET Core host exposing agents (SSE streaming)
│  ├─ InsightFlow.Connectors/           # connector abstraction + implementations
│  ├─ InsightFlow.Api/                  # public REST API (Minimal APIs, OpenAPI)
│  ├─ InsightFlow.Worker/               # Quartz.NET jobs
│  └─ InsightFlow.Web/                  # Blazor Web App + Telerik
├─ tests/
│  ├─ InsightFlow.Domain.Tests/
│  ├─ InsightFlow.Query.Tests/          # includes golden-file SQL tests
│  ├─ InsightFlow.Agents.Tests/         # fake IChatClient, sandbox tests
│  ├─ InsightFlow.Connectors.Tests/
│  ├─ InsightFlow.Architecture.Tests/   # enforces layering rules (NetArchTest or ArchUnitNET)
│  ├─ InsightFlow.IntegrationTests/     # Aspire.Hosting.Testing end-to-end
│  └─ InsightFlow.Benchmarks/           # BenchmarkDotNet: compiler + DuckDB at 10M rows
├─ evals/
│  └─ InsightFlow.Evals/                # AI accuracy harness + question set
└─ .github/
   ├─ workflows/ci.yml
   ├─ CODEOWNERS
   └─ pull_request_template.md
```

### Dependency rules (enforce in `InsightFlow.Architecture.Tests`)

- `Domain` → nothing (BCL only).
- `Contracts` → `Domain` only.
- `Persistence` → `Domain`.
- `Query` → `Domain`, `Contracts`. Must **not** reference Telerik, ASP.NET Core MVC, or `Agents`.
- `Agents` → `Domain`, `Contracts`, `Query` (for the sandbox and compiler only).
- `Connectors` → `Domain`, `Contracts`.
- Service hosts (`QueryService`, `AgentService`, `Api`, `Worker`) → their library + `Persistence` + `ServiceDefaults`.
- `Web` → `Contracts`, `ServiceDefaults` only. **Web never references `Query`, `Agents`, `Persistence` or `Domain` internals**; it talks to services over HTTP.
- Only `Web` references Telerik packages.

---

## 5. Coding standards

- File-scoped namespaces, primary constructors where they help, `sealed` by default, records for immutable data.
- `CancellationToken` on every async public method, passed all the way down. No `.Result` / `.Wait()`.
- `ILogger<T>` with source-generated `[LoggerMessage]` methods in hot paths. **Never log row data, prompts with data samples, or credentials** — log ids, counts, durations, token usage.
- System.Text.Json with source-generated `JsonSerializerContext` for `Contracts` and `VizSpec`.
- Minimal APIs grouped with `MapGroup`, `TypedResults`, `ProblemDetails` for errors, built-in .NET 10 OpenAPI; Scalar UI in Development only. Routes versioned under `/api/v1`.
- Options pattern with `ValidateDataAnnotations().ValidateOnStart()` for all config.
- Custom `ActivitySource` + `Meter` per service (`InsightFlow.Query`, `InsightFlow.Agents`, …) registered in ServiceDefaults so traces show compile → execute → cache.
- Tests: xUnit v3, `FluentAssertions` (or `Shouldly` — pick one, record it), `Verify` for golden files. Name tests `Method_State_Expected`.

---

## 6. Core contracts to implement (starting points — refine, don't weaken)

### 6.1 `VizSpec` (Domain)

```csharp
public enum Mark { Bar, Line, Area, Point, Pie, Heatmap, Table }
public enum Agg { None, Sum, Avg, Count, CountDistinct, Min, Max, Median }
public enum TimeUnit { Year, Quarter, Month, Week, Day, Hour }
public enum SortDirection { Asc, Desc }

public sealed record FieldRef(string Field, Agg Agg = Agg.None, TimeUnit? TimeUnit = null);

public sealed record Encoding(
    FieldRef? X, FieldRef? Y,
    FieldRef? Color = null, FieldRef? Size = null,
    FieldRef? Facet = null, FieldRef? Label = null)
{
    public IEnumerable<FieldRef> All() =>
        new[] { X, Y, Color, Size, Facet, Label }.OfType<FieldRef>();
}

public sealed record SortSpec(string Field, SortDirection Direction);

public sealed record VizSpec(
    int SchemaVersion,                 // start at 1; bump on breaking changes
    Guid DatasetVersionId,
    Mark Mark,
    Encoding Encoding,
    IReadOnlyList<FilterSpec> Filters,
    IReadOnlyList<SortSpec>? Sort = null,
    int Limit = 5_000);
```

`FilterSpec` is a polymorphic hierarchy (`EqualsFilter`, `InFilter`, `RangeFilter`, `RelativeDateFilter`, `TopNFilter`) serialized with `[JsonPolymorphic]` / `[JsonDerivedType]`. Add a `VizSpecValidator` that checks fields exist in the semantic model, aggregations are legal for the column type, and `Limit` ≤ 50,000.

### 6.2 Semantic model (Domain)

`SemanticModel` → `ModelTable` → `ModelColumn` (`Role`: Dimension | Measure, `DataType`, `DisplayName`, `Description`, `Synonyms`, `Format`), `Relationship` (from/to table, join keys, cardinality), `CalculatedMeasure` (a DuckDB SQL expression, validated by the sandbox before saving). The semantic model is used both by the compiler (joins, column resolution) and by the agents (prompt grounding).

### 6.3 Data Thread DAG (Domain)

`DatasetVersion` { `Id`, `TenantId`, `ParentIds`, `Kind` (Source | Extract | Derived), `SqlText?`, `Prompt?`, `ParquetUri`, `Schema` (JSON), `RowCount`, `CreatedBy`, `CreatedAt` } — immutable. `DataThread` { `Id`, `TenantId`, `Title`, `RootVersionId` } and `ThreadNode` linking versions + saved `VizSpec`s + agent explanation text.

### 6.4 Query engine (Query)

```csharp
public interface ISqlCompiler   { CompiledQuery Compile(VizSpec spec, SemanticModel model, IQueryDialect dialect); }
public interface IQueryDialect  { string Name { get; } string QuoteIdentifier(string id); string DateTrunc(TimeUnit unit, string expr); string Limit(int n); /* … */ }
public interface IQueryExecutor { Task<QueryResult> ExecuteAsync(CompiledQuery query, DatasetVersion version, CancellationToken ct); }
public interface IExtractStore  { Task<string> GetLocalPathAsync(DatasetVersion version, CancellationToken ct); Task<Uri> SaveAsync(TenantId tenant, Stream parquet, CancellationToken ct); }
public interface IQueryCache    { /* Redis, key = SHA256(tenant + datasetVersionId + canonical spec JSON) */ }
```

- Implement **`DuckDbDialect` fully**. Create `SqlServerDialect`, `PostgresDialect`, `MySqlDialect`, `OracleDialect` with the interface surface and a few golden tests, marked for future live pushdown.
- Column names must come from the semantic model (already quoted by the dialect) — **never from user or model text**. Filter values are always parameters.
- `IExtractStore`: Parquet lives in Blob under `tenants/{tenantId}/extracts/{datasetVersionId}.parquet`; the QueryService keeps a bounded local disk cache (LRU by size) because the sandbox disables DuckDB external access.

### 6.5 AI-SQL sandbox (Agents, uses Query)

```csharp
public interface ISqlSandbox
{
    Task<SandboxResult> RunAsync(SandboxRequest request, CancellationToken ct);
    // request: tenant, input DatasetVersion(s), SQL text, timeout, row cap
}
```

Requirements:
1. Fresh in-memory DuckDB connection per request. Load the input Parquet into a table named `input` (and `input_2`… for multi-input) **before** locking.
2. `SET enable_external_access = false; SET lock_configuration = true;`
3. Guard: reject anything that is not a single `SELECT` — use DuckDB's own parser (e.g. `json_serialize_sql`) rather than regex. Reject `ATTACH`, `COPY`, `INSTALL`, `LOAD`, `PRAGMA`, `SET`, multiple statements.
4. Timeout (default 15 s) + row cap (default 50,000) + memory limit (`SET memory_limit`).
5. **Write a test that proves cancellation actually interrupts a long-running DuckDB query.** If DuckDB.NET does not interrupt reliably, implement `ProcessIsolatedSqlSandbox` (child process that can be killed) behind the same interface and make it the default. Record the finding in an ADR.
6. Successful derived results are saved as a new child `DatasetVersion` (Parquet via DuckDB `COPY … TO … (FORMAT PARQUET)` executed by the trusted code path, not by AI SQL).

### 6.6 Model router and agents (Agents)

- Register two `IChatClient` providers: **Azure OpenAI** (`Azure.AI.OpenAI` → `.AsIChatClient()`) and **Anthropic** (official Anthropic .NET SDK's `IChatClient` adapter — verify the current package and method). Wrap both with `ChatClientBuilder` middleware: OpenTelemetry, logging (metadata only), distributed cache for identical prompts, and a `TokenBudgetChatClient` that enforces per-tenant monthly caps.
- `IModelRouter.GetClient(TenantId, AiTask)` where `AiTask` ∈ { `Routing`, `SqlGeneration`, `ChartRecommendation`, `InsightSummary` } — config-driven (e.g. small model for routing, strongest model for SQL). Keyed services for each provider.
- Build the **Analyst agent** with Agent Framework (verify current GA API, e.g. `AsAIAgent(...)`, sessions) and three tools, all read-only or sandboxed:
  - `DescribeModel(datasetVersionId)` → tables, columns, synonyms, sample distinct values (capped).
  - `RunSql(sql)` → goes through `ISqlSandbox`, returns schema + first 20 rows + row count.
  - `ProposeChart(...)` → returns a validated `VizSpec`, never UI markup.
- Implement the **derived-field flow** end to end: partial `VizSpec` with an unknown field + natural-language hint → agent writes SQL → sandbox → new child `DatasetVersion` → compiler builds chart query on it → response includes SQL, explanation, preview rows and final `VizSpec`.
- `AgentService` streams agent output to the client with **Server-Sent Events**.

### 6.7 Connectors (Connectors)

```csharp
public interface IDataSourceConnector
{
    DataSourceKind Kind { get; }
    ConnectorCapabilities Capabilities { get; } // SupportsLivePushdown, IsDocumentStore, SupportsIncremental
    Task<ConnectionTestResult> TestAsync(ConnectionProfile profile, CancellationToken ct);
    IAsyncEnumerable<SourceTable> DiscoverAsync(ConnectionProfile profile, CancellationToken ct);
    Task<ExtractResult> ExtractAsync(ExtractRequest request, IExtractWriter writer, CancellationToken ct);
}
```

- `IExtractWriter` streams rows into a DuckDB table via the **Appender**, then writes Parquet with `COPY … TO`. Stream; never load a whole source into memory.
- Credentials are never stored in `ConnectionProfile` rows — only a secret reference resolved via `ISecretStore` (Key Vault in cloud, user-secrets/local file in dev).
- **Implement fully as reference implementations:** CSV, Parquet, SQL Server (`Microsoft.Data.SqlClient`).
- **Create registered stubs** (throwing `NotImplementedException` with `// TODO(dev2):` and a short implementation note) for: Excel, PostgreSQL (`Npgsql`), MySQL (`MySqlConnector`), Oracle (`Oracle.ManagedDataAccess.Core`), MongoDB (`MongoDB.Driver`; sample N documents to infer schema, flatten nested objects to dotted columns, arrays to JSON text in v1), Cosmos DB NoSQL (`Microsoft.Azure.Cosmos`; same flattening rules).
- Write a reusable **connector contract test suite** (abstract base test class) that every connector's tests inherit, so Developer 2 gets the same quality bar for free.

### 6.8 Tenancy and security (Persistence + ServiceDefaults)

- `ITenantContext` resolved per request from the token claim (configurable claim name). Requests without a tenant are rejected (except health endpoints).
- EF Core global query filter on `TenantId` for every tenant-owned entity; an architecture/integration test proves a tenant cannot read another tenant's rows.
- Role claims: `Creator`, `Explorer`, `Viewer`, `TenantAdmin`; authorization policies defined in one place. (Developer 2 builds the role-management features; you build the policy plumbing.)
- Development auth handler that signs in a fake user for a seeded "Contoso Retail" tenant; Entra External ID configuration placeholders for the cloud.

---

## 7. Milestones

### M0 — Environment check and plan (plan mode, no code)
- Check: `dotnet --info` (.NET 10 SDK), Aspire CLI, Docker/Podman running, Git.
- Verify current stable versions for every package in this prompt and list them.
- Confirm Telerik NuGet feed access works via env vars (`TELERIK_NUGET_KEY`) and how the Telerik licence key is supplied (env var / licence file — never committed).
- Output: the plan, the package/version table, any API changes found versus this prompt. **Wait for approval.**

### M1 — Solution skeleton and orchestration  ⏸ *pause for review*
- Create all files/projects from §4 with references per the dependency rules.
- AppHost: PostgreSQL (with a database `insightflow`, data volume), Redis, Azure Storage emulator (blobs container `extracts`), Key Vault reference for cloud, parameters for AI keys. Wire `QueryService`, `AgentService`, `Api`, `Worker`, `Web` with `WithReference`/`WaitFor`; `Web` has external endpoints.
- ServiceDefaults: OpenTelemetry (traces, metrics, logs), health checks (`/health`, `/alive`), resilience handlers, service discovery.
- Each service starts and reports healthy in the Aspire dashboard.
- `InsightFlow.Architecture.Tests` implemented and passing.
- Done when: `aspire run` shows every resource healthy; build has zero warnings; architecture tests pass.

### M2 — Domain model
- `VizSpec`, filters, semantic model, Data Thread DAG, tenancy primitives (`TenantId` strongly typed), validators.
- JSON source-gen context; round-trip tests; validator tests; example `VizSpec` JSON fixtures in `tests/.../Fixtures`.

### M3 — Persistence, tenancy, identity plumbing
- EF Core 10 DbContext, configurations (JSONB for spec/schema), first migration, seeding of the dev tenant and a sample semantic model.
- `ITenantContext`, global query filters, tenant-isolation test, role policies, dev auth handler, `ISecretStore` (dev + Key Vault implementations).
- Migrations applied by a dedicated startup step (e.g. a migration worker resource in the AppHost), not by every service.

### M4 — Query engine
- `SqlCompiler`, `DuckDbDialect`, other dialect skeletons, `IQueryExecutor` (DuckDB over local Parquet cache), `IExtractStore`, Redis `IQueryCache`.
- `QueryService` endpoints: `POST /api/v1/query/viz` (VizSpec → rows + column metadata), `POST /api/v1/query/preview` (dataset version → first N rows + schema).
- Golden-file tests for compiled SQL across a matrix of specs (time units, aggregates, filters, joins via relationships, top-N).
- Benchmark: generate a deterministic 10M-row retail sales Parquet file; measure compile + group-by latency; record results in `docs/benchmarks.md`.

### M5 — Connector framework and extract pipeline
- Abstraction, `IExtractWriter` (Appender → Parquet → Blob), reference connectors (CSV, Parquet, SQL Server), stubs for the rest, contract test suite.
- `Worker`: Quartz.NET configured with PostgreSQL job store; one `ExtractRefreshJob` that runs a connector and registers a new `DatasetVersion`.
- `Api`: minimal endpoints to upload a CSV/Parquet file and to create a SQL Server connection + trigger an extract (enough for the vertical slice; Developer 2 expands the API).

### M6 — AI-SQL sandbox and agents
- `ISqlSandbox` (+ cancellation proof test, + process-isolated variant if needed), SQL guard tests with malicious inputs (multi-statement, `ATTACH`, `COPY`, `read_csv('/etc/passwd')`, `INSTALL httpfs`, comment tricks).
- Model router with Azure OpenAI + Anthropic `IChatClient`s, middleware pipeline, token budgets.
- Analyst agent + tools, derived-field flow, `AgentService` SSE endpoint.
- Unit tests use a **fake `IChatClient`** with scripted responses; one optional integration test runs against real providers only when keys are present.

### M7 — Eval harness
- `evals/InsightFlow.Evals`: loads the deterministic retail dataset + semantic model, runs **20 starter questions** (YAML/JSON file: question, expected result set, tolerance), calls the analyst agent, executes its SQL in the sandbox, compares **result sets** (not SQL text), outputs accuracy per model/provider as Markdown + JSON.
- Runnable locally with `dotnet run --project evals/InsightFlow.Evals -- --provider azure-openai|anthropic`. Add a manual-trigger GitHub Actions workflow for it.

### M8 — Web shell and vertical slice (skeleton only — Developer 2 owns the UI)
- Blazor Web App (Interactive Server), Telerik UI for Blazor installed and themed, `TelerikRootComponent` layout.
- Workspace page shell using **TelerikDockManager** panes: Data (field list), Canvas, Thread, Chat — placeholders except as below.
- **Vertical slice page:** pick a dataset version → choose mark, X, Y (+aggregate) from dropdowns → call QueryService → render with a `VizRenderer` component that maps `QueryResult` → Telerik Chart series. Include a `VegaLiteRenderer` stub component (JS interop, no logic yet) and the adapter interface that chooses between them.
- Typed HTTP clients for services using service discovery names from the AppHost.

### M9 — CI/CD and Azure Container Apps readiness
- `.github/workflows/ci.yml`: restore (Telerik feed via secret), build, test, architecture tests; cache NuGet.
- AppHost cloud configuration: Azure Container Apps environment, Azure PostgreSQL Flexible Server, Azure Redis, Azure Storage, Key Vault, Azure OpenAI and Anthropic keys as secure parameters; containers run locally (`RunAsContainer`/`RunAsEmulator`) and as Azure resources when published.
- Document the deploy command(s) (`aspire deploy` / `aspire do` / `azd` — whichever is current) in `docs/deploy.md`. **Do not provision or deploy anything without my explicit confirmation.**

### M10 — Documentation and handoff  ⏸ *final review*
- `CLAUDE.md`: project summary, locked decisions table (short), dependency rules, coding standards, commands (`aspire run`, test, eval, add migration), ownership map, "never do" list (no Python, no secrets, no Telerik outside Web, no data in logs, AI SQL only via `ISqlSandbox`).
- `docs/architecture.md` with Mermaid diagrams: component diagram, derived-field sequence diagram, Data Thread DAG example, deployment diagram on ACA.
- ADRs for D1–D15 plus any decisions made during the build (e.g. sandbox process model, assertion library).
- `docs/handoff-dev2.md`: every `TODO(dev2)` grouped by area, the contracts Developer 2 must code against, how to add a connector (with the contract test suite), how to add an API endpoint, how to add a Web page that renders a `VizSpec`, local setup steps.
- `CODEOWNERS` matching §3, PR template with checklist (tests, no warnings, tenant isolation considered, no secrets).
- Final summary to me: what works end to end, test counts, benchmark numbers, eval baseline accuracy per provider, known gaps.

---

## 8. Definition of done for this whole prompt

1. `git clone` → set env vars per README → `aspire run` brings up every service and resource healthy.
2. Vertical slice works: upload the sample CSV → extract to Parquet → build a bar chart in the Web slice page → chart renders from QueryService results (cached on repeat).
3. Derived-field flow works through `AgentService` with a real provider (when keys present) and with the fake client in tests.
4. Sandbox rejects every malicious case in the test suite; cancellation is proven.
5. Tenant isolation test passes.
6. Zero build warnings; all tests green; architecture tests enforce the dependency rules.
7. `CLAUDE.md`, architecture doc, ADRs and the Developer 2 handoff doc are complete enough that a new developer (and a new Claude Code session) can start without asking me questions.

## 9. Explicitly out of scope for this session

Full viz builder UI, dashboards with cross-filtering, sharing/embedding, subscriptions/alerts, exports, the remaining connector implementations, role-management screens, RLS rule editor, billing, live pushdown execution, MCP server, Vega-Lite rendering logic. Leave clean seams and `TODO(dev2)` / `TODO(roy)` markers for each.