using Ta.Windows.Core.Capture;
using Ta.Windows.Platform.Interop;

namespace Ta.Windows.Platform.Capture;

public enum VirtualDesktopMetric
{
    Left = 76,
    Top = 77,
    Width = 78,
    Height = 79,
}

public interface IVirtualDesktopService
{
    CaptureArea GetBounds();
}

public sealed class VirtualDesktopService : IVirtualDesktopService
{
    private readonly Func<VirtualDesktopMetric, int> metricReader;

    public VirtualDesktopService()
        : this(metric => NativeMethods.GetSystemMetrics((int)metric))
    {
    }

    public VirtualDesktopService(Func<VirtualDesktopMetric, int> metricReader)
    {
        this.metricReader = metricReader ?? throw new ArgumentNullException(nameof(metricReader));
    }

    public CaptureArea GetBounds()
    {
        var left = metricReader(VirtualDesktopMetric.Left);
        var top = metricReader(VirtualDesktopMetric.Top);
        var width = metricReader(VirtualDesktopMetric.Width);
        var height = metricReader(VirtualDesktopMetric.Height);

        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("Windows 没有返回有效的虚拟桌面尺寸，请检查显示器连接状态。");
        }

        return new CaptureArea(left, top, width, height);
    }
}
