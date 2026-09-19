namespace Ta.Pinning;

/// <summary>
/// 钉图的像素滤镜模式。对应 Mac 版 <c>PinnedImageView.FilteredMode</c>
/// （PinnedImageWindowController.swift:304-308）。
/// </summary>
public enum PinFilterMode
{
    /// <summary>不过滤。</summary>
    None = 0,

    /// <summary>灰度。Mac: CIPhotoEffectMono（:600）。</summary>
    Grayscale,

    /// <summary>反色。Mac: CIColorInvert（:602）。</summary>
    Inverted,
}

/// <summary>
/// 滤镜互斥切换。对应 Mac 的 :512-524 —— 灰度与反色**互斥**，
/// 开启其中一个必须把另一个的勾选清掉。
/// </summary>
public static class PinFilterModeMath
{
    /// <summary>
    /// 切换指定滤镜：已选中则关闭，否则选中并**排斥**另一项。
    /// </summary>
    public static PinFilterMode Toggle(PinFilterMode current, PinFilterMode requested) =>
        current == requested ? PinFilterMode.None : requested;

    /// <summary>两个滤镜菜单项是否同时可勾选 —— 永远为 false（互斥）。</summary>
    public static bool AreSimultaneouslySelectable(PinFilterMode a, PinFilterMode b) => a == PinFilterMode.None || b == PinFilterMode.None;
}

/// <summary>
/// 钉图的几何变换与缩放夹取。
///
/// 全部对应 Mac 版 PinnedImageWindowController.swift：
///   · <see cref="ResizeForWheel"/>     → scrollWheel 的普通滚轮分支（:457-464）
///   · <see cref="NextAlpha"/>          → scrollWheel 的 ⌘+滚轮分支（:452-455）
///   · <see cref="ThumbnailSize"/>      → toggleThumbnail（:584）
///   · <see cref="ResizeForAspect"/>    → resizeWindowForCurrentAspect（:620-628）
///   · <see cref="Swap"/> / <see cref="IsSideways"/> → rotate 的侧向换轴（:488-500）
///
/// 纯算术，不碰 Win32。
/// </summary>
public static class PinTransform
{
    // ── 滚轮缩放夹取（Mac: min(1200, max(120, …)) / min(900, max(60, …))，:459-460）
    public const double MinWidth = 120;
    public const double MaxWidth = 1200;
    public const double MinHeight = 60;
    public const double MaxHeight = 900;

    // ── 透明度（Mac: min(1, max(0.2, alpha + delta * 0.015))，:453）
    public const double MinAlpha = 0.2;
    public const double MaxAlpha = 1;
    public const double AlphaStep = 0.015;

    // ── 滚轮步进（Mac: delta >= 0 ? 1.06 : 0.94，:457）
    public const double WheelGrow = 1.06;
    public const double WheelShrink = 0.94;

    // ── 缩略图（Mac: 160 × min(220, max(72, 160 * aspect))，:584）
    public const double ThumbnailWidth = 160;
    public const double ThumbnailMaxHeight = 220;
    public const double ThumbnailMinHeight = 72;

    /// <summary>旋转到侧向（宽高互换）时判定。Mac: <c>!quarterTurns.isMultiple(of: 2)</c>（:370）。</summary>
    public static bool IsSideways(int quarterTurns) => Normalize(quarterTurns) % 2 != 0;

    /// <summary>把任意旋转圈数归一到 0–3。Mac: <c>(quarterTurns + delta + 4) % 4</c>（:490）。</summary>
    public static int Normalize(int quarterTurns) => ((quarterTurns % 4) + 4) % 4;

    /// <summary>宽高互换。Mac: <c>CGSize(width: frame.height, height: frame.width)</c>（:495）。</summary>
    public static PinSizeD Swap(PinSizeD size) => new(size.Height, size.Width);

    /// <summary>
    /// 普通滚轮缩放：宽夹 [120,1200]，高按新宽/旧宽比例换算后再夹 [60,900]。
    /// Mac: :457-460。
    /// </summary>
    public static PinSizeD ResizeForWheel(PinSizeD current, double scrollDeltaY)
    {
        var factor = scrollDeltaY >= 0 ? WheelGrow : WheelShrink;
        var newWidth = Math.Clamp(current.Width * factor, MinWidth, MaxWidth);
        var newHeight = Math.Clamp(current.Height * (newWidth / current.Width), MinHeight, MaxHeight);
        return new PinSizeD(newWidth, newHeight);
    }

    /// <summary>
    /// ⌘+滚轮调透明度。Mac: <c>alpha + scrollingDeltaY * 0.015</c> 夹到 [0.2, 1]（:453）。
    /// </summary>
    public static double NextAlpha(double current, double scrollDeltaY) =>
        Math.Clamp(current + (scrollDeltaY * AlphaStep), MinAlpha, MaxAlpha);

    /// <summary>
    /// 绕窗口中心缩放时的新原点：中心保持不变。
    /// Mac: <c>frame.origin.x -= (newWidth - frame.width) / 2</c>（:461-462）。
    /// Windows 同为左上原点、Y 向下，公式一致。
    /// </summary>
    public static PinPointD CenterPreservingOrigin(PinPointD origin, PinSizeD oldSize, PinSizeD newSize) =>
        new(
            origin.X - ((newSize.Width - oldSize.Width) / 2),
            origin.Y - ((newSize.Height - oldSize.Height) / 2));

    /// <summary>
    /// 缩略图尺寸。Mac: <c>160 × min(220, max(72, 160 * aspect))</c>（:584）。
    /// </summary>
    public static PinSizeD ThumbnailSize(int imageWidth, int imageHeight)
    {
        var aspect = (double)imageHeight / imageWidth;
        return new PinSizeD(
            ThumbnailWidth,
            Math.Min(ThumbnailMaxHeight, Math.Max(ThumbnailMinHeight, ThumbnailWidth * aspect)));
    }

    /// <summary>
    /// 裁剪后按新宽高比重排窗口。Mac: <c>frame.size.height = min(900, max(60, frame.width * aspect))</c>（:625）。
    /// 宽度不变，只按宽高比重算高度并夹取。
    /// </summary>
    public static PinSizeD ResizeForAspect(PinSizeD current, int imageWidth, int imageHeight)
    {
        var aspect = (double)imageHeight / imageWidth;
        return new PinSizeD(
            current.Width,
            Math.Clamp(current.Width * aspect, MinHeight, MaxHeight));
    }
}
