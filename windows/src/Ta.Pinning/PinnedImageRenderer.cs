using System.Runtime.InteropServices;
using Ta.Core.Imaging;
using Ta.Pinning.Imaging;

namespace Ta.Pinning;

/// <summary>
/// 钉图的软件合成器。
///
/// 对应 Mac 版 <c>PinnedImageView.draw(_:)</c>（PinnedImageWindowController.swift:357-397）
/// 加上 <c>hasShadow</c>（:163, :567）：
///   · 圆角 10（layer.cornerRadius，:315）
///   · 边框 白 0.28 / 宽 1（:317-318）
///   · 绘制顺序：黑填充 → 平移到中心 → 旋转 quarterTurns·π/2 → 镜像 → 绘制，
///     侧向时交换宽高（:359-374）
///   · 裁剪遮罩：黑 0.45 → 图 0.35 → 强调色虚线框 [6,4] 宽 2（:385-395）
///
/// **为什么自己写光栅化而不是用 GDI/Direct2D**：
///   1. 90° 旋转 + 镜像 + 高质量缩放用 GDI 表达不了一层 CTM，
///      D2D 又要引入 COM 互操作（参考文档 §15.2 明确「绝不用 System.Drawing」）。
///   2. 圆角与阴影都需要**逐像素 alpha**，只有 UpdateLayeredWindow 的
///      32 位 BGRA 表面能同时给出抗锯齿圆角和窗口阴影。
///   3. 纯托管字节数组运算可以被单测逐像素断言。
///
/// 输出格式：**自上而下、预乘 alpha 的 BGRA** —— UpdateLayeredWindow 的要求。
/// </summary>
public static class PinnedImageRenderer
{
    /// <summary>圆角半径。Mac: layer.cornerRadius = 10（:315）。</summary>
    public const int CornerRadiusPoints = 10;

    /// <summary>边框宽度。Mac: PinnedImageDecoration.visibleBorderWidth = 1（:278）。</summary>
    public const int BorderWidthPoints = 1;

    /// <summary>边框白色透明度。Mac: NSColor.white.withAlphaComponent(0.28)（:317）。</summary>
    public const double BorderAlpha = 0.28;

    /// <summary>
    /// 阴影外扩（pt）。NSWindow 的阴影画在窗口**之外**，而 Windows 的 WS_POPUP
    /// 没有系统阴影，所以这里把窗口放大一圈、把阴影画进外扩区里。
    /// </summary>
    public const int ShadowMarginPoints = 10;

    /// <summary>阴影强度。NSWindow 默认阴影约 0.4 上下，取 0.42 与覆盖层遮罩同量级。</summary>
    public const double ShadowStrength = 0.42;

    /// <summary>阴影模糊半径（px）。</summary>
    public const int ShadowBlurRadius = 7;

    /// <summary>裁剪遮罩的压暗。Mac: NSColor.black.withAlphaComponent(0.45)（:386）。</summary>
    public const double CropDim = 0.45;

    /// <summary>裁剪遮罩里再叠一次原图。Mac: fraction: 0.35（:389）。</summary>
    public const double CropImageFraction = 0.35;

    /// <summary>裁剪虚线框。Mac: setLineDash([6, 4], phase: 0), lineWidth = 2（:392-394）。</summary>
    public const int CropDashOn = 6;
    public const int CropDashOff = 4;
    public const int CropBorderWidth = 2;

    /// <summary>裁剪框颜色。Mac 用 NSColor.controlAccentColor（动态系统强调色，:390）；
    /// Windows 无逐像素等价物，固定为覆盖层同款 #0080FF（见参考文档 §14 风险 #10）。</summary>
    public const uint CropAccent = 0x0080FF;

    /// <summary>内容区之外的阴影外扩像素数。</summary>
    public static int ShadowMargin(bool showsShadow, double scale) =>
        showsShadow ? Math.Max(1, (int)Math.Round(ShadowMarginPoints * scale)) : 0;

