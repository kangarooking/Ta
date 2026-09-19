namespace Ta.Core.Drawing;

/// <summary>
/// 选区几何：缩放锚点、文字度量、笔画平滑。
///
/// 逐行对应 Mac 版：
///   · AnnotationSelectionGeometry  (AnnotationEditorWindowController.swift:397-412)
///   · AnnotationTextMetrics        (同文件 :414-456)
///   · AnnotationStrokeSmoother     (同文件 :458-473)
/// 三者均为纯算术，有测试锁定。
/// </summary>
public static class AnnotationSelectionGeometry
{
    public const double MinimumScale = 0.1;
    public const double MaximumScale = 20;
    public const double DenominatorEpsilon = 0.001;

    /// <summary>
    /// 等比缩放系数。把拖拽中的手柄位置投影到「锚点 → 原手柄」方向上。
    /// 分母过小时返回 1（保持原尺寸），对应 Mac: guard denominator > 0.001 else { return 1 }。
    /// </summary>
    public static double UniformScale(
        PointD anchor,
        PointD originalHandle,
        PointD draggedHandle)
    {
        var original = new PointD(originalHandle.X - anchor.X, originalHandle.Y - anchor.Y);
        var dragged = new PointD(draggedHandle.X - anchor.X, draggedHandle.Y - anchor.Y);
        var denominator = (original.X * original.X) + (original.Y * original.Y);

        if (denominator <= DenominatorEpsilon)
        {
            return 1;
        }

        var projected = ((dragged.X * original.X) + (dragged.Y * original.Y)) / denominator;
        return Math.Clamp(projected, MinimumScale, MaximumScale);
    }
}

/// <summary>
/// 文字度量。字号是离散阶梯而非连续值 —— 这决定了工具栏字号菜单的档位，
/// 必须与 Mac 版完全一致。
/// </summary>
public static class AnnotationTextMetrics
{
    /// <summary>对应 Mac: AnnotationTextMetrics.availableSizes</summary>
    public static readonly double[] AvailableSizes = [12, 14, 16, 18, 20, 24, 28, 32, 40, 48, 64, 72];

    public const double DefaultSize = 24;

    public static double Clamped(double size) =>
        Math.Clamp(size, AvailableSizes[0], AvailableSizes[^1]);

    /// <summary>
    /// 沿字号阶梯上下移动一档。
    /// direction > 0 取下一个更大的，&lt; 0 取下一个更小的，到顶/到底则停在端点。
    /// </summary>
    public static double Stepped(double size, int direction)
    {
        var current = Clamped(size);

        if (direction > 0)
        {
            foreach (var candidate in AvailableSizes)
            {
                if (candidate > current)
                {
                    return candidate;
                }
            }

            return AvailableSizes[^1];
        }

        if (direction < 0)
        {
            for (var i = AvailableSizes.Length - 1; i >= 0; i--)
            {
                if (AvailableSizes[i] < current)
                {
                    return AvailableSizes[i];
                }
            }

            return AvailableSizes[0];
        }

        return current;
    }
}

/// <summary>
/// 笔画平滑：把一次鼠标位移插值为等距点列。
/// 对应 Mac: AnnotationStrokeSmoother.points(from:to:maximumSpacing:)
///
/// 注意 spacing 有下限 0.5，因此距离极短时也至少产生一个点。
/// </summary>
public static class AnnotationStrokeSmoother
{
    public const double MinimumSpacing = 0.5;

    public static PointD[] Points(PointD start, PointD end, double maximumSpacing)
    {
        var distance = PointD.DistanceTo(start, end);
        if (distance <= 0)
        {
            return [];
        }

        var spacing = Math.Max(MinimumSpacing, maximumSpacing);
        var steps = Math.Max(1, (int)Math.Ceiling(distance / spacing));

        var result = new PointD[steps];
        for (var step = 1; step <= steps; step++)
        {
            var progress = (double)step / steps;
            result[step - 1] = new PointD(
                start.X + ((end.X - start.X) * progress),
                start.Y + ((end.Y - start.Y) * progress));
        }

        return result;
    }
}
