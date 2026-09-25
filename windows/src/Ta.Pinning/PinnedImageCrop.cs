using Ta.Core.Capture;

namespace Ta.Pinning;

/// <summary>
/// 裁剪选区 → 图像矩形的映射。
///
/// 对应 Mac 版 <c>PinnedImageView.mouseUp</c>（PinnedImageWindowController.swift:428-448）。
///
/// ⚠️ **Y 翻转的处置**（与参考文档 §14 风险 #24 同一类问题）：
/// Mac 的公式带一次 Y 翻转 ——
/// <c>y = current.minY + (1 - selected.maxY / bounds.height) * current.height</c>（:441）。
/// 那次翻转的作用是把 **AppKit 视图坐标（原点在左下、Y 向上）** 换算到
/// **CGImage 行序（第 0 行在顶部）**。它不是在翻图像，而是在翻坐标系。
///
/// Windows 的视图坐标本身就是左上原点、Y 向下，与像素行序同向，
/// 因此**同样的映射不再需要翻转**（与 <c>FrozenDisplayCropper</c> 的处置一致）。
///
/// 为了把这个判断变成可执行的回归门，这里同时提供两个入口：
///   · <see cref="MapMacFormula"/> —— Mac 原式（输入按 Y 向上约定）
///   · <see cref="TryMapViewRectToImage"/> —— Windows 实现（输入按 Y 向下约定）
/// 单测断言两者对「同一物理选区」给出**完全相同**的图像矩形。
/// </summary>
public static class PinnedImageCrop
{
    /// <summary>Mac: <c>guard selected.width &gt;= 12, selected.height &gt;= 12</c>（:437）。</summary>
    public const double MinimumSelection = 12;

    /// <summary>Mac: <c>guard newCrop.width &gt;= 2, newCrop.height &gt;= 2</c>（:445）。</summary>
    public const double MinimumResult = 2;

    /// <summary>
    /// Mac 原式。输入 <paramref name="selected"/> 按 AppKit 视图坐标（Y 向上）理解。
    /// </summary>
    public static RectD MapMacFormula(RectD selected, RectD bounds, RectD current)
    {
        var x = current.MinX + (selected.MinX / bounds.Width * current.Width);
        var y = current.MinY + ((1 - (selected.MaxY / bounds.Height)) * current.Height);
        var width = selected.Width / bounds.Width * current.Width;
        var height = selected.Height / bounds.Height * current.Height;
        return new RectD(x, y, width, height);
    }

    /// <summary>
    /// 把视图选区映射到图像矩形（Windows：左上原点、Y 向下，无翻转）。
    /// </summary>
    /// <param name="selected">视图内的选区（已归一化、已与视图边界求交）。</param>
    /// <param name="bounds">视图尺寸（即钉图内容区尺寸）。</param>
    /// <param name="current">当前裁剪矩形；传 null 表示整幅源图。</param>
    /// <param name="sourceWidth">源图宽，用于最终夹取。</param>
    /// <param name="sourceHeight">源图高，用于最终夹取。</param>
    public static bool TryMapViewRectToImage(
        RectD selected,
        RectD bounds,
        RectI? current,
        int sourceWidth,
        int sourceHeight,
        out RectI result)
    {
        // Mac: guard selected.width >= 12, selected.height >= 12（:437）
        if (selected.Width < MinimumSelection || selected.Height < MinimumSelection
            || bounds.Width <= 0 || bounds.Height <= 0
            || sourceWidth <= 0 || sourceHeight <= 0)
        {
            result = default;
            return false;
        }

        // Mac: let current = cropRect ?? CGRect(x: 0, y: 0, width: sourceCGImage.width, height: sourceCGImage.height)（:438）
        var currentRect = current ?? new RectI(0, 0, sourceWidth, sourceHeight);

        // Mac: :439-443 —— X 分量两边一致；Y 分量在 Windows 上**去掉**那次翻转。
        //
        // 推导：Mac 的选区在 AppKit 视图坐标（Y 向上），selUp.maxY = bounds.height − selDown.minY，
        // 代入 (1 − selUp.maxY / h) 正好化简为 selDown.minY / h。
        // 所以 Windows 上的 Y 映射就是「按选区上边的比例」—— 与 CGImage 行序同向，无需翻转。
        var x = currentRect.Left + (selected.MinX / bounds.Width * currentRect.Width);
        var y = currentRect.Top + (selected.MinY / bounds.Height * currentRect.Height);
        var width = selected.Width / bounds.Width * currentRect.Width;
        var height = selected.Height / bounds.Height * currentRect.Height;

        // Mac: CGRect.integral —— 原点向下取整、远边向上取整（取最小包含矩形）。
        var left = (int)Math.Floor(x);
        var top = (int)Math.Floor(y);
        var right = (int)Math.Ceiling(x + width);
        var bottom = (int)Math.Ceiling(y + height);

        // Mac: .intersection(CGRect(x: 0, y: 0, width: sourceCGImage.width, height: sourceCGImage.height))（:444）
        left = Math.Clamp(left, 0, sourceWidth);
        top = Math.Clamp(top, 0, sourceHeight);
        right = Math.Clamp(right, 0, sourceWidth);
        bottom = Math.Clamp(bottom, 0, sourceHeight);

        // Mac: guard newCrop.width >= 2, newCrop.height >= 2（:445）
        if (right - left < MinimumResult || bottom - top < MinimumResult)
        {
            result = default;
            return false;
        }

        result = new RectI(left, top, right, bottom);
        return true;
    }

    /// <summary>
    /// 把拖拽的两点归一化成左上-宽高矩形。对应 Mac 的 <c>normalizedRect</c>（:630-632）。
    /// </summary>
    public static RectD Normalize(PinPointD first, PinPointD second) =>
        new(
            Math.Min(first.X, second.X),
            Math.Min(first.Y, second.Y),
            Math.Abs(first.X - second.X),
            Math.Abs(first.Y - second.Y));

    /// <summary>与视图边界求交。对应 Mac 的 <c>.intersection(bounds)</c>（:388, :430）。</summary>
    public static RectD IntersectWithBounds(RectD rect, RectD bounds)
    {
        var left = Math.Max(rect.MinX, bounds.MinX);
        var top = Math.Max(rect.MinY, bounds.MinY);
        var right = Math.Min(rect.MaxX, bounds.MaxX);
        var bottom = Math.Min(rect.MaxY, bounds.MaxY);
        return right <= left || bottom <= top ? default : new RectD(left, top, right - left, bottom - top);
    }
}
