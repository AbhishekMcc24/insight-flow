# Handoff to Developer 2

Welcome. This repository is the **foundation** of Insight Flow. Your areas have a skeleton, the contracts, one working
reference path each, and `TODO(dev2)` markers wherever product work starts. Read [CLAUDE.md](../CLAUDE.md) (rules) and
[architecture.md](architecture.md) (how it fits together) first. The *why* behind each choice is in [adr/](adr/README.md).
Your milestones, and the brief for your AI assistant (Cursor), are in [prompts/02-developer2.md](prompts/02-developer2.md).
Cursor also loads the always-on rules in `.cursor/rules/`.

## What you own

| Area | Project(s) | State today |
|---|---|---|
| UI | `src/InsightFlow.Web` | Tailwind shell, workspace grid, Explorer (drag-and-drop), `/slice` chart builder, Assistant chat, ECharts renderer |
| Public API | `src/InsightFlow.Api` | Workspace Explorer endpoints, connections + extracts (create/test/discover/trigger), extract runs |
| Connectors | `src/InsightFlow.Connectors` | CSV, Parquet, SQL Server done; Excel, PostgreSQL, MySQL, Oracle, MongoDB, Cosmos DB stubs |
| Jobs | `src/InsightFlow.Worker` | Quartz (clustered), extract-run processor |
| Tenancy admin, roles, RLS | ServiceDefaults policies + Persistence | Plumbing only: roles, policies, tenant filter, dev auth |

Roy owns Domain, Query/QueryService, Agents/AgentService and the evals. Changes there go through him (CODEOWNERS).

## Local setup

1. Install the .NET SDK 10.0.401+, Node 22+ and Docker Desktop. Optionally install the Aspire CLI 13.6+.
2. Clone, then build. The first build runs `npm ci` and the Tailwind build for the Web project.

   ```bash
   dotnet build InsightFlow.slnx
   ```

3. Start everything: Postgres, Redis and Azurite containers, migrations + Contoso seed, all services and the dashboard.

   ```bash
   dotnet run --project src/InsightFlow.AppHost
   ```

4. Open http://localhost:5100. You are signed in as **Dev User** of the **Contoso Retail** tenant (development auth
   handler, roles Creator + TenantAdmin). `Shared/Sample Data` already contains `retail_sales.csv` and its dataset.
5. Optional AI: set AppHost user secrets (README "AI provider keys"). Without keys, the Assistant reports "No AI
   provider is configured" and everything else works.

Tests: `dotnet test --solution InsightFlow.slnx`. The integration tests need Docker. While editing styles, run
`npm run watch:css` in `src/InsightFlow.Web`.

If the AppHost fails with a DCP timeout on Windows, delete `%USERPROFILE%\.dcp\state.elevated` (ADR 0021).

## Contracts you code against

| Contract | Where | Notes |
|---|---|---|
| `VizSpec`, `VizEncoding`, `FieldRef`, `FilterSpec` (+ subtypes), `SortSpec`, enums | `InsightFlow.Domain.Viz` | The only Domain types Web may use (architecture test) |
| `VizQueryRequest/Response`, `PreviewRequest/Response`, `QueryResult`, `QueryColumn`, `ColumnType` | `Contracts/Query` | Result columns are named after channels (`x`, `y`, `color`…) and carry the `FieldRef` |
| Workspace DTOs (`FolderDto`, `ContentItemDto`, `FolderContentsResponse`, `UploadResponse`, `DatasetSummaryDto`, …) | `Contracts/Workspace` | |
| Connections DTOs | `Contracts/Connections` | |
| `AnalystRequest`, `AgentEvent` (+ `AgentEventTypes`), `DerivedFieldRequest/Response` | `Contracts/Agents` | SSE event names = `AgentEventTypes` |
| `ContractsJsonContext` | `Contracts` | Add every new DTO here (source-generated JSON) |
| `IDataSourceConnector`, `IExtractWriter`, `ConnectorCapabilities` | `Connectors/Abstractions.cs` | |
| `ITenantContext`, `InsightFlowPolicies`, `InsightFlowRoles` | ServiceDefaults `Security/` | Never read the tenant from the request body |
| `ISecretStore`, `SecretReference` | `Domain/Security` | Credentials are stored only as references |
| Typed clients (`WorkspaceApiClient`, `QueryApiClient`, `AgentApiClient`, `WorkspaceUploadClient`) | `Web/Services` | Errors surface as `ServiceCallException` (message safe to show) |

### Endpoints today

