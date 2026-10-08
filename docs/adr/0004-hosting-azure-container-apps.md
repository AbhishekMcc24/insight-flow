# 0004. Hosting on Azure Container Apps (D4)

- **Status:** Accepted
- **Date:** 2026-10-08
- **Deciders:** Roy (tech lead)

## Context

We want managed, scale-to-demand hosting without running Kubernetes ourselves, plus managed PostgreSQL, Redis and Blob storage.

## Decision

- Azure Container Apps environment via `AddAzureContainerAppEnvironment("aca")`, publish mode only.
- Azure Database for PostgreSQL Flexible Server (`AddAzurePostgresFlexibleServer(...).RunAsContainer()`), Azure Managed Redis (`AddAzureManagedRedis(...).RunAsContainer()`), Azure Storage (`.RunAsEmulator()` → Azurite) and Key Vault (ADR 0019).
- Locally, every resource is a container or emulator. Nothing touches a subscription on `aspire run`.
- Deployment is manual: `aspire publish` to review the Bicep, then `aspire deploy` (docs/deploy.md).

## Consequences

+ The same AppHost describes local and cloud.
- Some ACA gaps need workarounds: no run-to-completion jobs (ADR 0020), and Blazor Server scale-out needs sticky sessions (docs/deploy.md).
