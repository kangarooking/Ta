using Ta.Capture.Win32;
using Ta.Core.Capture;

namespace Ta.Capture.Tests.Win32;

/// <summary>
/// <see cref="WindowInfo"/> 构造的单元测试。
///
/// 对应 Mac 版 TaAgentCaptureService.swift:289-302 的 windows 映射。
/// </summary>
public class WindowInfoBuilderTests
{
    private static readonly IReadOnlyList<DisplayInfo> Displays = new[]
    {
        new DisplayInfo { Id = 0, Frame = new RectD(0, 0, 1920, 1080), PixelScale = 1, IsPrimary = true },
        new DisplayInfo { Id = 1, Frame = new RectD(1920, 0, 1920, 1080), PixelScale = 2, IsPrimary = false },
    };

    private static RawWindowObservation Window(
        IntPtr handle, RectD frame, int processId = 4242, int zOrder = 0) =>
        new(handle, processId, @"C:\Program Files\Mozilla Firefox\firefox.exe", "标签页",
            frame, true, false, false, 1, zOrder);

    [Fact]
    public void 应用名取映像文件名去扩展名()
    {
        var built = WindowInfoBuilder.Build(
            Displays,
            new[] { Window(new IntPtr(1), new RectD(100, 100, 800, 600)) },
            4242);

        var single = Assert.Single(built);
        // 对应 Mac 的 application.applicationName（"Firefox"），不是完整路径。
        Assert.Equal("firefox", single.AppName);
        // BundleOrIdentity 保留全路径 —— 参考文档 §14 风险 #38 要求身份是稳定字面量。
        Assert.Equal(@"C:\Program Files\Mozilla Firefox\firefox.exe", single.BundleOrIdentity);
        Assert.Equal("标签页", single.Title);
        Assert.Equal(4242, single.ProcessId);
        Assert.Equal(0, single.ZOrder);
    }

    /// <summary>Frame 不裁剪 —— 与 Mac 的 TaAgentWindowTarget.frame（未经 screen 求交）一致。</summary>
    [Fact]
    public void Frame保留未裁剪的窗口边界()
    {
        // 窗口右半越出主屏。
        var built = WindowInfoBuilder.Build(
            Displays,
            new[] { Window(new IntPtr(1), new RectD(1500, 100, 800, 600)) },
            4242);

        var single = Assert.Single(built);
        Assert.Equal(new RectD(1500, 100, 800, 600), single.Frame);
    }

    /// <summary>完全不落在任何屏上的窗口被剔除（不在用户视野内）。</summary>
    [Fact]
    public void 不在任何屏上的窗口被剔除()
    {
        var built = WindowInfoBuilder.Build(
            Displays,
            new[] { Window(new IntPtr(1), new RectD(9000, 9000, 800, 600)) },
            4242);

        Assert.Empty(built);
    }

    /// <summary>过滤条件与 WindowSnapTargetFilter 一致（非前台进程被剔除）。</summary>
    [Fact]
    public void 复用同一套过滤条件()
    {
        var built = WindowInfoBuilder.Build(
            Displays,
            new[] { Window(new IntPtr(1), new RectD(100, 100, 800, 600), processId: 7777) },
            4242);

        Assert.Empty(built);
    }

    [Fact]
    public void 取不到映像路径时用PID兜底且AppName非空()
    {
        var observation = new RawWindowObservation(
            new IntPtr(1), 4242, null, null, new RectD(100, 100, 800, 600),
            true, false, false, 1, 0);

        var built = WindowInfoBuilder.Build(Displays, new[] { observation }, 4242);

        var single = Assert.Single(built);
        Assert.False(string.IsNullOrWhiteSpace(single.AppName));
        Assert.Null(single.BundleOrIdentity);
    }

    /// <summary>宿主屏取相交面积最大者。</summary>
    [Fact]
    public void 宿主屏取相交面积最大者()
    {
        // 主要落在副屏（id=1）。
        var host = WindowInfoBuilder.HostDisplay(Displays, new RectD(1500, 100, 1000, 600));

        Assert.NotNull(host);
        Assert.Equal(1, host.Id);
    }
}
