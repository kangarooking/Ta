using System.Runtime.InteropServices;
using System.Drawing;
using Ta.Core.Capture;

namespace Ta.Shell.UI;

/// <summary>
/// 屏幕相关互操作：鼠标所在显示器、工作区、前台应用进程。
/// 对应 Mac 版的 <c>NSScreen.screens</c>、<c>NSEvent.mouseLocation</c>、
/// <c>NSWorkspace.frontmostApplication</c>、<c>WindowSnapService</c>。
/// </summary>
internal static class ScreenInterop
{
    private const int SPI_GETWORKAREA = 0x0030;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;

        public POINT(int x, int y)
        {
            this.x = x;
            this.y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;

        public int Width => right - left;
        public int Height => bottom - top;

        public Rectangle ToRectangle() => new(left, top, Width, Height);
    }

    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEXW
    {
        public int cbSize;

        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public char[] szDevice;
    }

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW lpmi);

    // ⚠️ GetDpiForMonitor 在 Shcore.dll（Win8.1+），user32 里没有这个导出。
    // 声明成 user32 会在运行时抛 EntryPointNotFoundException —— 之前整个截图
    // 链路「点击无反应」的最终根因就是它（异常被 StartCaptureAsync 吞掉）。
    [DllImport("Shcore.dll")]
    public static extern uint GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>MDT_EffectiveDpi。</summary>
    public const int MDT_EFFECTIVE_DPI = 0;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SystemParametersInfoW(int uiAction, int uiParam, out RECT pvParam, int fWinIni);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int virtualKeyCode);

    // ─────────────────────────────────────────────────────────────────────────
    // 便捷封装
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>枚举全部显示器。</summary>
    /// <remarks>
    /// ⚠️ Id 必须与 Ta.Capture 同源：CaptureDisplay 是按 <see cref="DisplayInfo.Id"/>
    /// 在 Ta.Capture 自己的快照里找显示器的。曾经这里把 HMONITOR 句柄值
    /// （如 0x10001=65537）直接当 Id —— 与 Ta.Capture 的合成索引（0..n）对不上，
    /// 冻结整屏永远抛「找不到显示器 id=65537」，整条截图链无声失败。
    /// 现在统一走 Ta.Capture.Win32 的枚举与编号。
    /// </remarks>
    public static IReadOnlyList<DisplayInfo> EnumerateDisplays()
    {
        var displays = Ta.Capture.Win32.DisplaySnapshot.ToDisplayInfos(
            Ta.Capture.Win32.DisplayEnumerator.Enumerate());

        if (displays.Count == 0)
        {
            // 兜底：枚举失败时给一个假的主屏，避免上层拿到空列表直接崩。
            return new[]
            {
                new DisplayInfo
                {
                    Id = 0,
                    Frame = new RectD(0, 0, 1920, 1080),
                    PixelScale = 1,
                    IsPrimary = true,
                },
            };
        }

        // 排序稳定：主屏在前，其余按 X。Id 已由 ToDisplayInfos 固化，重排不影响对应关系。
        return displays
            .OrderByDescending(d => d.IsPrimary)
            .ThenBy(d => d.Frame.X)
            .ToArray();
    }

    /// <summary>单块显示器的信息采样。<c>rect</c> 是 Win32 给的显示器矩形，与 GetMonitorInfo 一致。</summary>
    private static bool HandleMonitor(IntPtr hMonitor, List<DisplayInfo> result)
    {
        var info = new MONITORINFOEXW { cbSize = Marshal.SizeOf<MONITORINFOEXW>() };
        if (!GetMonitorInfoW(hMonitor, ref info))
        {
            return true;
        }

        GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out var dpiX, out _);

        // dpi 为 0（老系统不支持 per-monitor DPI）时按 96 算，即 100% 缩放。
        var scale = dpiX > 0 ? dpiX / 96.0 : 1.0;
        var isPrimary = (info.dwFlags & 0x00000001) != 0;   // MONITORINFOF_PRIMARY

        result.Add(new DisplayInfo
        {
            // 句柄值作为显示器 id：在本进程内唯一，且与 MonitorFromPoint 的返回值同源。
            Id = (int)hMonitor.ToInt64(),
            Frame = new RectD(info.rcMonitor.left, info.rcMonitor.top,
                info.rcMonitor.Width, info.rcMonitor.Height),
            PixelScale = scale,
            IsPrimary = isPrimary,
        });

        return true;
    }

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    /// <summary>鼠标当前所在的显示器。</summary>
    public static DisplayInfo? DisplayUnderCursor()
    {
        if (!GetCursorPos(out var cursor))
        {
            return null;
        }

        // 直接在权威快照里按矩形命中：不再单独 MonitorFromPoint —— 那条路
        // 返回的是 HMONITOR，与快照 Id 体系无关（历史上的 id=65537 事故）。
        var displays = EnumerateDisplays();
        return displays.FirstOrDefault(d =>
                   cursor.x >= d.Frame.X && cursor.x < d.Frame.X + d.Frame.Width &&
                   cursor.y >= d.Frame.Y && cursor.y < d.Frame.Y + d.Frame.Height)
            ?? displays.FirstOrDefault(d => d.IsPrimary)
            ?? displays.FirstOrDefault();
    }

    /// <summary>
    /// 包含指定点的显示器的<b>工作区</b>（排除任务栏）。
    /// 对应 Mac 版 <c>NSScreen.visibleFrame</c>（ResultBarController.swift:80）。
    /// </summary>
    public static Rectangle VisibleFrameFor(RectD screenFrame)
    {
        var probe = new POINT(
            (int)(screenFrame.X + screenFrame.Width / 2),
            (int)(screenFrame.Y + screenFrame.Height / 2));

        var monitor = MonitorFromPoint(probe, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFOEXW { cbSize = Marshal.SizeOf<MONITORINFOEXW>() };
        if (monitor != IntPtr.Zero && GetMonitorInfoW(monitor, ref info))
        {
            return info.rcWork.ToRectangle();
        }

        // 兜底：主屏工作区。
        if (SystemParametersInfoW(SPI_GETWORKAREA, 0, out var work, 0))
        {
            return work.ToRectangle();
        }

        return new Rectangle(
            (int)screenFrame.X, (int)screenFrame.Y,
            (int)screenFrame.Width, (int)screenFrame.Height);
    }

    /// <summary>
    /// 前台窗口所属的进程名。
    /// 对应 Mac 版 <c>NSWorkspace.shared.frontmostApplication</c> ——
    /// 隐私黑名单（<c>agentPrivacyDenylist</c>）与源应用提示都要用它。
    /// </summary>
    public static (int ProcessId, string? ProcessName) FrontmostApplication()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return (0, null);
        }

        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0)
        {
            return (0, null);
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return ((int)pid, process.ProcessName);
        }
        catch (ArgumentException)
        {
            // 进程已退出 —— 捕获瞬间的竞态，不是错误。
            return ((int)pid, null);
        }
        catch (InvalidOperationException)
        {
            return ((int)pid, null);
        }
    }
}
