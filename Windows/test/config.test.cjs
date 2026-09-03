const test = require("node:test");
const assert = require("node:assert/strict");
const { DEFAULT_SETTINGS, HOTKEY_ACTIONS, createProfileFromPreset, normalizeSettings, profileValidationMessage } = require("../lib/config.cjs");

test("migrates the first Windows preview settings into an encrypted profile slot", () => {
  const settings = normalizeSettings({ hotkey: "Control+Shift+S", baseUrl: "https://vision.example.com/v1/", model: "vision-1", apiKeyEncrypted: "encrypted-value" });
  assert.equal(settings.profiles.length, 1);
  assert.equal(settings.profiles[0].baseUrl, "https://vision.example.com/v1");
  assert.equal(settings.profiles[0].apiKeyEncrypted, "encrypted-value");
  assert.equal(settings.shortcuts.interactiveCapture, "Control+Alt+2");
});

test("creates a provider profile from a preset without persisting a plaintext key", () => {
  const profile = createProfileFromPreset("zhipu-api");
  assert.equal(profile.protocol, "openai");
  assert.equal(profile.apiKeyEncrypted, "");
  assert.equal(profile.visionModel, "glm-5v-turbo");
});

test("uses all supported action names and keeps shortcuts populated", () => {
  const settings = normalizeSettings({}, DEFAULT_SETTINGS);
  assert.deepEqual(Object.keys(settings.shortcuts), HOTKEY_ACTIONS.map((action) => action.id));
  assert.ok(Object.values(settings.shortcuts).every(Boolean));
});

test("removes legacy Agent bearer tokens during the ACL migration", () => {
  const settings = normalizeSettings({ agent: { enabled: true, tokenEncrypted: "legacy-secret" } });
  assert.equal(settings.agent.enabled, true);
  assert.equal("tokenEncrypted" in settings.agent, false);
});

test("enables the local Agent Bridge by default and when migrating old settings", () => {
  assert.equal(DEFAULT_SETTINGS.agent.enabled, true);
  assert.equal(normalizeSettings({ schemaVersion: 3, agent: { enabled: false } }).agent.enabled, true);
});

test("requires an endpoint, a vision model and an encrypted key before vision is enabled", () => {
  const profile = createProfileFromPreset("openai");
  assert.match(profileValidationMessage(profile), /API Key/);
  assert.equal(profileValidationMessage({ ...profile, apiKeyEncrypted: "safe" }), "");
});
