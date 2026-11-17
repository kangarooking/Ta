import Foundation

public enum AgentErrorCode: String, Codable, CaseIterable, Sendable {
    case taAppNotInstalled = "TA_APP_NOT_INSTALLED"
    case bridgeUnavailable = "BRIDGE_UNAVAILABLE"
    case protocolVersionMismatch = "PROTOCOL_VERSION_MISMATCH"
    case screenPermissionRequired = "SCREEN_PERMISSION_REQUIRED"
    case accessibilityPermissionRequired = "ACCESSIBILITY_PERMISSION_REQUIRED"
    case invalidRequest = "INVALID_REQUEST"
    case targetNotFound = "TARGET_NOT_FOUND"
    case targetChanged = "TARGET_CHANGED"
    case targetBlockedByPrivacyPolicy = "TARGET_BLOCKED_BY_PRIVACY_POLICY"
    case captureBusy = "CAPTURE_BUSY"
    case ocrEngineUnavailable = "OCR_ENGINE_UNAVAILABLE"
    case modelProfileNotConfigured = "MODEL_PROFILE_NOT_CONFIGURED"
    case cloudUploadNotAllowed = "CLOUD_UPLOAD_NOT_ALLOWED"
    case userActivityDetected = "USER_ACTIVITY_DETECTED"
    case artifactExpired = "ARTIFACT_EXPIRED"
    case cancelled = "CANCELLED"
    /// Client-side only: the caller stopped waiting, but Ta may still be executing
    /// the request. Never retry automatically (a cloud upload could run twice).
    case requestTimeout = "REQUEST_TIMEOUT"
    case internalError = "INTERNAL_ERROR"
}

public struct AgentErrorPayload: Codable, Equatable, Sendable {
    public let code: AgentErrorCode
    public let message: String
    public let hint: String?
    public let retryable: Bool

    public init(code: AgentErrorCode, message: String, hint: String? = nil, retryable: Bool) {
        self.code = code
        self.message = message
        self.hint = hint
        self.retryable = retryable
    }
}

public enum AgentProtocolError: Error, Equatable, Sendable {
    case unsupportedVersion(received: Int, supported: Int)
}

