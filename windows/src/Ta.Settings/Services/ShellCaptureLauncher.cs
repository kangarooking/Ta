using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Ta.Settings.Services;

/// <summary>
/// 拉起截图的真实实现：设置/欢迎窗是独立进程，截图由常驻的 Shell 主进程执行。
/// 这里以 <c>Ta.Shell.exe --capture-*</c> 拉起 Shell；Shell 有单实例互斥，
/// 已在运行时会把参数经命名管道转发给首实例，不会出现第二个托盘。
/// </summary>
public sealed class ShellCaptureLauncher : ICaptureLauncher
{
    private readonly string? _shellExePath;

    /// <summary>构造。<paramref name="shellExePath"/> 供测试注入，生产取应用同目录的 Ta.Shell.exe。</summary>
    public ShellCaptureLauncher(string? shellExePath = null)
    {
        _shellExePath = shellExePath;
    }

    /// <inheritdoc />
    public bool IsCapturing => false; // 跨进程 fire-and-forget，不追踪 Shell 内的会话状态。

    /// <inheritdoc />
    public string StatusText => "准备好了，选择一个操作开始拓取";

    /// <inheritdoc />
    public void StartCapture(Core.CaptureMode mode)
    {
        Launch("--capture-" + mode switch
        {
            Core.CaptureMode.Interactive => "interactive",
            Core.CaptureMode.Intelligent => "intelligent",
            Core.CaptureMode.Translation => "translation",
            Core.CaptureMode.Image => "image",
            Core.CaptureMode.Pin => "pin",
            Core.CaptureMode.Long => "long",
            _ => mode.ToString().ToLowerInvariant(),
        });
    }

    /// <inheritdoc />
    public void PinClipboardContent() => Launch("--pin-clipboard");

    private void Launch(string argument)
    {
        var exe = _shellExePath
            ?? Path.Combine(AppContext.BaseDirectory, "Ta.Shell.exe");
        if (!File.Exists(exe))
        {
            throw new FileNotFoundException($"找不到 Shell 主程序：{exe}。请确认 Ta.Shell 与 Ta.Settings 安装在同一目录。");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = argument,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }
}
