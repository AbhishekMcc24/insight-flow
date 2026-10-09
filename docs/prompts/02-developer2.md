# Insight Flow — Master Prompt 02: Developer 2 (product surface)

> **Who this is for:** Developer 2, and the AI coding assistant (Cursor) working with them.
> Paste this whole file into a new Cursor chat (Agent mode) at the start of each work stream, or reference it with
> `@docs/prompts/02-developer2.md`. The always-on rules in `.cursor/rules/` repeat the non-negotiables.

---

## 0. Your role and how to work

You are **Developer 2** on **Insight Flow** (with your AI assistant). **Roy** is the tech lead. Roy built the
foundation (Master Prompt 01, milestones M1–M10). You now own the **product surface**: the UI, the public API,
connectors, background jobs and tenancy administration. Roy owns the engine (Domain, Query, Agents) and the
architecture.

Working rules (for you **and** the AI assistant):

1. **Read before writing.** Read these first, in order:
   1. [`CLAUDE.md`](../../CLAUDE.md) — repo rules, commands, never-do list.
   2. [`docs/handoff-dev2.md`](../handoff-dev2.md) — your areas, contracts, endpoints, how-tos, every `TODO(dev2)`.
   3. [`docs/architecture.md`](../architecture.md) — how the pieces fit together.
   4. The ADRs relevant to the task, in [`docs/adr/`](../adr/README.md).
2. **Plan first, then code.** For every milestone below:
   - Write a short plan: the files to touch, any contract changes, and the tests.
   - Check it against the dependency rules.
   - If it changes anything in Roy's areas (see §3), stop and get Roy's agreement before writing code.
3. **Never guess package versions or API signatures.** Verify against NuGet, npm and the official docs
   (learn.microsoft.com, aspire.dev, duckdb.org, tailwindcss.com, echarts.apache.org). Packages go in
   `Directory.Packages.props` (Central Package Management) — never put a version in a csproj.
4. **Small, reviewable pull requests.** One branch per feature (`feat/<area>-<thing>`) and one PR per milestone or
   smaller. Use Conventional Commits. CI must be green, and you complete the PR template checklist.
5. **The AI assistant never commits, pushes, deploys, provisions Azure resources or deletes data unless the developer
   explicitly asks, each time.** Before a commit it shows `git diff --stat` and asks.
6. **Definition of done for every change:**
   - `dotnet build InsightFlow.slnx` passes with **zero warnings**.
   - `dotnet test --solution InsightFlow.slnx` is green.
   - New endpoints have integration tests, including a **cross-tenant** check.
   - UI changes are checked in a browser.
   - Docs or `TODO` markers are updated.

---

## 1. Product in one paragraph

Insight Flow combines Tableau-style governed, drag-and-drop BI (semantic model, shelves, dashboards, roles) with
AI-first exploration. With AI, users can drop in a field that doesn't exist yet and the AI derives it, and their
exploration history forms branching "Data Threads". Users connect data, model it once, explore by dragging fields or
asking in plain English, and pin results to dashboards.

Every chart is a canonical **`VizSpec`**, compiled to SQL by Roy's query engine and run by DuckDB over Parquet
extracts. The AI writes **DuckDB SQL only**, runs it in a sandbox, and always shows its SQL and a data preview.

---

## 2. What already exists (don't rebuild it)