| Service | Routes |
|---|---|
| Api `/api/v1/workspace` | `GET roots`, `GET folders/{id}/children`, `GET datasets`, `POST folders`, `PATCH folders/{id}`, `PATCH items/{id}`, `POST move`, `DELETE folders/{id}`, `DELETE items/{id}`, `POST folders/{id}/files` (streaming multipart, a `path` field before each file), `GET items/{id}/content`, `POST items/{id}/dataset` (202) |
| Api `/api/v1/connections` | `GET /`, `POST /`, `POST {id}/test`, `GET {id}/tables`, `POST {id}/extracts` |
| Api `/api/v1/extract-runs` | `GET {runId}` |
| Api `/api/v1/me` | current user |
| QueryService `/api/v1/query` | `POST viz`, `POST preview` |
| AgentService `/api/v1/agent` | `POST analyst` (SSE), `POST derived-field` |
| Web `/bff/workspace` | `POST folders/{id}/files` (requires header `X-InsightFlow-Request`), `GET items/{id}/content` |

Scalar API docs are served in Development at `/scalar` on each API service. The ports are shown in the Aspire dashboard.

## How to…

### Add a connector (e.g. PostgreSQL)

1. Replace the stub in `src/InsightFlow.Connectors/Stubs/StubConnectors.cs` with a real class, e.g.
   `Databases/PostgreSqlConnector.cs`. Use `Databases/SqlServerConnector.cs` as the model: settings from
   `ConnectionProfile.Settings`, the password from `ISecretStore` via the profile's `SecretReference`, and `DiscoverAsync`
   from the catalog. `ExtractAsync` streams rows into `IExtractWriter` (`BeginTableAsync` → append rows). Never buffer
   a whole table. The stub's comment has driver-specific notes (quoting, SSL, type mapping).
2. Keep the registration in `ConnectorsServiceCollectionExtensions` (one `AddSingleton<IDataSourceConnector, …>`).
3. Add tests: `public sealed class PostgreSqlConnectorTests : ConnectorContractTests`. Implement `ExpectedKind`,
   `ExpectedTableId`, `ExpectedRowCount`, `ExpectedColumns`, `CreateConnector()`, `CreateValidProfileAsync()` and
   `CreateUnreachableProfile()`. All eight contract tests then run for free: kind, test (valid / unreachable without
   throwing), discover, extract all rows and columns, `MaxRows`, unknown table, and cancellation. For a database that isn't always available, follow `SqlServerConnectorTests`: skip unless an
   environment variable such as `INSIGHTFLOW_TEST_POSTGRES` holds a connection string.
4. Mongo/Cosmos: sample `ConnectorOptions.DocumentSampleSize` documents, flatten nested objects to dotted columns,
   and store arrays as JSON text (v1 rule, D13).

### Add an API endpoint

1. Put the endpoint in a feature folder under `src/InsightFlow.Api/<Feature>/`. Use a `static class <Feature>Endpoints`
   with `Map<Feature>Endpoints(this RouteGroupBuilder group)`, mounted in `Program.cs` under
   `MapGroup("/api/v1").MapGroup("/<feature>")`.
2. Use `TypedResults` and return DTOs from `InsightFlow.Contracts` (add them to `ContractsJsonContext`). Errors use
   `ApiProblemException` (→ ProblemDetails). Validation failures use `TypedResults.ValidationProblem`.
3. Authorize with policies (`.RequireAuthorization(InsightFlowPolicies.CanCreate)`), never role strings. The tenant
   comes from `ITenantContext`, and EF filters by tenant automatically. For raw SQL, filter on `tenant_id` yourself.
4. Take a `CancellationToken` and pass it all the way down. Log ids and counts only.
5. Add an integration test in `tests/InsightFlow.IntegrationTests` (see `WorkspaceApiTests`). Use `Caller.NewTenant()`
   for isolation, and add a cross-tenant check.

### Add a Web page that renders a VizSpec

```razor
@page "/my-chart"
@inject QueryApiClient Query

@if (_response is not null)
{
    <VizRenderer Spec="_spec" Result="_response.Result" Class="h-96" />
}

@code {
    private VizSpec _spec = default!;
    private VizQueryResponse? _response;

    [SupplyParameterFromQuery] public Guid DatasetVersionId { get; set; }

    protected override async Task OnParametersSetAsync()
    {
        _spec = new VizSpec(VizSpec.CurrentSchemaVersion, DatasetVersionId, Mark.Bar,
            new VizEncoding(new FieldRef("region"), new FieldRef("revenue", Agg.Sum)), []);
        _response = await Query.VizAsync(new VizQueryRequest(_spec), CancellationToken.None);
    }
}
```

`VizRenderer` picks ECharts or `DataTable`. Catch `ServiceCallException` and show `<Alert Message=…/>`. For a
full builder, reuse `Components/Workspace/SliceBuilder.razor`.

### Add a mark or encoding to the ECharts builder

