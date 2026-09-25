using Ta.Capture.Win32;
using Ta.Core.Capture;

namespace Ta.Capture.Tests.Win32;

/// <summary>
/// 构造 <see cref="RawWindowObservation"/> 的测试辅助。
///
/// 默认值是一个「应该通过所有过滤」的窗口，各测试只覆盖自己要考察的那一个字段 ——
/// 这样断言失败时能立刻看出是哪条规则把它挡掉的。
/// </summary>
internal static class WindowFixture
{
    public const int FrontmostProcessId = 4242;
    public const int OtherProcessId = 9999;

    /// <summary>1280×800 的屏，原点 (0,0)。</summary>
    public static RectD ScreenFrame { get; } = new(0, 0, 1280, 800);

    public static RawWindowObservation Window(
        IntPtr handle,
        RectD frame,
        int processId = FrontmostProcessId,
        bool isVisible = true,
        bool isToolWindow = false,
        bool isCloaked = false,
        double alpha = 1,
        int zOrder = 0,
        string? imagePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        string? title = "窗口标题") =>
        new(handle, processId, imagePath, title, frame, isVisible, isToolWindow, isCloaked, alpha, zOrder);

    public static IntPtr Handle(int value) => new(value);
}

/// <summary>
/// 窗口枚举过滤的单元测试。
///
/// 这是验收条件 #1 的核心：过滤规则**完全不含 Win32 调用**，
/// 全部集中在 <see cref="WindowSnapTargetFilter"/> 与 <see cref="WindowSnapSnapshotPolicy"/> 上，
/// 因此这里可以凭空构造一份假窗口列表断言结果，不需要真的开窗口。
///
/// 每个用例都标注了对应的 Mac 源码位置（WindowSnapService.swift）。
/// </summary>
public class WindowSnapTargetFilterTests
{
    private static IReadOnlyList<WindowSnapTarget> Targets(
        IReadOnlyList<RawWindowObservation> windows,
        int? frontmost = WindowFixture.FrontmostProcessId,
        RectD? screen = null) =>
        WindowSnapTargetFilter.Targets(windows, frontmost, screen ?? WindowFixture.ScreenFrame);

    // ── 单条过滤规则 ────────────────────────────────────────────────

    /// <summary>Mac: guard layer &gt;= 0 → 对应 Windows 的未遮蔽检查。</summary>
    [Fact]
    public void 被遮蔽的窗口被剔除()
    {
        var windows = new[]
        {
            WindowFixture.Window(WindowFixture.Handle(1), new RectD(0, 0, 400, 300), isCloaked: true),
        };

        Assert.Empty(Targets(windows));
    }

    /// <summary>Mac 没有 toolwindow 概念，但 CGWindowList 默认排除桌面/面板图层。</summary>
    [Fact]
    public void 工具窗口被剔除()
    {
        var windows = new[]
        {
            WindowFixture.Window(WindowFixture.Handle(1), new RectD(0, 0, 400, 300), isToolWindow: true),
        };

        Assert.Empty(Targets(windows));
    }

    /// <summary>Mac: .optionOnScreenOnly。</summary>
    [Fact]
    public void 不可见窗口被剔除()
    {
        var windows = new[]
        {
            WindowFixture.Window(WindowFixture.Handle(1), new RectD(0, 0, 400, 300), isVisible: false),
        };

        Assert.Empty(Targets(windows));
    }

    /// <summary>Mac: guard alpha &gt; 0.01。注意是**严格大于**。</summary>
    [Theory]
    [InlineData(0.005, false)]   // 远低于阈值
    [InlineData(0.01, false)]    // 恰好等于阈值 → 仍剔除（Mac 用 > 而非 >=）
    [InlineData(0.0118, true)]   // 3/255，最小可见 alpha
    [InlineData(1, true)]
    public void alpha阈值按严格大于判定(double alpha, bool shouldPass)
    {
        var windows = new[]
        {
            WindowFixture.Window(WindowFixture.Handle(1), new RectD(0, 0, 400, 300), alpha: alpha),
        };

        var targets = Targets(windows);
        Assert.Equal(shouldPass, targets.Count == 1);
    }

