// The Playground canvas gestures: select, move, resize, rubber-band select, edit text in place and keyboard shortcuts.
// Blazor draws the elements; this script only follows the pointer and reports each finished gesture to .NET once, so dragging is smooth
// (no server round trip per mouse move). Positions are in canvas pixels; the canvas may be zoomed (data-scale) and snaps to a grid (data-snap).

const GRID = 12;
const MIN = 8;

export function attach(viewport, canvas, dotNet) {
  if (!canvas) return;
  if (canvas.__pg) {
    canvas.__pg.dotNet = dotNet;
    return;
  }

  const st = (canvas.__pg = { dotNet, selected: new Set(), drag: null, editing: null, marquee: null });
  const scale = () => parseFloat(canvas.dataset.scale || "1") || 1;
  const snap = () => canvas.dataset.snap === "1";
  const snapTo = (v) => (snap() ? Math.round(v / GRID) * GRID : v);
  const elementOf = (target) => (target && target.closest ? target.closest(".pg-el") : null);
  const num = (el, prop) => parseFloat(el.style[prop]) || 0;
  const point = (ev) => {
    const r = canvas.getBoundingClientRect();
    return { x: (ev.clientX - r.left) / scale(), y: (ev.clientY - r.top) / scale() };
  };
  const geometry = (el) => ({ id: el.dataset.id, x: num(el, "left"), y: num(el, "top"), w: num(el, "width"), h: num(el, "height") });
  const markSelected = () => canvas.querySelectorAll(".pg-el").forEach((e) => e.classList.toggle("pg-selected", st.selected.has(e.dataset.id)));
  const tell = () => dotNet.invokeMethodAsync("JsSelect", [...st.selected]);

  canvas.addEventListener("pointerdown", (ev) => {
    if (ev.button !== 0) return;
    if (st.editing) {
      if (st.editing.el.contains(ev.target)) return; // placing the caret
      finishEdit(true);
    }

    viewport.focus({ preventScroll: true });
    const handle = ev.target.closest ? ev.target.closest(".pg-handle") : null;
    const el = elementOf(ev.target);
    const additive = ev.shiftKey || ev.ctrlKey || ev.metaKey;

    if (handle && el) {
      canvas.setPointerCapture(ev.pointerId);
      st.drag = { mode: "resize", dir: handle.dataset.handle, start: point(ev), orig: geometry(el), el, moved: false };
      ev.preventDefault();
      return;
    }

    if (el) {
      const id = el.dataset.id;
      let reduced = false;
      if (!st.selected.has(id) || additive) {
        if (additive) {
          if (!st.selected.delete(id)) st.selected.add(id);
        } else {
          st.selected = new Set([id]);
        }
        markSelected();
        tell();
      } else if (st.selected.size > 1) {
        reduced = true; // a plain click inside a group narrows it to this element if no drag follows
      }

      const movers = [...canvas.querySelectorAll(".pg-el")].filter((e) => st.selected.has(e.dataset.id) && e.dataset.locked !== "1");
      canvas.setPointerCapture(ev.pointerId);
      st.drag = { mode: "move", start: point(ev), items: movers.map((e) => ({ el: e, ...geometry(e) })), moved: false, clicked: id, reduced };
      return;
    }

    // Empty canvas: clear the selection and start a rubber band.
    if (!additive && st.selected.size > 0) {
      st.selected = new Set();
      markSelected();
      tell();
    }

    canvas.setPointerCapture(ev.pointerId);
    const band = document.createElement("div");
    band.style.cssText = "position:absolute;border:1px solid #2563eb;background:rgba(37,99,235,.12);pointer-events:none;z-index:99999";
    canvas.appendChild(band);
    st.marquee = band;
    st.drag = { mode: "band", start: point(ev), additive, moved: false };
  });

  canvas.addEventListener("pointermove", (ev) => {
    const d = st.drag;
    if (!d) return;
    const p = point(ev);
    const dx = p.x - d.start.x;
    const dy = p.y - d.start.y;
    if (!d.moved && Math.hypot(dx, dy) * scale() < 3) return;
    d.moved = true;

    if (d.mode === "move") {
      for (const it of d.items) {
        it.el.style.left = snapTo(it.x + dx) + "px";
        it.el.style.top = snapTo(it.y + dy) + "px";
      }
    } else if (d.mode === "resize") {
      let { x, y, w, h } = d.orig;
      const dir = d.dir;
      if (dir.includes("e")) w = Math.max(MIN, snapTo(d.orig.x + d.orig.w + dx) - x);
      if (dir.includes("s")) h = Math.max(MIN, snapTo(d.orig.y + d.orig.h + dy) - y);
      if (dir.includes("w")) {
        const nx = Math.min(snapTo(d.orig.x + dx), d.orig.x + d.orig.w - MIN);
        w = d.orig.x + d.orig.w - nx;
        x = nx;
      }
      if (dir.includes("n")) {
        const ny = Math.min(snapTo(d.orig.y + dy), d.orig.y + d.orig.h - MIN);
        h = d.orig.y + d.orig.h - ny;
        y = ny;
      }
      if (ev.shiftKey && d.orig.w > 0 && d.orig.h > 0) {
        const ratio = d.orig.w / d.orig.h;
        if (dir.includes("e") || dir.includes("w")) h = w / ratio;
        else w = h * ratio;
      }
      Object.assign(d.el.style, { left: x + "px", top: y + "px", width: w + "px", height: h + "px" });
    } else if (d.mode === "band" && st.marquee) {
      const x = Math.min(d.start.x, p.x), y = Math.min(d.start.y, p.y);
      Object.assign(st.marquee.style, { left: x + "px", top: y + "px", width: Math.abs(dx) + "px", height: Math.abs(dy) + "px" });
    }
  });

  const end = (ev) => {
    const d = st.drag;
    if (!d) return;
    st.drag = null;
    if (canvas.hasPointerCapture(ev.pointerId)) canvas.releasePointerCapture(ev.pointerId);

    if (d.mode === "band") {
      const band = st.marquee;
      st.marquee = null;
      if (band) {
        const r = { x: num(band, "left"), y: num(band, "top"), w: num(band, "width"), h: num(band, "height") };
        band.remove();
        if (d.moved) {
          const hit = [...canvas.querySelectorAll(".pg-el")].filter((e) => {
            const g = geometry(e);
            return g.x < r.x + r.w && g.x + g.w > r.x && g.y < r.y + r.h && g.y + g.h > r.y;
          }).map((e) => e.dataset.id);
          st.selected = d.additive ? new Set([...st.selected, ...hit]) : new Set(hit);
          markSelected();
          tell();
        }
      }
      return;
    }

    if (!d.moved) {
      if (d.mode === "move" && d.reduced) {
        st.selected = new Set([d.clicked]);
        markSelected();
        tell();
      }
      return;
    }

    const changed = d.mode === "move" ? d.items.map((i) => geometry(i.el)) : [geometry(d.el)];
    dotNet.invokeMethodAsync("JsCommit", JSON.stringify(changed));
  };
  canvas.addEventListener("pointerup", end);
  canvas.addEventListener("pointercancel", end);

  // Double click edits the text of the element in place.
  canvas.addEventListener("dblclick", (ev) => {
    const el = elementOf(ev.target);
    if (!el || el.dataset.locked === "1") return;
    const text = el.querySelector("[data-text]");
    if (!text || el.dataset.editable !== "1") return;
    startEdit(el, text);
  });

  function startEdit(el, text) {
    const original = text.innerText;
    text.setAttribute("contenteditable", "plaintext-only");
    if (text.contentEditable !== "plaintext-only") text.setAttribute("contenteditable", "true");
    text.style.cursor = "text";
    text.style.userSelect = "text";
    text.focus();
    const range = document.createRange();
    range.selectNodeContents(text);
    const sel = window.getSelection();
    sel.removeAllRanges();
    sel.addRange(range);
    const onKey = (e) => {
      if (e.key === "Escape") {
        e.preventDefault();
        finishEdit(false);
      }
      e.stopPropagation();
    };
    const onBlur = () => finishEdit(true);
    text.addEventListener("keydown", onKey);
    text.addEventListener("blur", onBlur);
    st.editing = { el, text, original, onKey, onBlur };
  }

  function finishEdit(commit) {
    const e = st.editing;
    if (!e) return;
    st.editing = null;
    e.text.removeEventListener("keydown", e.onKey);
    e.text.removeEventListener("blur", e.onBlur);
    e.text.removeAttribute("contenteditable");
    e.text.style.cursor = "";
    e.text.style.userSelect = "";
    const value = e.text.innerText.replace(/ /g, " ");
    if (!commit) {
      e.text.innerText = e.original;
      return;
    }

    if (value !== e.original) dotNet.invokeMethodAsync("JsText", e.el.dataset.id, value);
  }

  viewport.addEventListener("keydown", (ev) => {
    if (st.editing) return;
    const tag = ev.target && ev.target.tagName ? ev.target.tagName.toLowerCase() : "";
    if (tag === "input" || tag === "textarea" || tag === "select" || (ev.target && ev.target.isContentEditable)) return;
    const mod = ev.ctrlKey || ev.metaKey;
    const key = ev.key.toLowerCase();
    const send = (cmd) => {
      ev.preventDefault();
      dotNet.invokeMethodAsync("JsCommand", cmd);
    };

    if (key === "delete" || key === "backspace") return send("delete");
    if (key === "escape") return send("deselect");
    if (key.startsWith("arrow")) {
      ev.preventDefault();
      const step = ev.shiftKey ? 10 : 1;
      const dx = key === "arrowleft" ? -step : key === "arrowright" ? step : 0;
      const dy = key === "arrowup" ? -step : key === "arrowdown" ? step : 0;
      dotNet.invokeMethodAsync("JsNudge", dx, dy);
      return;
    }

    if (!mod) return;
    if (key === "c") return send("copy");
    if (key === "x") return send("cut");
    if (key === "v") return send("paste");
    if (key === "d") return send("duplicate");
    if (key === "a") return send("selectall");
    if (key === "z") return send(ev.shiftKey ? "redo" : "undo");
    if (key === "y") return send("redo");
  });

  viewport.addEventListener(
    "wheel",
    (ev) => {
      if (!(ev.ctrlKey || ev.metaKey)) return;
      ev.preventDefault();
      dotNet.invokeMethodAsync("JsCommand", ev.deltaY < 0 ? "zoomin" : "zoomout");
    },
    { passive: false },
  );
}

