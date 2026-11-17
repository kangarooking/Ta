export type JSONValue =
  | null
  | boolean
  | number
  | string
  | JSONValue[]
  | { [key: string]: JSONValue }

export type TaMethod =
  | 'system.status'
  | 'system.capabilities'
  | 'system.permissions'
  | 'target.listDisplays'
  | 'target.listWindows'
  | 'capture.display'
  | 'capture.frontmost'
  | 'capture.window'
  | 'capture.region'
  | 'recognize.ocr'
  | 'analyze.image'
  | 'translate.text'
  | 'translate.image'
  | 'deliver.copy'
  | 'deliver.save'

export type TaCloudPolicy = 'auto' | 'allow' | 'deny'

/**
 * Optional parameters understood by the Ta Bridge (kept in sync with
 * TaAgentCapabilityService). All are optional; omit `cloud` to inherit the
 * policy the user configured in Ta.
 */
export interface TaOptionalParams {
  /** capture.*: longest output side in pixels (16...16384). */
  maxDimension?: number
  /** capture.window: app name fragment or exact Bundle ID, instead of windowId. */
  app?: string
  /** capture.window: window-title fragment, alone or together with `app`. */
  title?: string
  /** recognize.ocr: include per-line pixel bounding boxes in `data.lines`. */
  boxes?: boolean
  /** deliver.save: allow replacing an existing PNG. */
  overwrite?: boolean
  /** Cloud-capable methods: may only be stricter than the user's Ta setting. */
  cloud?: TaCloudPolicy
}

/** Error codes the Bridge server emits. Client-side failures are thrown as Errors. */
export type TaErrorCode =
  | 'PROTOCOL_VERSION_MISMATCH'
  | 'SCREEN_PERMISSION_REQUIRED'
  | 'INVALID_REQUEST'
  | 'TARGET_NOT_FOUND'
  | 'TARGET_CHANGED'
  | 'TARGET_BLOCKED_BY_PRIVACY_POLICY'
  | 'MODEL_PROFILE_NOT_CONFIGURED'
  | 'CLOUD_UPLOAD_NOT_ALLOWED'
  | 'CANCELLED'
  | 'INTERNAL_ERROR'

/** Methods that may hand an image or text to a cloud model and can take minutes. */
export const CLOUD_CAPABLE_METHODS: ReadonlySet<TaMethod> = new Set<TaMethod>([
  'recognize.ocr',
  'analyze.image',
  'translate.text',
  'translate.image',
])

export interface TaArtifact {
  id: string
  path: string
  mimeType: string
  width?: number
  height?: number
  bytes: number
  sha256: string
  expiresAt: string
}

export interface TaErrorPayload {
  code: string
  message: string
  hint?: string
  retryable: boolean
}

export interface TaResponse {
  protocolVersion: number
  requestId: string
  ok: boolean
  data?: JSONValue
  artifacts: TaArtifact[]
  meta?: {
    durationMs: number
    cloudUploaded: boolean
  }
  error?: TaErrorPayload
}

export interface TaRequest {
  protocolVersion: 1
  requestId: string
  method: TaMethod
  params: Record<string, JSONValue>
  client: {
    name: 'dsh-ta'
    version: '1.1.0'
  }
}

export class TaResponseError extends Error {
  readonly code: string
  readonly hint: string | undefined
  readonly retryable: boolean

  constructor(payload: TaErrorPayload) {
    super(payload.hint ? `${payload.message} ${payload.hint}` : payload.message)
    this.name = 'TaResponseError'
    this.code = payload.code
    this.hint = payload.hint
    this.retryable = payload.retryable
  }
}
