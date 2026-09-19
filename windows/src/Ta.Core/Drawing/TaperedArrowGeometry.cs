namespace Ta.Core.Drawing;

/// <summary>双精度二维点。</summary>
public readonly record struct PointD(double X, double Y)
{
    public static double DistanceTo(PointD a, PointD b) => Math.Sqrt(DistanceSquared(a, b));

    public static double DistanceSquared(PointD a, PointD b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return (dx * dx) + (dy * dy);
    }
}

/// <summary>
/// 锥形箭头几何。
///
/// 逐行对应 Mac 版 AnnotationEditorWindowController.swift:338-369 的
/// TaperedArrowGeometry.polygon(from:to:width:)。
///
/// 返回 7 个点，构成一个填充多边形（从不描边）：
///   尾部左 → 颈部左 → 头部左 → 尖端 → 头部右 → 颈部右 → 尾部右
/// 其中 points[3] 恒等于 end。
///
/// 该函数是纯算术、无平台依赖，被 AnnotationDrawingTests 锁定，
/// 因此必须逐字复现公式 —— 系数改动会直接改变输出图像。
/// </summary>
public static class TaperedArrowGeometry
{
    public const int PointCount = 7;

    /// <summary>长度小于该值时退化为 7 个重合点。对应 Mac: guard length > 0.5。</summary>
    public const double MinimumLength = 0.5;

    public static PointD[] Polygon(PointD start, PointD end, double width)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));

        if (length <= MinimumLength)
        {
            return new[] { start, start, start, start, start, start, start };
        }

        var direction = new PointD(dx / length, dy / length);
        var normal = new PointD(-direction.Y, direction.X);

        var baseWidth = Math.Max(2, width);
        var tailHalfWidth = Math.Max(1, baseWidth * 0.28);
        var neckHalfWidth = Math.Max(tailHalfWidth + 0.8, baseWidth * 0.72);
        var headHalfWidth = Math.Max(7, baseWidth * 2.2);
        var headLength = Math.Min(Math.Max(12, baseWidth * 4), length * 0.55);
        var neck = new PointD(
            end.X - (direction.X * headLength),
            end.Y - (direction.Y * headLength));

        static PointD Offset(PointD point, PointD n, double amount) =>
            new(point.X + (n.X * amount), point.Y + (n.Y * amount));

        return
        [
            Offset(start, normal, -tailHalfWidth),
            Offset(neck, normal, -neckHalfWidth),
            Offset(neck, normal, -headHalfWidth),
            end,
            Offset(neck, normal, headHalfWidth),
            Offset(neck, normal, neckHalfWidth),
            Offset(start, normal, tailHalfWidth),
        ];
    }
}