    /// <summary>Mac: guard quartzFrame.width &gt;= 24, quartzFrame.height &gt;= 16。两个维度各自判定。</summary>
    [Theory]
    [InlineData(24, 16, true)]    // 恰好达到下限 → 通过
    [InlineData(23, 16, false)]   // 宽度差 1 → 剔除
    [InlineData(24, 15, false)]   // 高度差 1 → 剔除
    [InlineData(23, 15, false)]
    public void 尺寸下限为24x16且含边界(int width, int height, bool shouldPass)
    {
        var windows = new[]
        {
            WindowFixture.Window(WindowFixture.Handle(1), new RectD(0, 0, width, height)),
        };

        var targets = Targets(windows);
        Assert.Equal(shouldPass, targets.Count == 1);
    }

    /// <summary>Mac: WindowSnapSnapshotPolicy.includes —— owner PID 必须等于前台 PID。</summary>
    [Fact]
    public void 非前台进程的窗口被剔除()
    {
        var windows = new[]
        {
            WindowFixture.Window(WindowFixture.Handle(1), new RectD(0, 0, 400, 300),
                processId: WindowFixture.OtherProcessId),
        };

        Assert.Empty(Targets(windows));
    }

    // ── 前台 PID 为 null 的放宽语义 ─────────────────────────────────

    /// <summary>
    /// ⚠️ Mac 版在 frontmostProcessID 为 nil 时 <b>返回 true</b>（放行全部窗口）。
    /// 见 WindowSnapService.swift:23-25。这是有意的：拿不到前台进程时不能把所有窗口都滤掉。
    /// 本测试锁定该语义，防止后续「顺手」改成全剔除。
    /// </summary>
    [Fact]
    public void 前台进程未知时放行所有进程的窗口()
    {
        var windows = new[]
        {
            WindowFixture.Window(WindowFixture.Handle(1), new RectD(0, 0, 400, 300),
                processId: WindowFixture.FrontmostProcessId),
            WindowFixture.Window(WindowFixture.Handle(2), new RectD(0, 400, 400, 300),
                processId: WindowFixture.OtherProcessId),
        };

        var targets = Targets(windows, frontmost: null);

        Assert.Equal(2, targets.Count);
    }

    /// <summary>前台 PID 为 null 时，**其它**过滤条件依然生效（不是完全放行）。</summary>
    [Fact]
    public void 前台进程未知时其余条件仍然生效()
    {
        var windows = new[]
        {
            WindowFixture.Window(WindowFixture.Handle(1), new RectD(0, 0, 400, 300), isVisible: false),
            WindowFixture.Window(WindowFixture.Handle(2), new RectD(0, 0, 20, 10)),
        };

        Assert.Empty(Targets(windows, frontmost: null));
    }

    // ── 与屏幕求交后的第二遍尺寸检查 ────────────────────────────────

    /// <summary>
    /// Mac 做两遍尺寸检查：先查原始矩形（:54），再查与 screen.frame 求交之后（:64）。
    /// 本用例覆盖第二遍 —— 窗口本身够大，但被屏幕边缘切掉一大半后不足 24×16。
    /// </summary>
    [Fact]
    public void 被屏幕边缘切到不足24宽的窗口被剔除()
    {
        // 窗口 (1250, 0, 400×300)，屏幕宽 1280 → 相交后只剩 30 宽（>= 24，通过）。
        // 这里用 (1265, 0, 400×300) → 相交后只剩 15 宽（< 24，剔除）。
        var windows = new[]
        {
            WindowFixture.Window(WindowFixture.Handle(1), new RectD(1265, 0, 400, 300)),
        };

        Assert.Empty(Targets(windows));
    }