/** After each render the page tells the script what is selected, so a drag starts from the right elements. */
export function setSelection(canvas, ids) {
  if (!canvas || !canvas.__pg) return;
  canvas.__pg.selected = new Set(ids);
}

export function detach(canvas) {
  if (canvas && canvas.__pg) canvas.__pg.dotNet = null;
}

/** Saves bytes streamed from .NET as a file download (the Excel export). */
export async function download(fileName, contentType, streamRef) {
  const buffer = await streamRef.arrayBuffer();
  const url = URL.createObjectURL(new Blob([buffer], { type: contentType }));
  const a = document.createElement("a");
  a.href = url;
  a.download = fileName;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10000);
}

/**
 * Prints the sheet alone, scaled to fit one landscape page, so the browser's "Save as PDF" produces the PDF. Everything else on the page is
 * hidden by a print-only style, and the page is put back as it was after printing.
 */
export function printSheet(canvas, title) {
  if (!canvas) return;
  const els = [...canvas.querySelectorAll(".pg-el")];
  let minX = 0, minY = 0, maxX = 800, maxY = 500;
  if (els.length > 0) {
    minX = Math.min(...els.map((e) => parseFloat(e.style.left) || 0));
    minY = Math.min(...els.map((e) => parseFloat(e.style.top) || 0));
    maxX = Math.max(...els.map((e) => (parseFloat(e.style.left) || 0) + (parseFloat(e.style.width) || 0)));
    maxY = Math.max(...els.map((e) => (parseFloat(e.style.top) || 0) + (parseFloat(e.style.height) || 0)));
  }

  const pad = 12;
  minX -= pad; minY -= pad; maxX += pad; maxY += pad;
  const scale = Math.min(1, 1050 / (maxX - minX), 720 / (maxY - minY));

  if (!document.getElementById("pg-print-style")) {
    const style = document.createElement("style");
    style.id = "pg-print-style";
    style.textContent = `
      @media print {
        @page { size: A4 landscape; margin: 8mm; }
        html.pg-printing, html.pg-printing body { background: #fff !important; }
        html.pg-printing body * { visibility: hidden !important; }
        html.pg-printing [data-pg-print], html.pg-printing [data-pg-print] * { visibility: visible !important; }
        html.pg-printing [data-pg-print] {
          position: fixed !important; left: 0 !important; top: 0 !important; background-image: none !important; box-shadow: none !important;
          transform: scale(var(--pg-s)) translate(var(--pg-x), var(--pg-y)) !important; transform-origin: 0 0 !important;
        }
        html.pg-printing .pg-handle { display: none !important; }
        html.pg-printing .pg-el { outline: none !important; }
      }`;
    document.head.appendChild(style);
  }

  const root = document.documentElement;
  const oldTitle = document.title;
  canvas.setAttribute("data-pg-print", "1");
  root.classList.add("pg-printing");
  root.style.setProperty("--pg-s", String(scale));
  root.style.setProperty("--pg-x", -minX + "px");
  root.style.setProperty("--pg-y", -minY + "px");
  document.title = title || "Playground";

  const done = () => {
    window.removeEventListener("afterprint", done);
    canvas.removeAttribute("data-pg-print");
    root.classList.remove("pg-printing");
    document.title = oldTitle;
  };
  window.addEventListener("afterprint", done);
  setTimeout(() => window.print(), 100);
}

