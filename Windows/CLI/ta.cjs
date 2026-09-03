#!/usr/bin/env node
"use strict";

const crypto = require("node:crypto");
const net = require("node:net");

const VERSION = "0.1.0";
const PROTOCOL_VERSION = 1;
const DEFAULT_PIPE = "\\\\.\\pipe\\ta-agent-v1";
const USAGE = `Ta for Windows CLI v${VERSION}

用法：
  ta status [--json]
  ta capabilities [--json]
  ta permissions [--json]
  ta capture screen [--json]
  ta ocr --artifact <截图工件 ID> [--languages chi_sim+eng] [--json]
  ta version

先在“拓 Ta → 设置 → Agent”启用 Bridge；当前 Windows 用户可直接使用 CLI，无需令牌。
`;

function usageError(message) { const error = new Error(message); error.code = "INVALID_ARGUMENT"; return error; }
function parseOptions(raw) {
  const args = [...raw]; const options = {};
  for (let index = 0; index < args.length;) {
    if (!args[index].startsWith("--")) { index += 1; continue; }
    const key = args[index].slice(2);
    if (key === "json") { options.json = true; args.splice(index, 1); continue; }
    const value = args[index + 1];
    if (!value || value.startsWith("--")) throw usageError(`--${key} 需要一个值。`);
    options[key] = value; args.splice(index, 2);
  }
  return { args, options };
}

function requestPipe(pipeName, request, timeoutMs) {
  return new Promise((resolve, reject) => {
    const socket = net.createConnection(pipeName);
    let buffer = "";
    const timer = setTimeout(() => { socket.destroy(); reject(new Error("连接 Ta Bridge 超时。")); }, timeoutMs);
    socket.setEncoding("utf8");
    socket.once("connect", () => socket.write(`${JSON.stringify(request)}\n`));
    socket.on("data", (chunk) => {
      buffer += chunk;
      const end = buffer.indexOf("\n");
      if (end < 0) return;
      clearTimeout(timer); socket.end();
      try { resolve(JSON.parse(buffer.slice(0, end))); } catch { reject(new Error("Ta Bridge 返回了无效 JSON。")); }
    });
    socket.once("error", (error) => { clearTimeout(timer); reject(new Error(`Ta Bridge 不可用：${error.message}`)); });
  });
}

async function run(command, options) {
  if (command[0] === "version") return { ok: true, data: { version: VERSION, protocolVersion: PROTOCOL_VERSION }, artifacts: [], meta: { durationMs: 0, cloudUploaded: false } };
  const method = ({ status: "system.status", capabilities: "system.capabilities", permissions: "system.permissions" })[command[0]];
  let resolvedMethod = method;
  let params = {};
  if (command[0] === "capture" && command[1] === "screen") resolvedMethod = "capture.display";
  if (command[0] === "ocr") {
    resolvedMethod = "recognize.ocr";
    if (!options.artifact) throw usageError("ocr 需要 --artifact <截图工件 ID>。 ");
    params = { artifactId: options.artifact };
    if (options.languages) params.languages = String(options.languages).split(/[+,\s]+/).filter(Boolean);
  }
  if (!resolvedMethod || command.length > (command[0] === "capture" ? 2 : 1)) throw usageError(`未知命令。\n\n${USAGE}`);
  const response = await requestPipe(String(options.pipe || DEFAULT_PIPE), {
    protocolVersion: PROTOCOL_VERSION,
    requestId: `ta_${crypto.randomUUID().replaceAll("-", "")}`,
    method: resolvedMethod,
    params,
    client: { name: "ta-cli-windows", version: VERSION }
  }, Math.max(1_000, Number(options.timeout ?? 10) * 1_000));
  return response;
}

function writeResult(result, asJSON) {
  if (asJSON) { process.stdout.write(`${JSON.stringify(result)}\n`); return; }
  if (!result.ok) { process.stderr.write(`Ta：${result.error?.message || "请求失败。"}\n`); return; }
  const data = result.data && typeof result.data === "object" ? JSON.stringify(result.data, null, 2) : String(result.data ?? "完成");
  process.stdout.write(`${data}\n`);
  for (const artifact of result.artifacts ?? []) process.stdout.write(`工件：${artifact.path}\n`);
}

(async () => {
  const { args, options } = parseOptions(process.argv.slice(2));
  if (!args.length || ["help", "--help", "-h"].includes(args[0])) { process.stdout.write(USAGE); return; }
  const result = await run(args, options);
  writeResult(result, Boolean(options.json));
  if (!result.ok) process.exitCode = 1;
})().catch((error) => {
  const asJSON = process.argv.includes("--json");
  const result = { ok: false, artifacts: [], error: { code: error.code || "BRIDGE_UNAVAILABLE", message: error.message || "请求失败。", retryable: false } };
  writeResult(result, asJSON); process.exitCode = 1;
});
