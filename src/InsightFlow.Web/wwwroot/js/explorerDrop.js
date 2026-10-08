// Workspace Explorer uploads: OS drag-and-drop of files *and folders* plus the Upload buttons.
// Files go straight to the Web's streaming BFF endpoint (not over the Blazor circuit) with one XHR per file so each
// gets a progress bar. A dropped folder keeps its structure: every file is sent with its relative path, and the Api
// creates missing sub-folders. Progress is reported back to the ExplorerPane component through a .NET reference.
const CONCURRENCY = 3;
const registrations = new WeakMap();
let nextUploadId = 1;

/** Wires drag-and-drop on `root`. Drop targets are elements with data-folder-id; anything else uses the selected folder. */
export function init(root, dotnet) {
  const state = { dotnet, defaultFolderId: null, depth: 0 };

  const isFileDrag = (e) => Array.from(e.dataTransfer?.types ?? []).includes("Files");
  const targetOf = (e) => e.target.closest?.("[data-folder-id]");

  const onDragOver = (e) => {
    if (!isFileDrag(e)) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = "copy";
    root.querySelectorAll(".if-drop-target").forEach((el) => el.classList.remove("if-drop-target"));
    (targetOf(e) ?? root).classList.add("if-drop-target");
  };
  const clear = () => root.querySelectorAll(".if-drop-target").forEach((el) => el.classList.remove("if-drop-target"));
  const onDragEnter = (e) => { if (isFileDrag(e)) state.depth++; };
  const onDragLeave = (e) => {
    if (!isFileDrag(e)) return;
    if (--state.depth <= 0) { state.depth = 0; clear(); root.classList.remove("if-drop-target"); }
  };
  const onDrop = async (e) => {
    if (!isFileDrag(e)) return;
    e.preventDefault();
    e.stopPropagation();
    state.depth = 0;
    clear();
    root.classList.remove("if-drop-target");
    const folderId = targetOf(e)?.dataset.folderId ?? state.defaultFolderId;
    if (!folderId) return;
    // Entries must be read synchronously from the event, before any await.
    const entries = Array.from(e.dataTransfer.items ?? [])
      .filter((i) => i.kind === "file")
      .map((i) => i.webkitGetAsEntry?.() ?? i.getAsFile());
    const files = [];
    for (const entry of entries) {
      if (entry instanceof File) files.push({ file: entry, path: entry.name });
      else if (entry) await collect(entry, "", files);
    }
    await uploadAll(state, folderId, files);
  };

  root.addEventListener("dragenter", onDragEnter);
  root.addEventListener("dragover", onDragOver);
  root.addEventListener("dragleave", onDragLeave);
  root.addEventListener("drop", onDrop);
  registrations.set(root, { state, dispose: () => {
    root.removeEventListener("dragenter", onDragEnter);
    root.removeEventListener("dragover", onDragOver);
    root.removeEventListener("dragleave", onDragLeave);
    root.removeEventListener("drop", onDrop);
  } });
}

export function setDefaultFolder(root, folderId) {
  const reg = registrations.get(root);
  if (reg) reg.state.defaultFolderId = folderId;
}

/** Opens the hidden file input (files or a whole directory) and uploads the selection into `folderId`. */
export function pick(root, input, folderId) {
  const reg = registrations.get(root);
  if (!reg || !input) return;
  input.onchange = async () => {
    const files = Array.from(input.files ?? []).map((f) => ({ file: f, path: f.webkitRelativePath || f.name }));
    input.value = "";
    await uploadAll(reg.state, folderId, files);
  };
  input.click();
}

export function dispose(root) {
  registrations.get(root)?.dispose();
  registrations.delete(root);
}

/** Uploads a list of { file, path } into a folder (exposed for automated checks). */
export async function uploadFiles(root, folderId, files) {
  const reg = registrations.get(root);
  if (reg) await uploadAll(reg.state, folderId, files.map((f) => ({ file: f, path: f.webkitRelativePath || f.name })));
}

async function collect(entry, prefix, out) {
  if (entry.isFile) {
    const file = await new Promise((resolve, reject) => entry.file(resolve, reject));
    out.push({ file, path: prefix + entry.name });
  } else if (entry.isDirectory) {
    const reader = entry.createReader();
    // readEntries returns results in batches; call until it returns an empty batch.
    for (;;) {
      const batch = await new Promise((resolve, reject) => reader.readEntries(resolve, reject));
      if (batch.length === 0) break;
      for (const child of batch) await collect(child, `${prefix}${entry.name}/`, out);
    }
  }
}

async function uploadAll(state, folderId, files) {
  if (files.length === 0) return;
  const jobs = files.map((f) => ({ ...f, id: nextUploadId++ }));
  for (const job of jobs) {
    await state.dotnet.invokeMethodAsync("OnUploadQueued", job.id, job.path, job.file.size);
  }
  let index = 0;
  const worker = async () => {
    while (index < jobs.length) {
      const job = jobs[index++];
      await uploadOne(state, folderId, job);
    }
  };
  await Promise.all(Array.from({ length: Math.min(CONCURRENCY, jobs.length) }, worker));
  await state.dotnet.invokeMethodAsync("OnUploadBatchCompleted", folderId);
}

function uploadOne(state, folderId, job) {
  return new Promise((resolve) => {
    const form = new FormData();
    form.append("path", job.path); // must precede the file part
    form.append("file", job.file, job.file.name);

    const xhr = new XMLHttpRequest();
    xhr.open("POST", `bff/workspace/folders/${encodeURIComponent(folderId)}/files`);
    xhr.setRequestHeader("X-InsightFlow-Request", "1");
    let lastReport = 0;
    xhr.upload.onprogress = (e) => {
      const now = performance.now();
      if (e.lengthComputable && now - lastReport > 150) {
        lastReport = now;
        state.dotnet.invokeMethodAsync("OnUploadProgress", job.id, e.loaded, e.total);
      }
    };
    const finish = (ok, error) => state.dotnet.invokeMethodAsync("OnUploadCompleted", job.id, ok, error ?? null).finally(resolve);
    xhr.onload = () => {
      let body = null;
      try { body = JSON.parse(xhr.responseText); } catch { /* not JSON */ }
      if (xhr.status >= 200 && xhr.status < 300) {
        const result = body?.files?.[0];
        finish(result?.success ?? true, result?.error);
      } else {
        finish(false, body?.detail ?? body?.title ?? `Upload failed (${xhr.status}).`);
      }
    };
    xhr.onerror = () => finish(false, "Network error.");
    xhr.send(form);
  });
}
