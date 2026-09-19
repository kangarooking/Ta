using System.IO;
using System.Windows;

namespace Ta.Settings;

/// <summary>
/// 最小启动入口。只负责把 <see cref="TaApplication"/> 拉起来。
/// WPF 要求入口方法带 <see cref="STAThreadAttribute"/>。
/// </summary>
public static class Program
{
    /// <summary>入口。</summary>
    [STAThread]
    public static void Main(string[] args)
    {
        var app = new TaApplication();
        app.DispatcherUnhandledException += (_, e) =>
        {
            // 崩溃栈落盘，供自动化冒烟测试与现场排查。
            LogCrash(e.Exception);
            e.Handled = false;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
        app.Run();
    }

    private static void LogCrash(Exception? ex)
    {
        if (ex is null)
        {
            return;
        }

        try
        {
            var path = Path.Combine(Path.GetTempPath(), "Ta.Settings-crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {ex}\n\n");
        }
        catch (Exception)
        {
            // 日志写不进也不能影响崩溃流程。
        }
    }
}
