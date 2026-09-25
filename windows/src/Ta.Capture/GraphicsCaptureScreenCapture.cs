using Ta.Core.Capture;
using Ta.Core.Imaging;
using Ta.Capture.Win32;
using Ta.Capture.WinRt;

namespace Ta.Capture;

/// <summary>
/// 基于 Windows.Graphics.Capture 的 <see cref="IScreenCapture"/> 实现。
///
/// 逐条对照 Mac 版：
/// - <see cref="CaptureDisplay"/> ← <c>ScreenCaptureService.captureDisplay</c>（:98-131）
/// - <see cref="CropFrozen"/>    ← <c>FrozenDisplayCropper.crop</c>（:53-66）
/// - <see cref="CaptureWindow"/> ← <c>ScreenCaptureService.captureWindow</c>（:133-158）
/// - <see cref="CaptureFrontmost"/> ← <c>TaAgentCaptureService.resolve(.frontmost)</c>（:161-168）
/// - <see cref="Windows"/>       ← <c>WindowSnapService.targets</c> + <c>TaAgentCaptureService</c> 的 windows 映射
///
/// 全局行为契约（参考文档 §3.1）：<see cref="CaptureDisplay"/> 返回**整屏冻结帧**，
/// 调用方从这一帧上框选，再用 <see cref="CropFrozen"/> 裁出，**绝不二次实时截图**。
/// </summary>
public sealed class GraphicsCaptureScreenCapture : IScreenCapture, IDisposable
{
    private readonly GraphicsCaptureGrabber _grabber = new();
    private bool _disposed;

    // ── 枚举 ────────────────────────────────────────────────────────

    public IReadOnlyList<DisplayInfo> Displays =>
        DisplaySnapshot.ToDisplayInfos(DisplayEnumerator.Enumerate());

    /// <summary>
    /// 可见窗口列表（已按 WindowSnapService.targets 的条件过滤）。
    ///
    /// ⚠️ 快照语义：每次取属性都重新枚举，与 Mac 版
    /// <c>TaAgentCaptureService.snapshot()</c> 一次性取全量快照的精神一致
    /// —— 前台进程在枚举**之前**冻结，避免枚举过程中前台切换导致过滤条件漂移。
    /// </summary>
    public IReadOnlyList<WindowInfo> Windows
    {
        get
        {
            var displays = Displays;
            var windows = WindowEnumerator.Enumerate();
            var frontmost = WindowEnumerator.FrontmostProcessId();
            return WindowInfoBuilder.Build(displays, windows, frontmost);
        }
    }

    // ── 捕获 ────────────────────────────────────────────────────────

    /// <summary>
    /// 捕获整块显示器，得到**冻结帧**。对应 Mac 版 captureDisplay(displayID:sourceRect:nil)
    /// （ScreenCaptureService.swift:98-131）。
    ///
    /// 像素尺寸精确等于 <c>round(frame.Size × pixelScale)</c>，对应 Mac 的
    /// <c>max(1, Int((size.width * pixelScale).rounded()))</c>（:169-170）与
    /// <c>scalesToFit = false</c>（:171）。
    /// </summary>
    public RgbaBitmap CaptureDisplay(int displayId, double pixelScale)
    {
        ThrowIfDisposed();

        var display = DisplaySnapshot.Find(Displays, displayId)
            ?? throw new CaptureException(CaptureFailure.DisplayUnavailable, $"找不到显示器 id={displayId}。");

        var width = PixelSize(display.Frame.Width, pixelScale);
        var height = PixelSize(display.Frame.Height, pixelScale);

        try
        {
            return _grabber.Grab(new CaptureTarget(MonitorHandle(display), IsWindow: false), width, height);
        }
        catch (Exception ex)
        {
            throw MapDisplayFailure(ex);
        }
    }

    /// <summary>
    /// 从已冻结的整屏图上裁出区域。
    ///
    /// 逐行对应 Mac 版 <c>FrozenDisplayCropper.crop</c>（ScreenCaptureService.swift:53-66），
    /// 换算数学直接复用 Ta.Core 的 <see cref="FrozenDisplayCropper"/>（已就绪，
    /// 且含 Mac 版锁定测试向量的等价用例）。
    /// </summary>
    public RgbaBitmap CropFrozen(RgbaBitmap frozenDisplay, CaptureSelection selection)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(frozenDisplay);

        if (!FrozenDisplayCropper.TryPixelRect(selection, frozenDisplay.Width, frozenDisplay.Height, out var rect))
        {
            throw new CaptureException(CaptureFailure.InvalidSelection, "选区与显示器无有效交集。");
        }

