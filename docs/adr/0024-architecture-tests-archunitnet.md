# 0024. Architecture tests with ArchUnitNET

- **Status:** Accepted
- **Date:** 2026-10-08
- **Deciders:** Roy (tech lead)

## Context

The dependency rules (Domain → nothing, Web → Contracts + ServiceDefaults only, etc.) must be enforced, not just documented. NetArchTest is unmaintained.

## Decision

- `TngTech.ArchUnitNET.xUnitV3` for type-level rules (e.g. Web uses only `InsightFlow.Domain.Viz` types from Domain).
- Project-graph tests read the csproj files (`ProjectGraphTests`).
- Package-placement tests check that AI SDKs live only in Agents, DB drivers only in Connectors, DuckDB only in Query/Connectors/Agents, and UI assets only in Web (`PackagePlacementTests`).

## Consequences

+ Layering violations fail CI. - New projects must be added to the rule tables.
