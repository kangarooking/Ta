using Ta.Core.Drawing;
using Xunit;

namespace Ta.Core.Tests.Drawing;

/// <summary>
/// 锥形箭头几何测试。
///
/// 逐条对应 Mac 版 AnnotationDrawingTests.swift:6-29 的断言。
/// 该函数是纯算术且有像素级测试锁定，是移植正确性的基准之一。
/// </summary>
public class TaperedArrowGeometryTests
{
    [Fact]
    public void 返回7个点()
    {
        var points = TaperedArrowGeometry.Polygon(new(0, 0), new(100, 0), 6);

        Assert.Equal(TaperedArrowGeometry.PointCount, points.Length);
    }

    [Fact]
    public void 尖端恒等于终点()
    {
        var end = new PointD(120, 45);
        var points = TaperedArrowGeometry.Polygon(new(10, 10), end, 6);

        Assert.Equal(end, points[3]);
    }

    [Fact]
    public void 箭头头部宽度足够()
    {
        var points = TaperedArrowGeometry.Polygon(new(0, 0), new(100, 0), 6);

        // points[2] 与 points[4] 是头部左右两点。横向箭头下偏移沿法向（垂直）展开，
        // 因此量的是两点间距离，而非某一轴上的差值。
        var headWidth = PointD.DistanceTo(points[2], points[4]);

        Assert.True(headWidth > 20, $"头部宽度 {headWidth} 应大于 20");
    }

    [Fact]
    public void 极短箭头退化为重合点()
    {
        var points = TaperedArrowGeometry.Polygon(new(0, 0), new(0.2, 0), 6);

        Assert.All(points, p => Assert.Equal(points[0], p));
    }

    [Fact]
    public void 短箭头仍保持头部形态()
    {
        // 箭头短于 headLength 时，头部两点必须仍位于起点一侧而非越过终点。
        var points = TaperedArrowGeometry.Polygon(new(0, 0), new(15, 0), 6);

        Assert.True(points[2].X < points[3].X, "头部左点应位于尖端左侧");
    }

    [Fact]
    public void 线宽下限为2像素()
    {
        var thin = TaperedArrowGeometry.Polygon(new(0, 0), new(100, 0), 0.1);
        var atMin = TaperedArrowGeometry.Polygon(new(0, 0), new(100, 0), 2);

        // baseWidth = max(2, width)，因此过细的线宽与下限值结果一致。
        Assert.Equal(atMin, thin);
    }
}

/// <summary>
/// 选区缩放与文字度量测试。
/// 对应 Mac 版 AnnotationDrawingTests 中的角锚定等比缩放与字号阶梯断言。
/// </summary>
public class AnnotationGeometryTests
{
    [Fact]
    public void 等比缩放在角锚定下保持宽高比()
    {
        // 锚点在左上，原手柄在右下 (100,100)；拖到 (200,200) 应得 2 倍。
        var scale = AnnotationSelectionGeometry.UniformScale(
            new PointD(0, 0), new PointD(100, 100), new PointD(200, 200));

        Assert.Equal(2.0, scale, 6);
    }

    [Fact]
    public void 缩放系数被夹在最小与最大之间()
    {
        var tooSmall = AnnotationSelectionGeometry.UniformScale(
            new PointD(0, 0), new PointD(100, 100), new PointD(0, 0));
        var tooLarge = AnnotationSelectionGeometry.UniformScale(
            new PointD(0, 0), new PointD(1, 1), new PointD(10000, 10000));

        Assert.Equal(AnnotationSelectionGeometry.MinimumScale, tooSmall);
        Assert.Equal(AnnotationSelectionGeometry.MaximumScale, tooLarge);
    }

    [Fact]
    public void 锚点与原手柄重合时返回1()
    {
        var scale = AnnotationSelectionGeometry.UniformScale(
            new PointD(5, 5), new PointD(5, 5), new PointD(50, 50));

        Assert.Equal(1.0, scale, 6);
    }

    [Fact]
    public void 字号阶梯只能落在预设档位上()
    {
        // 注意：先 clamp 再取档，因此低于最小档的输入会先被抬到 12，再向上走到 14。
        // 这与 Mac 版 AnnotationTextMetrics.stepped 的行为一致。
        Assert.Equal(14, AnnotationTextMetrics.Stepped(10, 1));
        Assert.Equal(14, AnnotationTextMetrics.Stepped(12, 1));
        Assert.Equal(12, AnnotationTextMetrics.Stepped(14, -1));
        Assert.Equal(28, AnnotationTextMetrics.Stepped(24, 1));
    }

    [Fact]
    public void 低于最小档的字号被抬到最小档再向上()
    {
        // clamp(10) = 12，方向上再取一档 → 14。
        Assert.Equal(14, AnnotationTextMetrics.Stepped(10, 1));
        // 恰好位于最小档时，向下无档可走，停在 12。
        Assert.Equal(12, AnnotationTextMetrics.Stepped(12, -1));
    }

    [Fact]
    public void 字号到顶到底时停在端点()
    {
        Assert.Equal(72, AnnotationTextMetrics.Stepped(72, 1));
        Assert.Equal(12, AnnotationTextMetrics.Stepped(12, -1));
    }

    [Fact]
    public void 方向为零时返回当前值()
    {
        Assert.Equal(24, AnnotationTextMetrics.Stepped(24, 0));
    }

    [Fact]
    public void 笔画平滑按间距插值且间距有下限()
    {
        var points = AnnotationStrokeSmoother.Points(new(0, 0), new(10, 0), 2.5);

        Assert.Equal(4, points.Length);
        Assert.Equal(new PointD(2.5, 0), points[0]);
        Assert.Equal(new PointD(10, 0), points[^1]);
    }

    [Fact]
    public void 零距离不产生任何点()
    {
        var points = AnnotationStrokeSmoother.Points(new(3, 3), new(3, 3), 2);

        Assert.Empty(points);
    }

    [Fact]
    public void 间距过小时仍至少产生一个点()
    {
        var points = AnnotationStrokeSmoother.Points(new(0, 0), new(0.1, 0), 0.001);

        Assert.Single(points);
    }
}
