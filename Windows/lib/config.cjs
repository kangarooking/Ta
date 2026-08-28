const { randomUUID } = require("node:crypto");

const SETTINGS_SCHEMA_VERSION = 4;

const PROVIDER_PRESETS = Object.freeze([
  { id: "zhipu-api", name: "智谱 API", badge: "推荐", protocol: "openai", recommended: true, description: "通用额度，支持识图和翻译", baseUrl: "https://open.bigmodel.cn/api/paas/v4", visionModel: "glm-5v-turbo", textModel: "glm-5.2", supportsVision: true },
  { id: "zhipu-coding", name: "智谱 Coding Plan", badge: "推荐", protocol: "openai", recommended: true, description: "订阅套餐，仅支持文字模型直连", baseUrl: "https://open.bigmodel.cn/api/coding/paas/v4", visionModel: "", textModel: "glm-5.2", supportsVision: false },
  { id: "deepseek", name: "DeepSeek", badge: "推荐", protocol: "openai", recommended: true, description: "中文理解自然，适合识图和翻译", baseUrl: "https://api.deepseek.com", visionModel: "deepseek-v4-flash-vision-exp", textModel: "deepseek-v4-flash", supportsVision: true },
  { id: "openai", name: "OpenAI", protocol: "openai", description: "综合能力强，适合复杂截图", baseUrl: "https://api.openai.com/v1", visionModel: "gpt-4o-mini", textModel: "gpt-4o-mini", supportsVision: true },
  { id: "gemini", name: "Gemini", protocol: "gemini", description: "Google 视觉与文字模型", baseUrl: "https://generativelanguage.googleapis.com", visionModel: "gemini-2.5-flash", textModel: "gemini-2.5-flash", supportsVision: true },
  { id: "anthropic", name: "Claude", protocol: "anthropic", description: "Anthropic 视觉与文字模型", baseUrl: "https://api.anthropic.com", visionModel: "", textModel: "", supportsVision: true },
  { id: "openrouter", name: "OpenRouter", protocol: "openai", description: "一个接口使用多家模型", baseUrl: "https://openrouter.ai/api/v1", visionModel: "", textModel: "", supportsVision: true },
  { id: "azure", name: "Azure OpenAI", protocol: "azure", description: "企业 Azure OpenAI 服务", baseUrl: "", visionModel: "", textModel: "", supportsVision: true },
  { id: "custom", name: "自定义兼容服务", protocol: "openai", description: "本地模型或 OpenAI-compatible 接口", baseUrl: "", visionModel: "", textModel: "", supportsVision: true }
]);

const DEFAULT_SHORTCUTS = Object.freeze({
  intelligentCapture: "Control+Alt+1", interactiveCapture: "Control+Alt+2", imageCapture: "Control+Alt+3",
  pinCapture: "Control+Alt+4", longCapture: "Control+Alt+5", translationCapture: "Control+Alt+6"
});

const HOTKEY_ACTIONS = Object.freeze([
  { id: "intelligentCapture", title: "智能识图", description: "截图后使用默认 AI 任务理解图片" },
  { id: "interactiveCapture", title: "通用截图", description: "框选后显示全部操作栏" },
  { id: "imageCapture", title: "复制图片", description: "框选后直接复制 PNG" },
  { id: "pinCapture", title: "截图钉住", description: "框选后直接置顶显示" },
  { id: "longCapture", title: "滚动长截图", description: "Windows 自动滚动模块即将接入" },
  { id: "translationCapture", title: "截图翻译", description: "框选后翻译为设定目标语言" }
]);

const RECOGNITION_TASKS = Object.freeze([
  { id: "general", title: "通用识图" }, { id: "extractText", title: "精确取字" },
  { id: "explainCode", title: "提取并解释代码" }, { id: "tableMarkdown", title: "表格转 Markdown" },
  { id: "tableCSV", title: "表格转 CSV" }, { id: "formulaLaTeX", title: "公式转 LaTeX" }
]);

const DEFAULT_SETTINGS = Object.freeze({
  schemaVersion: SETTINGS_SCHEMA_VERSION, profiles: [], activeProfileId: "", translationProfileId: "",
  shortcuts: { ...DEFAULT_SHORTCUTS },
  general: { launchOnLogin: false, minimizeToTray: true, revealSavedFile: false },
  recognition: { cloudEnabled: true, defaultTask: "general", localEnabled: true, localLanguages: "chi_sim+eng" },
  translation: { sourceLanguage: "自动检测", targetLanguage: "简体中文", mode: "text" },
  agent: { enabled: true, allowCloud: false, auditRetentionDays: 14 }
});

function presetById(id) { return PROVIDER_PRESETS.find((preset) => preset.id === id) ?? PROVIDER_PRESETS.at(-1); }
function normalizeUrl(value) { return String(value ?? "").trim().replace(/\/+$/, ""); }
function normalizeShortcut(value, fallback) { return String(value ?? "").trim() || fallback; }

function createProfileFromPreset(presetId, name) {
  const preset = presetById(presetId);
  return {
    id: randomUUID(), name: String(name ?? "").trim() || `${preset.name} 配置`, presetId: preset.id,
    protocol: preset.protocol, baseUrl: preset.baseUrl, visionModel: preset.visionModel, textModel: preset.textModel,
    apiKeyEncrypted: "", verifiedAt: ""
  };
}

