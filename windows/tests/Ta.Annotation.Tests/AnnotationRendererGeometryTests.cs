using Ta.Core.Agent;
using Ta.Core.Drawing;
using Ta.Core.Imaging;

namespace Ta.Annotation.Tests;

/// <summary>
/// 绘制几何验证。
///
/// 关键一条是 <see cref="箭头渲染结果等于锥形几何的七点"/>：
/// 用**测试侧独立实现的射线法**判定点在多边形内，再与渲染结果比对，
/// 确保渲染器真的画的是 <see cref="TaperedArrowGeometry"/> 给出的那 7 个点。
/// </summary>
public sealed class AnnotationRendererGeometryTests
{
    /// <summary>像素盒最远延伸到中心外 sqrt(0.5²+0.5²)。</summary>
    private const double PixelBoxRadius = 0.7072;

    /// <summary>
    /// 允许漏填的边界容差。16 条子扫描线间距 1/16px，
    /// 尖端碎片薄于此值时覆盖度可能舍入回背景色。
    /// </summary>
    private const double SliverTolerance = 0.75;

    private static readonly TaAgentAnnotationRenderer Renderer = new();

    // ── 箭头 ────────────────────────────────────────────────────

    [Fact]
    public void 箭头渲染结果等于锥形几何的七点()
    {
        using var source = TestImages.Solid(200, 150, 128, 128, 128);

        var operation = AnnotationOperation.CreateArrow(new AnnotationArrowOperation(
            "arrow",
            new AnnotationPoint(20, 75),
            new AnnotationPoint(180, 40),
            AnnotationColor.Red_,
            6,
            false));

        var rendered = Renderer.Render(source, null, [operation]);

        var expected = TaperedArrowGeometry.Polygon(
            new PointD(20, 75),
            new PointD(180, 40),
            6);

        Assert.Equal(TaperedArrowGeometry.PointCount, expected.Length);

        var missedInside = 0;
        var spuriousOutside = 0;
        var sliverMisses = 0;
        var spurious = new List<(int X, int Y)>();

        for (var y = 0; y < rendered.Height; y++)
        {
            for (var x = 0; x < rendered.Width; x++)
            {
                var changed = IsChanged(rendered, source, x, y);
                var centerInside = TestImages.PointInPolygon(x + 0.5, y + 0.5, expected);
                var distance = TestImages.DistanceToPolygon(x + 0.5, y + 0.5, expected);

                if (centerInside && !changed)
                {
                    // 数学尖端处的碎片可能比 1/16px 的子扫描线间距还薄，
                    // 覆盖度小到混合后仍舍入回背景色。只豁免紧贴边界的像素。
                    if (distance <= SliverTolerance)
                    {
                        sliverMisses++;
                        continue;
                    }

                    missedInside++;
                }

                // 像素盒最远只延伸到中心外 sqrt(0.5²+0.5²) ≈ 0.707px。
                // 距离大于该值却仍被着色 → 真的画到多边形外面去了。
                if (!centerInside && changed && distance > PixelBoxRadius)
                {
                    spuriousOutside++;
                    spurious.Add((x, y));
                }
            }
        }

        if (spurious.Count > 0)
        {
            var detail = string.Join(" ", spurious.Select(p =>
                $"({p.X},{p.Y}) d={TestImages.DistanceToPolygon(p.X + 0.5, p.Y + 0.5, expected):F3}"));
            throw new Xunit.Sdk.XunitException($"多边形外被着色的像素：{detail}");
        }

        Assert.Equal(0, missedInside);

        // 碎片漏填必须是个位数 —— 否则说明光栅化本身有系统性问题。
        Assert.True(sliverMisses < 8, $"边界碎片漏填 = {sliverMisses}");
    }

    [Fact]
    public void 锥形几何锁定_七点_尖端等于终点_头部够宽()
    {
        var points = TaperedArrowGeometry.Polygon(new PointD(10, 45), new PointD(110, 45), 6);

        Assert.Equal(7, points.Length);
        Assert.Equal(new PointD(110, 45), points[3]);

        // Mac AnnotationDrawingTests.swift:6-29 的锁定 —— width=6 时 headWidth > 20。
        var headWidth = PointD.DistanceTo(points[2], points[4]);
        Assert.True(headWidth > 20, $"headWidth = {headWidth}");
    }

    // ── 矩形圆角 ────────────────────────────────────────────────

