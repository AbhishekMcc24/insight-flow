# 0022. SQL sandbox runs in-process

- **Status:** Accepted
- **Date:** 2026-10-08
- **Deciders:** Roy (tech lead)

## Context

D7 required proof that cancellation interrupts a long DuckDB query, with a process-isolated sandbox as the fallback if it did not.

## Decision

- `DuckDbSqlSandbox` runs in-process. The query runs on a worker thread with `ct.Register(command.Cancel)` (→ `duckdb_interrupt`) plus `CancelAfter(timeout)`.
- `SandboxTests` proves that a long-running query (a cross join over `range(100000000000)`) stops promptly when cancelled (1 s) or when the sandbox timeout fires.
- `ProcessIsolatedSqlSandbox` was therefore **not** built. The `ISqlSandbox` seam allows adding it later (e.g. for memory isolation) without touching callers.

## Consequences

+ Lower latency and simpler deployment. - A DuckDB crash would take down the AgentService process; mitigated by memory_limit and row caps. Revisit if crashes are observed.