| Area | Working today |
|---|---|
| Run & infra | Aspire AppHost: Postgres, Redis and Azurite containers, a migration service, Contoso Retail seed. `dotnet run --project src/InsightFlow.AppHost` → http://localhost:5100, signed in as Dev User |
| Web (`src/InsightFlow.Web`) | Blazor Server + **Tailwind v4** + **Apache ECharts** (ADR 0028). `/workspace` (Explorer · Data · Canvas · Assistant panes, resizable/collapsible), `/slice` chart builder, Home dataset list, dark mode. Tailwind primitives in `Components/Ui` (Button, Pane, DataTable, Alert, Spinner, EmptyState, Icon) |
| Explorer | My Workspace + Shared roots. OS drag-and-drop of files and folders with progress bars, drag-to-move, rename/delete, download, "Create dataset" (CSV/Parquet → Parquet extract) |
| Charts | `EChartsOptionBuilder` (bar incl. stacked/horizontal, line, area, point, pie/donut, heatmap; table via `DataTable`), golden-tested; animations, morphing between marks, zoom, interactive legend, PNG export. `VizRenderer` picks the renderer |
| Web → services | Typed clients (`WorkspaceApiClient`, `QueryApiClient`, `AgentApiClient`, `WorkspaceUploadClient`), `ServiceCallCredentials` (user identity forwarded), a BFF proxy for uploads/downloads (`/bff/...`, CSRF header `X-InsightFlow-Request`) |
| Api (`src/InsightFlow.Api`) | `/api/v1/workspace/*` (tree, upload, download, move, delete, datasets, create dataset), `/api/v1/connections/*` (create, test, discover, trigger extract), `/api/v1/extract-runs/{id}`, `/api/v1/me` |
| Connectors | **CSV, Parquet, SQL Server** done. **Excel, PostgreSQL, MySQL, Oracle, MongoDB, Cosmos DB** are registered stubs with implementation notes. `ConnectorContractTests` base class |
| Worker | Quartz (clustered, Postgres store). `ExtractRunProcessor` claims runs with `SKIP LOCKED` |
| Security | Dev auth handler locally (Entra External ID config placeholders for the cloud). Tenant from claim; roles Viewer < Explorer < Creator < TenantAdmin; policies `CanView`/`CanExplore`/`CanCreate`/`CanAdministerTenant` |
| Engine (Roy) | `POST /api/v1/query/viz` and `/preview` (cached in Redis). `POST /api/v1/agent/analyst` (SSE) and `/derived-field` |
| CI | `.github/workflows/ci.yml` (build, unit, architecture, eval self-test, integration). Manual `AI evals` workflow |

---

## 3. Ownership and boundaries

**You own:**
- `src/InsightFlow.Web`
- `src/InsightFlow.Api`
- `src/InsightFlow.Connectors`
- `src/InsightFlow.Worker`
- their tests (`tests/InsightFlow.Web.Tests`, `tests/InsightFlow.Connectors.Tests`, your parts of `tests/InsightFlow.IntegrationTests`)
- tenancy administration, role management and row-level-security features

**Roy owns — ask before changing:**
- `InsightFlow.Domain` (VizSpec, semantic model, threads, workspace rules)
- `InsightFlow.Contracts` (DTO shape changes)
- `InsightFlow.Persistence` (entities, migrations)
- `InsightFlow.Query` / `QueryService`, `InsightFlow.Agents` / `AgentService`
- `AppHost`, `ServiceDefaults`, `evals/`, the architecture tests, `Directory.*.props`

When your feature needs one of these changes (a new entity, a new DTO, a new `Mark`), write a short proposal in
the PR description or an issue: what you need, the proposed shape, and why. Roy reviews it (CODEOWNERS enforces this).
Add contracts; don't weaken existing ones.

**Dependency rules** (the architecture tests will fail the build if you break them):
- `Web` → `Contracts` + `ServiceDefaults` only. From Domain it may use only `InsightFlow.Domain.Viz`. Web never
  references Query, Agents, Persistence or Connectors, and talks to services over HTTP.
- `Api` / `Worker` → their library + `Persistence` + `ServiceDefaults`. `Connectors` → `Domain` + `Contracts`.
- Database drivers only in `Connectors`. UI/JS/CSS and npm only in `Web`.

---

## 4. Non-negotiables (also in `.cursor/rules/`)

- **No Python**, anywhere. **No secrets** in code, config, tests or screenshots. Use user secrets, Aspire parameters or Key Vault.
- **Never log** row data, prompts containing data samples, or credentials. Log ids, counts and durations.
- **Tenant isolation:**
  - The tenant comes only from `ITenantContext` / `ICurrentTenant`, never from a request body or route.
  - New tenant-owned data implements `ITenantOwned`.
  - Hand-written SQL filters `tenant_id`.
  - Every new endpoint has a cross-tenant integration test.
- **Authorization** goes through `InsightFlowPolicies` (`.RequireAuthorization(InsightFlowPolicies.CanCreate)`), never role strings.
- **AI-generated SQL runs only through `ISqlSandbox`** (in AgentService); the Web never executes SQL.
- **Credentials** are stored only as a `SecretReference` through `ISecretStore`.
- **No CDN scripts.** Front-end packages are pinned in `src/InsightFlow.Web/package.json` and vendored at build.
  No component libraries without Roy's agreement; Tailwind primitives live in `Components/Ui`.
- `CancellationToken` everywhere; no `.Result` / `.Wait()`. Minimal APIs with `TypedResults` + ProblemDetails.
  Options validated on start. New DTOs added to `ContractsJsonContext`.
- **Blazor gotcha:** a handler inside a child component re-renders only that child. If it changes parent state, the
  parent must call `StateHasChanged()` (see `ExplorerPane`).
