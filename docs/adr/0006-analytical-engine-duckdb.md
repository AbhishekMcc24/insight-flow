# 0006. DuckDB over Parquet extracts (D6)

- **Status:** Accepted
- **Date:** 2026-10-08
- **Deciders:** Roy (tech lead)

## Context

Interactive BI needs fast group-bys over millions of rows without running a separate warehouse.

## Decision

- `DuckDB.NET.Data.Full` runs in-process in the QueryService (and in the Agents sandbox, ADR 0026). Extracts are immutable Parquet files in Blob storage.
- `SqlCompiler` compiles a `VizSpec` + `SemanticModel` through an `IQueryDialect`:
  - Column names come only from the model, quoted by the dialect.
  - Values are always parameters (`$p0`…).
  - Joins follow the shortest relationship path (LEFT JOIN).
  - Relative dates are resolved with `TimeProvider`.
  - Top-N uses a subquery, ORDER BY is deterministic, and `LIMIT n+1` detects truncation.
- `DuckDbQueryExecutor` opens a fresh in-memory connection per query and mounts Parquet views. It then sets `allowed_directories` to the extract cache, `enable_external_access=false` and `lock_configuration=true`.
- `BlobExtractStore` with an LRU local disk cache, since DuckDB cannot read Blob storage directly once external access is off.
- Dialects for PostgreSQL, SQL Server, MySQL and Oracle exist with golden tests only. TODO(roy): live pushdown.

## Consequences

+ 10M-row group-bys take 166–197 ms on a laptop; compilation takes about 10 µs (docs/benchmarks.md).
- QueryService replicas need local disk for the extract cache. A cold extract is downloaded on first use (seconds).
