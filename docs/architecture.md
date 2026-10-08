# Insight Flow architecture

Insight Flow is a multi-tenant SaaS analytics product. Users drop files into a workspace, turn them into datasets
(Parquet extracts), and explore by picking fields or by asking in plain English. Every chart is a `VizSpec`, compiled
to DuckDB SQL by our own compiler. The AI writes DuckDB SQL only, and it runs only in a locked-down sandbox.

Decisions and their reasons are in [docs/adr](adr/README.md). This page shows how the pieces fit together.

## Components

```mermaid
flowchart LR
    browser([Browser])

    subgraph web["InsightFlow.Web (Blazor Server + Tailwind + Vega-Lite)"]
        ui[Pages & components<br/>Workspace · Explorer · Slice · Assistant]
        bff[BFF endpoints<br/>/bff/workspace/*]
        clients[Typed HTTP clients<br/>+ ServiceCallCredentials]
    end

    subgraph services[Internal services]
        api[InsightFlow.Api<br/>/api/v1/workspace · connections · extract-runs]
        qs[InsightFlow.QueryService<br/>/api/v1/query/viz · preview]
        as[InsightFlow.AgentService<br/>/api/v1/agent/analyst SSE · derived-field]
        worker[InsightFlow.Worker<br/>Quartz · ExtractRunProcessor]
        mig[InsightFlow.MigrationService<br/>EF migrations + dev seed]
    end

    subgraph libs[Libraries]
        domain[Domain<br/>VizSpec · SemanticModel · Threads · Workspace]
        contracts[Contracts<br/>DTOs + source-gen JSON]
        query[Query<br/>SqlCompiler · dialects · DuckDB executor · extract store · cache]
        agents[Agents<br/>model router · Analyst · SqlGuard · sandbox]
        connectors[Connectors<br/>CSV · Parquet · SQL Server · stubs]
        persistence[Persistence<br/>EF Core · tenant filters · secret store]
    end

    pg[(PostgreSQL<br/>metadata + Quartz)]
    redis[(Redis<br/>query cache · token usage)]
    blob[(Blob storage<br/>files · extracts)]
    kv[(Key Vault<br/>cloud only)]
    llm{{Azure OpenAI / Anthropic}}

    browser -- SignalR circuit --> ui
    browser -- uploads / downloads --> bff
    ui --> clients
    bff --> clients
    clients --> api & qs & as
    api --> persistence & connectors
    qs --> query
    as --> agents
    agents --> query
    worker --> connectors & persistence
    mig --> persistence
    query --> blob & redis
    connectors --> blob
    persistence --> pg
    worker --> pg
    agents --> llm
    api & worker & qs & as -.-> kv
```

**Dependency rules** (enforced by `tests/InsightFlow.Architecture.Tests`):

- Domain → nothing.
- Contracts → Domain.
- Persistence → Domain.
- Query → Domain, Contracts.
- Agents → Domain, Contracts, Query.
- Connectors → Domain, Contracts.
- Service hosts → their library + Persistence + ServiceDefaults.
- Web → Contracts + ServiceDefaults only, using only `InsightFlow.Domain.Viz` types.
- AI SDKs live only in Agents, DB drivers only in Connectors, UI/JS assets only in Web.

| Component | Responsibility |
|---|---|
| **AppHost** | Declares every resource. Containers/emulators locally; Azure resources when published (ADR 0003, 0004) |
| **ServiceDefaults** | OpenTelemetry, health checks, resilience, service discovery, authentication + tenant + policies |
| **Domain** | Pure model and rules: `VizSpec` + validator, semantic model, dataset-version DAG, workspace tree rules, `TenantId` |
| **Contracts** | HTTP DTOs with a source-generated `ContractsJsonContext` |
| **Persistence** | `InsightFlowDbContext` with `tenant` and `soft_delete` filters, a write guard, migrations, `ISecretStore` |
| **Query / QueryService** | VizSpec → SQL, DuckDB execution over cached Parquet, Redis result cache |
| **Agents / AgentService** | Model router + middleware, Analyst agent and tools, derived-field planner, SQL guard + sandbox, SSE |
| **Connectors** | Source connectors writing Parquet through the DuckDB Appender; extract pipeline |
| **Api** | Workspace Explorer (folders, uploads, downloads, create dataset), connections, extract runs |
| **Worker** | Quartz (clustered, PostgreSQL store); claims and runs queued extract runs |
| **MigrationService** | Applies migrations once; seeds Contoso Retail in Development |
| **Web** | Tailwind UI, Vega-Lite rendering, BFF proxy for files, typed clients to the services |

## Vertical slice: drop a file → chart

```mermaid
sequenceDiagram
    autonumber
    actor U as User
    participant W as Web (Explorer)
    participant B as Web BFF
    participant A as Api
    participant S as Blob (files)
    participant K as Worker
    participant X as Blob (extracts)
    participant Q as QueryService
    participant R as Redis

    U->>W: drag folder/files onto a folder
    W->>B: XHR per file (path + file, X-InsightFlow-Request)
    B->>A: stream multipart (user credentials)
    A->>S: store tenants/{t}/files/{id} (SHA-256, limits)
    A-->>W: per-file results → tree refresh
    U->>W: Create dataset
    W->>A: POST /items/{id}/dataset
    A->>A: ExtractDefinition + ExtractRun (Pending)
    K->>A: (DB) claim run FOR UPDATE SKIP LOCKED
    K->>S: read file
    K->>X: Parquet via DuckDB Appender → tenants/{t}/extracts/{version}.parquet
    K->>K: DatasetVersion(Extract) + ContentItem(Dataset)
    W->>A: poll run → Succeeded
    U->>W: Open → pick mark / fields
    W->>Q: POST /api/v1/query/viz (VizSpec)
    Q->>R: cache lookup (tenant, version, model, spec)
    Q->>Q: compile → DuckDB over local Parquet cache (locked down)
    Q->>R: store result
    Q-->>W: rows + SQL + FromCache + duration
    W->>W: VegaLiteSpecBuilder → vega-embed
```