- **Zero warnings.** Fix the cause. Any `#pragma`/`NoWarn` needs a comment giving the reason.

---

## 5. Milestones (in this order unless Roy re-prioritises)

Each milestone ends with: a build at zero warnings, green tests, a browser check for UI, a PR with the checklist, and
a short summary (what was built, what was verified, what was deferred). Use `git grep -n "TODO(dev2)"` to find the
exact spots.

### D2-M0 — Orientation (no feature code)
- Clone, build, and run the AppHost. Reproduce the vertical slice: upload a CSV → Create dataset → chart on `/workspace`.
- Run all tests. Read the docs listed in §0.
- Replace the `@roy` / `@dev2` placeholders in `.github/CODEOWNERS` with the real GitHub handles (PR to Roy).
- Output: a written plan for D2-M1…M3, plus questions for Roy.

### D2-M1 — Connectors (quick wins with the contract suite)
- Implement **PostgreSQL**, **MySQL** and **Excel** (packages already referenced; notes in `Stubs/StubConnectors.cs`).
  Use `SqlServerConnector` as the model. Stream into `IExtractWriter`; never buffer whole tables.
- Each connector gets a test class inheriting `ConnectorContractTests` (8 contract tests for free). Database tests
  skip unless an environment variable holds a connection string (pattern: `SqlServerConnectorTests`). Optionally,
  run throwaway containers via Testcontainers or the AppHost — agree with Roy first.
- Excel: one `SourceTable` per worksheet. Remove the 501 in "Create dataset" for `.xlsx` once it works.
- Then **Oracle**, **MongoDB** and **Cosmos DB**. Document stores: sample `ConnectorOptions.DocumentSampleSize`
  documents, flatten nested objects to dotted columns, store arrays as JSON text (D13).

### D2-M2 — Connections UI and scheduled refresh
- **Api:**
  - Update/delete connections and secret rotation (a new `SecretReference`; the old one is deleted).
  - List a connection's extract definitions and runs.
  - A schedule (cron) on `ExtractDefinition`. This needs a Persistence change, so propose it to Roy.
- **Worker:** a Quartz job per scheduled definition that enqueues an `ExtractRun` (reuse `ExtractRunProcessor`).
  Must be idempotent and safe across replicas.
- **Web:** a Connections page:
  - list, create (the SQL Server/Postgres/MySQL form; the secret is entered once and never displayed again), test, discover tables
  - choose tables to extract → dataset items land in a chosen folder
  - schedule editor; run history with status

### D2-M3 — Visual builder (shelves) and saved charts
- **Data pane:**
  - Show semantic-model fields (dimensions/measures, display names, types) instead of raw columns. You need the model
    for a dataset version; ask Roy for an endpoint (e.g. `GET /api/v1/query/model?datasetVersionId=`).
  - Drag fields onto **shelves** (X, Y, Color, Size, Label) with aggregation and time-unit pickers.
  - Filters UI for every `FilterSpec` type: equals, in, range, relative date, top-N.
  - Sort.
  - Keep `SliceBuilder` working, or refactor it into the builder.
- **Saved charts → Workbooks:**
  - A workbook is a content item holding one or more named `VizSpec`s. The `Workbook` kind is reserved in `ContentKind`.
  - The persistence entity, DTOs and Api CRUD need Roy's review first.
  - In the Explorer, opening a workbook loads it.
- **ECharts:** facets (matrix coordinate system), size/label channels, and number/date formats from the semantic model. Add golden tests for
  every new case in `EChartsOptionBuilderTests`. A **new `Mark`** goes through Roy (the compiler and validator must
  support it).

### D2-M4 — Dashboards
- A Dashboard content item: a responsive grid of tiles, each tile a saved chart (VizSpec) or text.
- Edit mode: add, resize, move, remove tiles. View mode: tiles load in parallel and show cache badges.
- Dashboard-level filters applied to every tile (append `FilterSpec`s before querying).
  TODO: cross-filtering by clicking or brushing a mark (ECharts events forwarded to .NET) — plan it with Roy.

### D2-M5 — Data Threads and AI UI
- **Thread view:**
  - Show the DAG of dataset versions for a thread: nodes, the SQL and prompt behind each, and saved charts.
  - Branch from any node.
  - You need read endpoints for threads; ask Roy (Domain/Persistence/AgentService are his).
- **Derived-field UI:** in the builder, typing a field name that doesn't exist + a hint → `POST /api/v1/agent/derived-field`.
  Show the SQL, explanation and preview, and **ask the user to accept** before switching the chart to the new version.