    /// <summary>窗口的物理像素尺寸（含阴影外扩）。</summary>
    public static (int Width, int Height) WindowSize(PinSizeD content, double scale, bool showsShadow)
    {
        var margin = ShadowMargin(showsShadow, scale);
        return (Physical(content.Width, scale) + (margin * 2), Physical(content.Height, scale) + (margin * 2));
    }

    /// <summary>逻辑 pt → 物理 px。</summary>
    public static int Physical(double points, double scale) => Math.Max(1, (int)Math.Round(points * scale));

    /// <summary>
    /// 合成一帧。
    /// </summary>
    /// <param name="request">绘制请求（纯参数）。</param>
    /// <param name="scale">DPI 缩放（1 = 96dpi）。</param>
    /// <param name="cornerRadiusPx">圆角半径（物理 px）。</param>
    /// <param name="borderWidthPx">边框宽度（物理 px）。</param>
    /// <param name="shadowMarginPx">阴影外扩（物理 px）。</param>
    /// <returns>自上而下、预乘 alpha 的 BGRA 缓冲，尺寸为窗口物理尺寸。</returns>
    public static byte[] Compose(PinDrawRequest request, double scale, int cornerRadiusPx, int borderWidthPx, int shadowMarginPx)
    {
        var viewWidth = Math.Max(1, request.ViewWidth);
        var viewHeight = Math.Max(1, request.ViewHeight);
        var width = viewWidth + (shadowMarginPx * 2);
        var height = viewHeight + (shadowMarginPx * 2);

        var image = request.Source;
        var needsDispose = false;
        if (request.Crop is { } crop)
        {
            image = image.Crop(crop.Left, crop.Top, crop.Width, crop.Height);
            needsDispose = true;
        }

        if (request.Filter != PinFilterMode.None)
        {
            var filtered = PinPixelFilters.Apply(image, request.Filter);
            if (needsDispose)
            {
                image.Dispose();
            }

            image = filtered;
            needsDispose = true;
        }

        try
        {
            var buffer = new byte[(long)width * height * 4];

            if (request.ShowsShadow && shadowMarginPx > 0)
            {
                DrawShadow(buffer, width, height, viewWidth, viewHeight, shadowMarginPx, cornerRadiusPx);
            }

            // Mac: 绘制顺序 —— 旋转 → 镜像 → 缩放绘制到中心（:364-374）
            var sideways = PinTransform.IsSideways(request.QuarterTurns);
            var drawWidth = sideways ? viewHeight : viewWidth;
            var drawHeight = sideways ? viewWidth : viewHeight;

            // 采样缓冲提到双层循环之外：既避开 CA2014，也避免每像素动栈指针。
            Span<byte> pixel = stackalloc byte[4];

            for (var vy = 0; vy < viewHeight; vy++)
            {
                for (var vx = 0; vx < viewWidth; vx++)
                {
                    var (sourceX, sourceY) = MapViewToImage(
                        vx, vy, viewWidth, viewHeight, drawWidth, drawHeight,
                        image.Width, image.Height,
                        request.QuarterTurns, request.MirrorHorizontally, request.MirrorVertically);

                    SampleBilinear(image, sourceX, sourceY, pixel);

                    // 圆角覆盖（含抗锯齿）
                    var coverage = RoundedCoverage(vx, vy, viewWidth, viewHeight, cornerRadiusPx);
                    var index = (((vy + shadowMarginPx) * width) + (vx + shadowMarginPx)) * 4;

                    // Mac: draw(_:) 先把 bounds 填黑（:359-360），裁剪态再压暗 0.45（:386）
                    // —— 在已填黑的底上压暗等于无操作，随后以 0.35 叠一次原图（:389）。
                    var fraction = request.CropOverlay is not null ? CropImageFraction : 1;

                    // ⚠️ 通道序：源是 RGBA（RgbaBitmap 约定），输出缓冲是 BGRA
                    // （UpdateLayeredWindow 的 32 位 DIB 约定）。写错会红蓝互换，
                    // 而且只在真机屏幕上看得出 —— 单测必须锁住这一点。
                    BlendSrcOver(buffer, index, pixel[2], pixel[1], pixel[0],
                        (byte)Math.Round(255 * coverage * fraction));

                    if (request.ShowsBorder && borderWidthPx > 0)
                    {
                        DrawBorderAt(buffer, index, vx, vy, viewWidth, viewHeight, cornerRadiusPx, borderWidthPx, coverage);
                    }
                }
            }

            if (request.CropOverlay is { } overlay)
            {
                DrawCropSelection(buffer, width, height, shadowMarginPx, viewWidth, viewHeight, overlay.Start, overlay.Current);
            }

            if (request.Alpha < 1)
            {
                ApplyAlpha(buffer, request.Alpha);
            }

            return buffer;
        }
        finally
        {
            if (needsDispose)
            {
                image.Dispose();
            }
        }
    }

