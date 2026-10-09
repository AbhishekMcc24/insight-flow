# 0028. Charts rendered with Apache ECharts (replaces Vega-Lite)

- **Status:** Accepted (supersedes the chart-rendering part of ADR 0009)
- **Date:** 2026-10-08
- **Deciders:** Roy (tech lead)

## Context

The browser walkthrough showed working but "dull" charts. Vega-Lite has no animated transitions between states, and
its interactions (zoom, brushing, legend toggling) must be written into every spec as grammar parameters. Roy asked
for modern, smooth, interactive charts. Libraries compared (October 2026, versions and licences from npm):

| Library | Licence | Outcome |
|---|---|---|
| Apache ECharts 6.1.0 | Apache-2.0 | **Chosen** |
| ApexCharts 7.8.0 | Dual. The free Community licence excludes "embedding in a product or platform used by other people" (needs OEM) | Rejected: Insight Flow is a SaaS platform |
| AG Charts 14.2.0 | MIT Community + paid Enterprise; heatmap, animations, zoom, crosshair and sync are Enterprise | Rejected: key features paywalled |
| Highcharts 13.1.1 | Commercial SaaS licence | Rejected: cost and restrictive SaaS terms |
| Chart.js 4.5.1, Plotly.js 4.1.2, Observable Plot, uPlot | MIT/ISC | Too limited for BI, or too heavy / static |

## Decision

- **Apache ECharts 6.1** (`echarts`, pinned exactly in `src/InsightFlow.Web/package.json`) is the chart renderer for
  every mark except Table (still `DataTable`).
- It is vendored by the existing MSBuild front-end step: `dist/echarts.min.js` plus its `LICENSE` and `NOTICE` go to
  `wwwroot/lib/echarts/`, loaded as a global in `App.razor`. There is no CDN.
- `Charts/EChartsOptionBuilder.cs` (pure C#, golden-tested) maps `VizSpec` + `QueryResult` to an ECharts option:
  - colour series are pivoted in C#;
  - stacked bars are ordered by total;
  - pie slices follow the SQL order;
  - time-unit buckets get readable labels via `ChartLabels`;
  - raw dates use a time axis;
  - the theme mirrors the Tailwind tokens (light and dark).
- `wwwroot/js/echartsInterop.js` keeps one instance per element and calls `setOption(option, { notMerge: true })`.
  That lets series with `universalTransition` **morph** between marks (bar ⇄ pie ⇄ line).
- The interop also adds the number formatters JSON cannot carry (`Intl.NumberFormat`), and a `ResizeObserver`
  resizes the chart when panes resize or collapse.
- Built-in interactions:
  - an axis-pointer tooltip card, and an interactive legend;
  - zoom: wheel/drag plus a slider on time axes and on category axes with more than 24 points; wheel zoom on scatter;
  - a draggable colour-scale filter on heatmaps, and PNG export;
  - large-data rendering (`large` / LTTB `sampling`) above 2,000 points;
  - ARIA descriptions.

## Consequences

- **Gains:**
  - Smooth animations and mark morphing; interaction out of the box.
  - Box plot, treemap, sankey, calendar, gauge, matrix (small multiples), chord and beeswarm are available for
    future marks without changing library.
  - Apache-2.0 is free for commercial SaaS; we keep the `NOTICE` file with the redistributed bundle.
- **Costs:**
  - The bundle is larger: ECharts full is about 369 KB gzipped, versus about 260 KB for vega + vega-lite.
    A tree-shaken custom build is a later optimisation (TODO(dev2)).
  - ECharts options are imperative, so the C# builder does more mapping than the Vega-Lite grammar needed.
  - Changing theme rebuilds the option, which resets the zoom state.
- **Next steps (TODO(dev2)):**
  - Forward click/brush events to .NET for cross-filtering and drill-down; `connect` dashboard tiles.
  - Facets via the matrix coordinate system.
  - Optionally switch theme changes to ECharts 6 `setTheme` with registered themes, keeping the zoom state.
