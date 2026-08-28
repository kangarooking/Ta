const { normalizeUrl } = require("./config.cjs");

function imageParts(dataUrl) {
  const match = /^data:([^;,]+);base64,(.+)$/i.exec(String(dataUrl ?? ""));
  if (!match) throw new Error("截图数据无效，无法发送给视觉模型。");
  return { mimeType: match[1], base64: match[2] };
}

function appendPath(baseUrl, suffix) {
  const base = normalizeUrl(baseUrl);
  if (!base) throw new Error("请填写服务地址。");
  return base.toLowerCase().endsWith(suffix.toLowerCase()) ? base : `${base}/${suffix}`;
}

function endpointFor(profile) {
  const model = String(profile.visionModel || profile.textModel || "").trim();
  if (!model) throw new Error("请填写视觉模型。 ");
  switch (profile.protocol) {
    case "anthropic": return appendPath(profile.baseUrl, "v1/messages");
    case "gemini": return appendPath(profile.baseUrl, `v1beta/models/${encodeURIComponent(model)}:generateContent`);
    case "azure": return appendPath(profile.baseUrl, "openai/v1/chat/completions");
    default: return appendPath(profile.baseUrl, "chat/completions");
  }
}

function buildVisionRequest(profile, apiKey, dataUrl, prompt) {
  const { mimeType, base64 } = imageParts(dataUrl);
  const model = String(profile.visionModel || profile.textModel || "").trim();
  if (!apiKey?.trim()) throw new Error("请填写 API Key。 ");
  const endpoint = endpointFor(profile);
  const headers = { "Content-Type": "application/json" };
  let body;
  switch (profile.protocol) {
    case "anthropic":
      headers["x-api-key"] = apiKey;
      headers["anthropic-version"] = "2023-06-01";
      body = { model, max_tokens: 2048, messages: [{ role: "user", content: [
        { type: "image", source: { type: "base64", media_type: mimeType, data: base64 } }, { type: "text", text: prompt }
      ] }] };
      break;
    case "gemini":
      headers["x-goog-api-key"] = apiKey;
      body = { contents: [{ parts: [{ inlineData: { mimeType, data: base64 } }, { text: prompt }] }], generationConfig: { temperature: 0, maxOutputTokens: 2048 } };
      break;
    case "azure":
      headers["api-key"] = apiKey;
      body = openAIChatBody(model, dataUrl, prompt);
      break;
    default:
      headers.Authorization = `Bearer ${apiKey}`;
      body = openAIChatBody(model, dataUrl, prompt);
  }
  return { endpoint, headers, body };
}

function openAIChatBody(model, dataUrl, prompt) {
  return { model, temperature: 0, max_tokens: 2048, messages: [{ role: "user", content: [
    { type: "text", text: prompt }, { type: "image_url", image_url: { url: dataUrl } }
  ] }] };
}

function textFromResponse(protocol, payload) {
  if (protocol === "anthropic") return (payload.content ?? []).map((part) => part.text ?? "").join("\n").trim();
  if (protocol === "gemini") return (payload.candidates?.[0]?.content?.parts ?? []).map((part) => part.text ?? "").join("\n").trim();
  const content = payload.choices?.[0]?.message?.content;
  if (typeof content === "string") return content.trim();
  if (Array.isArray(content)) return content.map((part) => typeof part === "string" ? part : part?.text ?? "").join("\n").trim();
  return "";
}

async function callVision(profile, apiKey, dataUrl, prompt, fetchImplementation = fetch) {
  const request = buildVisionRequest(profile, apiKey, dataUrl, prompt);
  const response = await fetchImplementation(request.endpoint, {
    method: "POST", headers: request.headers, body: JSON.stringify(request.body)
  });
  const payload = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(payload?.error?.message || payload?.message || `视觉服务请求失败（HTTP ${response.status}）。`);
  const text = textFromResponse(profile.protocol, payload);
  if (!text) throw new Error("视觉服务没有返回可用文本。");
  return text;
}

module.exports = { appendPath, buildVisionRequest, callVision, endpointFor, textFromResponse };
