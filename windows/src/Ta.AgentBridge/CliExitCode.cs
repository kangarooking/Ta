using Ta.AgentBridge.Protocol;

namespace Ta.AgentBridge;

/// <summary>CLI 退出码。对应 Mac 版 CLIOutput.swift:4-25（参考文档 §11.2c）。</summary>
public enum CliExitCode
{
    Success = 0,
    Usage = 2,
    AppNotInstalled = 3,
    BridgeUnavailable = 4,
    PermissionDenied = 5,
    PrivacyBlocked = 6,
    RequestFailed = 7,
    Cancelled = 130,
}

/// <summary>退出码映射。对应 Mac 版 CLIExitCode.forResponse / forError。</summary>
public static class CliExitCodes
{
    /// <summary>按响应信封映射退出码。对应 Mac: CLIExitCode.forResponse。</summary>
    public static CliExitCode ForResponse(AgentResponseEnvelope response)
    {
        if (response.Ok)
        {
            return CliExitCode.Success;
        }

        return response.Error?.Code switch
        {
            AgentErrorCode.ScreenPermissionRequired or AgentErrorCode.AccessibilityPermissionRequired => CliExitCode.PermissionDenied,
            AgentErrorCode.TargetBlockedByPrivacyPolicy or AgentErrorCode.CloudUploadNotAllowed => CliExitCode.PrivacyBlocked,
            AgentErrorCode.TaAppNotInstalled => CliExitCode.AppNotInstalled,
            AgentErrorCode.BridgeUnavailable => CliExitCode.BridgeUnavailable,
            AgentErrorCode.Cancelled => CliExitCode.Cancelled,
            _ => CliExitCode.RequestFailed,
        };
    }

    /// <summary>按异常映射退出码。对应 Mac 版对 TaAppLauncherError / TaBridgeClientError 的处理。</summary>
    public static CliExitCode ForException(Exception error) => error switch
    {
        OperationCanceledException => CliExitCode.Cancelled,
        AgentBridgeClientException => CliExitCode.BridgeUnavailable,
        AgentBridgeServerException => CliExitCode.BridgeUnavailable,
        _ => CliExitCode.RequestFailed,
    };
}
