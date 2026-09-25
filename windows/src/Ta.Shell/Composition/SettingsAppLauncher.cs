namespace Ta.Shell.Composition;

using Ta.Shell.UI;

/// <summary>
/// 拉起设置窗（Ta.Settings 独立进程）。
///
/// 对应 Mac 版「菜单栏 → 打开拓/设置」弹主窗口/设置窗。Windows 侧设置模块是
/// 独立 WinExe（WPF），主程序以子进程拉起 —— 与 Mac 同一进程不同窗口的差异
/// 换来了两侧窗口框架互不拖累（WinForms 托盘 vs WPF 设置页）。
/// </summary>
public static class SettingsAppLauncher
{
    /// <summary>尝试定位 Ta.Settings 可执行文件；找不到返回 null。</summary>
    public static string? ResolveExecutable()
    {
        // 1) 发布形态：全部可执行文件并排。
        var sideBySide = Path.Combine(AppContext.BaseDirectory, "Ta.Settings.exe");
        if (File.Exists(sideBySide))
        {
            return sideBySide;
        }

        // 2) 开发形态：从 Shell 的 bin 目录向上找到仓库根，再进 Ta.Settings 的输出目录。
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
        {
            foreach (var configuration in new[] { "Debug", "Release" })
            {
                var candidate = Path.Combine(
                    directory.FullName, "Ta.Settings", "bin", configuration,
                    "net8.0-windows", "Ta.Settings.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>打开欢迎页（对应 ShowWelcome）。返回是否成功拉起。</summary>
    public static bool ShowWelcome(TrayIconHost tray)
    {
        return Launch(tray, "--window=welcome", "欢迎页");
    }

    /// <summary>打开设置窗（对应 ShowSettings）。返回是否成功拉起。</summary>
    public static bool ShowSettings(TrayIconHost tray)
    {
        return Launch(tray, "--window=settings", "设置窗");
    }

    private static bool Launch(TrayIconHost tray, string arguments, string label)
    {
        var executable = ResolveExecutable();
        if (executable is null)
        {
            tray.SetStatusText($"未找到设置模块（{label}）");
            return false;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
            });
            tray.SetStatusText($"已打开{label}");
            return true;
        }
        catch (Exception error)
        {
            tray.SetStatusText($"{label}启动失败");
            ProgramLog.Error($"{label}启动失败：{error}");
            return false;
        }
    }
}

/// <summary>组合根内部可用的日志转发（避免依赖 Program 的私有 Log）。</summary>
internal static class ProgramLog
{
    public static void Error(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ta", "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, $"ta-shell-{DateTime.Now:yyyyMMdd}.log"),
                $"{LogPrefix()} {message}" + Environment.NewLine);
        }
        catch (Exception)
        {
            // 日志失败不影响主流程。
        }
    }

    private static string LogPrefix() => $"[Ta] {DateTime.Now:HH:mm:ss.fff}";
}
