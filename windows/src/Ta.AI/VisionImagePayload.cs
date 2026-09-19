using Ta.Core.Imaging;

namespace Ta.AI;

/// <summary>
/// 送进 Provider 前的图像规格。
///
/// 对应参考文档 §9.3「图像编码规格」：
/// | 调用点 | 格式 | 质量 | 最长边 | mimeType |
/// | 多模态识图 | JPEG | 0.88 | 2048 | image/jpeg |
/// | 连接测试（识图） | JPEG | 0.88 | 1024 | image/jpeg |
/// | 翻译视觉 / DeepSeek-OCR-2 / OCR 增强包 | PNG | 无损 | 2560 | image/png |
///
/// 只有像素尺寸上限，**没有字节大小上限**。
/// 实际编码交给平台层的 <see cref="IImageEncoder"/>（WIC），本类只负责
/// 「降采样到最长边」这一与协议无关的预处理。
/// </summary>
public static class VisionImagePayload
{
    /// <summary>§9.3：多模态识图，JPEG 0.88，最长边 2048。</summary>
    public const int VisionMaxDimension = 2048;

    /// <summary>§9.3：识图连接测试，JPEG 0.88，最长边 1024。</summary>
    public const int ConnectionTestMaxDimension = 1024;

    /// <summary>§9.3：翻译视觉 / OCR-2 / 增强包，PNG 无损，最长边 2560。</summary>
    public const int PngMaxDimension = 2560;

    /// <summary>§9.3：JPEG compressionFactor 0.88 → 88。</summary>
    public const int JpegQuality = 88;

    /// <summary>送视觉模型的 JPEG 字节（<paramref name="maximumDimension"/> 默认 2048）。</summary>
    public static byte[] EncodeJpeg(RgbaBitmap bitmap, IImageEncoder encoder, int maximumDimension = VisionMaxDimension)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(encoder);

        using var scaled = Downscale(bitmap, maximumDimension);
        return encoder.EncodeJpeg(scaled, JpegQuality);
    }

    /// <summary>送视觉模型连接测试的 JPEG 字节（最长边 1024）。</summary>
    public static byte[] EncodeConnectionTestJpeg(RgbaBitmap bitmap, IImageEncoder encoder)
        => EncodeJpeg(bitmap, encoder, ConnectionTestMaxDimension);

    /// <summary>送翻译 / OCR-2 的 PNG 字节（无损，最长边 2560）。</summary>
    public static byte[] EncodePng(RgbaBitmap bitmap, IImageEncoder encoder, int maximumDimension = PngMaxDimension)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(encoder);

        using var scaled = Downscale(bitmap, maximumDimension);
        return encoder.EncodePng(scaled);
    }

    /// <summary>
    /// 缩放到最长边不超过 <paramref name="maximumDimension"/>。
    ///
    /// Mac: <c>MultimodalRecognitionService.encodeJPEG(_:maximumDimension:)</c>（:101-105）——
    /// <c>scale = min(1, maxDim / max(w, h))</c>，随后 <c>.rounded()</c>。这里用最近邻采样
    /// （Mac 走 AppKit 绘制，质量更高，但**尺寸**完全一致，协议层面无差异）。
    /// </summary>
    private static RgbaBitmap Downscale(RgbaBitmap source, int maximumDimension)
    {
        var longestEdge = Math.Max(source.Width, source.Height);
        if (longestEdge <= maximumDimension)
        {
            // 不需要缩放：拷一份，保证调用方拿到的是可独立释放的位图。
            var copy = new RgbaBitmap(source.Width, source.Height);
            Array.Copy(source.Pixels, copy.Pixels, source.Pixels.Length);
            return copy;
        }

        var scale = Math.Min(1d, maximumDimension / (double)longestEdge);
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        var target = new RgbaBitmap(width, height);
        for (var y = 0; y < height; y++)
        {
            var sourceY = (int)Math.Min(source.Height - 1, (long)y * source.Height / height);
            for (var x = 0; x < width; x++)
            {
                var sourceX = (int)Math.Min(source.Width - 1, (long)x * source.Width / width);
                var from = (sourceY * source.Stride) + (sourceX * RgbaBitmap.BytesPerPixel);
                var to = (y * target.Stride) + (x * RgbaBitmap.BytesPerPixel);
                Array.Copy(source.Pixels, from, target.Pixels, to, RgbaBitmap.BytesPerPixel);
            }
        }

        return target;
    }
}
