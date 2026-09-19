using Ta.Core.Imaging;

namespace Ta.OCR.Imaging;

/// <summary>
/// RGBA 面积平均缩放。
///
/// 对应 Mac 版 <c>prepareOCRImage</c>（<c>AIScreenshotApp/Recognition/OptionalOCRPackManager.swift:716-736</c>）
/// 里那次 <c>CGContext.draw</c>：<c>interpolationQuality = .high</c>。
///
/// ⚠️ **已知偏差**：CG 的 <c>.high</c> 是窗口化滤波（下采样时接近 Lanczos），
/// 这里用面积平均（盒式）。二者在像素级不相等，但：
/// · 这一步只用于「最长边 > 2560 的超大截图喂给 PaddleOCR」，缩小倍数通常 ≥1.2，
///   面积平均在这个区间是抗锯齿最好且无振铃的选择；
/// · 项目里已有先例 —— <c>Ta.Core.Imaging.GrayscaleResampler</c> 就是用面积平均
///   对应 Mac 的 <c>.medium</c>；
/// · OCR 对该尺度的插值差异不敏感，且没有任何测试锁定这一步的像素输出。
/// </summary>
public static class RgbaDownscaler
{
    public static RgbaBitmap Downscale(RgbaBitmap source, int targetWidth, int targetHeight)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetHeight);

        var result = new RgbaBitmap(targetWidth, targetHeight);

        var scaleX = (double)source.Width / targetWidth;
        var scaleY = (double)source.Height / targetHeight;

        for (var ty = 0; ty < targetHeight; ty++)
        {
            // 与 GrayscaleResampler 同一套边界规则：至少覆盖一个源像素，避免除零与空洞。
            var sourceYStart = (int)(ty * scaleY);
            var sourceYEnd = Math.Max(sourceYStart + 1, (int)((ty + 1) * scaleY));

            for (var tx = 0; tx < targetWidth; tx++)
            {
                var sourceXStart = (int)(tx * scaleX);
                var sourceXEnd = Math.Max(sourceXStart + 1, (int)((tx + 1) * scaleX));

                long r = 0, g = 0, b = 0, a = 0, count = 0;

                for (var sy = sourceYStart; sy < sourceYEnd && sy < source.Height; sy++)
                {
                    var rowStart = sy * source.Stride;
                    for (var sx = sourceXStart; sx < sourceXEnd && sx < source.Width; sx++)
                    {
                        var i = rowStart + (sx * RgbaBitmap.BytesPerPixel);
                        r += source.Pixels[i];
                        g += source.Pixels[i + 1];
                        b += source.Pixels[i + 2];
                        a += source.Pixels[i + 3];
                        count++;
                    }
                }

                var o = (ty * result.Stride) + (tx * RgbaBitmap.BytesPerPixel);
                if (count == 0)
                {
                    result.Pixels[o] = 0;
                    result.Pixels[o + 1] = 0;
                    result.Pixels[o + 2] = 0;
                    result.Pixels[o + 3] = 0;
                }
                else
                {
                    result.Pixels[o] = (byte)(r / count);
                    result.Pixels[o + 1] = (byte)(g / count);
                    result.Pixels[o + 2] = (byte)(b / count);
                    result.Pixels[o + 3] = (byte)(a / count);
                }
            }
        }

        return result;
    }
}
