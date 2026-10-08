# 0007. AI writes DuckDB SQL only, in a sandbox (D7)

- **Status:** Accepted
- **Date:** 2026-10-08
- **Deciders:** Roy (tech lead)

## Context

Model-written code is untrusted. Running Python would mean running arbitrary code; SQL over a locked engine is something we can contain.

## Decision

- The AI never runs Python. It produces DuckDB SQL, which runs only through `ISqlSandbox` (`DuckDbSqlSandbox`).
- `SqlGuard` parses the SQL with DuckDB's own parser (`json_serialize_sql($sql::VARCHAR)`) and walks the AST. Exactly one SELECT is allowed. Table functions are limited to `range`, `generate_series` and `unnest`; `query`, `read_*`, `duckdb_*` and the like are rejected. Every non-SELECT statement fails to serialize.
- Each run gets a fresh in-memory DB. Inputs are loaded as tables `input`, `input_2`… *before* locking. Then `enable_external_access=false`, `lock_configuration=true` and `memory_limit` (default 1GB) are set, with a timeout (default 15 s) and a row cap (default 50,000).
- Results are materialized to Parquet by trusted code (`CREATE TEMP TABLE __result` + `COPY`), never by AI SQL.
- Every AI answer shows its SQL and a data preview.

## Consequences

+ A malicious-input suite covers multi-statement, ATTACH, COPY, INSTALL, file readers, comment tricks and dynamic `query()` (SqlGuardTests).
- Some legitimate DuckDB features (file readers, extensions) are off-limits to the AI by design.