function normalizeProfile(input = {}, previous = {}) {
  const preset = presetById(input.presetId ?? previous.presetId ?? "custom");
  const protocol = ["openai", "azure", "anthropic", "gemini"].includes(input.protocol)
    ? input.protocol : (previous.protocol ?? preset.protocol);
  return {
    id: String(input.id ?? previous.id ?? randomUUID()),
    name: String(input.name ?? previous.name ?? preset.name).trim() || preset.name,
    presetId: preset.id, protocol, baseUrl: normalizeUrl(input.baseUrl ?? previous.baseUrl ?? preset.baseUrl),
    visionModel: String(input.visionModel ?? previous.visionModel ?? preset.visionModel).trim(),
    textModel: String(input.textModel ?? previous.textModel ?? preset.textModel).trim(),
    apiKeyEncrypted: String(input.apiKeyEncrypted ?? previous.apiKeyEncrypted ?? ""),
    verifiedAt: String(input.verifiedAt ?? previous.verifiedAt ?? "")
  };
}

function normalizeSection(value, fallback) { return { ...fallback, ...(value && typeof value === "object" ? value : {}) }; }
function profileIds(profiles) { return new Set(profiles.map((profile) => profile.id)); }

function normalizeSettings(input = {}, previous = DEFAULT_SETTINGS) {
  const rawProfiles = Array.isArray(input.profiles) ? input.profiles : (Array.isArray(previous.profiles) ? previous.profiles : []);
  const existing = new Map((previous.profiles ?? []).map((profile) => [profile.id, profile]));
  let profiles = rawProfiles.map((profile) => normalizeProfile(profile, existing.get(profile.id)));
  if (!profiles.length && input.baseUrl) {
    profiles = [normalizeProfile({ id: "migrated-default", name: "默认 AI 配置", presetId: "custom", protocol: "openai", baseUrl: input.baseUrl, visionModel: input.model, textModel: input.model, apiKeyEncrypted: input.apiKeyEncrypted ?? "" })];
  }
  const ids = profileIds(profiles);
  const requestedActive = String(input.activeProfileId ?? previous.activeProfileId ?? "");
  const requestedTranslation = String(input.translationProfileId ?? previous.translationProfileId ?? "");
  const shortcutsSource = normalizeSection(input.shortcuts, previous.shortcuts ?? DEFAULT_SHORTCUTS);
  const shortcuts = Object.fromEntries(HOTKEY_ACTIONS.map(({ id }) => [id, normalizeShortcut(shortcutsSource[id], DEFAULT_SHORTCUTS[id])]));
  const recognition = normalizeSection(input.recognition, previous.recognition ?? DEFAULT_SETTINGS.recognition);
  if (!RECOGNITION_TASKS.some((task) => task.id === recognition.defaultTask)) recognition.defaultTask = "general";
  const translation = normalizeSection(input.translation, previous.translation ?? DEFAULT_SETTINGS.translation);
  if (!String(translation.targetLanguage ?? "").trim()) translation.targetLanguage = "简体中文";
  const agent = normalizeSection(input.agent, previous.agent ?? DEFAULT_SETTINGS.agent);
  delete agent.tokenEncrypted;
  agent.enabled = true;
  return {
    schemaVersion: SETTINGS_SCHEMA_VERSION, profiles,
    activeProfileId: ids.has(requestedActive) ? requestedActive : profiles[0]?.id ?? "",
    translationProfileId: ids.has(requestedTranslation) ? requestedTranslation : (ids.has(requestedActive) ? requestedActive : profiles[0]?.id ?? ""),
    shortcuts, general: normalizeSection(input.general, previous.general ?? DEFAULT_SETTINGS.general), recognition, translation,
    agent
  };
}

function publicProfile(profile) { const { apiKeyEncrypted, ...values } = profile; return { ...values, hasApiKey: Boolean(apiKeyEncrypted) }; }
function publicSettings(settings) { return { ...settings, profiles: settings.profiles.map(publicProfile) }; }

function profileValidationMessage(profile, { requiresVision = true, requiresText = false } = {}) {
  if (!profile?.name?.trim()) return "请给这套配置起一个名称。";
  if (!profile.baseUrl?.trim()) return "请填写服务地址。";
  try { new URL(profile.baseUrl); } catch { return "服务地址格式无效。"; }
  if (requiresVision && !profile.visionModel?.trim()) return "请填写支持图片输入的视觉模型。";
  if (requiresText && !(profile.textModel || profile.visionModel)?.trim()) return "请填写文字模型。";
  if (!profile.apiKeyEncrypted) return "请填写 API Key。";
  return "";
}

module.exports = { DEFAULT_SETTINGS, DEFAULT_SHORTCUTS, HOTKEY_ACTIONS, PROVIDER_PRESETS, RECOGNITION_TASKS, SETTINGS_SCHEMA_VERSION, createProfileFromPreset, normalizeProfile, normalizeSettings, normalizeUrl, presetById, profileValidationMessage, publicSettings };
