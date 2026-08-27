using System.ComponentModel;
using System.Runtime.InteropServices;
using Ta.Windows.Core.Capture;
using Ta.Windows.Platform.Interop;

namespace Ta.Windows.Platform.Capture;

public interface IForegroundWindowService
{
    CaptureArea GetForegroundWindowArea();
}

public sealed class ForegroundWindowService(IVirtualDesktopService virtualDesktopService)
    : IForegroundWindowService
{
    private const int ExtendedFrameBoundsAttribute = 9;

    public CaptureArea GetForegroundWindowArea()
    {
        var windowHandle = NativeMethods.GetForegroundWindow();
        if (windowHandle == nint.Zero)
        {
            throw new InvalidOperationException("当前没有可截图的前台软件窗口。");
        }

        if (NativeMethods.IsIconic(windowHandle))
        {
            throw new InvalidOperationException("当前软件窗口已最小化，请先恢复窗口后再截图。");
        }

        NativeMethods.NativeRect rectangle;
        var dwmResult = NativeMethods.DwmGetWindowAttribute(
            windowHandle,
            ExtendedFrameBoundsAttribute,
            out rectangle,
            Marshal.SizeOf<NativeMethods.NativeRect>());
        if (dwmResult < 0 && !NativeMethods.GetWindowRect(windowHandle, out rectangle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取当前软件窗口边界。");
        }

        var width = rectangle.Right - rectangle.Left;
        var height = rectangle.Bottom - rectangle.Top;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("当前软件窗口没有有效的可见区域。");
        }

        var area = new CaptureArea(rectangle.Left, rectangle.Top, width, height);
        return area.Intersect(virtualDesktopService.GetBounds())
            ?? throw new InvalidOperationException("当前软件窗口不在可见桌面范围内。");
    }
}