/**
 * Draws the charts and images of a sheet to PNG pictures for the Excel export, so they look exactly as on the canvas. A chart is a white card
 * with its caption above the chart; the pictures go back to .NET in small pieces (a circuit message is limited in size).
 * items: [{ id, kind: "chart" | "image", w, h, option?, url?, caption?, color?, fontSize?, align?, fill?, stroke?, strokeWidth?, radius? }]
 */
export async function renderImages(items, dotNet) {
  const scale = 2;
  for (const it of items) {
    try {
      const canvas = document.createElement("canvas");
      canvas.width = Math.round(it.w * scale);
      canvas.height = Math.round(it.h * scale);
      const ctx = canvas.getContext("2d");
      ctx.scale(scale, scale);

      if (it.kind === "image") {
        const img = await loadImage(it.url);
        const k = Math.min(it.w / img.naturalWidth, it.h / img.naturalHeight);
        const w = img.naturalWidth * k, h = img.naturalHeight * k;
        ctx.drawImage(img, (it.w - w) / 2, (it.h - h) / 2, w, h);
      } else {
        const r = Math.min(it.radius || 8, it.w / 2, it.h / 2);
        ctx.beginPath();
        ctx.roundRect(0.5, 0.5, it.w - 1, it.h - 1, r);
        ctx.fillStyle = it.fill || "#ffffff";
        ctx.fill();
        const bw = it.strokeWidth ?? 1;
        if (bw > 0) {
          ctx.lineWidth = bw;
          ctx.strokeStyle = it.stroke || "#dbe3ea";
          ctx.stroke();
        }

        let top = 8;
        if (it.caption) {
          const fs = Math.max(11, it.fontSize || 13);
          ctx.font = `600 ${fs}px sans-serif`;
          ctx.fillStyle = it.color || "#0f172a";
          ctx.textBaseline = "top";
          let text = it.caption;
          while (text.length > 1 && ctx.measureText(text).width > it.w - 16) text = text.slice(0, -2) + "…";
          const tw = ctx.measureText(text).width;
          const x = it.align === "center" ? (it.w - tw) / 2 : it.align === "right" ? it.w - 8 - tw : 8;
          ctx.fillText(text, x, top);
          top += fs * 1.25 + 4;
        }

        const host = document.createElement("div");
        const cw = Math.max(40, it.w - 16), ch = Math.max(40, it.h - top - 8);
        host.style.cssText = `position:fixed;left:-10000px;top:0;width:${cw}px;height:${ch}px`;
        document.body.appendChild(host);
        const chart = window.echarts.init(host, null, { renderer: "canvas", width: cw, height: ch });
        const option = JSON.parse(it.option);
        option.animation = false;
        chart.setOption(option);
        const data = chart.getDataURL({ pixelRatio: scale, backgroundColor: it.fill || "#ffffff" });
        chart.dispose();
        host.remove();
        const img = await loadImage(data);
        ctx.drawImage(img, 8, top, cw, ch);
      }

      const base64 = canvas.toDataURL("image/png").split(",")[1];
      const size = 16000;
      const count = Math.max(1, Math.ceil(base64.length / size));
      for (let i = 0; i < count; i++) await dotNet.invokeMethodAsync("JsImage", it.id, i, count, base64.slice(i * size, (i + 1) * size));
    } catch (e) {
      console.warn("Could not draw an element for the export", it.id, e);
    }
  }
}

function loadImage(url) {
  return new Promise((resolve, reject) => {
    const img = new Image();
    img.onload = () => resolve(img);
    img.onerror = reject;
    img.src = url;
  });
}
