using Ta.Core.Capture;

namespace Ta.Pinning;

/// <summary>逻辑尺寸（pt）。对应 Mac 版的 CGSize。</summary>
public readonly record struct PinSizeD(double Width, double Height);

/// <summary>逻辑坐标点（pt）。对应 Mac 版的 CGPoint。</summary>
public readonly record struct PinPointD(double X, double Y);

/// <summary>
/// 钉图的初始尺寸与原点夹取。
///
/// 逐行对应 Mac 版 <c>Sources/AIScreenshotApp/UI/PinnedImageWindowController.swift</c>：
///   · <see cref="InitialSize"/> → <c>PinnedImageLayout.initialSize</c>（:5-27）
///   · <see cref="ClampOrigin"/>  → <c>createPin</c> 里的原点夹取（:147-150）
///   · <see cref="ConstrainFrame"/> → <c>PinnedImagePanel.constrainFrameRect</c>（:249-253）
///
/// 纯算术，不碰 Win32，可被单测完整锁定。
/// </summary>
public static class PinnedImageLayout
{
    /// <summary>Mac: <c>visibleFrame.width - 24</c>（:19-21）</summary>
    public const double ScreenInset = 24;

    /// <summary>Mac: <c>visibleFrame.minX + 12</c>（:148-149）</summary>
    public const double EdgeInset = 12;

    /// <summary>Mac: <c>min(640, screenLimit.width)</c>（:23）—— 仅剪贴板钉图走这支。</summary>
    public const double PasteboardMaxWidth = 640;

    /// <summary>Mac: <c>min(480, screenLimit.height)</c>（:23）</summary>
    public const double PasteboardMaxHeight = 480;

    /// <summary>
    /// 计算钉图的初始逻辑尺寸。
    /// </summary>
    /// <param name="imagePixels">源图像像素尺寸。</param>
    /// <param name="preferredLogicalSize">首选逻辑尺寸；截图钉图传 selection 的尺寸，剪贴板钉图传 null。</param>
    /// <param name="visibleFrame">可见屏范围（Windows 上取显示器工作区，即排除任务栏的矩形）。</param>
    public static PinSizeD InitialSize(
        (int Width, int Height) imagePixels,
        PinSizeD? preferredLogicalSize,
        RectD visibleFrame)
    {
        // Mac: preferred.flatMap { guard size.width >= 1, size.height >= 1 ... }（:10-13）
        var hasPreferred = preferredLogicalSize.HasValue
            && preferredLogicalSize.Value.Width >= 1
            && preferredLogicalSize.Value.Height >= 1;

        var baseSize = hasPreferred
            ? preferredLogicalSize!.Value
            : new PinSizeD(Math.Max(1, imagePixels.Width), Math.Max(1, imagePixels.Height));

        var screenLimit = new PinSizeD(
            Math.Max(1, visibleFrame.Width - ScreenInset),
            Math.Max(1, visibleFrame.Height - ScreenInset));

        var limit = hasPreferred
            ? screenLimit
            : new PinSizeD(
                Math.Min(PasteboardMaxWidth, screenLimit.Width),
                Math.Min(PasteboardMaxHeight, screenLimit.Height));

        // scale 上限 1 —— 小图绝不放大（Mac 测试 testSmallScreenshotPinIsNotArtificiallyEnlarged）。
        var scale = Math.Min(1, Math.Min(limit.Width / baseSize.Width, limit.Height / baseSize.Height));

        return new PinSizeD(baseSize.Width * scale, baseSize.Height * scale);
    }

    /// <summary>
    /// 把钉图原点夹到可见屏范围内。
    /// Mac: <c>min(max(visibleFrame.minX + 12, selection.globalRect.minX), visibleFrame.maxX - width - 12)</c>（:148-149）
    /// </summary>
    public static PinPointD ClampOrigin(PinPointD anchor, PinSizeD size, RectD visibleFrame) =>
        new(
            Math.Min(
                Math.Max(visibleFrame.MinX + EdgeInset, anchor.X),
                visibleFrame.MaxX - size.Width - EdgeInset),
            Math.Min(
                Math.Max(visibleFrame.MinY + EdgeInset, anchor.Y),
                visibleFrame.MaxY - size.Height - EdgeInset));

    /// <summary>
    /// 窗口框架约束 —— **原样返回**。
    ///
    /// 对应 Mac <c>PinnedImagePanel.constrainFrameRect</c>（:249-253）：
    /// NSWindow 默认会把无边框窗口夹到菜单栏下方，而钉图是自由画布对象，
    /// 必须保留用户拖拽出的精确位置（允许伸到任务栏下方）。
    ///
    /// Windows 的 <c>SetWindowPos</c> 本身不做任何夹取，因此这里只是把这个契约
    /// 显式化并交给单测锁定，防止将来有人「顺手」加上夹取逻辑。
    /// </summary>
    public static RectD ConstrainFrame(RectD frame) => frame;

    /// <summary>
    /// 一次性算出初始尺寸与原点，对应 Mac <c>createPin</c> 的 :140-150。
    /// </summary>
    public static (PinSizeD Size, PinPointD Origin) InitialFrame(
        (int Width, int Height) imagePixels,
        PinSizeD? preferredLogicalSize,
        RectD visibleFrame,
        PinPointD anchor)
    {
        var size = InitialSize(imagePixels, preferredLogicalSize, visibleFrame);
        return (size, ClampOrigin(anchor, size, visibleFrame));
    }
}

/// <summary>
/// 双击关闭判定。对应 Mac 版 <c>PinnedImageInteraction.shouldClose</c>（:256-260）。
/// 纯函数，单独成类以便与 Win32 双击检测解耦。
/// </summary>
public static class PinnedImageInteraction
{
    /// <summary>Mac: <c>clickCount &gt;= 2</c>。</summary>
    public static bool ShouldClose(int clickCount) => clickCount >= 2;
}
