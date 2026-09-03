const { app, BrowserWindow, clipboard, desktopCapturer, dialog, globalShortcut, ipcMain, Menu, nativeImage, Notification, safeStorage, screen, shell, Tray } = require("electron");
const crypto = require("node:crypto");
const fsSync = require("node:fs");
const fs = require("node:fs/promises");
const path = require("node:path");
const {
  DEFAULT_SETTINGS, HOTKEY_ACTIONS, PROVIDER_PRESETS, RECOGNITION_TASKS, createProfileFromPreset,
  normalizeProfile, normalizeSettings, presetById, profileValidationMessage, publicSettings
} = require("./lib/config.cjs");
const { callVision } = require("./lib/providers.cjs");
const { LocalOCRService } = require("./lib/local-ocr.cjs");
const { NamedPipeAgentBridge, PROTOCOL_VERSION } = require("./lib/agent-bridge.cjs");

const PRODUCT_NAME = "拓 Ta";
const AGENT_PIPE_NAME = "\\\\.\\pipe\\ta-agent-v1";
const WINDOWS_INSTALL_COMMAND = "irm https://github.com/kangarooking/Ta/releases/latest/download/install.ps1 | iex";
const WINDOWS_AGENT_INSTALL_PROMPT = `请帮我在这台 Windows 电脑安装拓（Ta）的 CLI 和 Agent Skill。请在 PowerShell 执行下面这条命令：\n${WINDOWS_INSTALL_COMMAND}\n\n安装完成后，运行 ta status --json 验证。确认 JSON 顶层 ok 为 true、data.bridge 为 ready；如果 Bridge 尚未就绪，请打开拓并等待它启动。不要索要、读取或输出任何 API Key，也不要修改云端模型配置。`;
const TEST_IMAGE = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLkpgAAAABJRU5ErkJggg==";
const TASK_PROMPTS = {
  general: "这是视觉理解任务。请描述图片中可见的主体、人物、物体、场景、动作、颜色和构图；图片有文字时再准确整理文字。即使没有文字也必须描述画面。直接输出结果，不要猜测看不清的内容。",
  extractText: "逐字提取截图中的全部可见文字，保持阅读顺序、段落和换行；不要总结、翻译或补写。",
  explainCode: "提取截图中的代码，先输出可复制的完整代码块，再用简洁中文说明语言、用途和明显问题；不要虚构被遮挡的代码。",
  tableMarkdown: "识别截图中的表格，严格按行列输出为 Markdown 表格；不要输出额外说明。",
  tableCSV: "识别截图中的表格并输出合法 CSV；只输出 CSV 内容。",
  formulaLaTeX: "识别截图中的数学公式并输出可复制的 LaTeX；只输出 LaTeX，不要解释或猜测模糊符号。"
};

let settings = { ...DEFAULT_SETTINGS };
let settingsWindow;
let captureWindow;
let tray;
let registeredShortcuts = {};
let localOCR;
let agentBridge;
let lastAgentCapture;
const pinWindows = new Map();
const editorWindows = new Map();

function settingsFile() { return path.join(app.getPath("userData"), "settings.json"); }
function agentAuditFile() { return path.join(app.getPath("userData"), "agent-audit-v1.json"); }
function agentArtifactsDirectory() { return path.join(app.getPath("userData"), "agent-artifacts"); }

async function loadSettings() {
  try { settings = normalizeSettings(JSON.parse(await fs.readFile(settingsFile(), "utf8"))); }
  catch { settings = normalizeSettings(DEFAULT_SETTINGS); }
}

async function persistSettings() {
  await fs.mkdir(path.dirname(settingsFile()), { recursive: true });
  await fs.writeFile(settingsFile(), `${JSON.stringify(settings, null, 2)}\n`, "utf8");
}

