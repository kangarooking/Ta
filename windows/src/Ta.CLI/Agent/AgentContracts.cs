namespace Ta.CLI.Agent;

/// <summary>对应 Mac: AgentProtocol（Sources/TaAgentContracts/AgentEnvelope.swift:3-5）。</summary>
public static class AgentProtocol
{
    public const int CurrentVersion = 1;
}

/// <summary>对应 Mac: AgentMethod（Sources/TaAgentContracts/AgentMethods.swift:3-26）。</summary>
public enum AgentMethod
{
    SystemHandshake,
    SystemStatus,
    SystemCapabilities,
    SystemPermissions,
    TargetListDisplays,
    TargetListWindows,
    CaptureDisplay,
    CaptureFrontmost,
    CaptureWindow,
    CaptureRegion,
    CaptureInteractive,
    CaptureScroll,
    RecognizeOCR,
    AnalyzeImage,
    TranslateText,
    TranslateImage,
    TransformImage,
    DeliverCopy,
    DeliverSave,
    DeliverPin,
    JobStatus,
    JobCancel,
}

public static class AgentMethodExtensions
{
    /// <summary>线上 wire 名称（rawValue）。</summary>
    public static string RawValue(this AgentMethod method) => method switch
    {
        AgentMethod.SystemHandshake => "system.handshake",
        AgentMethod.SystemStatus => "system.status",
        AgentMethod.SystemCapabilities => "system.capabilities",
        AgentMethod.SystemPermissions => "system.permissions",
        AgentMethod.TargetListDisplays => "target.listDisplays",
        AgentMethod.TargetListWindows => "target.listWindows",
        AgentMethod.CaptureDisplay => "capture.display",
        AgentMethod.CaptureFrontmost => "capture.frontmost",
        AgentMethod.CaptureWindow => "capture.window",
        AgentMethod.CaptureRegion => "capture.region",
        AgentMethod.CaptureInteractive => "capture.interactive",
        AgentMethod.CaptureScroll => "capture.scroll",
        AgentMethod.RecognizeOCR => "recognize.ocr",
        AgentMethod.AnalyzeImage => "analyze.image",
        AgentMethod.TranslateText => "translate.text",
        AgentMethod.TranslateImage => "translate.image",
        AgentMethod.TransformImage => "transform.image",
        AgentMethod.DeliverCopy => "deliver.copy",
        AgentMethod.DeliverSave => "deliver.save",
        AgentMethod.DeliverPin => "deliver.pin",
        AgentMethod.JobStatus => "job.status",
        AgentMethod.JobCancel => "job.cancel",
        _ => method.ToString(),
    };
}

/// <summary>对应 Mac: AgentCloudPolicy（Sources/TaAgentContracts/AgentMethods.swift:33-38）。</summary>
public enum AgentCloudPolicy
{
    Auto,
    Allow,
    Deny,
}

public static class AgentCloudPolicyExtensions
{
    public static string RawValue(this AgentCloudPolicy policy) => policy switch
    {
        AgentCloudPolicy.Auto => "auto",
        AgentCloudPolicy.Allow => "allow",
        AgentCloudPolicy.Deny => "deny",
        _ => policy.ToString(),
    };

    public static AgentCloudPolicy? Parse(string? raw) => raw switch
    {
        "auto" => AgentCloudPolicy.Auto,
        "allow" => AgentCloudPolicy.Allow,
        "deny" => AgentCloudPolicy.Deny,
        _ => null,
    };
}

/// <summary>对应 Mac: AgentErrorCode（Sources/TaAgentContracts/AgentErrors.swift:3-21）。</summary>
public enum AgentErrorCode
{
    TaAppNotInstalled,
    BridgeUnavailable,
    ProtocolVersionMismatch,
    ScreenPermissionRequired,
    AccessibilityPermissionRequired,
    InvalidRequest,
    TargetNotFound,
    TargetChanged,
    TargetBlockedByPrivacyPolicy,
    CaptureBusy,
    OcrEngineUnavailable,
    ModelProfileNotConfigured,
    CloudUploadNotAllowed,
    UserActivityDetected,
    ArtifactExpired,
    Cancelled,
    InternalError,
}

