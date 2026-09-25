using System.Diagnostics;

namespace Ta.CLI.Transport;

/// <summary>
/// 拉起 App 失败。对应 Mac: TaAppLauncherError（Sources/TaAgentClient/TaAppLauncher.swift:29-41）。
/// </summary>
public enum TaAppLauncherError
{
    /// <summary>App 未安装（找不到可执行文件）。</summary>
    AppNotInstalled,

    /// <summary>启动进程失败。</summary>
    LaunchFailed,
}

/// <summary>
/// 拉起异常。CLI 入口将其映射为退出码 3（TA_APP_NOT_INSTALLED）。
/// 对应 Mac: TaAppLauncherError 经 TaCLI.swift:37-39 映射。
/// </summary>
public sealed class TaAppLauncherException : Exception
{
    public TaAppLauncherException(TaAppLauncherError error, string message) : base(message)
    {
        Error = error;
    }

    public TaAppLauncherError Error { get; }
}

/// <summary>拉起抽象。编排层只依赖本接口，便于单测注入假实现。</summary>
public interface ITaAppLauncher
{
    Task LaunchAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Windows 版拓 App 拉起器。对应 Mac: TaAppLauncher（Sources/TaAgentClient/TaAppLauncher.swift:43-86）。
///
/// 行为对齐：
///   - 启动参数 ["--agent-bridge"]，不激活（对应 macOS NSWorkspace.OpenConfiguration.activates = false，
///     Windows 等价物 = CreateNoWindow + WindowStyle.Hidden + UseShellExecute=false 后台启动）；
///   - 路径可用环境变量 TA_APP_PATH 覆盖（对应 Mac 版 applicationURLFromEnvironment）；
///   - Windows 默认 %ProgramFiles%\拓\拓.exe（对应 macOS 的 /Applications/拓.app）。
/// </summary>
public sealed class TaAppLauncher : ITaAppLauncher
{
    /// <summary>对应 Mac: TaAppLauncher.defaultApplicationURL = /Applications/拓.app。</summary>
    public static string DefaultApplicationPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "拓",
            "拓.exe");

    /// <summary>启动参数，对应 Mac: TaAppLaunchPlan.arguments = ["--agent-bridge"]。</summary>
    public const string LaunchArgument = "--agent-bridge";

    private readonly string _applicationPath;

    public TaAppLauncher(string? applicationPath = null)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("TA_APP_PATH");
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            _applicationPath = Path.GetFullPath(fromEnvironment);
        }
        else
        {
            _applicationPath = applicationPath is { Length: > 0 }
                ? Path.GetFullPath(applicationPath)
                : DefaultApplicationPath;
        }
    }

    public string ApplicationPath => _applicationPath;

    /// <summary>
    /// 对应 Mac: TaAppLauncher.launch()（TaAppLauncher.swift:62-85）。
    /// App 不存在 → TaAppLauncherError.appNotInstalled（退出码 3）；
    /// 启动失败 → TaAppLauncherError.launchFailed。
    /// </summary>
    public Task LaunchAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_applicationPath))
        {
            // 对应 Mac: "找不到拓 App：\(path)"
            throw new TaAppLauncherException(
                TaAppLauncherError.AppNotInstalled,
                $"找不到拓 App：{_applicationPath}");
        }

        try
        {
            var startInfo = new ProcessStartInfo(_applicationPath, LaunchArgument)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(_applicationPath) ?? AppContext.BaseDirectory,
            };
            var process = Process.Start(startInfo);
            if (process is null)
            {
                throw new TaAppLauncherException(
                    TaAppLauncherError.LaunchFailed,
                    "无法在后台启动拓：进程未启动。");
            }
            return Task.CompletedTask;
        }
        catch (TaAppLauncherException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // 对应 Mac: "无法在后台启动拓：\(message)"
            throw new TaAppLauncherException(
                TaAppLauncherError.LaunchFailed,
                $"无法在后台启动拓：{exception.Message}");
        }
    }
}

/// <summary>默认客户端工厂：为每个请求新建命名管道客户端。</summary>
public sealed class NamedPipeBridgeClientFactory : ITaBridgeClientFactory
{
    public ITaBridgeClient Create(string socket, double timeoutSeconds) =>
        new NamedPipeBridgeClient(socket, timeoutSeconds);
}
