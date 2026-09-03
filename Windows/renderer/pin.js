const token = new URLSearchParams(location.search).get("token");
const image = document.querySelector("#pinned-image");
let imageDataUrl = "";
let opacity = 1;

window.ta.onPinImage((dataUrl) => {
  imageDataUrl = dataUrl;
  image.src = dataUrl;
});
window.ta.send("pin:ready", token);

document.querySelector(".controls").addEventListener("click", async (event) => {
  const action = event.target.closest("button")?.dataset.action;
  if (!action) return;
  if (action === "close") return window.ta.invoke("pin:close", token);
  if (action === "opacity") {
    opacity = opacity <= .35 ? 1 : Number((opacity - .2).toFixed(1));
    document.querySelector("#pin").style.opacity = opacity;
    return;
  }
  if (!imageDataUrl) return;
  if (action === "copy") return window.ta.invoke("pin:copy", imageDataUrl);
  if (action === "save") return window.ta.invoke("pin:save", imageDataUrl);
});