    [Fact]
    public void 矩形与椭圆渲染为轮廓_中心不被填充()
    {
        // 这条断言直接锁定「描边是空心环」——
        // 若 ClosedStrokeOutline 只构建外圈，矩形/椭圆会被填成实心，
        // 中心像素就会变色。
        using var source = TestImages.Solid(200, 160, 128, 128, 128);

        var rect = AnnotationOperation.CreateRectangle(RecipeHelper.Rect(
            "rect", new AnnotationRect(20, 20, 160, 120), lineWidth: 6));
        var rectRendered = Renderer.Render(source, null, [rect]);

        // 中心远离边界 → 必须是空心。
        Assert.False(IsChanged(rectRendered, source, 100, 80), "矩形中心不应被填充");
        Assert.False(IsChanged(rectRendered, source, 100, 25), "矩形上边内侧不应被填充");

        // 描边本身必须有墨迹。
        Assert.True(IsChanged(rectRendered, source, 100, 20), "矩形上边应有描边");
        Assert.True(IsChanged(rectRendered, source, 20, 80), "矩形左边应有描边");
        Assert.True(IsChanged(rectRendered, source, 179, 80), "矩形右边应有描边");
        Assert.True(IsChanged(rectRendered, source, 100, 139), "矩形下边应有描边");

        var ellipse = AnnotationOperation.CreateEllipse(RecipeHelper.Rect(
            "ellipse", new AnnotationRect(20, 20, 160, 120), lineWidth: 6));
        var ellipseRendered = Renderer.Render(source, null, [ellipse]);

        Assert.False(IsChanged(ellipseRendered, source, 100, 80), "椭圆中心不应被填充");
        Assert.True(IsChanged(ellipseRendered, source, 100, 20), "椭圆上顶点应有描边");
        Assert.True(IsChanged(ellipseRendered, source, 20, 80), "椭圆左顶点应有描边");
    }

    [Fact]
    public void 矩形圆角半径为五()
    {
        using var source = TestImages.Solid(140, 120, 128, 128, 128);

        // 线宽 6、圆角 5 → 拐角中心 (15,15)，描边外缘半径 5+3 = 8。
        var operation = AnnotationOperation.CreateRectangle(RecipeHelper.Rect(
            "rect", new AnnotationRect(10, 10, 100, 80), lineWidth: 6));

        var rendered = Renderer.Render(source, null, [operation]);

        // (11,11) 距拐角中心 5.66，落在 [2, 8] 环内 → 必须被描到。
        Assert.True(IsChanged(rendered, source, 11, 11), "圆角内侧应被描边覆盖");

        // (8,8) 距拐角中心 9.19 > 8 → 圆角把它切掉了。
        // 若圆角为 0（尖角 + miter），该像素会被覆盖。
        Assert.False(IsChanged(rendered, source, 8, 8), "圆角半径 5 应切掉尖角");
    }

    // ── 虚线 ────────────────────────────────────────────────────

    [Fact]
    public void 虚线矩形产生间隙()
    {
        using var source = TestImages.Solid(200, 100, 128, 128, 128);

        const double lineWidth = 8;
        var operation = AnnotationOperation.CreateRectangle(RecipeHelper.Rect(
            "rect", new AnnotationRect(10, 30, 180, 40), lineWidth: lineWidth, dashed: true));

        var rendered = Renderer.Render(source, null, [operation]);

        // 沿描边中线（顶边 y = 30）统计 on/off 游程。
        var runs = new List<int>();
        var current = 0;
        var on = false;
        for (var x = 0; x < rendered.Width; x++)
        {
            var covered = IsChanged(rendered, source, x, 30);
            if (covered == on)
            {
                current++;
                continue;
            }

            if (on)
            {
                runs.Add(current);
            }

            on = covered;
            current = 1;
        }

        Assert.True(runs.Count >= 3, $"虚线段数 = {runs.Count}");

        var average = runs.Average();
        var expectedDash = Math.Max(4, lineWidth * 2);
        var expectedGap = Math.Max(3, lineWidth * 1.4);

        // 起点相位 0 → 第一个游程是实线段。
        Assert.InRange(average, expectedDash * 0.6, expectedDash * 1.4);

        // 间隙长度单独量：取第一个 off 段。
        var gap = MeasureGap(rendered, source);
        Assert.InRange(gap, expectedGap * 0.6, expectedGap * 1.4);
    }

    [Fact]
    public void 虚线段是方头_不是圆头()
    {
        using var source = TestImages.Solid(120, 60, 128, 128, 128);

        // 单段虚线（长度小于一个 dash = 20），端点应为直角而非半圆。
        var operation = AnnotationOperation.CreatePen(new AnnotationStrokeOperation(
            "pen",
            [new AnnotationPoint(20, 30), new AnnotationPoint(60, 30)],
            AnnotationColor.Red_,
            10,
            true));

        var rendered = Renderer.Render(source, null, [operation]);

        // 方头：起点左前方的点 (16,33) 距起点 4.24 < 半径 5，
        // 若画成半圆端帽就会被覆盖。
        Assert.False(IsChanged(rendered, source, 16, 33), "方头端点不应有半圆外扩");

        // 端点处确实有墨迹（确保上面那条断言不是因为整段没画出来）。
        Assert.True(IsChanged(rendered, source, 25, 30), "虚线段本身应被画出来");
    }