1. If it is a new mark, add it to `Mark` (Roy's area: the compiler and validator must support it) — talk to Roy first.
2. In `src/InsightFlow.Web/Charts/EChartsOptionBuilder.cs`, add a case in `Build` (see `Cartesian`, `Pie`, `Heatmap`, `Scatter`). Formatters that need JS functions go in `wwwroot/js/echartsInterop.js`.
3. Add a golden test in `tests/InsightFlow.Web.Tests/EChartsOptionBuilderTests.cs`. Run it once, review
   `Golden/<name>.received.json`, and accept it (rename, or rerun with `INSIGHTFLOW_ACCEPT_GOLDEN=1`).

### Add a Tailwind UI component

Put it in `src/InsightFlow.Web/Components/Ui/` (see `Button.razor`, `Pane.razor`). Use the design tokens from
`Styles/app.css` (`brand-*`, slate neutrals, `dark:` variants), shared class strings in `UiStyles`, and no inline
`<style>`. The Tailwind build scans `Components/**` and `wwwroot/js/**` automatically. JS interop goes in
`wwwroot/js/*.js` as ES modules, imported with `JS.InvokeAsync<IJSObjectReference>("import", "./js/x.js")`. Never
load scripts from a CDN.

**Gotcha:** an event handler written inside a child component re-renders only that child. If it changes a parent's
state, the parent must call `StateHasChanged()` (see `ExplorerPane`).

### Add a background job

Write an `IJob` (Quartz 4: `ValueTask Execute(IJobExecutionContext context, CancellationToken ct)`) in
`src/InsightFlow.Worker` and register it in `Program.cs` inside `AddQuartz(...)`. Inside a job, use
`JobCurrentTenant` for tenant scope (see `ExtractRunProcessor`). Jobs run clustered, so make them idempotent.

### Add a database migration

```bash
dotnet ef migrations add <Name> --project src/InsightFlow.Persistence
```

The migration service applies it on the next run. Don't call `Migrate()` from services.

## Every `TODO(dev2)` by area

Run `git grep -n "TODO(dev2)"` for the live list.

**Web (`src/InsightFlow.Web`)**
- Workspace Data pane: semantic-model fields (dimensions/measures), drag to shelves, calculated fields (`Pages/WorkspacePage.razor`).
- Chart builder: drag fields onto shelves, filters UI, saved charts / workbooks (`Workspace/SliceBuilder.razor`).
- ECharts: facets (matrix), size/label channels, box plot, click/brush events → cross-filtering, tree-shaken bundle (`Charts/EChartsOptionBuilder.cs`, ADR 0028).
- More renderers behind `VizRenderer` (KPI cards, maps).
- Explorer: trash/restore, sharing, search, multi-select, keyboard navigation, live refresh (`Workspace/ExplorerPane.razor`).
- Assistant: Data Thread view (branching history), derived-field UI, conversation memory (`Workspace/AnalystChat.razor`).
- Home: recent threads, workbooks, dashboards.
- Entra: verify OIDC + token acquisition end to end (`Services/ServiceCallCredentials.cs`, ServiceDefaults `SecurityExtensions.cs`).
- Before more than one replica: a persistent Data Protection key ring and sticky sessions (docs/deploy.md).

**Api (`src/InsightFlow.Api`)**
- Workspace: trash/restore, per-folder sharing, search, bulk zip download, file versioning; dataset list paging.
- Replace lazy tenant provisioning with explicit tenant onboarding.
- Replace `NoOpUploadScanner` with a real scanner before accepting untrusted public uploads.
- Connections: update/delete, secret rotation, scheduled refreshes.

**Connectors (`src/InsightFlow.Connectors`)**
- Excel (ExcelDataReader), PostgreSQL (Npgsql), MySQL (MySqlConnector), Oracle (Oracle.ManagedDataAccess.Core),
  MongoDB (MongoDB.Driver), Cosmos DB (Microsoft.Azure.Cosmos). Packages are already referenced; notes are in `Stubs/StubConnectors.cs`.

**Worker**
- Scheduled refreshes: a cron per `ExtractDefinition` that enqueues an `ExtractRun`. Then subscriptions, alerts, exports.

**Tenancy, roles, security**
- Role-management screens; row-level security rules; tenant admin (ServiceDefaults `InsightFlowPolicies`, `Domain/Tenancy`).
- `tenant_id` claim in Entra External ID (custom claims provider / extension attribute) and the app registrations (docs/deploy.md).
- Workspace content: workbooks and dashboards content kinds (reserved in `ContentKind`); sharing on top of `IContentPermissionEvaluator`.

**Platform**
- Persist AI token usage in PostgreSQL for billing (`Agents/Ai/TokenBudgetChatClient.cs`, coordinate with Roy).
- Move Postgres and Redis in Azure to Entra auth (ADR 0023).
- Container Apps Job for migrations, when Aspire supports it (ADR 0020).
- Deploy workflow with GitHub OIDC and a protected environment (docs/deploy.md).

## Ground rules (short version of CLAUDE.md)

- Ask Roy before changing Domain, Query or Agents contracts. Add contracts rather than weakening them.
- No secrets in the repo. No row data, data-bearing prompts or credentials in logs.
- No Python. AI SQL runs only through `ISqlSandbox`.
- No UI/JS outside Web, and no CDN scripts.
- Zero warnings. Tests for every change; integration tests for new endpoints, including a cross-tenant check.
- Conventional Commits. A pull request needs CI green and the PR template checklist.