async function appendAgentAudit(entry) {
  const retentionDays = Math.min(90, Math.max(1, Number(settings.agent.auditRetentionDays) || 14));
  const cutoff = Date.now() - retentionDays * 24 * 60 * 60 * 1000;
  let records = [];
  try { records = JSON.parse(await fs.readFile(agentAuditFile(), "utf8")); } catch { records = []; }
  if (!Array.isArray(records)) records = [];
  records.push({ occurredAt: new Date().toISOString(), method: String(entry.method ?? "unknown"), client: String(entry.client ?? "unknown").slice(0, 80), outcome: entry.outcome === "allowed" ? "allowed" : "rejected", cloudUploaded: Boolean(entry.cloudUploaded) });
  records = records.filter((record) => Date.parse(record.occurredAt) >= cutoff).slice(-500);
  await fs.mkdir(path.dirname(agentAuditFile()), { recursive: true });
  await fs.writeFile(agentAuditFile(), `${JSON.stringify(records, null, 2)}\n`, "utf8");
}

function publicState() {
  return {
    ...publicSettings(settings),
    providerPresets: PROVIDER_PRESETS,
    hotkeyActions: HOTKEY_ACTIONS,
    recognitionTasks: RECOGNITION_TASKS,
    agentInstallation: agentInstallationStatus(),
    agentInstallCommand: WINDOWS_INSTALL_COMMAND,
    agentInstallPrompt: WINDOWS_AGENT_INSTALL_PROMPT
  };
}

function agentInstallationStatus() {
  const home = app.getPath("home");
  const codexHome = process.env.CODEX_HOME || path.join(home, ".codex");
  const cliPath = path.join(home, ".local", "bin", "ta.cmd");
  const locations = [
    path.join(codexHome, "skills", "ta"),
    path.join(home, ".agents", "skills", "ta"),
    path.join(home, ".claude", "skills", "ta")
  ].filter((location) => fsSync.existsSync(path.join(location, "SKILL.md")));
  return { cliInstalled: fsSync.existsSync(cliPath), installedSkillLocations: locations };
}
function profileById(id) { return settings.profiles.find((profile) => profile.id === id); }

function decryptApiKey(profile) {
  if (!profile?.apiKeyEncrypted) return "";
  if (!safeStorage.isEncryptionAvailable()) throw new Error("Windows 数据保护服务不可用，无法读取 API Key。");
  return safeStorage.decryptString(Buffer.from(profile.apiKeyEncrypted, "base64"));
}

function encryptApiKey(apiKey) {
  if (!safeStorage.isEncryptionAvailable()) throw new Error("Windows 数据保护服务不可用，无法保存 API Key。");
  return safeStorage.encryptString(apiKey).toString("base64");
}

function notify(title, body) { if (Notification.isSupported()) new Notification({ title, body }).show(); }

function applyGeneralSettings() {
  if (process.platform === "win32") app.setLoginItemSettings({ openAtLogin: Boolean(settings.general.launchOnLogin) });
}

function shortcutHandler(action) {
  switch (action) {
    case "intelligentCapture": return () => void startCapture("vision");
    case "interactiveCapture": return () => void startCapture();
    case "imageCapture": return () => void startCapture("copy");
    case "pinCapture": return () => void startCapture("pin");
    case "translationCapture": return () => void startCapture("translate");
    case "longCapture": return () => notify(PRODUCT_NAME, "Windows 自动滚动长截图正在接入 UI Automation 模块。");
    default: return () => undefined;
  }
}

function registerShortcuts(next = settings.shortcuts) {
  const previous = { ...registeredShortcuts };
  const seen = new Set();
  for (const action of HOTKEY_ACTIONS) {
    const accelerator = String(next[action.id] ?? "").trim();
    if (!accelerator) throw new Error(`请为“${action.title}”设置快捷键。`);
    if (seen.has(accelerator.toLowerCase())) throw new Error(`快捷键重复：${accelerator}`);
    seen.add(accelerator.toLowerCase());
  }
  globalShortcut.unregisterAll();
  try {
    for (const action of HOTKEY_ACTIONS) {
      const accelerator = next[action.id];
      if (!globalShortcut.register(accelerator, shortcutHandler(action.id))) throw new Error(`无法注册“${action.title}”：${accelerator}`);
    }
    registeredShortcuts = { ...next };
  } catch (error) {
    globalShortcut.unregisterAll();
    for (const action of HOTKEY_ACTIONS) {
      if (previous[action.id]) globalShortcut.register(previous[action.id], shortcutHandler(action.id));
    }
    registeredShortcuts = previous;
    throw error;
  }
}

