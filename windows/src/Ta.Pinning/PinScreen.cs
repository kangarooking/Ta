using Ta.Core.Capture;
using Ta.Pinning.Interop;

namespace Ta.Pinning;

/// <summary>
/// 显示器与光标查询。
///
/// 对应 Mac 版的两处：
///   · <c>NSScreen.visibleFrame</c>（:137-139）→ Windows 的显示器**工作区**
///     （GetMonitorInfo 的 rcWork，已排除任务栏），语义一致。
///   · <c>selectionAtMouse()</c>（:203-214）→ GetCursorPos + MonitorFromPoint。
///
/// 注意参考文档 §14 风险 #25：<c>EnumDisplayMonitors</c> 的顺序不保证，
/// 因此这里一律按**点**定位显示器，从不假设枚举顺序，也不用 screens.first。
/// </summary>
internal static class PinScreen
{
    private const uint MonitorDefaultToNull = 0;
    private const uint MonitorDefaultToPrimary = 1;

    /// <summary>默认 DPI（96 = 100%）。</summary>
    public const double DefaultScale = 1;

    /// <summary>光标所在的全局屏坐标（物理像素）。</summary>
    public static PinInterop.POINT CursorPosition()
    {
        PinInterop.GetCursorPos(out var cursor);
        return cursor;
    }

    /// <summary>光标所在显示器的工作区（物理像素）。</summary>
    public static RectD WorkAreaAtCursor() => WorkAreaInPixels(CursorPosition());

    /// <summary>给定点所在显示器的工作区（**逻辑 pt**，排除任务栏）。</summary>
    /// <remarks>
    /// 对应 Mac: NSScreen.visibleFrame。Mac 的 visibleFrame 本来就是 pt，
    /// 而 GetMonitorInfo 的 rcWork 是**物理像素**，所以这里必须按该屏缩放除回去，
    /// 否则钉图在非 100% 缩放的屏上会被放到 1.25× / 1.5× 的目标位置（真机验证抓到的实缺陷）。
    /// </remarks>
    public static RectD WorkAreaInPoints(PinInterop.POINT point)
    {
        var monitor = PinInterop.MonitorFromPoint(point, MonitorDefaultToNull);
        var handle = monitor != IntPtr.Zero
            ? monitor
            : PinInterop.MonitorFromPoint(new PinInterop.POINT(0, 0), MonitorDefaultToPrimary);

        var info = PinInterop.MONITORINFO.Create();
        if (!PinInterop.GetMonitorInfo(handle, ref info))
        {
            return new RectD(0, 0, 1920, 1040);
        }

        var scale = ScaleAt(point);
        return new RectD(
            info.rcWork.left / scale,
            info.rcWork.top / scale,
            info.rcWork.Width / scale,
            info.rcWork.Height / scale);
    }

    /// <summary>
    /// 给定点所在显示器的工作区（**物理像素**）。
    /// 与仓库其余部分（<c>CaptureSelection.ScreenFrame</c>、FrozenDisplayCropper）的单位一致。
    /// </summary>
    public static RectD WorkAreaInPixels(PinInterop.POINT point)
    {
        var monitor = PinInterop.MonitorFromPoint(point, MonitorDefaultToNull);
        return WorkAreaPixels(monitor);
    }

    private static RectD WorkAreaPixels(IntPtr monitor)
    {
        if (monitor != IntPtr.Zero)
        {
            var info = PinInterop.MONITORINFO.Create();
            if (PinInterop.GetMonitorInfo(monitor, ref info))
            {
                return new RectD(info.rcWork.left, info.rcWork.top, info.rcWork.Width, info.rcWork.Height);
            }
        }

        // 兜底：主显示器工作区；再不行给一个常见分辨率，绝不返回空矩形。
        var primary = PinInterop.MonitorFromPoint(new PinInterop.POINT(0, 0), MonitorDefaultToPrimary);
        var fallback = PinInterop.MONITORINFO.Create();
        if (PinInterop.GetMonitorInfo(primary, ref fallback))
        {
            return new RectD(fallback.rcWork.left, fallback.rcWork.top, fallback.rcWork.Width, fallback.rcWork.Height);
        }

        return new RectD(0, 0, 1920, 1040);
    }

    /// <summary>给定点的 DPI 缩放（1 = 100%）。对应 Mac: NSScreen.backingScaleFactor。</summary>
    public static double ScaleAt(PinInterop.POINT point)
    {
        var monitor = PinInterop.MonitorFromPoint(point, MonitorDefaultToNull);
        var handle = monitor != IntPtr.Zero
            ? monitor
            : PinInterop.MonitorFromPoint(new PinInterop.POINT(0, 0), MonitorDefaultToPrimary);

        try
        {
            var status = PinInterop.GetDpiForMonitor(handle, PinInterop.MDT_EFFECTIVE_DPI, out var dpiX, out _);
            if (status == 0 && dpiX > 0)
            {
                return dpiX / 96.0;
            }
        }
        catch (DllNotFoundException)
        {
            // Win7 上没有 shcore —— 回落到 100%。
        }
        catch (EntryPointNotFoundException)
        {
            // 同上。
        }

        return DefaultScale;
    }

    /// <summary>
    /// 光标处的「选区」。对应 Mac 的 <c>selectionAtMouse()</c>（:203-214）：
    /// 一个 1×1 的全局矩形，加上所在屏的工作区与缩放。
    /// 两个字段都用**物理像素**，与 <c>CaptureSelection</c> 在 Windows 侧的约定一致。
    /// </summary>
    public static CaptureSelection SelectionAtCursor(double? scale = null)
    {
        var cursor = CursorPosition();
        return new CaptureSelection
        {
            GlobalRect = new RectD(cursor.x, cursor.y, 1, 1),
            ScreenFrame = WorkAreaInPixels(cursor),
            BackingScaleFactor = scale ?? ScaleAt(cursor),
        };
    }
}
