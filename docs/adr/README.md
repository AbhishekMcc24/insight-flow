# Architecture decision records

One record per decision. 0001–0015 are the locked decisions D1–D15 of the foundation prompt
(`docs/prompts/01-foundation.md`). 0016+ were made while building. To change a decision, add a new ADR that supersedes
the old one; don't rewrite history.

| ADR | Decision | Status |
|---|---|---|
| [0001](0001-multi-tenant-saas.md) | Multi-tenant SaaS (D1) | Accepted |
| [0002](0002-runtime-dotnet10.md) | Runtime: .NET 10, C# 14 (D2) | Accepted |
| [0003](0003-orchestration-aspire.md) | Orchestration with Aspire 13 (D3) | Accepted |
| [0004](0004-hosting-azure-container-apps.md) | Hosting on Azure Container Apps (D4) | Accepted |
| [0005](0005-vizspec-contract.md) | VizSpec as the single chart contract (D5) | Accepted |
| [0006](0006-analytical-engine-duckdb.md) | DuckDB over Parquet extracts (D6) | Accepted |
| [0007](0007-ai-writes-duckdb-sql-only.md) | AI writes DuckDB SQL only, in a sandbox (D7) | Accepted |
| [0008](0008-ai-runtime-agent-framework.md) | AI runtime: Agent Framework on IChatClient (D8) | Accepted |
| [0009](0009-ui-blazor-tailwind-vega-lite.md) | UI: Blazor Web App + Tailwind CSS + Vega-Lite (D9, amended) | Accepted (amends D9) |
| [0010](0010-metadata-postgres-efcore.md) | Metadata in PostgreSQL via EF Core 10 (D10) | Accepted |
| [0011](0011-query-cache-redis.md) | Query result cache in Redis (D11) | Accepted |
| [0012](0012-data-threads-dag.md) | Data Threads as a DAG of immutable dataset versions (D12) | Accepted |
| [0013](0013-data-sources-v1.md) | Data sources v1: everything ingests to Parquet (D13) | Accepted |
| [0014](0014-identity-entra-external-id.md) | Identity: Entra External ID; development handler locally (D14) | Accepted |
| [0015](0015-background-jobs-quartz.md) | Background jobs with Quartz.NET (D15) | Accepted |
| [0016](0016-workspace-explorer.md) | In-app Workspace Explorer (folders + drag-and-drop uploads) | Accepted |
| [0017](0017-assertions-shouldly.md) | Assertion library: Shouldly | Accepted |
| [0018](0018-golden-files-in-repo.md) | In-repo golden-file helper instead of Verify | Accepted |
| [0019](0019-key-vault-publish-only.md) | Key Vault only in publish mode; local secret store in development | Accepted |
| [0020](0020-migration-service.md) | Dedicated migration service | Accepted |
| [0021](0021-aspire-nuget-dcp.md) | Resolve DCP and the dashboard from NuGet | Accepted |
| [0022](0022-sandbox-process-model.md) | SQL sandbox runs in-process | Accepted |
| [0023](0023-azure-data-auth.md) | Azure data-plane authentication (Postgres password, Redis access key) | Accepted (interim) |
| [0024](0024-architecture-tests-archunitnet.md) | Architecture tests with ArchUnitNET | Accepted |
| [0025](0025-ai-provider-sdks.md) | AI provider SDKs: OpenAI SDK for Azure OpenAI, official Anthropic SDK | Accepted |
| [0026](0026-duckdb-in-agents-and-interface-refinements.md) | DuckDB in Agents; refinements to the prompt's interfaces | Accepted |
| [0027](0027-web-bff-proxy.md) | Web BFF proxy for uploads/downloads; services stay internal | Accepted |
