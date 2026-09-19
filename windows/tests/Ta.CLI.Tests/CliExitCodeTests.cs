using Ta.CLI;
using Ta.CLI.Agent;
using Ta.CLI.Transport;

namespace Ta.CLI.Tests;

/// <summary>
/// 退出码映射测试。对应 Mac: CLIOutput.swift:4-25 的 CLIExitCode.forResponse
/// 与 TaCLI.swift:12-46 的异常 catch 链（《任务书》验收第 3 条）。
/// </summary>
public class CliExitCodeTests
{
    [Fact]
    public void OkResponseExitsZero()
    {
        Assert.Equal(CliExitCode.Success, CliOutput.ExitCodeFor(
            AgentResponseEnvelope.Success("req", TaJsonValue.NullValue)));
    }

    [Theory]
    [InlineData(AgentErrorCode.TaAppNotInstalled, CliExitCode.AppNotInstalled)]
    [InlineData(AgentErrorCode.BridgeUnavailable, CliExitCode.BridgeUnavailable)]
    [InlineData(AgentErrorCode.ScreenPermissionRequired, CliExitCode.PermissionDenied)]
    [InlineData(AgentErrorCode.AccessibilityPermissionRequired, CliExitCode.PermissionDenied)]
    [InlineData(AgentErrorCode.TargetBlockedByPrivacyPolicy, CliExitCode.PrivacyBlocked)]
    [InlineData(AgentErrorCode.CloudUploadNotAllowed, CliExitCode.PrivacyBlocked)]
    [InlineData(AgentErrorCode.Cancelled, CliExitCode.Cancelled)]
    [InlineData(AgentErrorCode.InvalidRequest, CliExitCode.RequestFailed)]
    [InlineData(AgentErrorCode.TargetNotFound, CliExitCode.RequestFailed)]
    [InlineData(AgentErrorCode.TargetChanged, CliExitCode.RequestFailed)]
    [InlineData(AgentErrorCode.CaptureBusy, CliExitCode.RequestFailed)]
    [InlineData(AgentErrorCode.OcrEngineUnavailable, CliExitCode.RequestFailed)]
    [InlineData(AgentErrorCode.ModelProfileNotConfigured, CliExitCode.RequestFailed)]
    [InlineData(AgentErrorCode.UserActivityDetected, CliExitCode.RequestFailed)]
    [InlineData(AgentErrorCode.ArtifactExpired, CliExitCode.RequestFailed)]
    [InlineData(AgentErrorCode.ProtocolVersionMismatch, CliExitCode.RequestFailed)]
    [InlineData(AgentErrorCode.InternalError, CliExitCode.RequestFailed)]
    public void ErrorCodesMapToExitCodes(AgentErrorCode code, CliExitCode expected)
    {
        var response = AgentResponseEnvelope.Failure("req",
            new AgentErrorPayload(code, "msg", Retryable: false));
        Assert.Equal(expected, CliOutput.ExitCodeFor(response));
    }

    [Fact]
    public void FailureWithoutErrorPayloadFallsBackToRequestFailed()
    {
        var response = new AgentResponseEnvelope { RequestID = "req", Ok = false };
        Assert.Equal(CliExitCode.RequestFailed, CliOutput.ExitCodeFor(response));
    }

    [Fact]
    public void ExitCodeValuesMatchMacContract()
    {
        Assert.Equal(0, (int)CliExitCode.Success);
        Assert.Equal(2, (int)CliExitCode.Usage);
        Assert.Equal(3, (int)CliExitCode.AppNotInstalled);
        Assert.Equal(4, (int)CliExitCode.BridgeUnavailable);
        Assert.Equal(5, (int)CliExitCode.PermissionDenied);
        Assert.Equal(6, (int)CliExitCode.PrivacyBlocked);
        Assert.Equal(7, (int)CliExitCode.RequestFailed);
        Assert.Equal(130, (int)CliExitCode.Cancelled);
    }

    // ---------- 异常驱动的退出码（对应 Mac: TaCLI.swift 的 catch 链） ----------

    [Fact]
    public void ParseErrorMapsToUsage()
    {
        var (text, code) = CliFailureHandler.Describe(new CliParseError("用法：ta <命令> [参数]"));
        Assert.Equal("用法：ta <命令> [参数]", text);
        Assert.Equal(CliExitCode.Usage, code);
    }

    [Fact]
    public void LauncherErrorMapsToAppNotInstalled()
    {
        var exception = new TaAppLauncherException(
            TaAppLauncherError.AppNotInstalled, "找不到拓 App：C:\\Program Files\\拓\\拓.exe");
        var (text, code) = CliFailureHandler.Describe(exception);
        Assert.Equal("TA_APP_NOT_INSTALLED: 找不到拓 App：C:\\Program Files\\拓\\拓.exe", text);
        Assert.Equal(CliExitCode.AppNotInstalled, code);
    }

    [Fact]
    public void BridgeErrorMapsToBridgeUnavailable()
    {
        var exception = new TaBridgeClientException(
            TaBridgeClientError.SocketFailure,
            "Bridge connect 失败（无法连接到命名管道 \\.\\pipe\\Ta\\agent-v1）",
            operation: "connect",
            code: TaBridgeClientException.UnknownCode);
        var (text, code) = CliFailureHandler.Describe(exception);
        Assert.Equal("BRIDGE_UNAVAILABLE: Bridge connect 失败（无法连接到命名管道 \\.\\pipe\\Ta\\agent-v1）", text);
        Assert.Equal(CliExitCode.BridgeUnavailable, code);
    }

    [Fact]
    public void CancellationMapsToCancelled()
    {
        var (text, code) = CliFailureHandler.Describe(new OperationCanceledException());
        Assert.Equal("CANCELLED: 请求已取消。", text);
        Assert.Equal(CliExitCode.Cancelled, code);
    }

    [Fact]
    public void UnclassifiedErrorMapsToInternalError()
    {
        var (text, code) = CliFailureHandler.Describe(new InvalidOperationException("boom"));
        Assert.Equal("INTERNAL_ERROR: boom", text);
        Assert.Equal(CliExitCode.RequestFailed, code);
    }
}
