using Ta.Core.Agent;
using Ta.Core.Drawing;
using Ta.Core.Imaging;

namespace Ta.Annotation;

/// <summary>
/// Agent 配方标注渲染器。
///
/// 逐行对应 Mac 版 <c>Sources/AIScreenshotApp/Agent/TaAgentAnnotationRenderer.swift</c>。
/// 注释里的 <c>Mac:NNN</c> 均指该文件行号。
///
/// ⚠️ 这是**两套渲染器中的配方版**（纯 CGContext + vImage + CoreText），
/// **不是**编辑器版（NSBezierPath + CoreImage）。两者模糊/马赛克算法不同，
/// 见参考文档 §14 风险 #8，切勿混用。
///
/// ## 坐标系（关键差异）
/// 配方坐标是**左上原点、Y 向下**（参考文档 §6.2）。Mac 的 CGContext 是左下原点，
/// 所以 Mac 渲染器每画一个元素都要先 <c>map()</c> 翻转 Y（Mac:361-367）。
/// Windows 的 <see cref="RgbaBitmap"/> 同样是左上原点、Y 向下 ——
/// **配方坐标可以直接当画布坐标用，不需要任何翻转**（参考文档 §5.1）。
/// 因此本文件里**没有** map(height:) 这类函数；这是有意为之，不是遗漏。
///
/// ## 单遍渲染
/// 操作按数组顺序单遍渲染（Mac:32-71）；crop 与 eraser 不绘制（Mac:67-68）。
/// 马赛克/模糊的源图是**裁剪后的原图**（base），因此它们永远露出原始像素，
/// 不会把已画上去的标注一起糊掉（Mac:49-64）。
/// </summary>
public sealed class TaAgentAnnotationRenderer
{
    /// <summary>矩形圆角半径。对应 Mac:99 的 cornerWidth/cornerHeight: 5。</summary>
    private const double RectangleCornerRadius = 5;

    /// <summary>编号气泡上的数字字号系数。对应 Mac:165 的 diameter * 0.56。</summary>
    private const double NumberFontScale = 0.56;

    /// <summary>放大镜白色圆环线宽。对应 Mac:248 的 setLineWidth(3)。</summary>
    private const double MagnifyRingWidth = 3;

    /// <summary>放大镜圆环内缩量。对应 Mac:249 的 insetBy(dx: 1.5, dy: 1.5)。</summary>
    private const double MagnifyRingInset = 1.5;

    /// <summary>渲染一个配方。</summary>
    public RgbaBitmap Render(RgbaBitmap source, AnnotationRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(recipe);

        return Render(source, CropRectOf(recipe), recipe.Operations);
    }

    /// <summary>
    /// 渲染。对应 Mac:8-76 的 render(sourceImage:cropRect:elements:)。
    /// cropRect 为 null 时用整幅源图（Mac:78-79）。
    /// </summary>
    public RgbaBitmap Render(
        RgbaBitmap source,
        AnnotationRect? cropRect,
        IReadOnlyList<AnnotationOperation> elements)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(elements);

        var baseImage = Crop(source, cropRect);
        var canvas = new RgbaBitmap(baseImage.Width, baseImage.Height);

        // 底色：把裁剪后的原图整幅铺进画布（Mac:28 的 context.draw(base, in: canvas)）。
        // 必须是拷贝 —— 不能直接持有 source，否则会污染调用方的位图。
        Array.Copy(baseImage.Pixels, canvas.Pixels, canvas.Pixels.Length);

        var drawer = new SoftwareCanvas(canvas);

        // 两个滤镜都懒计算一次，且都从 base 生成（Mac:30、49-64）。
        RgbaBitmap? mosaicImage = null;
        var blurImages = new Dictionary<int, RgbaBitmap>();

        foreach (var element in elements)
        {
            switch (element.Type)
            {
                case AnnotationOperationType.Rectangle:
                    DrawRect(element.RectShape!, ellipse: false, drawer);
                    break;

                case AnnotationOperationType.Ellipse:
                    DrawRect(element.RectShape!, ellipse: true, drawer);
                    break;

                case AnnotationOperationType.Arrow:
                    DrawArrow(element.Arrow!, drawer);
                    break;

                case AnnotationOperationType.Pen:
                case AnnotationOperationType.Highlighter:
                    DrawStroke(element.Stroke!, drawer);
                    break;

                case AnnotationOperationType.Text:
                    DrawText(element.Text!, drawer);
                    break;

                case AnnotationOperationType.Number:
                    DrawNumber(element.Number!, drawer);
                    break;

                case AnnotationOperationType.Mosaic:
                    mosaicImage ??= ImageFilters.Pixelate(baseImage, element.Mosaic!.Scale);
                    DrawMosaic(element.Mosaic!, mosaicImage, drawer);
                    break;

                case AnnotationOperationType.Blur:
                    var blur = element.Blur!;
                    // ⚠️ 缓存键是四舍五入后的半径，值却来自第一个命中该键的半径 ——
                    // 与 Mac:56-63 完全一致（radius 8.2 与 8.4 会共用同一张图）。
                    var key = (int)Math.Round(blur.Radius, MidpointRounding.AwayFromZero);
                    if (!blurImages.TryGetValue(key, out var blurred))
                    {
                        blurred = ImageFilters.BoxBlur(baseImage, blur.Radius);
                        blurImages[key] = blurred;
                    }

                    DrawFilteredRect(blur.Rect, blurred, drawer);
                    break;

                case AnnotationOperationType.Magnify:
                    DrawMagnify(element.Magnify!, baseImage, drawer);
                    break;

                case AnnotationOperationType.Crop:
                case AnnotationOperationType.Eraser:
                    // Mac:67-68 —— 不绘制。
                    break;
            }
        }

