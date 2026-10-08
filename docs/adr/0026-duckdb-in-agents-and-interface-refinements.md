# 0026. DuckDB in Agents; refinements to the prompt's interfaces

- **Status:** Accepted
- **Date:** 2026-10-08
- **Deciders:** Roy (tech lead)

## Context

While building, a few of the prompt's starting-point interfaces needed more information, and the sandbox needed DuckDB directly.

## Decision

- `InsightFlow.Agents` references DuckDB (sandbox + guard); the package-placement test allows it.
- `IQueryExecutor.ExecuteAsync(CompiledQuery, IReadOnlyList<DatasetVersion> sources, ct)`: a compiled query can span several dataset versions (joins), so the executor receives all sources.
- `IExtractStore.SaveAsync(TenantId, Guid datasetVersionId, Stream, ct)`: the caller assigns the version id first, so the blob path is deterministic and immutable. `LocalRoot` is exposed for DuckDB's `allowed_directories`.
- `IQueryDialect` gained `DateTrunc(unit, expr, dateOnly)` (DuckDB's `date_trunc` returns TIMESTAMP; date fields cast back to DATE) and `ApplyLimit`.
- `IModelRouter` has both `Resolve(tenant, task)` → `ModelRoute(Provider, Size, ModelId)`, for telemetry and evals, and `GetClient(tenant, task)` → `IChatClient`, plus `AvailableRoutes`.

## Consequences

+ The contracts reflect real needs; changes are additive in spirit. - Code written against the prompt's sketches needs these signatures.