function makeWindow(file, { loadOptions, ...options } = {}) {
  const win = new BrowserWindow({
    show: false, backgroundColor: "#191716",
    webPreferences: { preload: path.join(__dirname, "preload.cjs"), contextIsolation: true, nodeIntegration: false, sandbox: true },
    ...options
  });
  win.once("ready-to-show", () => win.show());
  void win.loadFile(path.join(__dirname, "renderer", file), loadOptions);
  return win;
}

function openSettings() {
  if (settingsWindow && !settingsWindow.isDestroyed()) { settingsWindow.show(); settingsWindow.focus(); return; }
  settingsWindow = makeWindow("settings.html", { title: PRODUCT_NAME, width: 1180, height: 800, minWidth: 920, minHeight: 650, autoHideMenuBar: true });
  settingsWindow.on("closed", () => { settingsWindow = undefined; });
}

async function screenFrameUnderCursor() {
  const display = screen.getDisplayNearestPoint(screen.getCursorScreenPoint());
  const thumbnailSize = { width: Math.round(display.size.width * display.scaleFactor), height: Math.round(display.size.height * display.scaleFactor) };
  const sources = await desktopCapturer.getSources({ types: ["screen"], thumbnailSize });
  const source = sources.find((candidate) => candidate.display_id === String(display.id)) ?? sources[0];
  if (!source || source.thumbnail.isEmpty()) throw new Error("无法读取当前显示器画面。");
  return { display, imageDataUrl: source.thumbnail.toDataURL() };
}

async function startCapture(defaultAction) {
  if (captureWindow && !captureWindow.isDestroyed()) return;
  try {
    const { display, imageDataUrl } = await screenFrameUnderCursor();
    captureWindow = new BrowserWindow({
      x: display.bounds.x, y: display.bounds.y, width: display.bounds.width, height: display.bounds.height,
      frame: false, transparent: false, resizable: false, movable: false, fullscreenable: false, skipTaskbar: true,
      alwaysOnTop: true, backgroundColor: "#111111",
      webPreferences: { preload: path.join(__dirname, "preload.cjs"), contextIsolation: true, nodeIntegration: false, sandbox: true }
    });
    captureWindow.setAlwaysOnTop(true, "screen-saver");
    captureWindow.setVisibleOnAllWorkspaces(true, { visibleOnFullScreen: true });
    captureWindow.once("ready-to-show", () => {
      captureWindow?.show(); captureWindow?.focus(); captureWindow?.webContents.send("capture:frame", { imageDataUrl, defaultAction });
    });
    captureWindow.on("closed", () => { captureWindow = undefined; });
    await captureWindow.loadFile(path.join(__dirname, "renderer", "overlay.html"));
  } catch (error) { notify(PRODUCT_NAME, error.message || "截图启动失败。"); }
}

function closeCapture() { if (captureWindow && !captureWindow.isDestroyed()) captureWindow.close(); }
function dataUrlToImage(dataUrl) { const image = nativeImage.createFromDataURL(dataUrl); if (image.isEmpty()) throw new Error("截图数据无效。"); return image; }

function dialogParent(event) {
  const win = BrowserWindow.fromWebContents(event.sender);
  return win && !win.isDestroyed() ? win : undefined;
}

