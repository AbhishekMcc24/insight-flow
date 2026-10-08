// Vega-Lite rendering for VegaLiteChart.razor. vega, vega-lite and vega-embed are vendored into wwwroot/lib/vega by the
// MSBuild front-end step and loaded as globals in App.razor (no CDN). Specs are produced server-side by
// VegaLiteSpecBuilder; this module only embeds/disposes them.
const views = new WeakMap();

export async function render(element, specJson) {
  if (!element) return;
  dispose(element);
  const spec = JSON.parse(specJson);
  const result = await window.vegaEmbed(element, spec, { actions: false, renderer: "svg" });
  views.set(element, result);
}

export function dispose(element) {
  const result = element && views.get(element);
  if (result) {
    result.finalize();
    views.delete(element);
  }
}