    /// <summary>被屏幕边缘切掉但仍 >= 24×16 的窗口应当保留，且 frame 是**裁剪后**的。</summary>
    [Fact]
    public void 部分越屏的窗口保留裁剪后的矩形()
    {
        var windows = new[]
        {
            WindowFixture.Window(WindowFixture.Handle(1), new RectD(1250, 0, 400, 300)),
        };

        var targets = Targets(windows);

        var single = Assert.Single(targets);
        // 屏幕右边界 1280，所以裁剪后是 (1250, 0, 30×300)。
        Assert.Equal(1250, single.Frame.MinX);
        Assert.Equal(0, single.Frame.MinY);
        Assert.Equal(30, single.Frame.Width);
        Assert.Equal(300, single.Frame.Height);
    }

    /// <summary>完全在屏幕之外的窗口被剔除（Mac: CGRect.intersection 返回 null）。</summary>
    [Fact]
    public void 完全在屏幕外的窗口被剔除()
    {
        var windows = new[]
        {
            WindowFixture.Window(WindowFixture.Handle(1), new RectD(5000, 5000, 400, 300)),
        };

        Assert.Empty(Targets(windows));
    }

    /// <summary>负原点副屏（参考文档 §14 风险 #16 提到的场景）。</summary>
    [Fact]
    public void 负原点的副屏也能正确求交()
    {
        var screen = new RectD(-1920, 0, 1920, 1080);
        var windows = new[]
        {
            // 完全落在副屏内。
            WindowFixture.Window(WindowFixture.Handle(1), new RectD(-1500, 100, 800, 600)),
            // 跨主副屏边界，只有左半在副屏上，相交后 820 宽 → 通过。
            WindowFixture.Window(WindowFixture.Handle(2), new RectD(-1000, 100, 1000, 600)),
            // 只在主屏上，与副屏无交集 → 剔除。
            WindowFixture.Window(WindowFixture.Handle(3), new RectD(100, 100, 800, 600)),
        };

        var targets = Targets(windows, screen: screen);

        Assert.Equal(2, targets.Count);
        Assert.All(targets, t => Assert.True(t.Frame.MinX >= -1920 && t.Frame.MaxX <= 0));
    }

    // ── ZOrder ─────────────────────────────────────────────────────

    /// <summary>Mac: zOrder = 枚举索引（CGWindowList 前→后）。EnumWindows 同样前→后。</summary>
    [Fact]
    public void zOrder取自枚举序次()
    {
        var windows = new[]
        {
            WindowFixture.Window(WindowFixture.Handle(1), new RectD(0, 0, 400, 300), zOrder: 7),
            WindowFixture.Window(WindowFixture.Handle(2), new RectD(0, 400, 400, 300), zOrder: 3),
        };

        var targets = Targets(windows);

        Assert.Equal(7, targets[0].ZOrder);
        Assert.Equal(3, targets[1].ZOrder);
        // 顺序保持枚举序，不按 zOrder 排序 —— 与 Mac 一致。
        Assert.Equal(WindowFixture.Handle(1), targets[0].WindowHandle);
    }

    // ── 命中规则 ───────────────────────────────────────────────────

    /// <summary>
    /// Mac: WindowSnapTargetSelector.target(at:from:)（WindowSnapService.swift:10-19）
    /// —— 取 zOrder 最小者。
    /// </summary>
    [Fact]
    public void 命中时取zOrder最小者()
    {
        var targets = new[]
        {
            new WindowSnapTarget(WindowFixture.Handle(1), new RectD(0, 0, 400, 300), ZOrder: 5),
            new WindowSnapTarget(WindowFixture.Handle(2), new RectD(0, 0, 400, 300), ZOrder: 2),
            new WindowSnapTarget(WindowFixture.Handle(3), new RectD(0, 0, 400, 300), ZOrder: 9),
        };

        var hit = WindowSnapTargetFilter.TargetAt(new PointD(200, 150), targets);

        Assert.Equal(WindowFixture.Handle(2), hit?.WindowHandle);
    }

