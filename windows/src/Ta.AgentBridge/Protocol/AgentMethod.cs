namespace Ta.AgentBridge.Protocol;

/// <summary>
/// Agent 方法全集。对应 Mac 版 TaAgentContracts/AgentMethods.swift:3-26。
///
/// raw value 即线格式里的 method 字符串，跨平台逐字一致 ——
/// Mac 的 ta CLI、Agent Skill、DeepSeek Harness 插件都按此字符串派发。
/// </summary>
public enum AgentMethod
{
    SystemHandshake,        // system.handshake
    SystemStatus,           // system.status
    SystemCapabilities,     // system.capabilities
    SystemPermissions,      // system.permissions
    TargetListDisplays,     // target.listDisplays
    TargetListWindows,      // target.listWindows
    CaptureDisplay,         // capture.display
    CaptureFrontmost,       // capture.frontmost
    CaptureWindow,          // capture.window
    CaptureRegion,          // capture.region
    CaptureInteractive,     // capture.interactive
    CaptureScroll,          // capture.scroll
    RecognizeOcr,           // recognize.ocr
    AnalyzeImage,           // analyze.image
    TranslateText,          // translate.text
    TranslateImage,         // translate.image
    TransformImage,         // transform.image
    DeliverCopy,            // deliver.copy
    DeliverSave,            // deliver.save
    DeliverPin,             // deliver.pin
    JobStatus,              // job.status
    JobCancel,              // job.cancel
}

public static class AgentMethodExtensions
{
    /// <summary>方法的线格式字符串。对应 Mac: AgentMethod.rawValue。</summary>
    public static string Raw(this AgentMethod method) => method switch
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
        AgentMethod.RecognizeOcr => "recognize.ocr",
        AgentMethod.AnalyzeImage => "analyze.image",
        AgentMethod.TranslateText => "translate.text",
        AgentMethod.TranslateImage => "translate.image",
        AgentMethod.TransformImage => "transform.image",
        AgentMethod.DeliverCopy => "deliver.copy",
        AgentMethod.DeliverSave => "deliver.save",
        AgentMethod.DeliverPin => "deliver.pin",
        AgentMethod.JobStatus => "job.status",
        AgentMethod.JobCancel => "job.cancel",
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, null),
    };

    /// <summary>
    /// 从线格式字符串解析方法。未知方法返回 false ——
    /// 对应 Mac 版 AgentMethod(rawValue:) 失败 → 整封请求解码失败。
    /// </summary>
    public static bool TryParse(string raw, out AgentMethod method)
    {
        switch (raw)
        {
            case "system.handshake": method = AgentMethod.SystemHandshake; return true;
            case "system.status": method = AgentMethod.SystemStatus; return true;
            case "system.capabilities": method = AgentMethod.SystemCapabilities; return true;
            case "system.permissions": method = AgentMethod.SystemPermissions; return true;
            case "target.listDisplays": method = AgentMethod.TargetListDisplays; return true;
            case "target.listWindows": method = AgentMethod.TargetListWindows; return true;
            case "capture.display": method = AgentMethod.CaptureDisplay; return true;
            case "capture.frontmost": method = AgentMethod.CaptureFrontmost; return true;
            case "capture.window": method = AgentMethod.CaptureWindow; return true;
            case "capture.region": method = AgentMethod.CaptureRegion; return true;
            case "capture.interactive": method = AgentMethod.CaptureInteractive; return true;
            case "capture.scroll": method = AgentMethod.CaptureScroll; return true;
            case "recognize.ocr": method = AgentMethod.RecognizeOcr; return true;
            case "analyze.image": method = AgentMethod.AnalyzeImage; return true;
            case "translate.text": method = AgentMethod.TranslateText; return true;
            case "translate.image": method = AgentMethod.TranslateImage; return true;
            case "transform.image": method = AgentMethod.TransformImage; return true;
            case "deliver.copy": method = AgentMethod.DeliverCopy; return true;
            case "deliver.save": method = AgentMethod.DeliverSave; return true;
            case "deliver.pin": method = AgentMethod.DeliverPin; return true;
            case "job.status": method = AgentMethod.JobStatus; return true;
            case "job.cancel": method = AgentMethod.JobCancel; return true;
            default: method = default; return false;
        }
    }
}

/// <summary>截图策略。对应 Mac: AgentCapturePolicy。⚠️ 仅用于 capabilities 宣告，从不被读取。</summary>
public enum AgentCapturePolicy
{
    Silent,
    Idle,
    Interactive,
}

public static class AgentCapturePolicyExtensions
{
    public static string Raw(this AgentCapturePolicy policy) => policy switch
    {
        AgentCapturePolicy.Silent => "silent",
        AgentCapturePolicy.Idle => "idle",
        AgentCapturePolicy.Interactive => "interactive",
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null),
    };
}

/// <summary>云端上传策略。对应 Mac: AgentCloudPolicy。</summary>
public enum AgentCloudPolicy
{
    Auto,
    Allow,
    Deny,
}

public static class AgentCloudPolicyExtensions
{
    public static string Raw(this AgentCloudPolicy policy) => policy switch
    {
        AgentCloudPolicy.Auto => "auto",
        AgentCloudPolicy.Allow => "allow",
        AgentCloudPolicy.Deny => "deny",
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null),
    };

    public static bool TryParse(string raw, out AgentCloudPolicy policy)
    {
        switch (raw)
        {
            case "auto": policy = AgentCloudPolicy.Auto; return true;
            case "allow": policy = AgentCloudPolicy.Allow; return true;
            case "deny": policy = AgentCloudPolicy.Deny; return true;
            default: policy = default; return false;
        }
    }
}
