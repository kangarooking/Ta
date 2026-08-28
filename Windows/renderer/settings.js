const panel = document.querySelector("#panel");
const toast = document.querySelector("#toast");
const title = document.querySelector("#section-title");
const kicker = document.querySelector("#section-kicker");
const tabLabels = { permissions: ["权限", "SYSTEM ACCESS"], general: ["常规", "GENERAL"], shortcuts: ["快捷键", "GLOBAL SHORTCUTS"], recognition: ["识别", "RECOGNITION"], translation: ["翻译", "TRANSLATION"], models: ["AI 模型", "MODEL CONFIGURATION"], agent: ["Agent", "AUTOMATION BOUNDARIES"] };
let state;
let activeTab = "models";
let profileDraft;
let toastTimer;

const escape = (value) => String(value ?? "").replace(/[&<>'"]/g, (character) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "'": "&#39;", '"': "&quot;" })[character]);
const option = (value, label, current) => `<option value="${escape(value)}" ${value === current ? "selected" : ""}>${escape(label)}</option>`;

function showToast(message) {
  toast.textContent = message;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { toast.textContent = ""; }, 5000);
}

async function refresh() {
  state = await window.ta.invoke("settings:read");
  if (!profileDraft || !state.profiles.some((profile) => profile.id === profileDraft.id)) profileDraft = state.profiles[0] ? { ...state.profiles[0], apiKey: "", removeApiKey: false } : undefined;
  render();
}

function heading(name, detail) { return `<div class="section-heading"><h2>${name}</h2><p>${detail}</p></div>`; }
function checked(value) { return value ? "checked" : ""; }
function profileName(id) { return state.profiles.find((profile) => profile.id === id)?.name ?? "尚未选择"; }

function modelsPanel() {
  if (!profileDraft) {
    return `${heading("AI 模型", "配置一次，即可用于 AI 识图与截图翻译。API Key 只以 Windows DPAPI 加密形式保存。")}
      <div class="empty"><div><b>还没有 AI 配置</b><p>先选择一个服务商预设；你也可以随时新建多套配置。</p><button class="primary" data-action="create-profile" data-preset="zhipu-api">+ 新建推荐配置</button></div></div>`;
  }
  const draft = profileDraft;
  const profileItems = state.profiles.map((profile) => `<button class="profile-item ${profile.id === draft.id ? "active" : ""}" data-action="select-profile" data-id="${profile.id}"><i class="dot ${profile.hasApiKey ? "" : "off"}"></i><strong>${escape(profile.name)}</strong><small>${escape(profileNameForPreset(profile.presetId))}${profile.id === state.activeProfileId ? " · 识图" : ""}${profile.id === state.translationProfileId ? " · 翻译" : ""}</small></button>`).join("");
  const presets = state.providerPresets.map((preset) => `<button class="provider-card ${draft.presetId === preset.id ? "selected" : ""}" data-action="apply-preset" data-preset="${preset.id}"><span class="provider-mark">${escape(preset.name.slice(0, 1))}</span><span><strong>${escape(preset.name)}${preset.badge ? `<em class="badge">${escape(preset.badge)}</em>` : ""}</strong><small>${escape(preset.description)}</small></span></button>`).join("");
  return `${heading("AI 模型", "多配置、预设、连接测试与用途分流均在此处管理。")}
    <div class="button-row"><p class="muted">当前识图：<b>${escape(profileName(state.activeProfileId))}</b>　截图翻译：<b>${escape(profileName(state.translationProfileId))}</b></p><button class="primary" data-action="create-profile" data-preset="custom">+ 新增</button></div>
    <div class="models-layout"><aside class="profile-list">${profileItems}</aside><section>
      <div class="steps"><div class="step"><b>1</b>选择服务商</div><div class="step"><b>2</b>填写 API Key</div><div class="step"><b>3</b>测试并保存</div></div>
      <div class="card"><h3>选择 AI 服务商</h3><p class="muted">选择预设后会填入接口与推荐模型；你仍可自行修改。</p><div class="provider-grid">${presets}</div></div>
      <div class="card"><h3>配置 ${escape(draft.name)}</h3><div class="fields">
        ${field("配置名称", "name", draft.name)}${field("协议", "protocol", draft.protocol, ["openai", "azure", "anthropic", "gemini"])}
        ${field("服务地址", "baseUrl", draft.baseUrl, null, true)}${field("视觉模型", "visionModel", draft.visionModel)}${field("文字模型（可选）", "textModel", draft.textModel)}
        <label class="field"><span>API Key ${draft.hasApiKey ? "（已保存）" : ""}</span><input data-profile-field="apiKey" type="password" autocomplete="off" placeholder="留空则保留当前 Key"></label>
        <label class="check"><input data-profile-field="removeApiKey" type="checkbox" ${checked(draft.removeApiKey)}> 删除已保存的 API Key</label>
      </div><div class="form-actions"><span class="muted">${draft.verifiedAt ? `最近测试：${new Date(draft.verifiedAt).toLocaleString()}` : "尚未完成连接测试"}</span><button class="secondary" data-action="test-profile">测试连接</button><button class="primary" data-action="save-profile">保存配置</button></div></div>
      <div class="card"><h3>这套配置的用途</h3><div class="button-row"><p class="muted">当前可把同一套模型用于识图与翻译，也可以分开指定。</p><div><button class="secondary" data-action="set-active" ${draft.id === state.activeProfileId ? "disabled" : ""}>用于识图</button> <button class="secondary" data-action="set-translation" ${draft.id === state.translationProfileId ? "disabled" : ""}>用于翻译</button> <button class="danger" data-action="delete-profile">删除</button></div></div></div>
    </section></div>`;
}

function profileNameForPreset(id) { return state.providerPresets.find((preset) => preset.id === id)?.name ?? "自定义"; }
function field(label, key, value, options, full) {
  const control = options ? `<select data-profile-field="${key}">${options.map((entry) => option(entry, entry, value)).join("")}</select>` : `<input data-profile-field="${key}" value="${escape(value)}" spellcheck="false">`;
  return `<label class="field ${full ? "full" : ""}"><span>${label}</span>${control}</label>`;
}

function permissionsPanel() {
  return `${heading("权限与本机数据", "Windows 使用桌面捕获；不会持续录屏，只在你主动发起截图时读取当前显示器。")}
    <div class="section-grid"><div class="card"><h3>屏幕捕获</h3><p class="muted">当前版本通过 Windows 桌面捕获获取鼠标所在显示器的静态画面。</p><div class="info-row"><span><strong id="capture-status">正在检查…</strong><small>单显示器选区已就绪；跨显示器选区进入下一阶段。</small></span><button class="secondary" data-action="open-privacy">打开 Windows 隐私设置</button></div></div>
    <div class="card"><h3>本机数据</h3><p class="muted">设置和经过 DPAPI 加密的 API Key 保存在当前 Windows 用户目录。</p><div class="info-row"><span><strong id="data-path">正在读取…</strong><small>不会写入截图识别正文或图片。</small></span><button class="secondary" data-action="open-data">打开数据目录</button></div></div></div>`;
}

function generalPanel() {
  const value = state.general;
  return `${heading("常规", "控制启动、托盘与保存后的 Windows 行为。")}<div class="card"><h3>运行方式</h3><div class="fields one">
    <label class="check"><input data-general="launchOnLogin" type="checkbox" ${checked(value.launchOnLogin)}> 登录 Windows 时启动拓</label>
    <label class="check"><input data-general="minimizeToTray" type="checkbox" ${checked(value.minimizeToTray)}> 关闭设置窗口后继续在系统托盘运行</label>
    <label class="check"><input data-general="revealSavedFile" type="checkbox" ${checked(value.revealSavedFile)}> 保存截图后在文件资源管理器中定位文件</label>
  </div><div class="form-actions"><button class="primary" data-action="save-general">保存常规设置</button></div></div>`;
}

function shortcutsPanel() {
  const rows = state.hotkeyActions.map((action) => `<tr><td><strong>${escape(action.title)}</strong><small>${escape(action.description)}</small></td><td><input data-shortcut="${action.id}" value="${escape(state.shortcuts[action.id])}" spellcheck="false"></td></tr>`).join("");
  return `${heading("快捷键", "六个全局快捷键会在保存时一次注册：有冲突或非法格式时保留上一次有效配置。")}<div class="card"><table class="shortcut-table"><thead><tr><th>功能</th><th>全局快捷键</th></tr></thead><tbody>${rows}</tbody></table><div class="form-actions"><button class="secondary" data-action="restore-shortcuts">恢复默认</button><button class="primary" data-action="save-shortcuts">保存并应用</button></div></div>`;
}

function recognitionPanel() {
  const options = state.recognitionTasks.map((task) => option(task.id, task.title, state.recognition.defaultTask)).join("");
  const profiles = state.profiles.map((profile) => option(profile.id, profile.name, state.activeProfileId)).join("");
  return `${heading("识别", "本地取字不上传截图；AI 识图和翻译才使用你选择的云端配置。")}<div class="card"><div class="fields one"><label class="check"><input data-recognition="localEnabled" type="checkbox" ${checked(state.recognition.localEnabled)}> 启用 Windows 本地 OCR</label><label class="field"><span>本地 OCR 语言包</span><select data-recognition="localLanguages">${option("chi_sim+eng", "简体中文 + 英文", state.recognition.localLanguages)}${option("eng", "英文", state.recognition.localLanguages)}${option("chi_tra+eng", "繁体中文 + 英文", state.recognition.localLanguages)}${option("jpn+eng", "日文 + 英文", state.recognition.localLanguages)}${option("kor+eng", "韩文 + 英文", state.recognition.localLanguages)}</select></label><label class="check"><input data-recognition="cloudEnabled" type="checkbox" ${checked(state.recognition.cloudEnabled)}> 允许主动框选的图片发送到已配置的云端 AI</label><label class="field"><span>默认智能识图任务</span><select data-recognition="defaultTask">${options}</select></label><label class="field"><span>用于识图的 AI 配置</span><select data-setting="activeProfileId">${profiles || "<option value=''>请先创建 AI 配置</option>"}</select></label></div><div class="form-actions"><button class="secondary" data-action="warmup-ocr">下载/预热本地语言包</button><button class="primary" data-action="save-recognition">保存识别设置</button></div></div>`;
}

function translationPanel() {
  const profiles = state.profiles.map((profile) => option(profile.id, profile.name, state.translationProfileId)).join("");
  return `${heading("翻译", "翻译会使用你指定的专属配置；翻译结果会复制到剪贴板，完整保留段落、列表、表格与代码结构。")}<div class="card"><div class="fields"><label class="field"><span>源语言</span><input data-translation="sourceLanguage" value="${escape(state.translation.sourceLanguage)}"></label><label class="field"><span>目标语言</span><input data-translation="targetLanguage" value="${escape(state.translation.targetLanguage)}"></label><label class="field full"><span>用于翻译的 AI 配置</span><select data-setting="translationProfileId">${profiles || "<option value=''>请先创建 AI 配置</option>"}</select></label></div><div class="form-actions"><button class="primary" data-action="save-translation">保存翻译设置</button></div></div><div class="coming">图片内文字替换与原图双语对照需要 Windows OCR 的文字定位；该模式会在本地 OCR 模块完成后启用，不会提前显示为可用。</div>`;
}

function agentPanel() {
  const installation = state.agentInstallation ?? { cliInstalled: false, installedSkillLocations: [] };
  const skillCount = installation.installedSkillLocations.length;
  const badge = (label, installed) => `<span class="install-badge ${installed ? "installed" : ""}"><b>${installed ? "✓" : "○"}</b>${escape(label)}</span>`;
  const block = (title, subtitle, icon, content, kind) => `<article class="install-block"><div class="install-block-header"><span class="install-icon">${icon}</span><div><strong>${escape(title)}</strong><small>${escape(subtitle)}</small></div><button class="secondary install-copy" data-action="copy-agent-install" data-kind="${kind}">▧　复制</button></div><code>${escape(content)}</code></article>`;
  return `<section class="agent-installation"><div class="install-heading"><div><h2>安装 CLI 与 Skill</h2>${badge("ta CLI", installation.cliInstalled)}${badge(skillCount ? `Ta Skill · ${skillCount} 处` : "Ta Skill", skillCount > 0)}</div><button class="install-refresh" data-action="refresh-agent-installation">↻　重新检测</button></div><p>一条命令同时安装或更新 CLI 与 Skill；自动校验下载文件，并适配 Codex 和通用 Agent Skills 目录。</p><div class="install-blocks">${block("方式一 · 终端", "复制命令并执行", "⌘", state.agentInstallCommand, "command")}${block("方式二 · 交给 Agent", "复制完整安装提示词", "✦", state.agentInstallPrompt, "prompt")}</div><div class="install-note">♢　安装后重启 Agent，再运行 <code>ta status --json</code> 验证连接。</div></section>`;
}

function render() {
  const [label, tag] = tabLabels[activeTab]; title.textContent = label; kicker.textContent = tag;
  document.querySelectorAll("nav button").forEach((button) => button.classList.toggle("active", button.dataset.tab === activeTab));
  panel.innerHTML = ({ models: modelsPanel, permissions: permissionsPanel, general: generalPanel, shortcuts: shortcutsPanel, recognition: recognitionPanel, translation: translationPanel, agent: agentPanel })[activeTab]();
  if (activeTab === "permissions") void loadSystemStatus();
}

async function loadSystemStatus() {
  try { const status = await window.ta.invoke("system:status"); const capture = document.querySelector("#capture-status"); const data = document.querySelector("#data-path"); if (capture) capture.textContent = status.screenCapture; if (data) data.textContent = status.dataPath; } catch (error) { showToast(error.message || "无法读取系统状态。"); }
}

function setDraftField(element) { profileDraft = { ...profileDraft, [element.dataset.profileField]: element.type === "checkbox" ? element.checked : element.value }; }
function applyPreset(id) { const preset = state.providerPresets.find((entry) => entry.id === id); if (!preset) return; profileDraft = { ...profileDraft, presetId: preset.id, protocol: preset.protocol, baseUrl: preset.baseUrl, visionModel: preset.visionModel, textModel: preset.textModel, name: profileDraft.name || `${preset.name} 配置` }; render(); }
function genericPatch(prefix) { return Object.fromEntries([...panel.querySelectorAll(`[data-${prefix}]`)].map((element) => [element.dataset[prefix], element.type === "checkbox" ? element.checked : element.value])); }

document.querySelector("nav").addEventListener("click", (event) => { const button = event.target.closest("button[data-tab]"); if (!button) return; activeTab = button.dataset.tab; render(); });
document.querySelector("#capture-now").addEventListener("click", () => window.ta.invoke("capture:start"));

panel.addEventListener("input", (event) => { if (event.target.dataset.profileField) setDraftField(event.target); });
panel.addEventListener("change", (event) => {
  if (event.target.dataset.profileField) setDraftField(event.target);
  if (event.target.dataset.profileField === "presetId") applyPreset(event.target.value);
});

panel.addEventListener("click", async (event) => {
  const action = event.target.closest("button[data-action]")?.dataset.action; if (!action) return;
  try {
    if (action === "create-profile") { const result = await window.ta.invoke("profiles:create", event.target.closest("button").dataset.preset); profileDraft = { ...result.profile, apiKey: "", removeApiKey: false }; await refresh(); return; }
    if (action === "select-profile") { const id = event.target.closest("button").dataset.id; profileDraft = { ...state.profiles.find((profile) => profile.id === id), apiKey: "", removeApiKey: false }; render(); return; }
    if (action === "apply-preset") return applyPreset(event.target.closest("button").dataset.preset);
    if (action === "save-profile") { const result = await window.ta.invoke("profiles:save", profileDraft); profileDraft = { ...result.profile, apiKey: "", removeApiKey: false }; await refresh(); showToast("AI 配置已保存。"); return; }
    if (action === "test-profile") { showToast("正在测试连接…"); const result = await window.ta.invoke("profiles:test", profileDraft); profileDraft.verifiedAt = result.testedAt; showToast(`连接成功：${result.result.slice(0, 80)}`); render(); return; }
    if (action === "delete-profile") { await window.ta.invoke("profiles:delete", profileDraft.id); profileDraft = undefined; await refresh(); showToast("配置已删除。"); return; }
    if (action === "set-active" || action === "set-translation") { await window.ta.invoke("profiles:select", { [action === "set-active" ? "activeProfileId" : "translationProfileId"]: profileDraft.id }); await refresh(); return; }
    if (action === "open-data") return window.ta.invoke("system:open-data");
    if (action === "open-privacy") return window.ta.invoke("system:open-privacy");
    if (action === "save-general") { await window.ta.invoke("settings:save", { general: genericPatch("general") }); await refresh(); showToast("常规设置已保存。"); return; }
    if (action === "save-recognition") { const recognition = genericPatch("recognition"); const activeProfileId = panel.querySelector('[data-setting="activeProfileId"]')?.value; await window.ta.invoke("settings:save", { recognition, activeProfileId }); await refresh(); showToast("识别设置已保存。"); return; }
    if (action === "warmup-ocr") { const languages = panel.querySelector('[data-recognition="localLanguages"]')?.value; showToast("正在下载本地 OCR 语言包…"); await window.ta.invoke("ocr:warmup", languages); showToast("本地 OCR 已就绪。"); return; }
    if (action === "save-translation") { const translation = genericPatch("translation"); const translationProfileId = panel.querySelector('[data-setting="translationProfileId"]')?.value; await window.ta.invoke("settings:save", { translation, translationProfileId }); await refresh(); showToast("翻译设置已保存。"); return; }
    if (action === "restore-shortcuts") { state.hotkeyActions.forEach((entry, index) => { const input = panel.querySelector(`[data-shortcut="${entry.id}"]`); if (input) input.value = `Control+Alt+${index + 1}`; }); return; }
    if (action === "save-shortcuts") { const shortcuts = Object.fromEntries([...panel.querySelectorAll("[data-shortcut]")].map((input) => [input.dataset.shortcut, input.value])); await window.ta.invoke("settings:save", { shortcuts }); await refresh(); showToast("全局快捷键已应用。"); }
    if (action === "refresh-agent-installation") { state.agentInstallation = await window.ta.invoke("agent:installation-status"); render(); showToast("CLI 与 Skill 状态已重新检测。"); return; }
    if (action === "copy-agent-install") { const kind = event.target.closest("button").dataset.kind; await window.ta.invoke("agent:copy-installation", kind); showToast(kind === "prompt" ? "Agent 安装提示词已复制。" : "终端安装命令已复制。"); return; }
  } catch (error) { showToast(error.message || "操作失败。"); }
});

void refresh().catch((error) => { panel.innerHTML = `<div class="empty"><div><b>无法加载设置</b><p>${escape(error.message || "未知错误")}</p></div></div>`; });
