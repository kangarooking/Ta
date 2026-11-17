import type { AttachmentStore, ImageAttachmentRef } from '@deepseek-ai/dsh-attachment'
import { importArtifacts } from './attachments.js'
import { TaBridgeClient } from './bridge-client.js'
import type { JSONValue, TaArtifact, TaMethod, TaResponse } from './contracts.js'
import { CLOUD_CAPABLE_METHODS, TaResponseError } from './contracts.js'

/** Cloud-capable calls routinely exceed the short default Bridge timeout. */
const CLOUD_CAPABLE_TIMEOUT_MS = 180_000

export interface TaToolArtifactValue {
  taArtifactId: string
  mimeType: string
  width?: number
  height?: number
  bytes: number
  sha256: string
  expiresAt: string
  attachment: {
    attachmentId: string
    mediaType: 'image/png' | 'image/jpeg' | 'image/webp' | 'image/gif'
    bytes: number
    width: number
    height: number
    name?: string
  }
}

export interface TaToolValue {
  requestId: string
  data?: JSONValue
  artifacts: TaToolArtifactValue[]
  meta?: {
    durationMs: number
    cloudUploaded: boolean
  }
}

export class TaToolRuntime {
  private readonly artifacts = new Map<string, TaArtifact>()

  constructor(
    readonly bridge: TaBridgeClient,
    readonly attachments: AttachmentStore,
  ) {}

  async call(
    method: TaMethod,
    params: Record<string, JSONValue>,
    signal?: AbortSignal,
  ): Promise<TaToolValue> {
    const response = await this.bridge.request(method, params, {
      ...(signal === undefined ? {} : { signal }),
      ...(CLOUD_CAPABLE_METHODS.has(method)
        ? { timeoutMs: Math.max(this.bridge.config.timeoutMs, CLOUD_CAPABLE_TIMEOUT_MS) }
        : {}),
    })
    if (!response.ok) {
      throw new TaResponseError(response.error ?? {
        code: 'INTERNAL_ERROR', message: 'Ta returned an unknown failure.', retryable: false,
      })
    }
    return this.materialize(response, signal)
  }

  imageParams(artifactId?: string): Record<string, JSONValue> {
    if (artifactId === undefined) return {}
    const artifact = this.artifacts.get(artifactId)
    if (artifact === undefined) {
      throw new Error(`Unknown or expired Ta artifact ID: ${artifactId}`)
    }
    const expiry = Date.parse(artifact.expiresAt)
    if (Number.isFinite(expiry) && expiry <= Date.now()) {
      this.artifacts.delete(artifactId)
      throw new Error(`Unknown or expired Ta artifact ID: ${artifactId}`)
    }
    return { inputPath: artifact.path }
  }

  private async materialize(response: TaResponse, signal?: AbortSignal): Promise<TaToolValue> {
    const refs = await importArtifacts(response.artifacts, this.attachments, signal)
    const artifacts = response.artifacts.map((artifact, index) => {
      this.artifacts.set(artifact.id, artifact)
      return artifactValue(artifact, refs[index]!)
    })
    return {
      requestId: response.requestId,
      ...(response.data === undefined ? {} : { data: response.data }),
      artifacts,
      ...(response.meta === undefined ? {} : { meta: response.meta }),
    }
  }
}

function artifactValue(artifact: TaArtifact, ref: ImageAttachmentRef): TaToolArtifactValue {
  return {
    taArtifactId: artifact.id,
    mimeType: artifact.mimeType,
    ...(artifact.width === undefined ? {} : { width: artifact.width }),
    ...(artifact.height === undefined ? {} : { height: artifact.height }),
    bytes: artifact.bytes,
    sha256: artifact.sha256,
    expiresAt: artifact.expiresAt,
    attachment: {
      attachmentId: String(ref.attachmentId),
      mediaType: ref.mediaType,
      bytes: ref.bytes,
      width: ref.width,
      height: ref.height,
      ...(ref.name === undefined ? {} : { name: ref.name }),
    },
  }
}
