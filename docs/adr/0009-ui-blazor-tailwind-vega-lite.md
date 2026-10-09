# 0009. UI: Blazor Web App + Tailwind CSS + Vega-Lite (D9, amended)

- **Status:** Accepted (amends D9). The chart-renderer part is superseded by ADR 0028 (Apache ECharts).
- **Date:** 2026-10-08
- **Deciders:** Roy (tech lead)

## Context

The original prompt named Telerik UI for Blazor, with Telerik Chart as the main renderer. Roy corrected this: the UI stack is **Tailwind CSS**, and **Vega-Lite is the only chart renderer** (see docs/prompts/01-foundation.md errata).

## Decision

- Blazor Web App, Interactive Server render mode.
- Tailwind CSS v4 (`@tailwindcss/cli`), built by MSBuild targets in `InsightFlow.Web.csproj`: `npm ci` → Tailwind build → copy the vendored `vega`, `vega-lite` and `vega-embed` bundles into `wwwroot/lib/vega`. Versions are pinned exactly in `package.json`, and `SkipNpm=true` skips the step.
- No component library: small Tailwind primitives live under `Components/Ui`.
- Charts: `VegaLiteSpecBuilder` (pure C#, golden-tested) turns `VizSpec` + `QueryResult` into Vega-Lite v6 JSON, rendered by `VegaLiteChart.razor` through `vegaInterop.js`. The Table mark uses `DataTable`.
- No CDN scripts. Only Web contains UI/JS assets (architecture test).

## Consequences

+ No licence keys or private feeds; CI needs Node only.
- Developer 2 grows the design system. Advanced marks (facets, box plot, density, selections) are TODO(dev2) in the spec builder.
