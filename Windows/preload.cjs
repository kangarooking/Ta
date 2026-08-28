const { contextBridge, ipcRenderer } = require("electron");

const invokeChannels = new Set([
  "settings:read", "settings:save", "profiles:create", "profiles:save", "profiles:delete", "profiles:test", "profiles:select",
  "system:status", "system:open-data", "system:open-privacy", "agent:installation-status", "agent:copy-installation", "ocr:status", "ocr:warmup", "capture:start", "capture:cancel", "capture:copy",
  "capture:save", "capture:pin", "capture:edit", "capture:vision", "pin:copy",
  "pin:save", "pin:close", "editor:copy", "editor:save", "editor:pin", "editor:close"
]);

contextBridge.exposeInMainWorld("ta", {
  invoke(channel, ...args) {
    if (!invokeChannels.has(channel)) throw new Error(`未允许的桌面调用：${channel}`);
    return ipcRenderer.invoke(channel, ...args);
  },
  send(channel, ...args) {
    if (channel !== "pin:ready" && channel !== "editor:ready") throw new Error(`未允许的桌面事件：${channel}`);
    ipcRenderer.send(channel, ...args);
  },
  onCaptureFrame(listener) { ipcRenderer.on("capture:frame", (_event, frame) => listener(frame)); },
  onPinImage(listener) { ipcRenderer.on("pin:image", (_event, dataUrl) => listener(dataUrl)); },
  onEditorImage(listener) { ipcRenderer.on("editor:image", (_event, dataUrl) => listener(dataUrl)); }
});
