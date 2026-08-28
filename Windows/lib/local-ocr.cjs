const path = require("node:path");
const { createWorker } = require("tesseract.js");

function normalizeLanguages(value) {
  const languages = Array.isArray(value) ? value : String(value ?? "").split(/[+,\s]+/);
  const allowed = new Set(["eng", "chi_sim", "chi_tra", "jpn", "kor"]);
  const result = [...new Set(languages.map((language) => String(language).trim()).filter((language) => allowed.has(language)))];
  return result.length ? result : ["chi_sim", "eng"];
}

function normalizeOCRText(value) {
  return String(value ?? "").replace(/\r\n/g, "\n").replace(/\n{3,}/g, "\n\n").trim();
}

class LocalOCRService {
  constructor({ cachePath, onProgress = () => undefined } = {}) {
    this.cachePath = cachePath ?? path.join(process.cwd(), ".ta-ocr-cache");
    this.onProgress = onProgress;
    this.workers = new Map();
  }

  async workerFor(languages) {
    const normalized = normalizeLanguages(languages);
    const key = normalized.join("+");
    if (!this.workers.has(key)) {
      const worker = await createWorker(normalized, 1, {
        cachePath: this.cachePath,
        logger: (message) => this.onProgress({ languages: normalized, ...message })
      });
      this.workers.set(key, worker);
    }
    return this.workers.get(key);
  }

  async warmUp(languages) {
    await this.workerFor(languages);
    return { languages: normalizeLanguages(languages), ready: true };
  }

  async recognize(image, languages) {
    const worker = await this.workerFor(languages);
    const { data } = await worker.recognize(image);
    const text = normalizeOCRText(data.text);
    if (!text) throw new Error("未能从该选区识别出文字。");
    return { text, confidence: Math.round(data.confidence ?? 0), languages: normalizeLanguages(languages) };
  }

  async terminate() {
    await Promise.all([...this.workers.values()].map((worker) => worker.terminate()));
    this.workers.clear();
  }
}

module.exports = { LocalOCRService, normalizeLanguages, normalizeOCRText };
