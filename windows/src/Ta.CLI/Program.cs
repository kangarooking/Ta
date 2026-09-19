using System.Text;
using Ta.CLI.Agent;
using Ta.CLI.Transport;

namespace Ta.CLI;

/// <summary>
/// 异常 → (输出文本, 退出码) 映射。从 Program.Main 的 catch 链抽出，便于单测。
/// 逐项对应 Mac: Sources/TaCLI/TaCLI.swift:13-46 的 catch 链与退出码约定。
/// </summary>
public static class CliFailureHandler
{
    /// <summary>
    /// 映射规则（CLIOutput.swift:4-25 + TaCLI.swift:13-46）：
    ///   CliParseError（含 help）     → usage(2)，消息 = 解析错误原文
    ///   TaAppLauncherException       → TA_APP_NOT_INSTALLED(3)
    ///   TaBridgeClientException      → BRIDGE_UNAVAILABLE(4)
    ///   OperationCanceledException   → CANCELLED: 请求已取消。(130)
    ///   其他一切                     → INTERNAL_ERROR(7)（含 recipe 读取失败等未分类异常）
    /// </summary>
    public static (string Text, CliExitCode Code) Describe(Exception exception) => exception switch
    {
        CliParseError parseError => (parseError.Message, CliExitCode.Usage),
        TaAppLauncherException launcherError =>
            ($"TA_APP_NOT_INSTALLED: {launcherError.Message}", CliExitCode.AppNotInstalled),
        TaBridgeClientException bridgeError =>
            ($"BRIDGE_UNAVAILABLE: {bridgeError.Message}", CliExitCode.BridgeUnavailable),
        OperationCanceledException => ("CANCELLED: 请求已取消。", CliExitCode.Cancelled),
        Exception other => ($"INTERNAL_ERROR: {other.Message}", CliExitCode.RequestFailed),
    };
}

/// <summary>
/// ta 命令行入口。对应 Mac: Sources/TaCLI/TaCLI.swift:6-54。
///
/// 退出码（CLIOutput.swift:4-25 / 参考文档 §11.2c）：
///   0 成功 / 2 usage / 3 appNotInstalled / 4 bridgeUnavailable /
///   5 permissionDenied / 6 privacyBlocked / 7 requestFailed / 130 cancelled。
/// 输出约定：成功 → stdout，失败 → stderr；始终以恰好一个 "\n" 结尾
/// （⚠️ 不能用 WriteLine —— Windows 的 Environment.NewLine 是 "\r\n"）。
/// SIGINT（Ctrl+C）取消执行中的任务，退出码 130。
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] rawArguments)
    {
        ConfigureConsoleEncoding();

        CliInvocation invocation;
        try
        {
            invocation = CliParser.Parse(rawArguments);
        }
        catch (Exception exception)
        {
            // 对应 Mac: TaCLI.swift:12-19 —— 解析错误 → stderr + 退出码 2。
            var (text, code) = CliFailureHandler.Describe(exception);
            Console.Error.Write(text);
            Console.Error.Write('\n');
            return (int)code;
        }

        // 对应 Mac: DispatchSource SIGINT → execution.cancel()（TaCLI.swift:21-26）。
        using var cancellationSource = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;   // 阻止进程立即终止，让任务有机会清理。
            cancellationSource.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            var response = await CliRunner.Default.ExecuteAsync(invocation, cancellationSource.Token)
                .ConfigureAwait(false);
            var rendered = CliOutput.Render(response, invocation.OutputFormat);
            var destination = response.Ok ? Console.Out : Console.Error;
            // 对应 Mac: write(rendered, to: destination) —— 输出始终以恰好一个 "\n" 结尾。
            destination.Write(rendered);
            destination.Write('\n');
            return (int)CliOutput.ExitCodeFor(response);
        }
        catch (Exception exception)
        {
            // 对应 Mac: TaCLI.swift:34-46 的 catch 链（CancellationError / TaAppLauncherError /
            // TaBridgeClientError / 通用 INTERNAL_ERROR）。
            var (text, code) = CliFailureHandler.Describe(exception);
            Console.Error.Write(text);
            Console.Error.Write('\n');
            return (int)code;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    /// <summary>
    /// 保证中文/全角符号（拓、×、（））在任何控制台代码页下都能正确输出。
    /// 重定向到管道时 .NET 默认已是 UTF8NoBOM；附加到控制台时显式切换为 UTF-8。
    /// 某些宿主（无控制台）设置会抛异常，忽略即可。
    /// </summary>
    private static void ConfigureConsoleEncoding()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch (Exception)
        {
            // 忽略：无控制台 / 句柄无效的场景下保持默认编码。
        }
    }
}