    /// <summary>Mac: 并列 zOrder 时取**面积最小者**。</summary>
    [Fact]
    public void zOrder并列时取面积最小者()
    {
        var targets = new[]
        {
            new WindowSnapTarget(WindowFixture.Handle(1), new RectD(0, 0, 400, 300), ZOrder: 3),
            new WindowSnapTarget(WindowFixture.Handle(2), new RectD(0, 0, 100, 100), ZOrder: 3),
        };

        var hit = WindowSnapTargetFilter.TargetAt(new PointD(50, 50), targets);

        Assert.Equal(WindowFixture.Handle(2), hit?.WindowHandle);
    }

    /// <summary>点不在任何窗口上时返回 null。</summary>
    [Fact]
    public void 点不在任何窗口上时无命中()
    {
        var targets = new[]
        {
            new WindowSnapTarget(WindowFixture.Handle(1), new RectD(0, 0, 100, 100), ZOrder: 1),
        };

        Assert.Null(WindowSnapTargetFilter.TargetAt(new PointD(500, 500), targets));
    }

    // ── 组合场景 ───────────────────────────────────────────────────

    /// <summary>
    /// 综合场景：一份混合了各种「坏」窗口的假列表，只有该通过的通过。
    /// 这份用例等价于 Mac 版 WindowSnapAndSelectionTests 对整个 targets 链路的锁定。
    /// </summary>
    [Fact]
    public void 混合窗口列表只有合规窗口通过()
    {
        var windows = new[]
        {
            WindowFixture.Window(WindowFixture.Handle(1), new RectD(0, 0, 400, 300), zOrder: 0),          // 通过
            WindowFixture.Window(WindowFixture.Handle(2), new RectD(0, 0, 400, 300), isVisible: false, zOrder: 1),
            WindowFixture.Window(WindowFixture.Handle(3), new RectD(0, 0, 400, 300), isToolWindow: true, zOrder: 2),
            WindowFixture.Window(WindowFixture.Handle(4), new RectD(0, 0, 400, 300), isCloaked: true, zOrder: 3),
            WindowFixture.Window(WindowFixture.Handle(5), new RectD(0, 0, 400, 300), alpha: 0.001, zOrder: 4),
            WindowFixture.Window(WindowFixture.Handle(6), new RectD(0, 0, 23, 15), zOrder: 5),
            WindowFixture.Window(WindowFixture.Handle(7), new RectD(0, 0, 400, 300),
                processId: WindowFixture.OtherProcessId, zOrder: 6),
            WindowFixture.Window(WindowFixture.Handle(8), new RectD(0, 400, 500, 350), zOrder: 7),       // 通过
        };

        var targets = Targets(windows);

        Assert.Equal(2, targets.Count);
        Assert.Equal(WindowFixture.Handle(1), targets[0].WindowHandle);
        Assert.Equal(WindowFixture.Handle(8), targets[1].WindowHandle);
    }

    /// <summary>空列表与全被剔除的列表都返回空集合而非 null。</summary>
    [Fact]
    public void 空输入返回空集合()
    {
        Assert.Empty(Targets(Array.Empty<RawWindowObservation>()));
    }

    /// <summary>PassesFilters 单独可测（不含屏幕求交那一步）。</summary>
    [Fact]
    public void PassesFilters只做属性级判断()
    {
        var offscreenButValid = WindowFixture.Window(
            WindowFixture.Handle(1), new RectD(9000, 9000, 400, 300));

        // 完全越屏但属性合规：PassesFilters 应通过（越屏由 Targets 的求交步骤剔除）。
        Assert.True(WindowSnapTargetFilter.PassesFilters(offscreenButValid, WindowFixture.FrontmostProcessId));
        // 太小：属性级就不通过。
        Assert.False(WindowSnapTargetFilter.PassesFilters(
            WindowFixture.Window(WindowFixture.Handle(2), new RectD(0, 0, 10, 10)),
            WindowFixture.FrontmostProcessId));
    }
}
