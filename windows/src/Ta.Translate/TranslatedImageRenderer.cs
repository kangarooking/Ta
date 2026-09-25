using Ta.Core.Capture;
using Ta.Core.Imaging;
using Ta.Translate.Rendering;

namespace Ta.Translate;

/// <summary>
/// 译文回写原图的渲染器 —— 纯托管实现（直接写 <see cref="RgbaBitmap.Pixels"/>，
/// 文字栅格化走 Win32 GDI 遮罩，不用 System.Drawing / GDI+）。
///
/// 逐行对应 Mac 版 <c>TranslatedImageRenderer.swift</c>：
/// · textOnly（:20-21）：原样返回图；
/// · fullImage（:29-59）：同尺寸覆盖 —— 就地替换文字；
/// · bilingualImage（:61-130）：在原图<b>下方</b>追加面板（输出高 = 原图高 + 面板高）。
///
/// ⚠️ Y 方向（Windows移植参考文档 §5.1 + Mac 测试
/// <c>TranslatedImageRendererTests.swift:26-41</c>）：Mac 的位图上下文是<b>左下原点</b>，
/// 图像画在 AppKit y ∈ [panelHeight, +H]（像素空间的<b>上</b>半），面板落在下半；
/// Windows 像素空间同样是<b>左上原点</b>，因此「图像在上、面板在下」的排布<b>原样成立</b> ——
/// Mac 版为翻转 Y 轴所写的坐标运算在 Windows 上直接消失。本实现用 Mac 测试同款断言锁定
/// （输出 (4,4) 像素 == 原图 (4,4) 像素），并用「Y 方向实测」用例显式验证面板落在哪一侧。
/// </summary>
public sealed class TranslatedImageRenderer
{
    /// <summary>
    /// 渲染。对应 Mac: TranslatedImageRenderer.render（:12-27）。
    /// </summary>
    /// <exception cref="ScreenshotTranslationServiceError">lines 为空时（Mac: missingTextBoxes）。</exception>
    public RgbaBitmap Render(
        RgbaBitmap image,
        IReadOnlyList<TranslatedOcrLine> lines,
        ScreenshotTranslationMode mode,
        string targetLanguage)
    {
        if (lines.Count == 0)
        {
            throw ScreenshotTranslationServiceError.MissingTextBoxes();
        }

        return mode switch
        {
            ScreenshotTranslationMode.TextOnly => image,
            ScreenshotTranslationMode.FullImage => RenderFullTranslation(image, lines),
            ScreenshotTranslationMode.BilingualImage => RenderBilingualPanel(image, lines, targetLanguage),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };
    }

    /// <summary>
    /// 区域平均色 —— 盒式平均（无插值、无缩放）。
    ///
    /// 对应 Mac: averageColor（:157-185）—— Mac 把区域画进 1×1 CGContext
    /// （interpolationQuality = .low）取单像素均值，本质就是盒式平均。
    /// ⚠️ Mac 那里有一次 Y 翻转（<c>y: image.height - drawingRect.maxY</c>，因为
    /// CGImage.cropping 是左下原点）；Windows 像素空间左上原点、Y 向下，翻转消失。
    /// 区域边界取 floor/ceil 与 CGImage.cropping 的整数化一致。
    /// </summary>
    public static RgbaColor AverageColor(RgbaBitmap image, RectD region)
    {
        var left = Math.Max(0, (int)Math.Floor(region.MinX));
        var top = Math.Max(0, (int)Math.Floor(region.MinY));
        var right = Math.Min(image.Width, (int)Math.Ceiling(region.MaxX));
        var bottom = Math.Min(image.Height, (int)Math.Ceiling(region.MaxY));

        long sumR = 0;
        long sumG = 0;
        long sumB = 0;
        var count = 0L;

        for (var y = top; y < bottom; y++)
        {
            var row = y * image.Stride;
            for (var x = left; x < right; x++)
            {
                var i = row + (x * RgbaBitmap.BytesPerPixel);
                sumR += image.Pixels[i];
                sumG += image.Pixels[i + 1];
                sumB += image.Pixels[i + 2];
                count++;
            }
        }

        if (count == 0)
        {
            // 对应 Mac 的兜底 .white（裁剪失败时）。
            return RgbaColor.White;
        }

        return new RgbaColor(
            (byte)(sumR / count),
            (byte)(sumG / count),
            (byte)(sumB / count));
    }

