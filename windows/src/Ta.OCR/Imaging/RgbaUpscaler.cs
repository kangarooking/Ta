using Ta.Core.Imaging;

namespace Ta.OCR.Imaging;

/// <summary>
/// RGBA 双线性上采样 —— 专为「小字 OCR」场景（实测：13px 中文在 Windows 媒体 OCR 上
/// 识别率很差，2x 放大后命中率显著提升）。
///
/// 为什么在识别前放大：Windows.Media.Ocr 内部按自身模型的感受野工作，对小号文字
/// 的抗锯齿细节不敏感；先把小字放大到「中号」再喂给它，等价于把文字特征拉到模型
/// 擅长的尺度。这是 OCR 工程的常规做法，代价只是一次 4 倍像素的插值。
///
/// 与 <see cref="RgbaDownscaler"/> 的取舍一致：不做浮点卷积堆栈，直接双线性插值 ——
/// 放大场景下双线性平滑无振铃，且实现可预期（无任何测试锁定像素级输出）。
/// </summary>
public static class RgbaUpscaler
{
    /// <summary>
    /// 放大 <paramref name="factor"/> 倍（默认 2x）。factor 必须 &gt;= 1。
    /// </summary>
    public static RgbaBitmap Upscale(RgbaBitmap source, double factor = 2.0)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (factor < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(factor), "放大倍数必须 >= 1。");
        }

        if (factor == 1)
        {
            return source;
        }

        var targetWidth = Math.Max(1, (int)Math.Round(source.Width * factor, MidpointRounding.AwayFromZero));
        var targetHeight = Math.Max(1, (int)Math.Round(source.Height * factor, MidpointRounding.AwayFromZero));
        return Resize(source, targetWidth, targetHeight);
    }

    /// <summary>
    /// OCR 专用策略：小图放大 2x（提升小字识别），大图原样返回（避免超识别器上限）。
    /// 阈值：放大后最长边仍不超过 <paramref name="maximumDimension"/> 才放大。
    /// </summary>
    public static RgbaBitmap PrepareForRecognition(
        RgbaBitmap image,
        int maximumDimension = OCRImagePreparation.DefaultMaximumDimension)
    {
        ArgumentNullException.ThrowIfNull(image);

        // ⚠️ 实测教训（2026-09-19）：阈值 900 太激进 —— 常规选区（数百像素）被放大后
        // 双线性平滑把笔画糊开，Windows 媒体 OCR 反而**一行都识别不出**（日志实锤：
        // Processing → 0.2 秒后「未发现文字」）。放大仅对「极小图」有意义（文字像素
        // 高度不足 6-8px 时），且必须限定在小范围内，宁可少放大也不能致识别倒退。
        const int TinyImageThreshold = 320;
        var largest = Math.Max(image.Width, image.Height);
        if (Math.Min(image.Width, image.Height) >= TinyImageThreshold)
        {
            return image;
        }

        // 放大 2x 后仍在识别器上限内才做。
        if (largest * 2 > maximumDimension)
        {
            return image;
        }

        return Upscale(image, 2.0);
    }

    /// <summary>
    /// 最近邻整数放大 —— 保硬边（不做插值平滑）。
    /// 用于对比实验：小字 OCR 场景下，平滑插值会把笔画糊开导致引擎识别不出，
    /// 最近邻至少保持笔画锐利（虽然块状，但引擎对硬边更敏感）。
    /// </summary>
    /// <summary>
    /// 反相（深底浅字 → 浅底深字）。OCR 模型对「深字浅底」的检出率显著更高，
    /// 深色界面（IDE/终端/深色 App）的浅色文字常被整片漏检 —— 反相是标准对策。
    /// </summary>
    public static RgbaBitmap Invert(RgbaBitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var result = new RgbaBitmap(source.Width, source.Height);
        for (var i = 0; i < source.Pixels.Length; i += RgbaBitmap.BytesPerPixel)
        {
            result.Pixels[i] = (byte)(255 - source.Pixels[i]);
            result.Pixels[i + 1] = (byte)(255 - source.Pixels[i + 1]);
            result.Pixels[i + 2] = (byte)(255 - source.Pixels[i + 2]);
            result.Pixels[i + 3] = source.Pixels[i + 3];
        }

        return result;
    }

    /// <summary>
    /// 加白边（向四周扩展指定像素的白色背景）。OCR 引擎对**紧贴图像边缘**的文字
    /// 检出率低 —— 用户框选时文字常被切在图像边界上，加一圈留白是标准对策。
    /// </summary>
    public static RgbaBitmap PadWhitespace(RgbaBitmap source, int padding)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (padding <= 0)
        {
            return source;
        }

        var width = source.Width + (padding * 2);
        var height = source.Height + (padding * 2);
        var result = new RgbaBitmap(width, height);

        // 先铺白（不透明）。
        for (var i = 0; i < result.Pixels.Length; i += RgbaBitmap.BytesPerPixel)
        {
            result.Pixels[i] = 255;
            result.Pixels[i + 1] = 255;
            result.Pixels[i + 2] = 255;
            result.Pixels[i + 3] = 255;
        }

        for (var y = 0; y < source.Height; y++)
        {
            var srcRow = y * source.Stride;
            var dstRow = ((y + padding) * result.Stride) + (padding * RgbaBitmap.BytesPerPixel);
            Array.Copy(source.Pixels, srcRow, result.Pixels, dstRow, source.Width * RgbaBitmap.BytesPerPixel);
        }

        return result;
    }

    public static RgbaBitmap UpscaleNearest(RgbaBitmap source, int factor)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(factor, 1);

        if (factor == 1)
        {
            return source;
        }

        var targetWidth = source.Width * factor;
        var targetHeight = source.Height * factor;
        var result = new RgbaBitmap(targetWidth, targetHeight);

        for (var ty = 0; ty < targetHeight; ty++)
        {
            var sy = ty / factor;
            var srcRow = sy * source.Stride;
            var dstRow = ty * result.Stride;
            for (var tx = 0; tx < targetWidth; tx++)
            {
                var sx = tx / factor;
                var s = srcRow + (sx * RgbaBitmap.BytesPerPixel);
                var d = dstRow + (tx * RgbaBitmap.BytesPerPixel);
                result.Pixels[d] = source.Pixels[s];
                result.Pixels[d + 1] = source.Pixels[s + 1];
                result.Pixels[d + 2] = source.Pixels[s + 2];
                result.Pixels[d + 3] = 255;
            }
        }

        return result;
    }

    /// <summary>双线性重采样到指定尺寸。</summary>
    public static RgbaBitmap Resize(RgbaBitmap source, int targetWidth, int targetHeight)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetHeight);

        var result = new RgbaBitmap(targetWidth, targetHeight);
        var scaleX = (double)source.Width / targetWidth;
        var scaleY = (double)source.Height / targetHeight;

        for (var ty = 0; ty < targetHeight; ty++)
        {
            // 目标像素中心映射回源坐标（-0.5 修正后取邻域）。
            var sy = ((ty + 0.5) * scaleY) - 0.5;
            if (sy < 0)
            {
                sy = 0;
            }

            var y0 = (int)Math.Floor(sy);
            var y1 = Math.Min(y0 + 1, source.Height - 1);
            var wy = sy - y0;
            if (y0 >= source.Height)
            {
                y0 = y1 = source.Height - 1;
                wy = 0;
            }

            var rowY0 = y0 * source.Stride;
            var rowY1 = y1 * source.Stride;
            var outRow = ty * result.Stride;

            for (var tx = 0; tx < targetWidth; tx++)
            {
                var sx = ((tx + 0.5) * scaleX) - 0.5;
                if (sx < 0)
                {
                    sx = 0;
                }

                var x0 = (int)Math.Floor(sx);
                var x1 = Math.Min(x0 + 1, source.Width - 1);
                var wx = sx - x0;
                if (x0 >= source.Width)
                {
                    x0 = x1 = source.Width - 1;
                    wx = 0;
                }

                var i00 = rowY0 + (x0 * RgbaBitmap.BytesPerPixel);
                var i10 = rowY0 + (x1 * RgbaBitmap.BytesPerPixel);
                var i01 = rowY1 + (x0 * RgbaBitmap.BytesPerPixel);
                var i11 = rowY1 + (x1 * RgbaBitmap.BytesPerPixel);
                var o = outRow + (tx * RgbaBitmap.BytesPerPixel);

                for (var c = 0; c < RgbaBitmap.BytesPerPixel; c++)
                {
                    var top = (source.Pixels[i00 + c] * (1 - wx)) + (source.Pixels[i10 + c] * wx);
                    var bottom = (source.Pixels[i01 + c] * (1 - wx)) + (source.Pixels[i11 + c] * wx);
                    var value = (top * (1 - wy)) + (bottom * wy);
                    result.Pixels[o + c] = (byte)Math.Clamp((int)Math.Round(value), 0, 255);
                }
            }
        }

        return result;
    }
}
