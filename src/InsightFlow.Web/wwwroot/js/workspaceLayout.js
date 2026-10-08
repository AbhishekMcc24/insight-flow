// Resizable panes for the workspace grid. Each [data-splitter="<css-var>"] handle resizes the pane before it by
// updating a CSS variable on the grid element (e.g. --pane-explorer). Widths are remembered per browser.
const STORAGE_KEY = "insightflow.panes";

export function init(grid) {
  if (!grid) return;
  const saved = read();
  for (const [name, value] of Object.entries(saved)) grid.style.setProperty(name, value);

  grid.querySelectorAll("[data-splitter]").forEach((handle) => {
    handle.addEventListener("pointerdown", (e) => {
      const variable = handle.dataset.splitter;
      const pane = handle.previousElementSibling;
      if (!pane) return;
      const startX = e.clientX;
      const startWidth = pane.getBoundingClientRect().width;
      handle.setPointerCapture(e.pointerId);
      const move = (ev) => {
        const width = Math.max(180, Math.min(640, startWidth + ev.clientX - startX));
        grid.style.setProperty(variable, `${Math.round(width)}px`);
      };
      const up = () => {
        handle.removeEventListener("pointermove", move);
        handle.removeEventListener("pointerup", up);
        write(variable, grid.style.getPropertyValue(variable));
      };
      handle.addEventListener("pointermove", move);
      handle.addEventListener("pointerup", up);
    });
  });
}

function read() {
  try { return JSON.parse(localStorage.getItem(STORAGE_KEY) ?? "{}"); } catch { return {}; }
}

function write(name, value) {
  try { localStorage.setItem(STORAGE_KEY, JSON.stringify({ ...read(), [name]: value })); } catch { /* storage unavailable */ }
}
