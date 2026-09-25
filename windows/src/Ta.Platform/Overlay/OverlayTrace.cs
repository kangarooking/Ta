using System.IO;

namespace Ta.Platform;

/// <summary>覆盖层链路诊断留痕（%TEMP%/ta-overlay-diag.log）—— 排查操作栏按钮→动作。</summary>
public static class OverlayTrace
{
    public static void Write(string message)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "ta-overlay-diag.log");
            File.AppendAllText(path,
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