async function saveImage(dataUrl, parentWindow) {
  const defaultPath = path.join(app.getPath("pictures"), `Ta-${new Date().toISOString().replace(/[:.]/g, "-")}.png`);
  const options = { title: "保存截图", defaultPath, filters: [{ name: "PNG 图片", extensions: ["png"] }] };
  // 截图覆盖层处于最高层；将原生对话框绑定为其模态子窗口，避免被覆盖层遮住。
  const choice = parentWindow
    ? await dialog.showSaveDialog(parentWindow, options)
    : await dialog.showSaveDialog(options);
  if (choice.canceled || !choice.filePath) return { canceled: true };
  await fs.writeFile(choice.filePath, dataUrlToImage(dataUrl).toPNG());
  if (settings.general.revealSavedFile) shell.showItemInFolder(choice.filePath);
  notify(PRODUCT_NAME, `已保存：${path.basename(choice.filePath)}`);
  return { canceled: false, filePath: choice.filePath };
}

function createPin(dataUrl) {
  const token = crypto.randomUUID(); const image = dataUrlToImage(dataUrl); const size = image.getSize();
  const width = Math.min(760, Math.max(220, size.width)); const height = Math.min(620, Math.max(110, Math.round(width * size.height / size.width) + 42));
  const win = new BrowserWindow({ title: `${PRODUCT_NAME} · 钉图`, width, height, minWidth: 160, minHeight: 110, frame: false, transparent: true, alwaysOnTop: true, resizable: true,
    webPreferences: { preload: path.join(__dirname, "preload.cjs"), contextIsolation: true, nodeIntegration: false, sandbox: true } });
  win.setAlwaysOnTop(true, "floating"); pinWindows.set(token, { win, dataUrl }); win.on("closed", () => pinWindows.delete(token));
  void win.loadFile(path.join(__dirname, "renderer", "pin.html"), { query: { token } });
}

function createEditor(dataUrl) {
  const token = crypto.randomUUID();
  const win = makeWindow("editor.html", { title: `${PRODUCT_NAME} · 标注`, width: 1060, height: 760, minWidth: 680, minHeight: 500, autoHideMenuBar: true, loadOptions: { query: { token } } });
  editorWindows.set(token, { win, dataUrl }); win.on("closed", () => editorWindows.delete(token));
}

function promptForAction(action) {
  if (action === "ocr") return TASK_PROMPTS.extractText;
  if (action === "translate") return `识别图片内容，并从“${settings.translation.sourceLanguage}”翻译为“${settings.translation.targetLanguage}”。保留段落、列表、表格与代码结构；只输出译文。`;
  return TASK_PROMPTS[settings.recognition.defaultTask] ?? TASK_PROMPTS.general;
}

async function runVision(action, imageDataUrl) {
  if (!settings.recognition.cloudEnabled) throw new Error("云端 AI 已在“识别”设置中关闭。");
  const profileId = action === "translate" ? settings.translationProfileId : settings.activeProfileId;
  const profile = profileById(profileId);
  const validation = profileValidationMessage(profile, { requiresVision: true });
  if (validation) throw new Error(validation);
  const text = await callVision(profile, decryptApiKey(profile), imageDataUrl, promptForAction(action));
  clipboard.writeText(text); notify(PRODUCT_NAME, action === "translate" ? "译文已复制到剪贴板" : "结果已复制到剪贴板");
  return text;
}

async function runLocalOCR(imageDataUrl, languages = settings.recognition.localLanguages) {
  if (!settings.recognition.localEnabled) throw new Error("本地 OCR 已在“识别”设置中关闭。");
  const result = await localOCR.recognize(imageDataUrl, languages);
  clipboard.writeText(result.text);
  notify(PRODUCT_NAME, `本地取字已复制（置信度 ${result.confidence}%）`);
  return result;
}

function agentFailure(requestId, code, message, retryable = false, startedAt = Date.now()) {
  return { protocolVersion: PROTOCOL_VERSION, requestId, ok: false, artifacts: [], meta: { durationMs: Math.max(0, Date.now() - startedAt), cloudUploaded: false }, error: { code, message, retryable } };
}