    /// <summary>
    /// 双语面板的高度（像素）。对应 Mac: panelHeight（:80-81）。
    /// 公开以便测试锁定「输出高 = 原图高 + 面板高」。
    /// </summary>
    public static int MeasurePanelHeight(int imageWidth, IReadOnlyList<TranslatedOcrLine> lines)
    {
        var horizontalPadding = Math.Max(28, imageWidth * 0.035);
        var contentWidth = imageWidth - (horizontalPadding * 2);
        var sourcePointSize = Math.Max(15, Math.Min(24, (double)imageWidth / 70));
        var targetPointSize = Math.Max(16, Math.Min(27, (double)imageWidth / 62));
        var headerPointSize = Math.Max(18, Math.Min(30, (double)imageWidth / 55));

        var rowSpacing = Math.Max(14, targetPointSize * 0.7);
        var textSpacing = Math.Max(4, targetPointSize * 0.22);
        var headerHeight = headerPointSize * 2.4;

        double total = headerHeight + horizontalPadding;

        using var engine = new GdiTextEngine();
        foreach (var line in lines)
        {
            total += MeasureTextHeight(engine, line.SourceText, contentWidth, sourcePointSize, FontFaces.WeightMedium)
                + textSpacing
                + MeasureTextHeight(engine, line.TranslatedText, contentWidth, targetPointSize, FontFaces.WeightSemibold)
                + rowSpacing;
        }

        return (int)Math.Ceiling(total);
    }

    /// <summary>
    /// fullImage：同尺寸覆盖（Mac: :29-59）。
    /// </summary>
    private static RgbaBitmap RenderFullTranslation(
        RgbaBitmap image,
        IReadOnlyList<TranslatedOcrLine> lines)
    {
        var output = Clone(image);
        var imageRect = new RectD(0, 0, image.Width, image.Height);

        using var engine = new GdiTextEngine();

        foreach (var line in lines)
        {
            // pixelRect —— 归一化 bbox → 像素矩形，.integral（floor/ceil）（Mac: :40, :148-155）。
            var rect = PixelRect(line.BoundingBox, image.Width, image.Height);

            // 内缩为负值 = 向外扩张（Mac: :41）。注意扩张量用的是扩张**前**的高度
            // （Swift 参数在赋值前求值，语义一致）。
            var expandX = Math.Max(2, rect.Height * 0.08);
            var expandY = Math.Max(2, rect.Height * 0.12);
            var grown = RectD.FromLTRB(
                rect.MinX - expandX,
                rect.MinY - expandY,
                rect.MaxX + expandX,
                rect.MaxY + expandY);

            var clipped = Intersect(grown, imageRect);

            // < 4×4 跳过（Mac: :43）。
            if (clipped.Width < 4 || clipped.Height < 4)
            {
                continue;
            }

            var background = AverageColor(image, clipped);

            // 底色 alpha 0.96 的圆角矩形，半径 min(5, h*0.12)（Mac: :45-46）。
            BitmapPainter.FillRoundedRect(
                output,
                clipped,
                background with { A = (byte)Math.Round(0.96 * 255) },
                Math.Min(5, clipped.Height * 0.12),
                Math.Min(5, clipped.Height * 0.12));

            // 文字内缩 +max(2, h*0.07) / +max(1, h*0.05)（Mac: :49-50）。
            var textRect = Inset(
                clipped,
                Math.Max(2, clipped.Height * 0.07),
                Math.Max(1, clipped.Height * 0.05));

            DrawFittedText(engine, output, line.TranslatedText, textRect, RgbaColor.ContrastingTextColor(background));
        }

        return output;
    }

