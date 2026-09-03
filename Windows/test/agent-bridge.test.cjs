const test = require("node:test");
const assert = require("node:assert/strict");
const net = require("node:net");
const { MAX_REQUEST_BYTES, NamedPipeAgentBridge, PROTOCOL_VERSION, decodeRequest } = require("../lib/agent-bridge.cjs");

test("accepts protocol v1 framed requests", () => {
  assert.deepEqual(decodeRequest('{"protocolVersion":1,"requestId":"1","method":"system.status","params":{},"client":{"name":"test","version":"1"}}'), {
    protocolVersion: PROTOCOL_VERSION, requestId: "1", method: "system.status", params: {}, client: { name: "test", version: "1" }
  });
});

test("rejects malformed and oversized agent requests", () => {
  assert.throws(() => decodeRequest('{"requestId":1}'), /requestId 和 method/);
  assert.throws(() => decodeRequest("x".repeat(MAX_REQUEST_BYTES + 1)), /64 KB/);
});

test("serves a protocol v1 response over a Windows named pipe", async () => {
  const pipeName = `\\\\.\\pipe\\ta-test-${process.pid}-${Date.now()}`;
  const bridge = new NamedPipeAgentBridge({
    pipeName,
    handleRequest: async (request) => ({ protocolVersion: PROTOCOL_VERSION, requestId: request.requestId, ok: true, data: { bridge: "ready" }, artifacts: [], meta: { durationMs: 0, cloudUploaded: false } })
  });
  await bridge.start();
  try {
    const response = await new Promise((resolve, reject) => {
      const socket = net.createConnection(pipeName); let buffer = "";
      socket.setEncoding("utf8");
      socket.once("connect", () => socket.write('{"protocolVersion":1,"requestId":"test","method":"system.status","params":{},"client":{"name":"test","version":"1"}}\n'));
      socket.on("data", (chunk) => { buffer += chunk; if (!buffer.includes("\n")) return; socket.end(); resolve(JSON.parse(buffer.trim())); });
      socket.once("error", reject);
    });
    assert.deepEqual(response.data, { bridge: "ready" });
    assert.equal(response.ok, true);
  } finally { await bridge.stop(); }
});
