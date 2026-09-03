const root = document.querySelector("#capture");
const image = document.querySelector("#screen-image");
const dim = document.querySelector("#dim");
const selectionElement = document.querySelector("#selection");
const sizeLabel = document.querySelector("#selection-size");
const toolbar = document.querySelector("#toolbar");
const hint = document.querySelector("#hint");
const status = document.querySelector("#status");

let start;
let selection;
let busy = false;
let defaultAction;

window.ta.onCaptureFrame(({ imageDataUrl, defaultAction: requestedAction }) => {
  image.src = imageDataUrl;
  defaultAction = requestedAction;
});

function pointerPosition(event) {
  const rect = root.getBoundingClientRect();
  return { x: event.clientX - rect.left, y: event.clientY - rect.top };
}

function normalizeRect(first, last) {
  return {
    x: Math.max(0, Math.min(first.x, last.x)),
    y: Math.max(0, Math.min(first.y, last.y)),
    width: Math.abs(last.x - first.x),
    height: Math.abs(last.y - first.y)
  };
}

function showSelection() {
  if (!selection) return;
  dim.hidden = true;
  selectionElement.hidden = false;
  selectionElement.style.left = `${selection.x}px`;
  selectionElement.style.top = `${selection.y}px`;
  selectionElement.style.width = `${selection.width}px`;
  selectionElement.style.height = `${selection.height}px`;
  sizeLabel.textContent = `${Math.round(selection.width)} × ${Math.round(selection.height)}`;
}

function positionToolbar() {
  if (!selection) return;
  toolbar.hidden = false;
  const gap = 10;
  const toolbarWidth = toolbar.offsetWidth;
  const toolbarHeight = toolbar.offsetHeight;
  let left = Math.max(gap, Math.min(root.clientWidth - toolbarWidth - gap, selection.x));
  let top = selection.y + selection.height + 29;
  if (top + toolbarHeight > root.clientHeight - gap) top = Math.max(gap, selection.y - toolbarHeight - gap);
  toolbar.style.left = `${left}px`;
  toolbar.style.top = `${top}px`;
}

function resetSelection() {
  start = undefined;
  selection = undefined;
  selectionElement.hidden = true;
  toolbar.hidden = true;
  dim.hidden = false;
  hint.hidden = false;
}

function selectedDataUrl() {
  if (!selection || !image.naturalWidth || selection.width < 2 || selection.height < 2) {
    throw new Error("请先框选有效区域。");
  }
  const xScale = image.naturalWidth / root.clientWidth;
  const yScale = image.naturalHeight / root.clientHeight;
  const canvas = document.createElement("canvas");
  canvas.width = Math.max(1, Math.round(selection.width * xScale));
  canvas.height = Math.max(1, Math.round(selection.height * yScale));
  const context = canvas.getContext("2d");
  context.drawImage(
    image,
    Math.round(selection.x * xScale), Math.round(selection.y * yScale), canvas.width, canvas.height,
    0, 0, canvas.width, canvas.height
  );
  return canvas.toDataURL("image/png");
}

function setBusy(next, message = "") {
  busy = next;
  toolbar.querySelectorAll("button").forEach((button) => { button.disabled = next; });
  status.textContent = message;
}

root.addEventListener("pointerdown", (event) => {
  if (busy || event.target.closest("#toolbar")) return;
  root.setPointerCapture(event.pointerId);
  start = pointerPosition(event);
  selection = { ...start, width: 0, height: 0 };
  toolbar.hidden = true;
  hint.hidden = true;
  showSelection();
});

root.addEventListener("pointermove", (event) => {
  if (!start) return;
  selection = normalizeRect(start, pointerPosition(event));
  showSelection();
});

root.addEventListener("pointerup", (event) => {
  if (!start) return;
  root.releasePointerCapture(event.pointerId);
  selection = normalizeRect(start, pointerPosition(event));
  start = undefined;
  if (selection.width < 8 || selection.height < 8) return resetSelection();
  showSelection();
  if (defaultAction) void performAction(defaultAction);
  else positionToolbar();
});

async function performAction(action) {
  if (!action || busy) return;
  if (action === "cancel") return window.ta.invoke("capture:cancel");
  try {
    const dataUrl = selectedDataUrl();
    if (action === "copy") return window.ta.invoke("capture:copy", dataUrl);
    if (action === "pin") return window.ta.invoke("capture:pin", dataUrl);
    if (action === "edit") return window.ta.invoke("capture:edit", dataUrl);
    if (action === "save") {
      setBusy(true, "正在选择保存位置…");
      const result = await window.ta.invoke("capture:save", dataUrl);
      if (result.canceled) setBusy(false, "已取消保存。");
      return;
    }
    setBusy(true, action === "ocr" ? "正在本机取字…" : (action === "translate" ? "正在翻译…" : "正在调用视觉模型…"));
    await window.ta.invoke("capture:vision", action, dataUrl);
  } catch (error) {
    setBusy(false, error.message || "操作失败。");
  }
}

toolbar.addEventListener("click", (event) => {
  const action = event.target.closest("button")?.dataset.action;
  void performAction(action);
});

window.addEventListener("keydown", (event) => {
  if (event.key === "Escape") window.ta.invoke("capture:cancel");
  if (event.key === "Enter" && selection && !busy) toolbar.querySelector('[data-action="copy"]').click();
});
