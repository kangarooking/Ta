const test = require("node:test");
const assert = require("node:assert/strict");
const { buildVisionRequest, textFromResponse } = require("../lib/providers.cjs");

const image = "data:image/png;base64,aGVsbG8=";

test("builds OpenAI-compatible and Azure vision requests", () => {
  const openai = buildVisionRequest({ protocol: "openai", baseUrl: "https://example.com/v1", visionModel: "vision" }, "key", image, "read");
  assert.equal(openai.endpoint, "https://example.com/v1/chat/completions");
  assert.equal(openai.headers.Authorization, "Bearer key");
  const azure = buildVisionRequest({ protocol: "azure", baseUrl: "https://resource.openai.azure.com", visionModel: "vision" }, "key", image, "read");
  assert.match(azure.endpoint, /openai\/v1\/chat\/completions$/);
  assert.equal(azure.headers["api-key"], "key");
});

test("builds Anthropic and Gemini native vision requests", () => {
  const anthropic = buildVisionRequest({ protocol: "anthropic", baseUrl: "https://api.anthropic.com", visionModel: "claude" }, "key", image, "read");
  assert.equal(anthropic.endpoint, "https://api.anthropic.com/v1/messages");
  assert.equal(anthropic.body.messages[0].content[0].source.data, "aGVsbG8=");
  const gemini = buildVisionRequest({ protocol: "gemini", baseUrl: "https://generativelanguage.googleapis.com", visionModel: "gemini-flash" }, "key", image, "read");
  assert.match(gemini.endpoint, /models\/gemini-flash:generateContent$/);
  assert.equal(gemini.headers["x-goog-api-key"], "key");
});

test("extracts text from each supported response shape", () => {
  assert.equal(textFromResponse("openai", { choices: [{ message: { content: "hello" } }] }), "hello");
  assert.equal(textFromResponse("anthropic", { content: [{ text: "hello" }] }), "hello");
  assert.equal(textFromResponse("gemini", { candidates: [{ content: { parts: [{ text: "hello" }] } }] }), "hello");
});
