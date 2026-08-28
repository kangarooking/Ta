const token = new URLSearchParams(location.search).get("token");
const canvas = document.querySelector("#canvas");
const context = canvas.getContext("2d");
const color = document.querySelector("#color");
const lineWidth = document.querySelector("#line-width");
const status = document.querySelector("#status");
const image = new Image();
let mode = "pen";
let shapes = [];
let activeShape;

function canvasPoint(event) {
  const bounds = canvas.getBoundingClientRect();
  return {
    x: (event.clientX - bounds.left) * canvas.width / bounds.width,
    y: (event.clientY - bounds.top) * canvas.height / bounds.height
  };
}

function applyStroke() {
  context.strokeStyle = color.value;
  context.fillStyle = color.value;
  context.lineWidth = Number(lineWidth.value);
  context.lineJoin = "round";
  context.lineCap = "round";
}

function drawArrow(shape) {
  const angle = Math.atan2(shape.y2 - shape.y1, shape.x2 - shape.x1);
  const head = Math.max(12, shape.width * 2.3);
  context.beginPath();
  context.moveTo(shape.x1, shape.y1);
  context.lineTo(shape.x2, shape.y2);
  context.lineTo(shape.x2 - head * Math.cos(angle - Math.PI / 6), shape.y2 - head * Math.sin(angle - Math.PI / 6));
  context.moveTo(shape.x2, shape.y2);
  context.lineTo(shape.x2 - head * Math.cos(angle + Math.PI / 6), shape.y2 - head * Math.sin(angle + Math.PI / 6));
  context.stroke();
}

function drawShape(shape) {
  context.strokeStyle = shape.color;
  context.fillStyle = shape.color;
  context.lineWidth = shape.width;
  context.lineJoin = "round";
  context.lineCap = "round";
  if (shape.type === "pen") {
    if (shape.points.length < 2) return;
    context.beginPath();
    context.moveTo(shape.points[0].x, shape.points[0].y);
    for (const point of shape.points.slice(1)) context.lineTo(point.x, point.y);
    context.stroke();
  } else if (shape.type === "rect") {
    context.strokeRect(Math.min(shape.x1, shape.x2), Math.min(shape.y1, shape.y2), Math.abs(shape.x2 - shape.x1), Math.abs(shape.y2 - shape.y1));
  } else if (shape.type === "arrow") {
    drawArrow(shape);
  } else if (shape.type === "text") {
    context.font = `700 ${Math.max(18, shape.width * 4)}px "Microsoft YaHei UI", sans-serif`;
    context.textBaseline = "top";
    context.fillText(shape.text, shape.x1, shape.y1);
  }
}

function redraw() {
  context.clearRect(0, 0, canvas.width, canvas.height);
  context.drawImage(image, 0, 0, canvas.width, canvas.height);
  shapes.forEach(drawShape);
  if (activeShape) drawShape(activeShape);
}

function modeButton() {
  document.querySelectorAll("[data-mode]").forEach((button) => button.classList.toggle("selected", button.dataset.mode === mode));
}

canvas.addEventListener("pointerdown", (event) => {
  const point = canvasPoint(event);
  canvas.setPointerCapture(event.pointerId);
  const width = Number(lineWidth.value);
  if (mode === "text") {
    const text = window.prompt("输入标注文字：");
    if (text?.trim()) {
      shapes.push({ type: "text", x1: point.x, y1: point.y, text: text.trim(), color: color.value, width });
      redraw();
    }
    return;
  }
  activeShape = mode === "pen"
    ? { type: "pen", points: [point], color: color.value, width }
    : { type: mode, x1: point.x, y1: point.y, x2: point.x, y2: point.y, color: color.value, width };
});

canvas.addEventListener("pointermove", (event) => {
  if (!activeShape) return;
  const point = canvasPoint(event);
  if (activeShape.type === "pen") activeShape.points.push(point);
  else { activeShape.x2 = point.x; activeShape.y2 = point.y; }
  redraw();
});

canvas.addEventListener("pointerup", (event) => {
  if (!activeShape) return;
  canvas.releasePointerCapture(event.pointerId);
  if (activeShape.type !== "pen" || activeShape.points.length > 1) shapes.push(activeShape);
  activeShape = undefined;
  redraw();
});

document.querySelector("header").addEventListener("click", async (event) => {
  const button = event.target.closest("button");
  if (!button) return;
  if (button.dataset.mode) { mode = button.dataset.mode; modeButton(); return; }
  if (button.id === "undo") { shapes.pop(); redraw(); return; }
  if (button.id === "reset") { shapes = []; redraw(); return; }
  const action = button.dataset.action;
  if (!action) return;
  try {
    if (action === "close") return window.ta.invoke("editor:close", token);
    const dataUrl = canvas.toDataURL("image/png");
    if (action === "copy") await window.ta.invoke("editor:copy", dataUrl);
    if (action === "save") await window.ta.invoke("editor:save", dataUrl);
    if (action === "pin") await window.ta.invoke("editor:pin", dataUrl);
    status.textContent = action === "copy" ? "标注图片已复制。" : action === "save" ? "已完成保存操作。" : "标注图片已钉住。";
  } catch (error) {
    status.textContent = error.message || "操作失败。";
  }
});

window.ta.onEditorImage((dataUrl) => { image.src = dataUrl; });
image.addEventListener("load", () => {
  canvas.width = image.naturalWidth;
  canvas.height = image.naturalHeight;
  redraw();
});
window.ta.send("editor:ready", token);