- **Assistant:**
  - Render the `AgentEvent` stream better (markdown answer, collapsible SQL, preview table).
  - "Show on canvas" for chart proposals.
  - Conversation history per thread.

### D2-M6 — Tenancy, identity and roles
- **Entra External ID end to end** in a test tenant (Roy provides it):
  - The app registrations (docs/deploy.md).
  - The `tenant_id` claim (custom claims provider or directory extension).
  - Verify `AddInsightFlowWebSecurity` and `ServiceCallCredentials` token acquisition (both marked `TODO(dev2)`).
- **Tenant onboarding:** an admin flow that creates a tenant, its Shared root and its first TenantAdmin. It replaces the
  lazy provisioning in `WorkspaceService`.
- **Role management** screens: invite users, assign Viewer/Explorer/Creator/TenantAdmin (`CanAdministerTenant`).
- **Row-level security** rules per dataset — design with Roy first, because enforcement belongs in the query engine.

### D2-M7 — Explorer and platform hardening
- Explorer: trash/restore (soft delete exists), per-folder sharing on top of `IContentPermissionEvaluator`, search,
  multi-select, keyboard navigation, live refresh, bulk zip download.
- A real `IUploadScanner` (malware scanning) before untrusted public uploads.
- Web scale-out: a persistent Data Protection key ring and sticky sessions (docs/deploy.md "Scaling the Web app").
- A GitHub Actions deploy workflow with OIDC and a protected `production` environment. **Roy approves any deploy.**

### Out of scope (needs a separate prompt / Roy's go-ahead)
Billing, embedding/sharing outside the tenant, subscriptions/alerts/exports, live query pushdown, an MCP server,
changes to the AI sandbox or compiler internals.

---

## 6. How-tos (short; details in docs/handoff-dev2.md)

- **New API endpoint:**
  - Use `src/InsightFlow.Api/<Feature>/<Feature>Endpoints.cs` with `Map<Feature>Endpoints(this RouteGroupBuilder)`,
    mounted under `/api/v1/<feature>`.
  - Use DTOs from Contracts and policies from `InsightFlowPolicies`; throw `ApiProblemException` for errors.
  - Add an integration test with `Caller.NewTenant()` and a cross-tenant check.
- **New Web page:** `Components/Pages/<Name>.razor` with `@page`, using typed clients (never `HttpClient` directly).
  Catch `ServiceCallException` and render `<Alert>`. Render charts with `<VizRenderer Spec=… Result=…/>`.
- **New connector:** see D2-M1 and `ConnectorContractTests`.
- **New Quartz job:** `IJob` with `ValueTask Execute(IJobExecutionContext, CancellationToken)`, registered in
  `Worker/Program.cs`. Tenant scope comes from `JobCurrentTenant`.
- **Golden files:** run the test, review `Golden/*.received.*`, then accept (rename, or `INSIGHTFLOW_ACCEPT_GOLDEN=1`).
- **Migration (Roy reviews):**

  ```bash
  dotnet ef migrations add <Name> --project src/InsightFlow.Persistence
  ```

---

## 7. Commands

```bash
dotnet build InsightFlow.slnx
```

```bash
dotnet test --solution InsightFlow.slnx
```

```bash
dotnet run --project src/InsightFlow.AppHost
```

- Single project: `dotnet test --project tests/InsightFlow.Connectors.Tests`.
- CSS watch: `npm run watch:css` in `src/InsightFlow.Web`.
- The integration tests need Docker running.
- If the AppHost times out starting DCP on Windows, delete `%USERPROFILE%\.dcp\state.elevated`.

---

## 8. Instructions specifically for the AI assistant (Cursor)

- Start every task by reading `CLAUDE.md`, `docs/handoff-dev2.md` and the files you will change. Search the codebase
  for an existing pattern before inventing one, and match the surrounding style and comment density.
- Propose a plan and wait for the developer's OK before large edits. Flag any change to Roy's areas (§3) explicitly.
- Verify package versions and APIs on the web before using them. Never invent a method or a version number.
- After edits:
  - Run `dotnet build InsightFlow.slnx` and the relevant test projects, and fix warnings and failures.
  - For UI, run the app and check the page.
  - Report honestly what was and wasn't verified.
- Never commit, push, deploy, create Azure resources, delete data, or edit secrets unless the developer explicitly
  asks for that specific action. Show `git diff --stat` before a commit.
- Never put real credentials, connection strings or customer data in code, tests, prompts or logs.
- Leave `TODO(dev2)` markers for deferred work, and `TODO(roy)` where Roy's input is needed.