    /// <summary>
    /// 视图像素 → 源图连续坐标（单位：源图像素）。
    ///
    /// 推导（对应 Mac 的 CTM：translate 到中心 → rotate(quarterTurns·π/2) → scale(镜像)）：
    /// 显示点 = R·S·p，故 p = S·Rᵀ·显示点（S 是反射矩阵，自逆）。
    /// R 是 Y 向下空间里的顺时针 90°，Rᵀ(x,y) = (y, −x)；每个 90° 再作用一次即得四个分支。
    ///
    /// 校验（单测里也锁了）：2×1 的 [红|蓝] 向右旋转 90° 后，
    /// 视图像素 (0,0) 必须取到源图 (0,0) 的红、(0,1) 取到源图 (1,0) 的蓝。
    /// </summary>
    public static (double X, double Y) MapViewToImage(
        int viewX, int viewY, int viewWidth, int viewHeight,
        int drawWidth, int drawHeight, int imageWidth, int imageHeight,
        int quarterTurns, bool mirrorH, bool mirrorV)
    {
        var centerX = (viewX + 0.5) - (viewWidth / 2.0);
        var centerY = (viewY + 0.5) - (viewHeight / 2.0);

        double x, y;
        switch (PinTransform.Normalize(quarterTurns))
        {
            case 1:      // 向右旋转 90°（顺时针）
                x = centerY;
                y = -centerX;
                break;
            case 2:
                x = -centerX;
                y = -centerY;
                break;
            case 3:      // 向左旋转 90°（逆时针）
                x = -centerY;
                y = centerX;
                break;
            default:
                x = centerX;
                y = centerY;
                break;
        }

        if (mirrorH)
        {
            x = -x;
        }

        if (mirrorV)
        {
            y = -y;
        }

        return (
            ((x + (drawWidth / 2.0)) / drawWidth) * imageWidth,
            ((y + (drawHeight / 2.0)) / drawHeight) * imageHeight);
    }

    private static void SampleBilinear(RgbaBitmap image, double x, double y, Span<byte> destination)
    {
        // 连续坐标约定：像素 i 覆盖 [i, i+1)，中心在 i + 0.5。
        var sampleX = x - 0.5;
        var sampleY = y - 0.5;
        var x0 = (int)Math.Floor(sampleX);
        var y0 = (int)Math.Floor(sampleY);
        var fx = sampleX - x0;
        var fy = sampleY - y0;

        for (var channel = 0; channel < 4; channel++)
        {
            var top = Lerp(Pixel(image, x0, y0, channel), Pixel(image, x0 + 1, y0, channel), fx);
            var bottom = Lerp(Pixel(image, x0, y0 + 1, channel), Pixel(image, x0 + 1, y0 + 1, channel), fx);
            destination[channel] = (byte)Math.Round(Lerp(top, bottom, fy));
        }
    }