    // ── 放大镜 ──────────────────────────────────────────────────

    [Fact]
    public void 放大镜就近邻放大源区域并画白色圆环()
    {
        // 水平渐变：像素值 = x，便于核对采样来源。
        using var source = TestImages.HorizontalGradient(200, 200);
        using var plain = TestImages.HorizontalGradient(200, 200);

        var operation = AnnotationOperation.CreateMagnify(new AnnotationMagnifyOperation(
            "magnify", new AnnotationRect(20, 20, 60, 60), 2));

        var rendered = Renderer.Render(source, null, [operation]);

        // 源矩形 = 目标中心 30×30 → (35,35) 起。
        // 画布 (64,50) → u = 44.5/60 → 源 x = 35 + 22.25 = 57。
        Assert.Equal(57, TestImages.Pixel(rendered, 64, 50).R);
        Assert.Equal(57, TestImages.Pixel(plain, 57, 50).R);

        // 画布 (36,50) → u = 16.5/60 → 源 x = 35 + 8.25 = 43。
        Assert.Equal(43, TestImages.Pixel(rendered, 36, 50).R);

        // 白色圆环：内缩 1.5、线宽 3 → 椭圆顶点处 (50, 21) 应为白。
        var ring = TestImages.Pixel(rendered, 50, 21);
        Assert.True(ring.R > 240 && ring.G > 240 && ring.B > 240, $"圆环颜色 = {ring}");
    }

    // ── 高亮笔半透明 ────────────────────────────────────────────

    [Fact]
    public void 高亮笔按直通alpha叠加_不是不透明()
    {
        // 黑底 + #FFD60A @ alpha 0.35 → 结果应是 35% 强度。
        using var source = TestImages.Solid(120, 60, 0, 0, 0);

        var operation = AnnotationOperation.CreateHighlighter(new AnnotationStrokeOperation(
            "highlight",
            [new AnnotationPoint(20, 30), new AnnotationPoint(100, 30)],
            AnnotationColor.Highlighter,
            16,
            false));

        var rendered = Renderer.Render(source, null, [operation]);
        var pixel = TestImages.Pixel(rendered, 60, 30);

        Assert.InRange(pixel.R, 255 * 0.35 * 0.7, 255 * 0.35 * 1.3);
        Assert.InRange(pixel.G, 214 * 0.35 * 0.7, 214 * 0.35 * 1.3);
        Assert.True(pixel.R < 200, "不应是不透明叠加");
    }

    // ── 编号 ────────────────────────────────────────────────────

    [Fact]
    public void 编号气泡里数字居中且为白色()
    {
        using var source = TestImages.Solid(120, 90, 128, 128, 128);

        var operation = AnnotationOperation.CreateNumber(new AnnotationNumberOperation(
            "step", new AnnotationPoint(60, 45), 7, AnnotationColor.Red_, 40));

        var rendered = Renderer.Render(source, null, [operation]);

        // 气泡范围内应有白色像素（数字），且白点集中在中心附近。
        var whiteX = new List<int>();
        for (var y = 30; y < 60; y++)
        {
            for (var x = 40; x < 80; x++)
            {
                var (r, g, b) = TestImages.Pixel(rendered, x, y);
                if (r > 240 && g > 240 && b > 240)
                {
                    whiteX.Add(x);
                }
            }
        }

        Assert.True(whiteX.Count > 20, $"白色数字像素 = {whiteX.Count}");
        var center = whiteX.Average();
        Assert.InRange(center, 52, 68);
    }

    private static bool IsChanged(RgbaBitmap rendered, RgbaBitmap source, int x, int y)
    {
        var a = TestImages.Pixel(rendered, x, y);
        var b = TestImages.Pixel(source, x, y);
        return a.R != b.R || a.G != b.G || a.B != b.B;
    }

    private static int MeasureGap(RgbaBitmap rendered, RgbaBitmap source)
    {
        var gap = 0;
        for (var x = 0; x < rendered.Width; x++)
        {
            if (!IsChanged(rendered, source, x, 30))
            {
                gap++;
            }
            else if (gap > 0)
            {
                return gap;
            }
        }

        return gap;
    }
}
