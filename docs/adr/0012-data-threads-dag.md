# 0012. Data Threads as a DAG of immutable dataset versions (D12)

- **Status:** Accepted
- **Date:** 2026-10-08
- **Deciders:** Roy (tech lead)

## Context

Exploration branches: users and the AI derive new datasets from earlier ones, and must be able to go back and fork.

## Decision

- `DatasetVersion` is immutable: Source, Extract or Derived. It records parents, SQL, prompt, Parquet URI, schema, row count and author.
- `DataThread` + `ThreadNode` link versions, saved `VizSpec`s and agent explanations; `LineageGraph` walks the DAG.
- The derived-field flow writes a new child version and a thread node.

## Consequences

+ Full lineage and reproducibility (SQL + parents).
- Storage grows with exploration. TODO(dev2): retention / garbage collection of unreferenced derived versions.