    private static double Pixel(RgbaBitmap image, int x, int y, int channel)
    {
        var clampedX = Math.Clamp(x, 0, image.Width - 1);
        var clampedY = Math.Clamp(y, 0, image.Height - 1);
        return image.Pixels[(clampedY * image.Stride) + (clampedX * RgbaBitmap.BytesPerPixel) + channel];
    }

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);

    /// <summary>
    /// 圆角矩形的覆盖度（含抗锯齿）。1 = 完全在内，0 = 完全在外。
    /// 对应 CALayer 的 cornerRadius + masksToBounds（:315-316）。
    /// </summary>
    public static double RoundedCoverage(int x, int y, int width, int height, int radius) =>
        Clamp01(0.5 - RoundedRectSignedDistance(x + 0.5, y + 0.5, width, height, radius));

    /// <summary>
    /// 圆角矩形 SDF：负值在内。坐标用像素中心。
    ///
    /// 标准圆角盒 SDF（iq）：
    /// <code>
    /// q = |p − center| − (halfSize − r)
    /// d = min(max(q.x, q.y), 0) + length(max(q, 0)) − r
    /// </code>
    /// ⚠️ 关键在 <c>length(max(q, 0))</c>：两个分量必须**各自先夹到 ≥0** 再求长度。
    /// 若直接 <c>sqrt(qx² + qy²)</c>，内部点（两分量都为负）会算出很大的正值，
    /// 圆角内整片被判成「外部」→ 内容区全透明、边框与阴影一起消失。
    /// 纯逻辑单测因此专门锁了「中心为 1、角落为 0」。
    /// </summary>
    public static double RoundedRectSignedDistance(double x, double y, double width, double height, double radius)
    {
        var halfWidth = width / 2.0;
        var halfHeight = height / 2.0;
        var effectiveRadius = Math.Max(0, Math.Min(radius, Math.Min(halfWidth, halfHeight)));

        var qx = Math.Abs(x - halfWidth) - (halfWidth - effectiveRadius);
        var qy = Math.Abs(y - halfHeight) - (halfHeight - effectiveRadius);

        var outsideX = Math.Max(qx, 0);
        var outsideY = Math.Max(qy, 0);
        var outside = Math.Sqrt((outsideX * outsideX) + (outsideY * outsideY));

        return outside + Math.Min(Math.Max(qx, qy), 0) - effectiveRadius;
    }

    private static void DrawBorderAt(
        byte[] buffer, int index, int x, int y, int width, int height,
        int radius, int borderWidth, double coverage)
    {
        // CALayer 的 border 画在边界上、向内各半。这里取 d ∈ [−borderWidth, 0] 的内侧一圈。
        var distance = RoundedRectSignedDistance(x + 0.5, y + 0.5, width, height, radius);
        if (distance > 0 || distance < -borderWidth)
        {
            return;
        }

        var borderCoverage = Clamp01(((-distance) / borderWidth) * coverage);
        var borderAlpha = (byte)Math.Round(255 * BorderAlpha * borderCoverage);
        BlendSrcOver(buffer, index, 255, 255, 255, borderAlpha);
    }

    /// <summary>src-over 合成，缓冲与源均为预乘 BGRA。</summary>
    private static void BlendSrcOver(byte[] buffer, int index, byte r, byte g, byte b, byte a)
    {
        if (a == 0)
        {
            return;
        }

        var inverse = 255 - a;
        buffer[index] = ClampByte(buffer[index] + ((r * a) / 255));
        buffer[index + 1] = ClampByte(buffer[index + 1] + ((g * a) / 255));
        buffer[index + 2] = ClampByte(buffer[index + 2] + ((b * a) / 255));
        buffer[index + 3] = ClampByte(buffer[index + 3] + a + ((buffer[index + 3] * inverse) / 255));
    }

    private static void DrawCropSelection(
        byte[] buffer, int width, int height, int margin,
        int viewWidth, int viewHeight, PinPointD start, PinPointD current)
    {
        // 覆盖层坐标已由调用方换算成「视图像素」（含阴影外扩偏移由 margin 补上）。
        var left = (int)Math.Floor(Math.Min(start.X, current.X));
        var top = (int)Math.Floor(Math.Min(start.Y, current.Y));
        var right = (int)Math.Ceiling(Math.Max(start.X, current.X));
        var bottom = (int)Math.Ceiling(Math.Max(start.Y, current.Y));

        var b = (byte)(CropAccent & 0xFF);
        var g = (byte)((CropAccent >> 8) & 0xFF);
        var r = (byte)((CropAccent >> 16) & 0xFF);

        for (var y = Math.Max(0, top); y <= Math.Min(viewHeight - 1, bottom); y++)
        {
            for (var x = Math.Max(0, left); x <= Math.Min(viewWidth - 1, right); x++)
            {
                var onEdge = x - left < CropBorderWidth || right - x <= CropBorderWidth
                    || y - top < CropBorderWidth || bottom - y <= CropBorderWidth;
                if (!onEdge)
                {
                    continue;
                }

                // 沿周长做 [6,4] 虚线 —— 从左上角起顺时针。
                var perimeter = PerimeterPosition(x, y, left, top, right, bottom);
                if (((perimeter % (CropDashOn + CropDashOff)) + (CropDashOn + CropDashOff)) % (CropDashOn + CropDashOff) >= CropDashOn)
                {
                    continue;
                }

                var index = (((y + margin) * width) + (x + margin)) * 4;
                BlendSrcOver(buffer, index, r, g, b, 255);
            }
        }
    }

    private static int PerimeterPosition(int x, int y, int left, int top, int right, int bottom)
    {
        var perimeterWidth = Math.Max(1, right - left);
        var perimeterHeight = Math.Max(1, bottom - top);
        if (y - top < CropBorderWidth)
        {
            return x - left;
        }

        if (x - right > -CropBorderWidth)
        {
            return perimeterWidth + (y - top);
        }

        if (bottom - y <= CropBorderWidth)
        {
            return perimeterWidth + perimeterHeight + (right - x);
        }

        return (perimeterWidth * 2) + perimeterHeight + (bottom - y);
    }

    private static void DrawShadow(
        byte[] buffer, int width, int height,
        int viewWidth, int viewHeight, int margin, int radius)
    {
        var mask = new byte[width * height];
        for (var y = 0; y < viewHeight; y++)
        {
            for (var x = 0; x < viewWidth; x++)
            {
                mask[((y + margin) * width) + (x + margin)] = (byte)Math.Round(255 * RoundedCoverage(x, y, viewWidth, viewHeight, radius));
            }
        }

        // 两遍盒式模糊近似高斯 —— 形状是圆角矩形，模糊只是把 alpha 摊开。
        BoxBlur(mask, width, height, ShadowBlurRadius);
        BoxBlur(mask, width, height, Math.Max(2, ShadowBlurRadius / 2));

        for (var i = 0; i < mask.Length; i++)
        {
            var alpha = (byte)Math.Round(ShadowStrength * mask[i]);
            BlendSrcOver(buffer, i * 4, 0, 0, 0, alpha);
        }
    }

    /// <summary>可分离盒式模糊（含边界夹取），就地覆盖。</summary>
    public static void BoxBlur(byte[] data, int width, int height, int radius)
    {
        if (radius <= 0 || width <= 0 || height <= 0)
        {
            return;
        }

        var temp = new byte[data.Length];

        // 横向
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sum = 0;
                var count = 0;
                for (var offset = -radius; offset <= radius; offset++)
                {
                    var sampleX = Math.Clamp(x + offset, 0, width - 1);
                    sum += data[(y * width) + sampleX];
                    count++;
                }

                temp[(y * width) + x] = (byte)(sum / count);
            }
        }

        // 纵向
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sum = 0;
                var count = 0;
                for (var offset = -radius; offset <= radius; offset++)
                {
                    var sampleY = Math.Clamp(y + offset, 0, height - 1);
                    sum += temp[(sampleY * width) + x];
                    count++;
                }

                data[(y * width) + x] = (byte)(sum / count);
            }
        }
    }

    private static void ApplyAlpha(byte[] buffer, double alpha)
    {
        var factor = Math.Clamp(alpha, 0, 1);
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (byte)Math.Round(buffer[i] * factor);
        }
    }

    private static byte ClampByte(int value) => (byte)Math.Clamp(value, 0, 255);

    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);
}
