# 0001. Multi-tenant SaaS (D1)

- **Status:** Accepted
- **Date:** 2026-10-08
- **Deciders:** Roy (tech lead)

## Context

Insight Flow is sold as a service. Running one deployment per customer would multiply operations cost and slow down releases.

## Decision

One deployment serves many tenants.
- Every tenant-owned metadata row implements `ITenantOwned` and carries a strongly typed `TenantId`.
- EF Core applies a named global query filter `"tenant"` to every tenant-owned entity. `SaveChanges` throws `TenantIsolationException` when a write targets another tenant.
- Blob paths are prefixed per tenant: `tenants/{tenantId}/extracts/{datasetVersionId}.parquet` and `tenants/{tenantId}/files/{storedFileId}`. User text never forms part of a blob path.
- Hand-written SQL (recursive CTEs in `WorkspaceTreeQueries`) filters on `tenant_id` explicitly.

## Consequences

+ Cheap onboarding and a single upgrade path.
- Isolation depends on code discipline. It is enforced by tests at four layers: `TenantIsolationTests` (DB), the HTTP-level workspace and query tests, and the agent tests.
- Noisy neighbours are possible on shared compute; per-tenant token budgets cover AI (ADR 0008). TODO(dev2): per-tenant query quotas.
