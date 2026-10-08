# 0005. VizSpec as the single chart contract (D5)

- **Status:** Accepted
- **Date:** 2026-10-08
- **Deciders:** Roy (tech lead)

## Context

The UI, the AI and saved workbooks all describe charts. If each had its own format, the charts the AI proposes would drift from what users can build.

## Decision

- One versioned record, `VizSpec` (`InsightFlow.Domain.Viz`): mark, `VizEncoding` (x, y, color, size, facet, label as `FieldRef`s with aggregation and time unit), a polymorphic `FilterSpec` (`equals`, `in`, `range`, `relativeDate`, `topN`, discriminator `"type"`), sort and limit (≤ 50,000).
- `SchemaVersion` starts at 1. A breaking change bumps it and adds an upgrader.
- `VizSpecValidator` checks the spec against the semantic model (fields exist, aggregations are legal for the type, channel requirements per mark, sort on encoded fields).
- Serialization uses the source-generated `VizJsonContext` / `ContractsJsonContext`.
- The record was renamed from `Encoding` to `VizEncoding` to avoid clashing with `System.Text.Encoding` (approved by Roy).

## Consequences

+ The compiler (ADR 0006), the agent tool `ProposeChart` and the Vega-Lite builder (ADR 0009) all speak one language.
- Every new chart feature starts with a contract change and validator rules.
