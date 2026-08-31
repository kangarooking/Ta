using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Ta.Windows.Core.Capture;
using Ta.Windows.Platform.Interop;

namespace Ta.Windows.Platform.Display;

public readonly record struct PhysicalScreenPoint(int X, int Y);

public interface ICursorPositionService
{
    PhysicalScreenPoint GetPosition();
}

public sealed class CursorPositionService : ICursorPositionService
{
    public PhysicalScreenPoint GetPosition()
    {
        if (!NativeMethods.GetCursorPos(out var point))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取鼠标位置，截图尚未开始。");
        }

        return new PhysicalScreenPoint(point.X, point.Y);
    }
}

public interface IPhysicalWindowService
{
    void CoverArea(Window window, CaptureArea area, bool topMost = true);
}

public sealed class PhysicalWindowService : IPhysicalWindowService
{
    public void CoverArea(Window window, CaptureArea area, bool topMost = true)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero)
        {
            throw new InvalidOperationException("选择层窗口尚未初始化。");
        }

        var insertAfter = topMost ? NativeMethods.TopMostWindow : nint.Zero;
        var flags = NativeMethods.SetWindowPositionNoActivate |
                    NativeMethods.SetWindowPositionShowWindow;
        if (!NativeMethods.SetWindowPos(
                handle,
                insertAfter,
                area.X,
                area.Y,
                area.Width,
                area.Height,
                flags))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法覆盖当前虚拟桌面，截图尚未开始。");
        }
    }
}

public static class DpiAwarenessService
{
    public static bool TryEnablePerMonitorV2()
    {
        try
        {
            return NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.PerMonitorAwareV2);
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}

public static class DesktopCompositionService
{
    public static bool TryFlush() => NativeMethods.DwmFlush() >= 0;
}