    /// <summary>
    /// bilingualImage：原图下方追加面板（Mac: :61-130）。
    /// </summary>
    private static RgbaBitmap RenderBilingualPanel(
        RgbaBitmap image,
        IReadOnlyList<TranslatedOcrLine> lines,
        string targetLanguage)
    {
        var width = image.Width;
        var horizontalPadding = Math.Max(28, width * 0.035);
        var contentWidth = width - (horizontalPadding * 2);

        var sourcePointSize = Math.Max(15, Math.Min(24, (double)width / 70));
        var targetPointSize = Math.Max(16, Math.Min(27, (double)width / 62));
        var headerPointSize = Math.Max(18, Math.Min(30, (double)width / 55));

        var rowSpacing = Math.Max(14, targetPointSize * 0.7);
        var textSpacing = Math.Max(4, targetPointSize * 0.22);
        var headerHeight = headerPointSize * 2.4;

        using var engine = new GdiTextEngine();

        // 行高（Mac: :75-79）。
        var rowHeights = lines
            .Select(line => MeasureTextHeight(engine, line.SourceText, contentWidth, sourcePointSize, FontFaces.WeightMedium)
                + textSpacing
                + MeasureTextHeight(engine, line.TranslatedText, contentWidth, targetPointSize, FontFaces.WeightSemibold)
                + rowSpacing)
            .ToList();

        var panelHeight = (int)Math.Ceiling(headerHeight + rowHeights.Sum() + horizontalPadding);
        var totalHeight = image.Height + panelHeight;
        var panelTop = image.Height;

        var output = new RgbaBitmap(width, totalHeight);

        // 1. 整幅填面板底色（Mac: :87-88，calibratedWhite 0.97）。
        output.Fill(RenderColors.PanelBackground.R, RenderColors.PanelBackground.G, RenderColors.PanelBackground.B);

        // 2. 原图贴到上侧 [0, H)（Mac: :89-91 —— AppKit 画在 y ∈ [panelH, panelH+H]，
        //    像素空间即上半；Windows 无需翻转，直接贴顶部）。
        for (var y = 0; y < image.Height; y++)
        {
            output.CopyRowFrom(image, y, y);
        }

        // 3. 接缝 1px separator（Mac: :93-95 —— AppKit y = panelH-1 ↔ 像素行 H）。
        BitmapPainter.FillRect(output, 0, panelTop, width, 1, RenderColors.Separator);

        // 4. 标题（Mac: :97-102）。基线 = 面板顶 + headerPt*1.65。
        var headerText = $"双语翻译 · {targetLanguage}";
        var headerFace = FontFaces.ResolveFace(headerText);
        var headerFont = engine.GetFont(headerFace, FontFaces.WeightBold, headerPointSize);
        headerFont.DrawLine(
            output,
            headerText,
            (int)Math.Round(horizontalPadding),
            panelTop + (int)Math.Round(headerPointSize * 1.65),
            RenderColors.Label);

        // 5. 行（Mac: :104-123）。自面板顶向下推进游标：
        //    s 从 panelTop + headerPt*2.9 起步（= 标题基线 + 1.25*pt 后第一块的顶）。
        //    ⚠️ 必须加上 panelTop —— 游标是「相对面板顶」推导出来的，
        //    漏加会把行文字画到原图区域上（Y 方向实测用例锁定这一点）。
        var s = panelTop + (headerPointSize * 2.9);

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];

            // 源文块顶 = s。
            var sourceHeight = MeasureTextHeight(engine, line.SourceText, contentWidth, sourcePointSize, FontFaces.WeightMedium);
            DrawTextBlock(
                engine,
                output,
                line.SourceText,
                horizontalPadding,
                s,
                contentWidth,
                sourcePointSize,
                FontFaces.WeightMedium,
                RenderColors.SecondaryLabel);
            s += sourceHeight;

