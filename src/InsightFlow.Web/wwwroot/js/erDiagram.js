// Drag-to-connect for the interactive ER diagram (ErDiagram.razor, Interactive mode).
// Pressing a connector dot (an element with data-handle, data-column-id, data-x, data-y) starts a line that follows the pointer entirely in the
// browser; releasing it over another column row (an element with data-column-id) reports the pair back to .NET. Nothing is sent to the
// server while dragging, so it stays smooth. Escape cancels. Works with mouse, pen and touch.

const SVG_NS = "http://www.w3.org/2000/svg";
const instances = new WeakMap();

export function init(svg, dotNetRef) {
  if (!svg || instances.has(svg)) {
    return;
  }

  const point = (clientX, clientY) => {
    const p = svg.createSVGPoint();
    p.x = clientX;
    p.y = clientY;
    return p.matrixTransform(svg.getScreenCTM().inverse());
  };

  // Dragging the canvas: hold the mouse button on an empty part of the diagram and drag to move around it (edit and read-only views).
  const scroller = svg.closest("[data-pan]");
  const onPanDown = (event) => {
    if (!scroller || event.button > 0 || event.target.closest?.("[data-handle], [data-scrollbar], [data-nodrag], [data-drag], [data-edge], button, a, input, select")) {
      return;
    }

    event.preventDefault();
    const startX = event.clientX;
    const startY = event.clientY;
    const startLeft = scroller.scrollLeft;
    const startTop = scroller.scrollTop;
    scroller.classList.add("cursor-grabbing");
    const onMove = (e) => {
      scroller.scrollLeft = startLeft - (e.clientX - startX);
      scroller.scrollTop = startTop - (e.clientY - startY);
    };

    const stop = () => {
      scroller.classList.remove("cursor-grabbing");
      window.removeEventListener("pointermove", onMove);
      window.removeEventListener("pointerup", stop);
      window.removeEventListener("pointercancel", stop);
    };

    window.addEventListener("pointermove", onMove);
    window.addEventListener("pointerup", stop);
    window.addEventListener("pointercancel", stop);
  };

  // Scrolling a card's columns with the mouse wheel. Only cards that have more columns than fit take the wheel; elsewhere the page scrolls
  // as usual. Turns are gathered and sent once per frame, so a fast flick is one round trip.
  let pendingRows = 0;
  let pendingCard = null;
  let frame = 0;
  const onWheel = (event) => {
    const card = event.target.closest?.("[data-card]");
    if (!card || !card.querySelector("[data-scrollbar]")) {
      return;
    }

    event.preventDefault();
    const id = card.getAttribute("data-card");
    if (pendingCard !== id) {
      pendingRows = 0;
      pendingCard = id;
    }

    pendingRows += event.deltaMode === 1 ? event.deltaY : event.deltaY / 30;
    if (!frame) {
      frame = requestAnimationFrame(() => {
        frame = 0;
        const rows = pendingRows < 0 ? Math.ceil(pendingRows) : Math.floor(pendingRows);
        pendingRows -= rows;
        if (rows !== 0) {
          dotNetRef.invokeMethodAsync("OnCardScrolled", pendingCard, rows);
        }
      });
    }
  };

  // Pressing the scrollbar jumps the window there; dragging moves it with the pointer.
  const startScrollbar = (event, bar) => {
    event.preventDefault();
    const id = bar.getAttribute("data-card-id");
    const trackY = Number(bar.getAttribute("data-track-y"));
    const trackH = Number(bar.getAttribute("data-track-h"));
    const total = Number(bar.getAttribute("data-total"));
    const visible = Number(bar.getAttribute("data-visible"));
    const thumb = Number(bar.getAttribute("data-thumb"));
    let queued = false;
    let latest = 0;
    const send = (e) => {
      const y = point(e.clientX, e.clientY).y;
      const fraction = Math.min(1, Math.max(0, (y - trackY - thumb / 2) / Math.max(1, trackH - thumb)));
      latest = Math.round(fraction * (total - visible));
      if (!queued) {
        queued = true;
        requestAnimationFrame(() => {
          queued = false;
          dotNetRef.invokeMethodAsync("OnCardScrollTo", id, latest);
        });
      }
    };

    const stop = () => {
      window.removeEventListener("pointermove", send);
      window.removeEventListener("pointerup", stop);
      window.removeEventListener("pointercancel", stop);
    };

    window.addEventListener("pointermove", send);
    window.addEventListener("pointerup", stop);
    window.addEventListener("pointercancel", stop);
    send(event);
  };

  // Moving a table: dragging its header slides the whole card (and its dots) with the pointer, hides the lines attached to it while it
  // moves, and reports the distance to .NET on release, which re-routes every line to the new position.
  const startCardDrag = (event, grip) => {
    const card = grip.closest("[data-card]");
    if (!card) {
      return;
    }

    event.preventDefault();
    const tableId = card.getAttribute("data-card");
    const toSvg = (clientX, clientY) => {
      const point = svg.createSVGPoint();
      point.x = clientX;
      point.y = clientY;
      return point.matrixTransform(svg.getScreenCTM().inverse());
    };

    const start = toSvg(event.clientX, event.clientY);
    let dx = 0;
    let dy = 0;
    let moved = false;
    const attached = [...svg.querySelectorAll(`[data-edge-from="${CSS.escape(tableId)}"], [data-edge-to="${CSS.escape(tableId)}"]`)];
    svg.dataset.moving = "true";
    card.style.cursor = "grabbing";

    const onMove = (e) => {
      const p = toSvg(e.clientX, e.clientY);
      dx = p.x - start.x;
      dy = p.y - start.y;
      if (!moved && Math.hypot(dx, dy) < 4) {
        return;
      }

      if (!moved) {
        moved = true;
        attached.forEach((el) => (el.style.visibility = "hidden"));
      }

      card.setAttribute("transform", `translate(${dx} ${dy})`);
    };

    const finish = (commit) => {
      window.removeEventListener("pointermove", onMove);
      window.removeEventListener("pointerup", onUp);
      window.removeEventListener("pointercancel", onCancel);
      window.removeEventListener("keydown", onKey);
      delete svg.dataset.moving;
      card.style.cursor = "";
      if (commit && moved) {
        // .NET re-renders the card at its new place; leave the transform until then so it does not jump back for a frame.
        dotNetRef.invokeMethodAsync("OnCardMoved", tableId, dx, dy).then(() => {
          card.removeAttribute("transform");
          attached.forEach((el) => (el.style.visibility = ""));
        });
      } else {
        card.removeAttribute("transform");
        attached.forEach((el) => (el.style.visibility = ""));
      }
    };

    const onUp = () => finish(true);
    const onCancel = () => finish(false);
    const onKey = (e) => {
      if (e.key === "Escape") {
        finish(false);
      }
    };

    window.addEventListener("pointermove", onMove);
    window.addEventListener("pointerup", onUp);
    window.addEventListener("pointercancel", onCancel);
    window.addEventListener("keydown", onKey);
  };

  const onDown = (event) => {
    if (event.button > 0 || event.target.closest?.("[data-nodrag]")) {
      return;
    }

    const bar = event.target.closest?.("[data-scrollbar]");
    if (bar) {
      startScrollbar(event, bar);
      return;
    }

    const grip = event.target.closest?.("[data-drag]");
    if (grip) {
      startCardDrag(event, grip);
      return;
    }

    const handle = event.target.closest?.("[data-handle]");
    if (!handle) {
      return;
    }

    event.preventDefault();
    event.stopPropagation();

    const fromId = handle.getAttribute("data-column-id");
    const x1 = Number(handle.getAttribute("data-x"));
    const y1 = Number(handle.getAttribute("data-y"));
    const side = handle.getAttribute("data-side") || "right";

    const layer = document.createElementNS(SVG_NS, "g");
    layer.setAttribute("pointer-events", "none");
    const path = document.createElementNS(SVG_NS, "path");
    path.setAttribute("fill", "none");
    path.setAttribute("stroke", "#176b73");
    path.setAttribute("stroke-width", "3");
    path.setAttribute("stroke-dasharray", "7 5");
    path.setAttribute("stroke-linecap", "round");
    const dot = document.createElementNS(SVG_NS, "circle");
    dot.setAttribute("r", "6");
    dot.setAttribute("fill", "#176b73");
    dot.setAttribute("stroke", "white");
    dot.setAttribute("stroke-width", "2");
    layer.append(path, dot);
    svg.append(layer);
    svg.dataset.dragging = "true";

    let highlighted = null;
    const clearHighlight = () => {
      if (highlighted) {
        highlighted.style.stroke = "";
        highlighted.style.strokeWidth = "";
        highlighted = null;
      }
    };

    const toSvg = (clientX, clientY) => {
      const point = svg.createSVGPoint();
      point.x = clientX;
      point.y = clientY;
      return point.matrixTransform(svg.getScreenCTM().inverse());
    };

    // The column id under the pointer (a row rectangle or a connector dot), other than the one being dragged from.
    const targetAt = (clientX, clientY) => {
      const el = document.elementFromPoint(clientX, clientY)?.closest?.("[data-column-id]");
      const id = el?.getAttribute("data-column-id");
      return id && id !== fromId ? id : null;
    };

    const onMove = (e) => {
      const p = toSvg(e.clientX, e.clientY);
      const dx = Math.max(40, Math.abs(p.x - x1) / 2);
      const out = side === "left" ? -1 : 1;
      path.setAttribute("d", `M ${x1} ${y1} C ${x1 + out * dx} ${y1}, ${p.x - out * dx} ${p.y}, ${p.x} ${p.y}`);
      dot.setAttribute("cx", String(p.x));
      dot.setAttribute("cy", String(p.y));

      const targetId = targetAt(e.clientX, e.clientY);
      const rect = targetId ? svg.querySelector(`rect[data-row][data-column-id="${CSS.escape(targetId)}"]`) : null;
      if (rect !== highlighted) {
        clearHighlight();
        if (rect) {
          rect.style.stroke = "#176b73";
          rect.style.strokeWidth = "2";
          highlighted = rect;
        }
      }
    };

    const finish = (e, cancelled) => {
      window.removeEventListener("pointermove", onMove);
      window.removeEventListener("pointerup", onUp);
      window.removeEventListener("pointercancel", onCancel);
      window.removeEventListener("keydown", onKey);
      clearHighlight();
      layer.remove();
      delete svg.dataset.dragging;
      if (!cancelled) {
        const toId = targetAt(e.clientX, e.clientY);
        if (toId) {
          dotNetRef.invokeMethodAsync("OnDragConnect", fromId, toId);
        }
      }
    };

    const onUp = (e) => finish(e, false);
    const onCancel = (e) => finish(e, true);
    const onKey = (e) => {
      if (e.key === "Escape") {
        finish(e, true);
      }
    };

    window.addEventListener("pointermove", onMove);
    window.addEventListener("pointerup", onUp);
    window.addEventListener("pointercancel", onCancel);
    window.addEventListener("keydown", onKey);
    onMove(event);
  };

  svg.addEventListener("pointerdown", onDown);
  svg.addEventListener("wheel", onWheel, { passive: false });
  // Minimap: a small overview of the whole diagram in the corner. The box on it is what the canvas shows now; it follows scrolling,
  // zooming and resizing, and pressing or dragging on the minimap moves the canvas there. All of it runs in the browser.
  const canvasFrame = scroller?.parentElement;
  const findMinimap = () => canvasFrame?.querySelector("[data-minimap]") ?? null;
  const zoomOf = () => {
    const width = svg.getBoundingClientRect().width;
    const units = svg.viewBox.baseVal.width;
    return width > 0 && units > 0 ? width / units : 1;
  };

  const sync = () => {
    const mm = findMinimap();
    const view = mm?.querySelector("[data-mm-view]");
    if (!mm || !view || !scroller) {
      return;
    }

    const box = mm.viewBox.baseVal;
    const zoom = zoomOf();
    const w = Math.min(box.width, scroller.clientWidth / zoom);
    const h = Math.min(box.height, scroller.clientHeight / zoom);
    view.setAttribute("x", String(Math.max(0, Math.min(box.width - w, scroller.scrollLeft / zoom))));
    view.setAttribute("y", String(Math.max(0, Math.min(box.height - h, scroller.scrollTop / zoom))));
    view.setAttribute("width", String(w));
    view.setAttribute("height", String(h));
  };

  const onMiniDown = (event) => {
    const mm = event.target.closest?.("[data-minimap]");
    if (!mm || event.button > 0) {
      return;
    }

    event.preventDefault();
    event.stopPropagation();
    const go = (e) => {
      const point = mm.createSVGPoint();
      point.x = e.clientX;
      point.y = e.clientY;
      const at = point.matrixTransform(mm.getScreenCTM().inverse());
      const zoom = zoomOf();
      scroller.scrollLeft = at.x * zoom - scroller.clientWidth / 2;
      scroller.scrollTop = at.y * zoom - scroller.clientHeight / 2;
    };

    const stop = () => {
      window.removeEventListener("pointermove", go);
      window.removeEventListener("pointerup", stop);
      window.removeEventListener("pointercancel", stop);
    };

    window.addEventListener("pointermove", go);
    window.addEventListener("pointerup", stop);
    window.addEventListener("pointercancel", stop);
    go(event);
  };

  scroller?.addEventListener("scroll", sync, { passive: true });
  canvasFrame?.addEventListener("pointerdown", onMiniDown);
  window.addEventListener("resize", sync);
  sync();

  scroller?.addEventListener("pointerdown", onPanDown);
  instances.set(svg, { onDown, onWheel, onPanDown, scroller, sync, onMiniDown, canvasFrame });
}

export function dispose(svg) {
  const handler = svg && instances.get(svg);
  if (handler) {
    svg.removeEventListener("pointerdown", handler.onDown);
    svg.removeEventListener("wheel", handler.onWheel);
    handler.scroller?.removeEventListener("pointerdown", handler.onPanDown);
    handler.scroller?.removeEventListener("scroll", handler.sync);
    handler.canvasFrame?.removeEventListener("pointerdown", handler.onMiniDown);
    window.removeEventListener("resize", handler.sync);
    instances.delete(svg);
  }
}

/** Re-reads the canvas size and position after the diagram was redrawn (new tables, zoom, collapse), so the minimap box stays right. */
export function sync(svg) {
  instances.get(svg)?.sync();
}
