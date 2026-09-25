namespace Ta.Capture.Win32;

/// <summary>双精度点。用于窗口吸附的命中测试。</summary>
public readonly record struct PointD(double X, double Y);

/// <summary>
/// 屏幕几何小工具。
///
/// 全部围绕 <see cref="Ta.Core.Capture.RectD"/>（左上原点、Y 向下）运算。
/// Mac 版在这几处要做 Y 翻转（WindowSnapService.swift:60），Windows 上两边同向，
/// 翻转逻辑按参考文档 §5.1 的结论**整体移除**。
/// </summary>
public static class ScreenGeometry
{
    /// <summary>
    /// 矩形求交。语义对应 Mac 的 <c>CGRect.intersection</c>：无重叠时返回**空矩形**
    /// （而非 null），与 Ta.Core 的 FrozenDisplayCropper.Intersect 保持一致。
    /// </summary>
    public static Ta.Core.Capture.RectD Intersect(Ta.Core.Capture.RectD a, Ta.Core.Capture.RectD b)
    {
        var left = Math.Max(a.MinX, b.MinX);
        var top = Math.Max(a.MinY, b.MinY);
        var right = Math.Min(a.MaxX, b.MaxX);
        var bottom = Math.Min(a.MaxY, b.MaxY);

        return right <= left || bottom <= top
            ? default
            : Ta.Core.Capture.RectD.FromLTRB(left, top, right, bottom);
    }

    /// <summary>矩形是否包含该点（左闭右开的上边界，与 CGRect.contains 的常规理解一致）。</summary>
    public static bool Contains(Ta.Core.Capture.RectD rect, PointD point) =>
        point.X >= rect.MinX && point.X < rect.MaxX
        && point.Y >= rect.MinY && point.Y < rect.MaxY;

    /// <summary>
    /// 向「外」取整到整数边界。对应 Mac 的 <c>CGRect.integral</c>。
    /// 向外的方向是 left/top 向下取整、right/bottom 向上取整，保证取整后不丢内容。
    /// </summary>
    public static Ta.Core.Capture.RectD Integral(Ta.Core.Capture.RectD rect)
    {
        if (rect.IsEmpty)
        {
            return default;
        }

        return Ta.Core.Capture.RectD.FromLTRB(
            Math.Floor(rect.MinX),
            Math.Floor(rect.MinY),
            Math.Ceiling(rect.MaxX),
            Math.Ceiling(rect.MaxY));
    }

    public static double Area(Ta.Core.Capture.RectD rect) => rect.IsEmpty ? 0 : rect.Width * rect.Height;
}
