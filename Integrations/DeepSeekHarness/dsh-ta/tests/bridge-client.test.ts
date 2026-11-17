import assert from 'node:assert/strict'
import { mkdtemp, rm } from 'node:fs/promises'
import { createServer, type Server } from 'node:net'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, test } from 'node:test'
import { TaBridgeClient } from '../src/bridge-client.js'

const servers: Server[] = []
const roots: string[] = []

afterEach(async () => {
  await Promise.all(servers.splice(0).map(server => new Promise<void>(resolve => server.close(() => resolve()))))
  await Promise.all(roots.splice(0).map(root => rm(root, { recursive: true, force: true })))
})

test('Bridge client preserves the four-byte frame and request envelope', async () => {
  const root = await mkdtemp(join(tmpdir(), 'dsh-ta-'))
  roots.push(root)
  const socketPath = join(root, 'agent.sock')
  let observed: unknown
  const server = createServer(socket => {
    const chunks: Buffer[] = []
    socket.on('data', chunk => {
      chunks.push(chunk)
      const frame = Buffer.concat(chunks)
      if (frame.length < 4) return
      const length = frame.readUInt32BE(0)
      if (frame.length < length + 4) return
      observed = JSON.parse(frame.subarray(4, length + 4).toString('utf8'))
      const response = Buffer.from(JSON.stringify({
        protocolVersion: 1,
        requestId: 'req-1',
        ok: true,
        data: { bridge: 'ready' },
        artifacts: [],
      }))
      const header = Buffer.alloc(4)
      header.writeUInt32BE(response.length)
      socket.end(Buffer.concat([header, response]))
    })
  })
  servers.push(server)
  await new Promise<void>((resolve, reject) => server.listen(socketPath, error => error ? reject(error) : resolve()))

  const client = new TaBridgeClient({ socketPath, timeoutMs: 1_000, autoLaunch: false })
  const response = await client.request('system.status', {}, { requestId: 'req-1' })

  assert.equal(response.ok, true)
  assert.deepEqual(observed, {
    protocolVersion: 1,
    requestId: 'req-1',
    method: 'system.status',
    params: {},
    client: { name: 'dsh-ta', version: '1.1.0' },
  })
})

test('AbortSignal closes a pending Bridge request', async () => {
  const root = await mkdtemp(join(tmpdir(), 'dsh-ta-'))
  roots.push(root)
  const socketPath = join(root, 'agent.sock')
  const server = createServer(() => {})
  servers.push(server)
  await new Promise<void>((resolve, reject) => server.listen(socketPath, error => error ? reject(error) : resolve()))
  const controller = new AbortController()
  const client = new TaBridgeClient({ socketPath, timeoutMs: 5_000, autoLaunch: false })
  const pending = client.request('analyze.image', {}, { signal: controller.signal })

  controller.abort()

  await assert.rejects(pending, error => error instanceof Error && error.name === 'AbortError')
})
