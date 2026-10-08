# 0010. Metadata in PostgreSQL via EF Core 10 (D10)

- **Status:** Accepted
- **Date:** 2026-10-08
- **Deciders:** Roy (tech lead)

## Context

Metadata is relational (tenants, folders, versions, threads) and has document-shaped parts (specs, schemas, models).

## Decision

- EF Core 10 + Npgsql. JSONB for `VizSpec`, schemas and semantic models (`Conversions.cs`); snake_case columns.
- Named query filters `"tenant"` and `"soft_delete"` (ADR 0001).
- Migrations: `InitialCreate`, `ConnectionsAndExtracts`. Tool: `dotnet-ef` 10.0.12 (local tool manifest). Applied by the migration service (ADR 0020).
- `ICurrentTenant` variants: request (`DelegateCurrentTenant`), fixed, system (migrations/seeding) and job (Worker).

## Consequences

+ Strong consistency, and JSONB keeps the contracts flexible.
- Adding a migration: `dotnet ef migrations add <Name> --project src/InsightFlow.Persistence`.