function agentSuccess(requestId, data, artifacts = [], cloudUploaded = false, startedAt = Date.now()) {
  return { protocolVersion: PROTOCOL_VERSION, requestId, ok: true, data, artifacts, meta: { durationMs: Math.max(0, Date.now() - startedAt), cloudUploaded } };
}

async function saveAgentCapture(imageDataUrl) {
  await fs.mkdir(agentArtifactsDirectory(), { recursive: true });
  const expiration = Date.now() - 30 * 60 * 1000;
  for (const entry of await fs.readdir(agentArtifactsDirectory(), { withFileTypes: true })) {
    if (!entry.isFile() || !entry.name.endsWith(".png")) continue;
    const candidate = path.join(agentArtifactsDirectory(), entry.name);
    try { if ((await fs.stat(candidate)).mtimeMs < expiration) await fs.unlink(candidate); } catch { /* a concurrent cleanup is harmless */ }
  }
  const id = crypto.randomUUID();
  const filePath = path.join(agentArtifactsDirectory(), `${id}.png`);
  await fs.writeFile(filePath, dataUrlToImage(imageDataUrl).toPNG());
  lastAgentCapture = { id, filePath, imageDataUrl, createdAt: new Date().toISOString() };
  return { id, path: filePath, mimeType: "image/png", expiresAt: new Date(Date.now() + 30 * 60 * 1000).toISOString() };
}

async function routeAgentRequest(request) {
  const startedAt = Date.now();
  const statusData = {
    bridge: settings.agent.enabled && agentBridge ? "ready" : "disabled",
    version: app.getVersion(), pipe: AGENT_PIPE_NAME,
    authorization: settings.agent.enabled ? "current_windows_user_acl" : "disabled"
  };
  if (request.method === "system.status") return agentSuccess(request.requestId, statusData, [], false, startedAt);
  if (!settings.agent.enabled) return agentFailure(request.requestId, "AGENT_DISABLED", "请先在“设置 → Agent”中启用本机 Agent Bridge。", false, startedAt);

  if (request.method === "system.capabilities") {
    return agentSuccess(request.requestId, { methods: ["system.status", "system.capabilities", "system.permissions", "capture.display", "recognize.ocr"], protocolVersion: PROTOCOL_VERSION, cloud: "deny" }, [], false, startedAt);
  }
  if (request.method === "system.permissions") {
    return agentSuccess(request.requestId, { screenCapture: "Windows Desktop Capture", localOCR: settings.recognition.localEnabled ? "enabled" : "disabled", cloudForAgent: settings.agent.allowCloud ? "enabled" : "disabled" }, [], false, startedAt);
  }
  if (request.method === "capture.display") {
    const { display, imageDataUrl } = await screenFrameUnderCursor();
    const artifact = await saveAgentCapture(imageDataUrl);
    return agentSuccess(request.requestId, { target: { kind: "display", displayId: String(display.id), bounds: display.bounds } }, [artifact], false, startedAt);
  }
  if (request.method === "recognize.ocr") {
    if (!settings.recognition.localEnabled) return agentFailure(request.requestId, "LOCAL_OCR_DISABLED", "本地 OCR 已在设置中关闭。", false, startedAt);
    const artifactId = String(request.params?.artifactId ?? "");
    if (!lastAgentCapture || artifactId !== lastAgentCapture.id || Date.now() - Date.parse(lastAgentCapture.createdAt) > 30 * 60 * 1000) return agentFailure(request.requestId, "ARTIFACT_NOT_FOUND", "请先用 capture.display 创建当前会话的截图工件。", false, startedAt);
    const result = await localOCR.recognize(lastAgentCapture.imageDataUrl, request.params?.languages ?? settings.recognition.localLanguages);
    return agentSuccess(request.requestId, result, [], false, startedAt);
  }
  return agentFailure(request.requestId, "CAPABILITY_NOT_AVAILABLE", `Windows Bridge 尚未公开方法：${request.method}`, false, startedAt);
}

