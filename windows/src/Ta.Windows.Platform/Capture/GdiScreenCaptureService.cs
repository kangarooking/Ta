using System.Drawing;
using System.Drawing.Imaging;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Ta.Windows.Core.Capture;
using Ta.Windows.Platform.Interop;

namespace Ta.Windows.Platform.Capture;

public interface IScreenCaptureService
{
    Bitmap Capture(CaptureArea area);
}

public sealed class GdiScreenCaptureService : IScreenCaptureService
{
    public const long DefaultMaximumPixelCount = 50_000_000;

    private readonly IVirtualDesktopService virtualDesktopService;
    private readonly long maximumPixelCount;

    public GdiScreenCaptureService(
        IVirtualDesktopService virtualDesktopService,
        long maximumPixelCount = DefaultMaximumPixelCount)
    {
        this.virtualDesktopService = virtualDesktopService
            ?? throw new ArgumentNullException(nameof(virtualDesktopService));
        this.maximumPixelCount = maximumPixelCount > 0
            ? maximumPixelCount
            : throw new ArgumentOutOfRangeException(nameof(maximumPixelCount));
    }

    public Bitmap Capture(CaptureArea area)
    {
        var desktopBounds = virtualDesktopService.GetBounds();
        if (!desktopBounds.Contains(area))
        {
            throw new ArgumentOutOfRangeException(
                nameof(area),
                "截图区域超出当前虚拟桌面，请重新选择区域。");
        }

        var pixelCount = checked((long)area.Width * area.Height);
        if (pixelCount > maximumPixelCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(area),
                $"截图包含 {pixelCount:N0} 个像素，超过安全上限 {maximumPixelCount:N0}。当前结果未写入剪贴板。");
        }

        var bitmap = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppPArgb);
        try
        {
            CopyPixels(bitmap, area);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static void CopyPixels(Bitmap bitmap, CaptureArea area)
    {
        var sourceDeviceContext = NativeMethods.GetDC(nint.Zero);
        if (sourceDeviceContext == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 无法读取桌面画面，当前剪贴板没有变化。");
        }

        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            var destinationDeviceContext = graphics.GetHdc();
            try
            {
                if (!NativeMethods.BitBlt(
                        destinationDeviceContext,
                        0,
                        0,
                        area.Width,
                        area.Height,
                        sourceDeviceContext,
                        area.X,
                        area.Y,
                        NativeMethods.SourceCopyWithLayeredWindows))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 截图调用失败，当前剪贴板没有变化。");
                }
            }
            finally
            {
                graphics.ReleaseHdc(destinationDeviceContext);
            }
        }
        finally
        {
            NativeMethods.ReleaseDC(nint.Zero, sourceDeviceContext);
        }
    }
}