            s += textSpacing;

            // 译文块顶 = s。
            var targetHeight = MeasureTextHeight(engine, line.TranslatedText, contentWidth, targetPointSize, FontFaces.WeightSemibold);
            DrawTextBlock(
                engine,
                output,
                line.TranslatedText,
                horizontalPadding,
                s,
                contentWidth,
                targetPointSize,
                FontFaces.WeightSemibold,
                RenderColors.Label);
            s += targetHeight;

            s += rowSpacing;

            // 行分隔线（Mac: :119-122 —— AppKit y = cursorY + rowSpacing*0.45，
            // 转成像素行即 s - rowSpacing*0.45，位于行间距区域中偏上）。
            if (index < lines.Count - 1)
            {
                var separatorY = (int)Math.Round(s - (rowSpacing * 0.45));
                var blended = new RgbaColor(
                    (byte)(((255 * RenderColors.PanelBackground.R)
                        + ((RenderColors.Separator.R - RenderColors.PanelBackground.R) * 140) + 127) / 255),
                    (byte)(((255 * RenderColors.PanelBackground.G)
                        + ((RenderColors.Separator.G - RenderColors.PanelBackground.G) * 140) + 127) / 255),
                    (byte)(((255 * RenderColors.PanelBackground.B)
                        + ((RenderColors.Separator.B - RenderColors.PanelBackground.B) * 140) + 127) / 255));
                BitmapPainter.FillRect(
                    output,
                    (int)Math.Round(horizontalPadding),
                    separatorY,
                    (int)Math.Round(contentWidth),
                    1,
                    blended);
            }
        }

        return output;
    }

    /// <summary>
    /// 按矩形拟合字号（Mac: drawFittedText 的循环，:193-205）。
    /// 公开以便测试锁定「起始 min(72, max(8, h*0.72))，递减到适配或 ≥ 7」。
    /// </summary>
    public static double FitFontSize(string text, double width, double height)
    {
        using var engine = new GdiTextEngine();
        return FitFontSizeCore(engine, FontFaces.ResolveFace(text), text, new RectD(0, 0, width, height));
    }

    /// <summary>按给定字号度量换行后的（最大行宽, 总高度）。对应 Mac: boundingRect 的 size。</summary>
    public static (double MaxLineWidth, double TotalHeight) MeasureText(
        string text,
        double width,
        double fontSize)
    {
        using var engine = new GdiTextEngine();
        var layout = TextLayoutEngine.WrapAndMeasure(
            engine,
            text,
            width,
            FontFaces.ResolveFace(text),
            FontFaces.WeightMedium,
            fontSize);
        return (layout.MaxLineWidth, layout.TotalHeight);
    }

    /// <summary>
    /// drawFittedText（Mac: :193-211）。
    /// 起始字号 min(72, max(8, rect.height*0.72))，每次减 1 直到适配 rect ± 0.5 或字号 ≤ 7；
    /// 段落按词换行、左对齐。
    /// </summary>
    private static void DrawFittedText(
        GdiTextEngine engine,
        RgbaBitmap target,
        string text,
        RectD rect,
        RgbaColor color)
    {
        var face = FontFaces.ResolveFace(text);
        var fontSize = FitFontSizeCore(engine, face, text, rect);
        var layout = TextLayoutEngine.WrapAndMeasure(engine, text, rect.Width, face, FontFaces.WeightMedium, fontSize);

        var font = engine.GetFont(face, FontFaces.WeightMedium, fontSize);

        // 从矩形顶部开始逐行画（Mac 用 NSString.draw(in:options:) 的默认顶对齐语义）。
        var y = rect.MinY;
        foreach (var line in layout.Lines)
        {
            font.DrawLine(target, line, (int)Math.Round(rect.MinX), (int)Math.Round(y + font.Ascent), color);
            y += font.LineHeight;
        }
    }

    /// <summary>
    /// 字号拟合循环本体（Mac: :194-205）。
    /// 关键语义：检查通过即在当前字号<b>停下</b>（不从初值一路减到 7）；
    /// 若始终不适配，则停在 7（while 条件 fontSize &gt; 7 保证下界）。
    /// </summary>
    private static double FitFontSizeCore(GdiTextEngine engine, string face, string text, RectD rect)
    {
        var fontSize = Math.Min(72, Math.Max(8, rect.Height * 0.72));
        var layout = TextLayoutEngine.WrapAndMeasure(engine, text, rect.Width, face, FontFaces.WeightMedium, fontSize);

        // Mac: while fontSize > 7 { if fits break; fontSize -= 1 }
        while (fontSize > 7)
        {
            if (layout.MaxLineWidth <= rect.Width + 0.5 && layout.TotalHeight <= rect.Height + 0.5)
            {
                break;
            }

            fontSize -= 1;
            layout = TextLayoutEngine.WrapAndMeasure(engine, text, rect.Width, face, FontFaces.WeightMedium, fontSize);
        }

        return fontSize;
    }

    /// <summary>把一个文本块（可能多行）从 top 开始排版绘制。</summary>
    private static void DrawTextBlock(
        GdiTextEngine engine,
        RgbaBitmap target,
        string text,
        double x,
        double top,
        double contentWidth,
        double pointSize,
        int weight,
        RgbaColor color)
    {
        var face = FontFaces.ResolveFace(text);
        var font = engine.GetFont(face, weight, pointSize);
        var lines = TextLayoutEngine.WrapLines(engine, text, contentWidth, font);

        var y = top;
        foreach (var line in lines)
        {
            font.DrawLine(target, line, (int)Math.Round(x), (int)Math.Round(y + font.Ascent), color);
            y += font.LineHeight;
        }
    }

    /// <summary>
    /// textHeight（Mac: :224-232）：给定宽度下的换行后总高度（ceil）。
    /// <paramref name="weight"/> 必须与实际绘制该文本所用的字重一致
    /// （源文 medium、译文 semibold）—— 不同字重的字宽不同，换行结果也会不同。
    /// </summary>
    private static double MeasureTextHeight(
        GdiTextEngine engine,
        string text,
        double contentWidth,
        double pointSize,
        int weight)
    {
        var face = FontFaces.ResolveFace(text);
        var font = engine.GetFont(face, weight, pointSize);
        var lines = TextLayoutEngine.WrapLines(engine, text, contentWidth, font);
        return Math.Ceiling(lines.Count * font.LineHeight);
    }

    /// <summary>pixelRect（Mac: :148-155）—— 归一化 bbox → 像素矩形，.integral。</summary>
    private static RectD PixelRect(RectD normalized, int imageWidth, int imageHeight)
    {
        var left = Math.Floor(normalized.MinX * imageWidth);
        var top = Math.Floor(normalized.MinY * imageHeight);
        var right = Math.Ceiling(normalized.MaxX * imageWidth);
        var bottom = Math.Ceiling(normalized.MaxY * imageHeight);
        return RectD.FromLTRB(left, top, right, bottom);
    }

    /// <summary>两矩形求交（对应 Mac 的 CGRect.intersection）。</summary>
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

    /// <summary>insetBy(dx:dy:)（AppKit 语义：正值内缩）。</summary>
    private static RectD Inset(RectD rect, double dx, double dy) =>
        RectD.FromLTRB(rect.MinX + dx, rect.MinY + dy, rect.MaxX - dx, rect.MaxY - dy);

    private static RgbaBitmap Clone(RgbaBitmap source)
    {
        var clone = new RgbaBitmap(source.Width, source.Height);
        Array.Copy(source.Pixels, clone.Pixels, source.Pixels.Length);
        return clone;
    }
}