async function reconcileAgentBridge() {
  if (agentBridge) { await agentBridge.stop(); agentBridge = undefined; }
  if (!settings.agent.enabled) return;
  agentBridge = new NamedPipeAgentBridge({
    pipeName: AGENT_PIPE_NAME,
    handleRequest: routeAgentRequest,
    onAudit: (entry) => { void appendAgentAudit(entry); }
  });
  try { await agentBridge.start(); }
  catch (error) { agentBridge = undefined; throw new Error(`Agent Bridge 无法启动：${error.message || error}`); }
}

async function testProfile(draft) {
  const existing = profileById(draft.id) ?? {};
  const profile = normalizeProfile(draft, existing);
  const apiKey = String(draft.apiKey ?? "").trim() || decryptApiKey(existing);
  if (!apiKey) throw new Error("请填写 API Key 后再测试。 ");
  const result = await callVision(profile, apiKey, TEST_IMAGE, "这是连接测试。请只回复：连接成功");
  return { result, testedAt: new Date().toISOString() };
}

function createTray() {
  tray = new Tray(path.join(__dirname, "renderer", "tray-icon.png")); tray.setToolTip(PRODUCT_NAME); refreshTrayMenu(); tray.on("double-click", openSettings);
}

function refreshTrayMenu() {
  if (!tray) return;
  tray.setContextMenu(Menu.buildFromTemplate([
    { label: `通用截图（${settings.shortcuts.interactiveCapture}）`, click: () => void startCapture() },
    { label: "设置", click: openSettings }, { type: "separator" }, { label: "退出", click: () => app.quit() }
  ]));
}

async function saveSettingsPatch(patch) {
  const next = normalizeSettings({ ...settings, ...patch }, settings);
  const shortcutsChanged = JSON.stringify(next.shortcuts) !== JSON.stringify(settings.shortcuts);
  const agentChanged = JSON.stringify(next.agent) !== JSON.stringify(settings.agent);
  if (shortcutsChanged) registerShortcuts(next.shortcuts);
  settings = next; applyGeneralSettings(); await persistSettings();
  if (agentChanged || (settings.agent.enabled && !agentBridge)) await reconcileAgentBridge();
  refreshTrayMenu(); return publicState();
}

async function saveProfile(draft) {
  const index = settings.profiles.findIndex((profile) => profile.id === draft.id);
  const previous = index >= 0 ? settings.profiles[index] : {};
  const profile = normalizeProfile(draft, previous);
  if (String(draft.apiKey ?? "").trim()) profile.apiKeyEncrypted = encryptApiKey(String(draft.apiKey).trim());
  if (draft.removeApiKey) profile.apiKeyEncrypted = "";
  const profiles = [...settings.profiles]; if (index >= 0) profiles[index] = profile; else profiles.push(profile);
  const saved = await saveSettingsPatch({ profiles, activeProfileId: settings.activeProfileId || profile.id, translationProfileId: settings.translationProfileId || profile.id });
  return { settings: saved, profile: publicSettings({ ...settings, profiles: [profile] }).profiles[0] };
}

