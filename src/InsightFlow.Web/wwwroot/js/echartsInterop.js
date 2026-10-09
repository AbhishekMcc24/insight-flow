// Apache ECharts rendering for EChartsChart.razor (ADR 0028). ECharts is vendored into wwwroot/lib/echarts by the
// MSBuild front-end step and loaded as a global in App.razor (no CDN). Options are produced server-side by
// EChartsOptionBuilder; this module only adds what JSON cannot carry (number formatters) and manages the instance:
// - one ECharts instance per element, reused across updates so `universalTransition` can morph bar ⇄ pie ⇄ line;
// - a ResizeObserver resizes the chart when its pane is resized or collapsed (no window resize event fires then).
const charts = new WeakMap();

const numberFormat = new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 });
const compactFormat = new Intl.NumberFormat(undefined, { notation: "compact", maximumFractionDigits: 1 });
const formatValue = (v) => (typeof v === "number" ? numberFormat.format(v) : v ?? "—");

export function render(element, optionJson) {
  if (!element) return;
  const option = JSON.parse(optionJson);
  addFormatters(option);

  let entry = charts.get(element);
  if (!entry) {
    const chart = window.echarts.init(element, null, { renderer: "canvas" });
    let frame = 0;
    const observer = new ResizeObserver(() => {
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(() => chart.resize({ animation: { duration: 200 } }));
    });
    observer.observe(element);
    entry = { chart, observer };
    charts.set(element, entry);
  }

  // notMerge replaces the previous option entirely; series with universalTransition morph between chart types.
  entry.chart.setOption(option, { notMerge: true });
}

export function dispose(element) {
  const entry = element && charts.get(element);
  if (entry) {
    entry.observer.disconnect();
    entry.chart.dispose();
    charts.delete(element);
  }
}

function addFormatters(option) {
  if (option.tooltip) option.tooltip.valueFormatter = formatValue;
  for (const key of ["xAxis", "yAxis"]) {
    const axes = Array.isArray(option[key]) ? option[key] : option[key] ? [option[key]] : [];
    for (const axis of axes) {
      if (axis.type === "value") axis.axisLabel = { ...axis.axisLabel, formatter: (v) => compactFormat.format(v) };
    }
  }
  if (option.visualMap) option.visualMap.formatter = (v) => compactFormat.format(v);
}