## Derived-field flow ("drop a field that doesn't exist yet")

```mermaid
sequenceDiagram
    autonumber
    actor U as User
    participant W as Web
    participant G as AgentService
    participant P as DerivedFieldPlanner
    participant M as IChatClient (router)
    participant SG as SqlGuard
    participant SB as DuckDB sandbox
    participant X as Extract store
    participant DB as PostgreSQL
    participant C as Query engine

    U->>W: chart with unknown field "margin_pct" + hint
    W->>G: POST /api/v1/agent/derived-field (VizSpec, FieldName, Hint)
    G->>P: plan(dataset version, model, hint)
    P->>M: prompt grounded in the semantic model (no row data logged)
    M-->>P: DuckDB SQL over table `input`
    P->>SG: json_serialize_sql + AST walk (single SELECT only)
    SG-->>P: ok / rejection (→ repair loop)
    P->>SB: run (inputs loaded, then external access off + config locked, timeout, row cap)
    SB-->>P: result materialized to Parquet by trusted code
    G->>X: SaveAsync(tenant, new version id, parquet)
    G->>DB: DatasetVersion(Derived, parents=[source], SQL, prompt) + ThreadNode
    G->>C: compile & run the chart on the new version
    G-->>W: SQL, explanation, preview, new VizSpec, chart rows, thread ids
```

The **Analyst** (`POST /api/v1/agent/analyst`) uses the same pieces as tools (`DescribeModel`, `RunSql`,
`ProposeChart`) and streams `text`, `tool`, `sql` (with preview), `chart`, `error` and `done` events over SSE.

## Data Thread DAG example

Dataset versions are immutable. A thread is a path through the DAG, and any node can be branched.

```mermaid
flowchart TD
    s["v1 · Source<br/>retail_sales.csv (stored file)"]
    e["v2 · Extract<br/>Parquet, 5,000 rows"]
    d1["v3 · Derived<br/>+ margin_pct<br/>SQL: SELECT *, (revenue-cost)/revenue …"]
    d2["v4 · Derived<br/>only returned orders<br/>SQL: SELECT * FROM input WHERE is_returned"]
    d3["v5 · Derived<br/>monthly margin by region"]
    j["v6 · Derived (2 parents)<br/>join with targets.csv extract"]
    t["v7 · Extract<br/>targets.csv"]

    s --> e
    e --> d1
    e --> d2
    d1 --> d3
    d1 --> j
    t --> j

    classDef node fill:#eef3ff,stroke:#3b6ee0;
    class s,e,d1,d2,d3,j,t node;
```

Each `ThreadNode` points at a version and stores the saved `VizSpec`(s) and the agent's explanation.
`LineageGraph` answers "where did this come from?" by walking `ParentIds`.

## Deployment on Azure Container Apps

```mermaid
flowchart TB
    user([Users]) -->|HTTPS| webapp

    subgraph rg[Resource group]
        subgraph env[Container Apps environment]
            webapp["web<br/>external ingress · 1 replica"]
            apiapp["api<br/>internal · 1–5"]
            qsapp["queryservice<br/>internal · 1–5"]
            asapp["agentservice<br/>internal · 1–3"]
            wk["worker<br/>no ingress · 1–3"]
            mg["migrations<br/>1 · keep-alive"]
        end
        acr[(Container Registry)]
        law[(Log Analytics)]
        pgf[(PostgreSQL Flexible Server)]
        amr[(Azure Managed Redis)]
        st[(Storage account<br/>files · extracts)]
        kv[(Key Vault)]
    end

    entra{{Entra External ID}}
    aoai{{Azure OpenAI / Anthropic}}

    webapp -->|OIDC| entra
    webapp --> apiapp & qsapp & asapp
    apiapp & qsapp & asapp & wk & mg --> pgf
    apiapp & qsapp & asapp --> amr
    apiapp & qsapp & asapp & wk -->|managed identity| st
    apiapp & qsapp & asapp & wk & mg -->|secret refs| kv
    asapp --> aoai
    env -.-> law
    acr -.-> env
```

Built with `aspire publish` / `aspire deploy`; see [deploy.md](deploy.md). Postgres and Redis use password and access-key
auth through Key Vault for now (ADR 0023).

## Cross-cutting concerns

- **Tenancy:**
  - The tenant comes from the token claim.
  - EF applies the `tenant` filter, and `SaveChanges` checks writes.
  - Blob paths are prefixed per tenant, and every endpoint loads data in the caller's tenant.
  - Tests: `TenantIsolationTests` plus the cross-tenant checks in the workspace, query and agent tests.
- **Security:**
  - The fallback policy requires a tenant, and the role policies are defined in one place.
  - The development auth handler works only in Development.
  - The AI's SQL runs only through `ISqlSandbox`, and secrets are only ever held as references.
- **Observability:**
  - ServiceDefaults registers the `InsightFlow.*` activity sources and meters, so traces show compile → execute → cache and agent tool calls.
  - Logs carry ids, counts, durations and token usage — never row data, prompts with data, or credentials.
- **Performance:** about 10 µs to compile; 166–197 ms for group-bys over 10M rows on a laptop ([benchmarks.md](benchmarks.md)).
  Results are cached in Redis, keyed by spec + version + model.