        return canvas;
    }

    /// <summary>裁剪。对应 Mac:78-85 的 croppedImage(_:rect:)。</summary>
    private static RgbaBitmap Crop(RgbaBitmap source, AnnotationRect? rect)
    {
        if (rect is null)
        {
            return source;
        }

        var value = rect.Value;

        // CGRect.integral：原点向下取整、右下角向上取整，保证结果包含原矩形。
        var left = (int)Math.Floor(value.X);
        var top = (int)Math.Floor(value.Y);
        var right = (int)Math.Ceiling(value.X + value.Width);
        var bottom = (int)Math.Ceiling(value.Y + value.Height);

        if (right <= left || bottom <= top)
        {
            throw new TaAgentAnnotationRenderError(TaAgentAnnotationRenderError.InvalidCrop);
        }

        // CGImage.cropping(to:) 会把越界部分裁掉，这里与图像边界求交复现该行为。
        var x0 = Math.Max(left, 0);
        var y0 = Math.Max(top, 0);
        var x1 = Math.Min(right, source.Width);
        var y1 = Math.Min(bottom, source.Height);

        if (x1 <= x0 || y1 <= y0)
        {
            throw new TaAgentAnnotationRenderError(TaAgentAnnotationRenderError.InvalidCrop);
        }

        return source.Crop(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>配方里 index 0 的 crop 就是 cropRect（Mac 由会话层拆出来传入）。</summary>
    private static AnnotationRect? CropRectOf(AnnotationRecipe recipe)
    {
        var first = recipe.Operations.Count > 0 ? recipe.Operations[0] : null;
        return first?.Type == AnnotationOperationType.Crop ? first.Crop?.Rect : null;
    }

    // ── 各元素绘制 ────────────────────────────────────────────────

    /// <summary>矩形 / 椭圆描边。对应 Mac:87-103 的 drawRect(_:style:ellipse:height:context:)。</summary>
    private static void DrawRect(AnnotationRectOperation style, bool ellipse, SoftwareCanvas drawer)
    {
        var color = Rgba.From(style.Color);
        var rect = style.Rect;

        // Windows 上配方坐标即画布坐标，无需翻转（对比 Mac:95 的 map(rect, height:)）。
        var centerline = ellipse
            ? StrokeGeometry.Ellipse(
                new PointD(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2)),
                rect.Width / 2,
                rect.Height / 2)
            : StrokeGeometry.RoundedRectangle(rect, RectangleCornerRadius);

        if (style.Dashed)
        {
            var (dash, gap) = DashPattern(style.LineWidth);
            foreach (var piece in StrokeGeometry.DashPieces(centerline, dash, gap, closed: true))
            {
                drawer.FillPolygon(
                    StrokeGeometry.StrokeOutline(piece, style.LineWidth, roundCap: false),
                    color);
            }
        }
        else
        {
            drawer.FillPolygon(
                StrokeGeometry.ClosedStrokeOutline(centerline, style.LineWidth),
                color);
        }
    }

    /// <summary>
    /// 箭头：7 点锥形多边形**填充**。对应 Mac:105-121。
    /// 几何直接调用 <see cref="TaperedArrowGeometry"/>，不在此重写。
    /// </summary>
    private static void DrawArrow(AnnotationArrowOperation operation, SoftwareCanvas drawer)
    {
        // Mac 先 map 再算多边形；map 是 Y 翻转（镜像），多边形整体镜像后填充结果不变。
        var points = TaperedArrowGeometry.Polygon(
            new PointD(operation.Start.X, operation.Start.Y),
            new PointD(operation.End.X, operation.End.Y),
            operation.LineWidth);

        drawer.FillPolygon(points, Rgba.From(operation.Color));
    }

    /// <summary>画笔 / 高亮笔。对应 Mac:123-139 的 drawStroke(_:height:context:)。</summary>
    private static void DrawStroke(AnnotationStrokeOperation operation, SoftwareCanvas drawer)
    {
        if (operation.Points.Length == 0)
        {
            return;
        }

        var color = Rgba.From(operation.Color);

        // 单点 → 直径 = lineWidth 的填充圆（Mac:129-132）。
        if (operation.Points.Length == 1)
        {
            var only = operation.Points[0];
            drawer.FillPolygon(
                StrokeGeometry.Circle(new PointD(only.X, only.Y), operation.LineWidth / 2),
                color);
            return;
        }

        var points = ToPoints(operation.Points);

        if (operation.Dashed)
        {
            var (dash, gap) = DashPattern(operation.LineWidth);
            foreach (var piece in StrokeGeometry.DashPieces(points, dash, gap, closed: false))
            {
                drawer.FillPolygon(
                    StrokeGeometry.StrokeOutline(piece, operation.LineWidth, roundCap: false),
                    color);
            }
        }
        else
        {
            drawer.FillPolygon(
                StrokeGeometry.StrokeOutline(points, operation.LineWidth, roundCap: true),
                color);
        }
    }

    /// <summary>文字。对应 Mac:141-157 的 drawText(_:height:context:)。</summary>
    private static void DrawText(AnnotationTextOperation operation, SoftwareCanvas drawer)
    {
        var contours = GlyphRasterizer.Outlines(operation.Text, operation.FontSize, bold: false);
        if (contours.Count == 0)
        {
            return;
        }

        // Mac:154 —— textPosition.y = height - origin.y - fontSize。
        // 换算到左上原点画布即「第一条基线在 origin.y + fontSize」。
        var baselineY = operation.Origin.Y + operation.FontSize;
        var color = Rgba.From(operation.Color);

        foreach (var contour in contours)
        {
            var translated = new PointD[contour.Points.Length];
            for (var i = 0; i < contour.Points.Length; i++)
            {
                translated[i] = new PointD(
                    operation.Origin.X + contour.Points[i].X,
                    baselineY + contour.Points[i].Y);
            }

            drawer.FillPolygon(translated, color, contour.Rule);
        }
    }

    /// <summary>编号气泡。对应 Mac:159-179 的 drawNumber(_:height:context:)。</summary>
    private static void DrawNumber(AnnotationNumberOperation operation, SoftwareCanvas drawer)
    {
        var center = new PointD(operation.Center.X, operation.Center.Y);
        var radius = operation.Diameter / 2;

        drawer.FillPolygon(
            StrokeGeometry.Circle(center, radius),
            Rgba.From(operation.Color));

        var fontSize = operation.Diameter * NumberFontScale;
        var text = operation.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var contours = GlyphRasterizer.Outlines(text, fontSize, bold: true);
        if (contours.Count == 0)
        {
            return;
        }

        // 把字形包围盒居中到气泡中心 —— 对应 Mac:176-177 的
        // CTLineGetBoundsWithOptions(.useGlyphPathBounds) 居中算法。
        var bounds = GlyphRasterizer.ContourBounds(contours);
        if (bounds.Empty)
        {
            return;
        }

        var offsetX = center.X - ((bounds.MinX + bounds.MaxX) / 2);
        var offsetY = center.Y - ((bounds.MinY + bounds.MaxY) / 2);

        foreach (var contour in contours)
        {
            var translated = new PointD[contour.Points.Length];
            for (var i = 0; i < contour.Points.Length; i++)
            {
                translated[i] = new PointD(
                    offsetX + contour.Points[i].X,
                    offsetY + contour.Points[i].Y);
            }

            drawer.FillPolygon(translated, Rgba.White, contour.Rule);
        }
    }

    /// <summary>马赛克。对应 Mac:181-209 的 drawMosaic(_:filtered:canvas:height:context:)。</summary>
    private static void DrawMosaic(
        AnnotationMosaicOperation operation,
        RgbaBitmap filtered,
        SoftwareCanvas drawer)
    {
        var placement = new ImagePlacement(
            filtered,
            0,
            0,
            filtered.Width,
            filtered.Height,
            0,
            0,
            filtered.Width,
            filtered.Height,
            NearestNeighbor: true);

        switch (operation.Mode)
        {
            case AnnotationMosaicMode.Rect:
                if (operation.Rect is { } rect)
                {
                    drawer.FillPolygonWithImage(StrokeGeometry.Rectangle(rect), placement);
                }

                break;

            case AnnotationMosaicMode.Brush:
                if (operation.Points is not { Length: > 0 } points)
                {
                    return;
                }

                // Mac:201-205 —— clip 到**描边路径**：圆头圆角、线宽 = lineWidth。
                var outline = points.Length == 1
                    ? StrokeGeometry.Circle(new PointD(points[0].X, points[0].Y), operation.LineWidth / 2)
                    : StrokeGeometry.StrokeOutline(ToPoints(points), operation.LineWidth, roundCap: true);

                drawer.FillPolygonWithImage(outline, placement);
                break;
        }
    }

    /// <summary>模糊矩形。对应 Mac:211-222 的 drawFilteredRect(_:filtered:canvas:height:context:)。</summary>
    private static void DrawFilteredRect(AnnotationRect rect, RgbaBitmap filtered, SoftwareCanvas drawer)
    {
        var placement = new ImagePlacement(
            filtered,
            0,
            0,
            filtered.Width,
            filtered.Height,
            0,
            0,
            filtered.Width,
            filtered.Height,
            NearestNeighbor: true);

        drawer.FillPolygonWithImage(StrokeGeometry.Rectangle(rect), placement);
    }

    /// <summary>局部放大。对应 Mac:224-250 的 drawMagnify(_:source:height:context:)。</summary>
    private static void DrawMagnify(
        AnnotationMagnifyOperation operation,
        RgbaBitmap source,
        SoftwareCanvas drawer)
    {
        var target = operation.Rect;
        var sourceWidth = target.Width / operation.Factor;
        var sourceHeight = target.Height / operation.Factor;

        var rawSource = new AnnotationRect(
            target.X + ((target.Width - sourceWidth) / 2),
            target.Y + ((target.Height - sourceHeight) / 2),
            sourceWidth,
            sourceHeight);

        // Mac:238 —— .integral 后再与图像边界求交。
        var left = (int)Math.Floor(rawSource.X);
        var top = (int)Math.Floor(rawSource.Y);
        var right = (int)Math.Ceiling(rawSource.X + rawSource.Width);
        var bottom = (int)Math.Ceiling(rawSource.Y + rawSource.Height);

        var x0 = Math.Max(left, 0);
        var y0 = Math.Max(top, 0);
        var x1 = Math.Min(right, source.Width);
        var y1 = Math.Min(bottom, source.Height);

        if (x1 <= x0 || y1 <= y0)
        {
            throw new TaAgentAnnotationRenderError(TaAgentAnnotationRenderError.InvalidMagnifyRegion);
        }

        // 椭圆 clip → 画放大后的源区域（Mac:243-245）。
        var placement = new ImagePlacement(
            source,
            target.X,
            target.Y,
            target.Width,
            target.Height,
            x0,
            y0,
            x1 - x0,
            y1 - y0,
            NearestNeighbor: true);

        drawer.FillPolygonWithImage(EllipseOf(target), placement);

        // 白色圆环：inset 1.5、线宽 3（Mac:247-249）。
        var ring = EllipseOf(new AnnotationRect(
            target.X + MagnifyRingInset,
            target.Y + MagnifyRingInset,
            Math.Max(target.Width - (2 * MagnifyRingInset), 0),
            Math.Max(target.Height - (2 * MagnifyRingInset), 0)));

        drawer.FillPolygon(
            StrokeGeometry.ClosedStrokeOutline(ring, MagnifyRingWidth),
            Rgba.White);
    }

    private static List<PointD> EllipseOf(AnnotationRect rect) =>
        StrokeGeometry.Ellipse(
            new PointD(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2)),
            rect.Width / 2,
            rect.Height / 2);

    /// <summary>
    /// 虚线模式。对应 Mac:354 —— setLineDash(phase: 0, lengths: [max(4, w*2), max(3, w*1.4)])。
    /// ⚠️ 基于**未经 strokeScale 缩放**的 lineWidth 计算 —— 配方版渲染比例恒为 1:1。
    /// </summary>
    private static (double Dash, double Gap) DashPattern(double lineWidth) =>
        (Math.Max(4, lineWidth * 2), Math.Max(3, lineWidth * 1.4));

    private static PointD[] ToPoints(AnnotationPoint[] points)
    {
        var result = new PointD[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            result[i] = new PointD(points[i].X, points[i].Y);
        }

        return result;
    }
}

/// <summary>标注渲染失败。错误文案与 Mac:379-385 逐字一致。</summary>
public sealed class TaAgentAnnotationRenderError : Exception
{
    public const string ContextCreationFailed = "无法创建标注画布。";
    public const string ImageCreationFailed = "无法生成标注图片。";
    public const string InvalidCrop = "无法裁剪指定图片区域。";
    public const string InvalidMagnifyRegion = "放大镜区域没有可用像素。";

    /// <summary>滤镜失败文案。对应 Mac:384。</summary>
    public static string FilterFailed(string name) => $"图像滤镜执行失败：{name}。";

    public TaAgentAnnotationRenderError(string message) : base(message)
    {
    }
}