public static class AgentErrorCodeExtensions
{
    /// <summary>线上 wire 名称（rawValue）。</summary>
    public static string RawValue(this AgentErrorCode code) => code switch
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
        _ => code.ToString(),
    };

    public static AgentErrorCode Parse(string raw) => raw switch
    {
        "TA_APP_NOT_INSTALLED" => AgentErrorCode.TaAppNotInstalled,
        "BRIDGE_UNAVAILABLE" => AgentErrorCode.BridgeUnavailable,
        "PROTOCOL_VERSION_MISMATCH" => AgentErrorCode.ProtocolVersionMismatch,
        "SCREEN_PERMISSION_REQUIRED" => AgentErrorCode.ScreenPermissionRequired,
        "ACCESSIBILITY_PERMISSION_REQUIRED" => AgentErrorCode.AccessibilityPermissionRequired,
        "INVALID_REQUEST" => AgentErrorCode.InvalidRequest,
        "TARGET_NOT_FOUND" => AgentErrorCode.TargetNotFound,
        "TARGET_CHANGED" => AgentErrorCode.TargetChanged,
        "TARGET_BLOCKED_BY_PRIVACY_POLICY" => AgentErrorCode.TargetBlockedByPrivacyPolicy,
        "CAPTURE_BUSY" => AgentErrorCode.CaptureBusy,
        "OCR_ENGINE_UNAVAILABLE" => AgentErrorCode.OcrEngineUnavailable,
        "MODEL_PROFILE_NOT_CONFIGURED" => AgentErrorCode.ModelProfileNotConfigured,
        "CLOUD_UPLOAD_NOT_ALLOWED" => AgentErrorCode.CloudUploadNotAllowed,
        "USER_ACTIVITY_DETECTED" => AgentErrorCode.UserActivityDetected,
        "ARTIFACT_EXPIRED" => AgentErrorCode.ArtifactExpired,
        "CANCELLED" => AgentErrorCode.Cancelled,
        "INTERNAL_ERROR" => AgentErrorCode.InternalError,
        _ => throw new System.Text.Json.JsonException($"未知错误码：{raw}"),
    };
}

/// <summary>对应 Mac: AgentClientInfo（Sources/TaAgentContracts/AgentEnvelope.swift:7-15）。</summary>
public sealed record AgentClientInfo(string Name, string Version)
{
    public const string TaCliName = "ta-cli";
    public const string TaCliVersion = "1.0.1";

    public static AgentClientInfo TaCli() => new(TaCliName, TaCliVersion);
}

/// <summary>对应 Mac: AgentRequestEnvelope（Sources/TaAgentContracts/AgentEnvelope.swift:17-51）。</summary>
public sealed record AgentRequestEnvelope
{
    public int ProtocolVersion { get; init; } = AgentProtocol.CurrentVersion;
    public required string RequestID { get; init; }
    public required AgentMethod Method { get; init; }
    public IReadOnlyDictionary<string, TaJsonValue> Params { get; init; } = new Dictionary<string, TaJsonValue>();
    public AgentClientInfo Client { get; init; } = AgentClientInfo.TaCli();
}

/// <summary>对应 Mac: AgentResponseMetadata（Sources/TaAgentContracts/AgentEnvelope.swift:53-61）。</summary>
public sealed record AgentResponseMetadata(int DurationMs, bool CloudUploaded);

/// <summary>对应 Mac: AgentErrorPayload（Sources/TaAgentContracts/AgentErrors.swift:23-35）。</summary>
public sealed record AgentErrorPayload(AgentErrorCode Code, string Message, bool Retryable)
{
    public string? Hint { get; init; }
}

/// <summary>对应 Mac: AgentArtifact（Sources/TaAgentContracts/AgentArtifacts.swift:3-32）。</summary>
public sealed record AgentArtifact(
    string Id,
    string Path,
    string MimeType,
    int Bytes,
    string Sha256,
    DateTimeOffset ExpiresAt)
{
    public int? Width { get; init; }
    public int? Height { get; init; }
}

/// <summary>
/// 对应 Mac: AgentResponseEnvelope（Sources/TaAgentContracts/AgentEnvelope.swift:63-121）。
/// artifacts 默认空数组（非可选），data / meta / error 默认为 null 且在 JSON 中省略。
/// </summary>
public sealed record AgentResponseEnvelope
{
    public int ProtocolVersion { get; init; } = AgentProtocol.CurrentVersion;
    public required string RequestID { get; init; }
    public required bool Ok { get; init; }
    public TaJsonValue? Data { get; init; }
    public IReadOnlyList<AgentArtifact> Artifacts { get; init; } = Array.Empty<AgentArtifact>();
    public AgentResponseMetadata? Meta { get; init; }
    public AgentErrorPayload? Error { get; init; }

    /// <summary>对应 Mac: AgentResponseEnvelope.success（AgentEnvelope.swift:100-113）。</summary>
    public static AgentResponseEnvelope Success(
        string requestID,
        TaJsonValue? data = null,
        IReadOnlyList<AgentArtifact>? artifacts = null,
        AgentResponseMetadata? meta = null) =>
        new()
        {
            RequestID = requestID,
            Ok = true,
            Data = data,
            Artifacts = artifacts ?? Array.Empty<AgentArtifact>(),
            Meta = meta,
        };

    /// <summary>对应 Mac: AgentResponseEnvelope.failure（AgentEnvelope.swift:115-120）。</summary>
    public static AgentResponseEnvelope Failure(string requestID, AgentErrorPayload error) =>
        new()
        {
            RequestID = requestID,
            Ok = false,
            Error = error,
        };
}
