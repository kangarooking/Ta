import { spawn } from 'node:child_process'
import { randomUUID } from 'node:crypto'
import { createConnection, type Socket } from 'node:net'
import type { JSONValue, TaMethod, TaRequest, TaResponse } from './contracts.js'

const MAX_FRAME_BYTES = 16 * 1024 * 1024

export interface TaBridgeClientConfig {
  socketPath: string
  appPath?: string
  timeoutMs: number
  autoLaunch: boolean
}

export interface TaBridgeRequestOptions {
  requestId?: string
  signal?: AbortSignal
  /** Overrides the configured timeout for this request (e.g. slow cloud calls). */
  timeoutMs?: number
}

export class TaBridgeClient {
  readonly config: TaBridgeClientConfig

  constructor(config: TaBridgeClientConfig) {
    this.config = config
  }

  async request(
    method: TaMethod,
    params: Record<string, JSONValue>,
    options: TaBridgeRequestOptions = {},
  ): Promise<TaResponse> {
    const request: TaRequest = {
      protocolVersion: 1,
      requestId: options.requestId ?? createRequestId(),
      method,
      params,
      client: { name: 'dsh-ta', version: '1.0.1' },
    }
    const timeoutMs = options.timeoutMs ?? this.config.timeoutMs
    try {
      return await this.requestOnce(request, options.signal, timeoutMs)
    } catch (error) {
      if (!this.config.autoLaunch || !isBridgeUnavailable(error) || options.signal?.aborted) throw error
    }

    await this.launchApp(options.signal)
    const deadline = Date.now() + Math.min(this.config.timeoutMs, 5_000)
    let lastError: unknown
    while (Date.now() < deadline) {
      throwIfAborted(options.signal)
      try {
        return await this.requestOnce(request, options.signal, timeoutMs)
      } catch (error) {
        lastError = error
        if (!isBridgeUnavailable(error)) throw error
        await abortableDelay(80, options.signal)
      }
    }
    throw lastError instanceof Error ? lastError : new Error('Ta Bridge did not become ready.')
  }

  private requestOnce(
    request: TaRequest,
    signal: AbortSignal | undefined,
    timeoutMs: number,
  ): Promise<TaResponse> {
    throwIfAborted(signal)
    return new Promise<TaResponse>((resolve, reject) => {
      let socket: Socket | undefined
      let settled = false
      let buffer = Buffer.alloc(0)
      let expectedLength: number | undefined
      const timer = setTimeout(() => {
        // Not retried: Ta may still be executing the request (and may already have
        // uploaded to a cloud model), so re-sending could run it twice.
        finish(new DOMException(
          `Ta Bridge timed out after ${timeoutMs} ms. The request may still be running in Ta; do not retry automatically.`,
          'TimeoutError',
        ))
      }, timeoutMs)

      const onAbort = () => finish(abortError(signal))
      const finish = (error?: unknown, response?: TaResponse) => {
        if (settled) return
        settled = true
        clearTimeout(timer)
        signal?.removeEventListener('abort', onAbort)
        socket?.destroy()
        if (error !== undefined) reject(error)
        else resolve(response!)
      }

      signal?.addEventListener('abort', onAbort, { once: true })
      socket = createConnection(this.config.socketPath)
      socket.once('error', error => finish(error))
      socket.on('data', chunk => {
        buffer = Buffer.concat([buffer, chunk])
        if (expectedLength === undefined && buffer.length >= 4) {
          expectedLength = buffer.readUInt32BE(0)
          if (expectedLength > MAX_FRAME_BYTES) {
            finish(new Error(`Ta Bridge response exceeds ${MAX_FRAME_BYTES} bytes.`))
            return
          }
        }
        if (expectedLength === undefined || buffer.length < expectedLength + 4) return
        try {
          const payload = buffer.subarray(4, expectedLength + 4).toString('utf8')
          finish(undefined, JSON.parse(payload) as TaResponse)
        } catch (error) {
          finish(error)
        }
      })
      socket.once('connect', () => {
        const payload = Buffer.from(JSON.stringify(request), 'utf8')
        if (payload.length > MAX_FRAME_BYTES) {
          finish(new Error(`Ta Bridge request exceeds ${MAX_FRAME_BYTES} bytes.`))
          return
        }
        const header = Buffer.allocUnsafe(4)
        header.writeUInt32BE(payload.length)
        socket?.write(Buffer.concat([header, payload]))
      })
      socket.once('end', () => {
        if (!settled) finish(new Error('Ta Bridge closed before returning a complete response.'))
      })
    })
  }

  private async launchApp(signal?: AbortSignal): Promise<void> {
    throwIfAborted(signal)
    const appPath = this.config.appPath ?? '/Applications/拓.app'
    await new Promise<void>((resolve, reject) => {
      const child = spawn('/usr/bin/open', ['-g', '-n', appPath, '--args', '--agent-bridge'], {
        stdio: 'ignore',
      })
      const onAbort = () => {
        child.kill()
        reject(abortError(signal))
      }
      signal?.addEventListener('abort', onAbort, { once: true })
      child.once('error', error => {
        signal?.removeEventListener('abort', onAbort)
        reject(error)
      })
      child.once('exit', code => {
        signal?.removeEventListener('abort', onAbort)
        if (code === 0) resolve()
        else reject(new Error(`Unable to launch Ta at ${appPath} (open exited ${code ?? 'unknown'}).`))
      })
    })
  }
}

function createRequestId(): string {
  return `dsh_${randomUUID().replaceAll('-', '').toLowerCase()}`
}

function isBridgeUnavailable(error: unknown): boolean {
  const code = (error as NodeJS.ErrnoException | undefined)?.code
  return code === 'ENOENT' || code === 'ECONNREFUSED'
}

function throwIfAborted(signal?: AbortSignal): void {
  if (signal?.aborted) throw abortError(signal)
}

function abortError(signal?: AbortSignal): Error {
  return signal?.reason instanceof Error
    ? signal.reason
    : new DOMException('Ta Bridge request was cancelled.', 'AbortError')
}

async function abortableDelay(ms: number, signal?: AbortSignal): Promise<void> {
  throwIfAborted(signal)
  await new Promise<void>((resolve, reject) => {
    const timer = setTimeout(() => {
      signal?.removeEventListener('abort', onAbort)
      resolve()
    }, ms)
    const onAbort = () => {
      clearTimeout(timer)
      reject(abortError(signal))
    }
    signal?.addEventListener('abort', onAbort, { once: true })
  })
}
