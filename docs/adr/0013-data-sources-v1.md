# 0013. Data sources v1: everything ingests to Parquet (D13)

- **Status:** Accepted
- **Date:** 2026-10-08
- **Deciders:** Roy (tech lead)

## Context

There are many source types and little time. Querying sources live would multiply engine work.

## Decision

- `IDataSourceConnector` (Kind, Capabilities, Test, Discover, Extract) writes through `IExtractWriter`: `DuckDbExtractWriter` streams rows with the DuckDB Appender (or native readers) into Parquet (ZSTD).
- Full reference implementations: CSV, Parquet, SQL Server. Registered stubs: Excel, PostgreSQL, MySQL, Oracle, MongoDB and Cosmos DB, each with implementation notes (`TODO(dev2)`).
- Credentials are never stored in `ConnectionProfile`, only a `SecretReference` resolved through `ISecretStore`.
- `ConnectorContractTests<T>` gives every connector the same quality bar.

## Consequences

+ One query engine for every source.
- Data is as fresh as the last extract. Scheduled refresh is TODO(dev2); live pushdown is TODO(roy).
