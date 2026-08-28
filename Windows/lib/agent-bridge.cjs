const { spawn } = require("node:child_process");
const fs = require("node:fs");
const path = require("node:path");
const readline = require("node:readline");

const MAX_REQUEST_BYTES = 64 * 1024;
const PROTOCOL_VERSION = 1;
const PIPE_PREFIX = "\\\\.\\pipe\\";

function decodeRequest(line) {
  if (Buffer.byteLength(line, "utf8") > MAX_REQUEST_BYTES) throw new Error("请求超过 64 KB 限制。");
  const request = JSON.parse(line);
  if (!request || typeof request !== "object" || request.protocolVersion !== PROTOCOL_VERSION || typeof request.requestId !== "string" || typeof request.method !== "string") {
    throw new Error("请求必须包含 protocolVersion: 1、字符串 requestId 和 method。");
  }
  if (request.params !== undefined && (!request.params || typeof request.params !== "object" || Array.isArray(request.params))) throw new Error("params 必须是对象。");
  if (request.client !== undefined && (!request.client || typeof request.client.name !== "string" || typeof request.client.version !== "string")) throw new Error("client 必须包含 name 和 version。");
  return request;
}

function failureResponse(requestId, code, message) {
  return { protocolVersion: PROTOCOL_VERSION, requestId, ok: false, artifacts: [], error: { code, message, retryable: false } };
}

function pipeLeafName(pipeName) {
  const value = String(pipeName ?? "");
  return value.startsWith(PIPE_PREFIX) ? value.slice(PIPE_PREFIX.length) : value;
}

function powerShellExecutable() {
  return path.join(process.env.SystemRoot || "C:\\Windows", "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
}

function defaultHostScript() {
  const unpacked = path.join(process.resourcesPath || "", "app.asar.unpacked", "scripts", "agent-pipe-host.ps1");
  return fs.existsSync(unpacked) ? unpacked : path.join(__dirname, "..", "scripts", "agent-pipe-host.ps1");
}

class NamedPipeAgentBridge {
  constructor({ pipeName, handleRequest, onAudit = () => undefined, hostScript = defaultHostScript() }) {
    this.pipeName = pipeName;
    this.handleRequest = handleRequest;
    this.onAudit = onAudit;
    this.hostScript = hostScript;
    this.host = undefined;
    this.output = undefined;
    this.hostStderr = "";
    this.stopping = false;
    this.resolveReady = undefined;
    this.rejectReady = undefined;
  }

  async start() {
    if (this.host) return;
    this.stopping = false;
    const ready = new Promise((resolve, reject) => { this.resolveReady = resolve; this.rejectReady = reject; });
    const args = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", this.hostScript, "-PipeName", pipeLeafName(this.pipeName)];
    const host = spawn(powerShellExecutable(), args, { stdio: ["pipe", "pipe", "pipe"], windowsHide: true });
    this.host = host;
    host.stderr.setEncoding("utf8");
    host.stderr.on("data", (chunk) => { this.hostStderr = `${this.hostStderr}${chunk}`.slice(-4_000); });
    this.output = readline.createInterface({ input: host.stdout, crlfDelay: Infinity });
    this.output.on("line", (line) => this.handleHostLine(line));
    host.once("error", (error) => this.failStart(new Error(`无法启动受 ACL 保护的 Windows Pipe Host：${error.message}`)));
    host.once("exit", (code) => {
      if (!this.stopping) this.failStart(new Error(`Windows Pipe Host 意外退出（${code ?? "unknown"}）。`));
      this.host = undefined;
    });
    const timer = setTimeout(() => this.failStart(new Error("等待 Windows Pipe Host 就绪超时。")), 10_000);
    try { await ready; } finally { clearTimeout(timer); }
  }

  failStart(error) {
    if (!this.rejectReady) return;
    const reject = this.rejectReady;
    this.rejectReady = undefined;
    this.resolveReady = undefined;
    reject(error);
  }

  handleHostLine(line) {
    if (line === "READY") {
      if (this.resolveReady) {
        const resolve = this.resolveReady;
        this.resolveReady = undefined;
        this.rejectReady = undefined;
        resolve();
      }
      return;
    }
    if (!line.startsWith("REQUEST ")) return;
    void this.handleForwardedRequest(line.slice("REQUEST ".length));
  }

  async handleForwardedRequest(encodedRequest) {
    let request;
    let response;
    try {
      request = decodeRequest(Buffer.from(encodedRequest, "base64").toString("utf8"));
      response = await this.handleRequest(request);
      this.onAudit({ method: request.method, client: request.client?.name ?? "unknown", outcome: response.ok ? "allowed" : "rejected", cloudUploaded: Boolean(response.meta?.cloudUploaded) });
    } catch (error) {
      response = failureResponse(request?.requestId ?? "unknown", "INVALID_REQUEST", error.message || "请求失败。");
      this.onAudit({ method: request?.method ?? "invalid", client: request?.client?.name ?? "unknown", outcome: "rejected", cloudUploaded: false });
    }
    if (this.host?.stdin.writable) this.host.stdin.write(`RESPONSE ${Buffer.from(JSON.stringify(response), "utf8").toString("base64")}\n`);
  }

  async stop() {
    if (!this.host) return;
    this.stopping = true;
    const host = this.host;
    this.output?.close();
    await new Promise((resolve) => {
      host.once("exit", resolve);
      host.kill();
      setTimeout(resolve, 2_000);
    });
    this.host = undefined;
    this.output = undefined;
    this.stopping = false;
  }
}

module.exports = { MAX_REQUEST_BYTES, PROTOCOL_VERSION, NamedPipeAgentBridge, decodeRequest, defaultHostScript, failureResponse, pipeLeafName };