function bindIpc() {
  ipcMain.handle("settings:read", () => publicState());
  ipcMain.handle("settings:save", (_event, patch) => saveSettingsPatch(patch));
  ipcMain.handle("profiles:create", async (_event, presetId) => saveProfile(createProfileFromPreset(presetId)));
  ipcMain.handle("profiles:save", (_event, draft) => saveProfile(draft));
  ipcMain.handle("profiles:delete", async (_event, id) => {
    const profiles = settings.profiles.filter((profile) => profile.id !== id);
    return saveSettingsPatch({ profiles, activeProfileId: settings.activeProfileId === id ? profiles[0]?.id ?? "" : settings.activeProfileId, translationProfileId: settings.translationProfileId === id ? profiles[0]?.id ?? "" : settings.translationProfileId });
  });
  ipcMain.handle("profiles:test", (_event, draft) => testProfile(draft));
  ipcMain.handle("profiles:select", (_event, roles) => saveSettingsPatch(roles));
  ipcMain.handle("system:status", () => ({ dataPath: app.getPath("userData"), platform: process.platform, version: app.getVersion(), screenCapture: "Windows 桌面捕获已就绪", agentBridge: settings.agent.enabled && agentBridge ? "已启用" : "未启用" }));
  ipcMain.handle("system:open-data", () => shell.openPath(app.getPath("userData")));
  ipcMain.handle("system:open-privacy", () => shell.openExternal("ms-settings:privacy"));
  ipcMain.handle("agent:installation-status", () => agentInstallationStatus());
  ipcMain.handle("agent:copy-installation", (_event, kind) => {
    const content = kind === "prompt" ? WINDOWS_AGENT_INSTALL_PROMPT : WINDOWS_INSTALL_COMMAND;
    clipboard.writeText(content);
    return { copied: kind === "prompt" ? "prompt" : "command" };
  });
  ipcMain.handle("ocr:status", () => ({ enabled: Boolean(settings.recognition.localEnabled), languages: settings.recognition.localLanguages }));
  ipcMain.handle("ocr:warmup", async (_event, languages) => localOCR.warmUp(languages ?? settings.recognition.localLanguages));
  ipcMain.handle("capture:start", (_event, action) => void startCapture(action));
  ipcMain.handle("capture:cancel", closeCapture);
  ipcMain.handle("capture:copy", (_event, dataUrl) => { clipboard.writeImage(dataUrlToImage(dataUrl)); notify(PRODUCT_NAME, "截图已复制到剪贴板"); closeCapture(); });
  ipcMain.handle("capture:save", async (event, dataUrl) => { const result = await saveImage(dataUrl, dialogParent(event)); if (!result.canceled) closeCapture(); return result; });
  ipcMain.handle("capture:pin", (_event, dataUrl) => { createPin(dataUrl); closeCapture(); });
  ipcMain.handle("capture:edit", (_event, dataUrl) => { createEditor(dataUrl); closeCapture(); });
  ipcMain.handle("capture:vision", async (_event, action, dataUrl) => { const result = action === "ocr" ? await runLocalOCR(dataUrl) : await runVision(action, dataUrl); closeCapture(); return result; });
  ipcMain.on("pin:ready", (event, token) => { const entry = pinWindows.get(token); if (entry) event.sender.send("pin:image", entry.dataUrl); });
  ipcMain.handle("pin:copy", (_event, dataUrl) => clipboard.writeImage(dataUrlToImage(dataUrl)));
  ipcMain.handle("pin:save", (event, dataUrl) => saveImage(dataUrl, dialogParent(event)));
  ipcMain.handle("pin:close", (_event, token) => pinWindows.get(token)?.win.close());
  ipcMain.on("editor:ready", (event, token) => { const entry = editorWindows.get(token); if (entry) event.sender.send("editor:image", entry.dataUrl); });
  ipcMain.handle("editor:copy", (_event, dataUrl) => clipboard.writeImage(dataUrlToImage(dataUrl)));
  ipcMain.handle("editor:save", (event, dataUrl) => saveImage(dataUrl, dialogParent(event)));
  ipcMain.handle("editor:pin", (_event, dataUrl) => createPin(dataUrl));
  ipcMain.handle("editor:close", (_event, token) => editorWindows.get(token)?.win.close());
}

app.whenReady().then(async () => {
  app.setAppUserModelId("com.kangarooking.ta.windows"); await loadSettings();
  localOCR = new LocalOCRService({ cachePath: path.join(app.getPath("userData"), "ocr-cache") });
  applyGeneralSettings(); bindIpc();
  try { await reconcileAgentBridge(); } catch (error) { notify(PRODUCT_NAME, error.message || "Agent Bridge 启动失败。"); }
  try { registerShortcuts(); } catch (error) { notify(PRODUCT_NAME, error.message); }
  createTray(); openSettings();
});
app.on("will-quit", () => { globalShortcut.unregisterAll(); void agentBridge?.stop(); void localOCR?.terminate(); });
app.on("activate", openSettings);
