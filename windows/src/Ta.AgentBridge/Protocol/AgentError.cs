namespace Ta.AgentBridge.Protocol;

/// <summary>
/// Agent 错误码。对应 Mac 版 TaAgentContracts/AgentErrors.swift:3-21。
/// raw value 即线格式里的 code 字符串。
/// </summary>
public enum AgentErrorCode
{
    TaAppNotInstalled,                 // TA_APP_NOT_INSTALLED
    BridgeUnavailable,                 // BRIDGE_UNAVAILABLE
    ProtocolVersionMismatch,           // PROTOCOL_VERSION_MISMATCH
    ScreenPermissionRequired,          // SCREEN_PERMISSION_REQUIRED
    AccessibilityPermissionRequired,   // ACCESSIBILITY_PERMISSION_REQUIRED
    InvalidRequest,                    // INVALID_REQUEST
    TargetNotFound,                    // TARGET_NOT_FOUND
    TargetChanged,                     // TARGET_CHANGED
    TargetBlockedByPrivacyPolicy,      // TARGET_BLOCKED_BY_PRIVACY_POLICY
    CaptureBusy,                       // CAPTURE_BUSY
    OcrEngineUnavailable,              // OCR_ENGINE_UNAVAILABLE
    ModelProfileNotConfigured,         // MODEL_PROFILE_NOT_CONFIGURED
    CloudUploadNotAllowed,             // CLOUD_UPLOAD_NOT_ALLOWED
    UserActivityDetected,              // USER_ACTIVITY_DETECTED
    ArtifactExpired,                   // ARTIFACT_EXPIRED
    Cancelled,                         // CANCELLED
    InternalError,                     // INTERNAL_ERROR
}

public static class AgentErrorCodeExtensions
{
    public static string Raw(this AgentErrorCode code) => code switch
    {
        AgentErrorCode.TaAppNotInstalled => "TA_APP_NOT_INSTALLED",
        AgentErrorCode.BridgeUnavailable => "BRIDGE_UNAVAILABLE",
        AgentErrorCode.ProtocolVersionMismatch => "PROTOCOL_VERSION_MISMATCH",
        AgentErrorCode.ScreenPermissionRequired => "SCREEN_PERMISSION_REQUIRED",
        AgentErrorCode.AccessibilityPermissionRequired => "ACCESSIBILITY_PERMISSION_REQUIRED",
        AgentErrorCode.InvalidRequest => "INVALID_REQUEST",
        AgentErrorCode.TargetNotFound => "TARGET_NOT_FOUND",
        AgentErrorCode.TargetChanged => "TARGET_CHANGED",
        AgentErrorCode.TargetBlockedByPrivacyPolicy => "TARGET_BLOCKED_BY_PRIVACY_POLICY",
        AgentErrorCode.CaptureBusy => "CAPTURE_BUSY",
        AgentErrorCode.OcrEngineUnavailable => "OCR_ENGINE_UNAVAILABLE",
        AgentErrorCode.ModelProfileNotConfigured => "MODEL_PROFILE_NOT_CONFIGURED",
        AgentErrorCode.CloudUploadNotAllowed => "CLOUD_UPLOAD_NOT_ALLOWED",
        AgentErrorCode.UserActivityDetected => "USER_ACTIVITY_DETECTED",
        AgentErrorCode.ArtifactExpired => "ARTIFACT_EXPIRED",
        AgentErrorCode.Cancelled => "CANCELLED",
        AgentErrorCode.InternalError => "INTERNAL_ERROR",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
    };
}

/// <summary>
/// 错误负载。对应 Mac 版 TaAgentContracts/AgentErrors.swift:23-35。
/// hint 为 null 时在 JSON 中省略（§14 风险 #40）。
/// </summary>
public sealed record AgentErrorPayload
{
    public AgentErrorCode Code { get; init; }
    public string Message { get; init; }
    public string? Hint { get; init; }
    public bool Retryable { get; init; }

    public AgentErrorPayload(AgentErrorCode code, string message, string? hint = null, bool retryable = false)
    {
        Code = code;
        Message = message;
        Hint = hint;
        Retryable = retryable;
    }
}

/// <summary>协议版本不匹配。对应 Mac: AgentProtocolError.unsupportedVersion。</summary>
public sealed class AgentProtocolException : Exception
{
    public int Received { get; }
    public int Supported { get; }

    public AgentProtocolException(int received, int supported)
        : base($"Bridge 协议版本不兼容：收到 {received}，当前支持 {supported}。")
    {
        Received = received;
        Supported = supported;
    }
}
