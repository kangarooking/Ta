const test = require("node:test");
const assert = require("node:assert/strict");
const { normalizeLanguages, normalizeOCRText } = require("../lib/local-ocr.cjs");

test("normalizes supported local OCR language choices", () => {
  assert.deepEqual(normalizeLanguages("chi_sim+eng+unknown"), ["chi_sim", "eng"]);
  assert.deepEqual(normalizeLanguages([]), ["chi_sim", "eng"]);
});

test("normalizes line endings and excessive OCR spacing", () => {
  assert.equal(normalizeOCRText("\r\n  Ta\r\n\r\n\r\nOCR  \r\n"), "Ta\n\nOCR");
});
