namespace Ta.Core.Capture;

/// <summary>
/// 捕获选区。对应 Mac 版 SelectionOverlayController.swift:5-31 的 CaptureSelection。
///
/// ⚠️ 坐标系差异（详见 Windows移植参考文档 §5.1）：
/// Mac 版在这里同时携带三个空间的信息 —— 全局 AppKit 屏空间（Y 向上）、
/// 覆盖层视图局部空间、以及像素空间（Y 向下），并通过多处显式翻转互相换算。
///
/// Windows 屏幕坐标统一为左上原点、Y 向下，与像素空间同向，
/// 因此 Mac 版那几处翻转换算在 Windows 上会**消失**。
/// 本类型保留字段以便对照，但不做翻转 —— 翻转逻辑集中于
/// <see cref="FrozenDisplayCropper"/>，便于单点审计。
/// </summary>
public readonly record struct CaptureSelection
{
    /// <summary>全局屏坐标下的选区矩形。</summary>
    public RectD GlobalRect { get; init; }

    /// <summary>目标显示器在全局屏坐标下的边界。</summary>
    public RectD ScreenFrame { get; init; }

    /// <summary>显示器 DPI 缩放（1 = 100%）。对应 Mac: NSScreen.backingScaleFactor。</summary>
    public double BackingScaleFactor { get; init; }

    // 结构体含字段初始值，故必须显式声明无参构造函数。
    public CaptureSelection() => BackingScaleFactor = 1;

    /// <summary>
    /// 冻结的整屏图像，或 null 表示需要实时捕获。
    /// 对应 Mac 版的关键设计：先冻结整屏再框选（CaptureCoordinator.swift:169）。
    /// </summary>
    public int? FrozenDisplayImageWidth { get; init; }
    public int? FrozenDisplayImageHeight { get; init; }
}

/// <summary>双精度矩形。</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double MinX => X;
    public double MinY => Y;
    public double MaxX => X + Width;
    public double MaxY => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static RectD FromLTRB(double left, double top, double right, double bottom) =>
        new(left, top, right - left, bottom - top);
}

/// <summary>整数矩形，单位：像素。</summary>
public readonly record struct RectI(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;

    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>裁剪失败原因。</summary>
public enum CropError
{
    /// <summary>选区与显示器无有效交集。</summary>
    InvalidSelection,

    /// <summary>选区尺寸不足。</summary>
    SelectionTooSmall,
}

/// <summary>
/// 从冻结的整屏图像中裁出选区。
///
/// 逐行对应 Mac 版 ScreenCaptureService.swift:22-67 的 FrozenDisplayCropper。
///
/// 关键实现要点：使用**比例法**换算（imageWidth / screenFrame.width），
/// 而非直接乘 backingScaleFactor。这样在混合 DPI 多屏下依然正确，
/// 是 Mac 版刻意的设计，必须保留。
///
/// Windows 与 Mac 的差别仅在于 Mac 版需要
/// localTop = screenFrame.maxY - clipped.maxY 这一次 Y 翻转，
/// Windows 上该翻转消失（两边都 Y 向下）。
/// </summary>
public static class FrozenDisplayCropper
{
    /// <summary>裁剪结果必须至少 1 像素。对应 Mac: guard pixelRect.width >= 1。</summary>
    public const double MinimumPixels = 1;

    public static bool TryPixelRect(
        CaptureSelection selection,
        int imageWidth,
        int imageHeight,
        out RectI result)
    {
        var clipped = Intersect(selection.GlobalRect, selection.ScreenFrame);

        var invalid = clipped.IsEmpty
            || clipped.Width < MinimumPixels
            || clipped.Height < MinimumPixels
            || selection.ScreenFrame.Width <= 0
            || selection.ScreenFrame.Height <= 0
            || imageWidth <= 0
            || imageHeight <= 0;

        if (invalid)
        {
            result = default;
            return false;
        }

        // 比例法换算 —— 天然 DPI 正确，不要改成乘 scale。
        var scaleX = imageWidth / selection.ScreenFrame.Width;
        var scaleY = imageHeight / selection.ScreenFrame.Height;

        // Windows：无需 Y 翻转，两边同为左上原点、Y 向下。
        var localX = clipped.MinX - selection.ScreenFrame.MinX;
        var localY = clipped.MinY - selection.ScreenFrame.MinY;

        var left = (int)Math.Floor(localX * scaleX);
        var top = (int)Math.Floor(localY * scaleY);
        var right = (int)Math.Ceiling((localX + clipped.Width) * scaleX);
        var bottom = (int)Math.Ceiling((localY + clipped.Height) * scaleY);

        // 夹取到图像边界内。
        left = Math.Clamp(left, 0, imageWidth);
        top = Math.Clamp(top, 0, imageHeight);
        right = Math.Clamp(right, 0, imageWidth);
        bottom = Math.Clamp(bottom, 0, imageHeight);

        var pixels = new RectI(left, top, right, bottom);
        if (pixels.IsEmpty || pixels.Width < MinimumPixels || pixels.Height < MinimumPixels)
        {
            result = default;
            return false;
        }

        result = pixels;
        return true;
    }

    /// <summary>
    /// Mac 版用 CGRect.intersection，其语义是「返回 null 而非空矩形」。
    /// 这里以 IsEmpty 表达同一结果。
    /// </summary>
    private static RectD Intersect(RectD a, RectD b)
    {
        var left = Math.Max(a.MinX, b.MinX);
        var top = Math.Max(a.MinY, b.MinY);
        var right = Math.Min(a.MaxX, b.MaxX);
        var bottom = Math.Min(a.MaxY, b.MaxY);

        return right <= left || bottom <= top
            ? default
            : RectD.FromLTRB(left, top, right, bottom);
    }
}