        try
        {
            return frozenDisplay.Crop(rect.Left, rect.Top, rect.Width, rect.Height);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new CaptureException(CaptureFailure.InvalidSelection, ex.Message);
        }
    }

    /// <summary>
    /// 捕获单个窗口。对应 Mac 版 <c>captureWindow(windowID:pixelScale:showsCursor:)</c>
    /// （ScreenCaptureService.swift:133-158）。
    ///
    /// 与 Mac 的两处差异（均已在参考文档 §13 标注）：
    /// <list type="number">
    ///   <item><description><c>ignoreWindowShadowsSingleWindow</c> 无 Windows 对应物。
    ///   我们改用 DWMWA_EXTENDED_FRAME_BOUNDS（本身不含阴影）+ 关掉 WGC 的黄边框，
    ///   效果等价于「只含窗口本身」。</description></item>
    ///   <item><description>额外设置 <c>IncludeSecondaryWindows = false</c>，确保只捕获这一个窗口，
    ///   与 Mac 的 <c>SCContentFilter(desktopIndependentWindow:)</c> 语义一致。</description></item>
    /// </list>
    /// </summary>
    public RgbaBitmap CaptureWindow(IntPtr windowHandle, double pixelScale)
    {
        ThrowIfDisposed();

        if (windowHandle == IntPtr.Zero)
        {
            throw new CaptureException(CaptureFailure.WindowUnavailable, "窗口句柄为空。");
        }

        var frame = WindowEnumerator.TryGetWindowFrame(windowHandle)
            ?? throw new CaptureException(CaptureFailure.WindowUnavailable, "取不到窗口边界，窗口可能已销毁。");

        // Mac: max(1, pixelScale)（ScreenCaptureService.swift:149）。
        var scale = Math.Max(1, pixelScale);
        var width = PixelSize(frame.Width, scale);
        var height = PixelSize(frame.Height, scale);

        try
        {
            return _grabber.Grab(new CaptureTarget(windowHandle, IsWindow: true), width, height);
        }
        catch (Exception ex)
        {
            throw MapWindowFailure(ex);
        }
    }

    /// <summary>
    /// 捕获前台应用的主窗口。
    ///
    /// 逐行对应 Mac 版 <c>TaAgentCaptureService.resolve(.frontmost)</c>（:161-168）：
    /// <code>
    /// snapshot.windows.filter({ $0.ownerProcessID == processID }).min(by: { $0.zOrder &lt; $1.zOrder })
    /// </code>
    /// 缩放系数按 windowResolution（:233-236）取「与窗口相交的所有屏里最大的 pixelScale」。
    /// </summary>
    public RgbaBitmap CaptureFrontmost(double pixelScale)
    {
        ThrowIfDisposed();

        var frontmostProcessId = WindowEnumerator.FrontmostProcessId();
        if (frontmostProcessId is null)
        {
            throw new CaptureException(CaptureFailure.WindowUnavailable, "拿不到前台进程。");
        }

        var window = Windows
            .Where(w => w.ProcessId == frontmostProcessId.Value)
            .MinBy(w => w.ZOrder)
            ?? throw new CaptureException(CaptureFailure.WindowUnavailable, "前台应用没有可见窗口。");

        var displays = Displays;
        var scale = DisplaySnapshot.PixelScaleForRect(displays, window.Frame);

        // Mac 的 windowResolution 会用算出来的 scale 覆盖调用方传入的 pixelScale，
        // 这里保持同样行为（TaAgentCaptureService.swift:246）。
        return CaptureWindow(window.WindowHandle, Math.Max(1, scale));
    }

    // ── 权限 ────────────────────────────────────────────────────────

    /// <summary>
    /// Windows 没有 TCC 式的「屏幕录制」权限门（参考文档 §13 对照表最后一段、
    /// §14 风险 #29）。但 WGC 本身有运行前提（Win10 1903+、D3D11 可用），
    /// 所以用 <c>GraphicsCaptureSession.IsSupported</c> 表达「本环境能不能捕获」。
    /// </summary>
    public bool HasPermission => GraphicsCaptureGrabber.IsSupported();

    /// <summary>
    /// 请求权限。Windows 无等价物（无法程序化授予），按接口约定返回当前状态。
    /// </summary>
    public bool RequestPermission() => HasPermission;

    // ── 内部 ────────────────────────────────────────────────────────

    /// <summary>
    /// 由 <see cref="DisplayInfo"/> 反查 HMONITOR。
    /// <para>
    /// DisplayInfo 不保存句柄（它的 Id 是快照分配的合成索引，见参考文档 §14 风险 #39），
    /// 所以要按边界重新匹配。多块显示器边界完全相同（克隆模式）时取第一个，
    /// 这种情况下捕获内容本来就一致。
    /// </para>
    /// </summary>
    private static IntPtr MonitorHandle(DisplayInfo display)
    {
        foreach (var record in DisplayEnumerator.Enumerate())
        {
            if (record.Bounds == display.Frame)
            {
                return record.Handle;
            }
        }

        throw new CaptureException(CaptureFailure.DisplayUnavailable, "显示器已变更，请重新枚举。");
    }

    /// <summary>像素尺寸：max(1, round(size × scale))。对应 Mac: max(1, Int((size * scale).rounded()))。</summary>
    private static int PixelSize(double logicalSize, double pixelScale) =>
        Math.Max(1, (int)Math.Round(logicalSize * pixelScale));

    /// <summary>
    /// 把底层异常映射为 <see cref="CaptureException"/>。
    ///
    /// 对应 Mac 版 <c>TaAgentCaptureService.capture(prepared)</c> 的错误归一
    /// （TaAgentCaptureService.swift:132-148）：
    /// <list type="bullet">
    ///   <item><description>E_ACCESSDENIED (0x80070005) → <see cref="CaptureFailure.PermissionDenied"/>
    ///   —— 目标属于提权进程 / 正在安全桌面（UAC 提示、锁屏）。参考文档 §14 风险 #29。</description></item>
    ///   <item><description>其余 → <see cref="CaptureFailure.DisplayUnavailable"/></description></item>
    /// </list>
    /// </summary>
    private static CaptureException MapDisplayFailure(Exception ex) =>
        new(IsAccessDenied(ex) ? CaptureFailure.PermissionDenied : CaptureFailure.DisplayUnavailable,
            ex.Message);

    private static CaptureException MapWindowFailure(Exception ex) =>
        new(IsAccessDenied(ex) ? CaptureFailure.PermissionDenied : CaptureFailure.WindowUnavailable,
            ex.Message);

    private static bool IsAccessDenied(Exception ex) => ex.HResult == unchecked((int)0x8007_0005);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _grabber.Dispose();
    }
}
