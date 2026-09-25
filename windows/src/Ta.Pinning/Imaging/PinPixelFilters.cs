using Ta.Core.Imaging;

namespace Ta.Pinning.Imaging;

/// <summary>
/// 钉图的像素滤镜：灰度 / 反色。
///
/// 对应 Mac 版 <c>PinnedImageView.displayedImage</c> 的两个分支
/// （PinnedImageWindowController.swift:595-618）：
///   · 灰度 → CIFilter "CIPhotoEffectMono"（:600）
///   · 反色 → CIFilter "CIColorInvert"（:602）
///
/// 纯字节数组运算，不依赖 CoreImage / WIC / System.Drawing。
/// 与 Mac 的差异（如实记录）：
///   · CIPhotoEffectMono 是「胶片黑白」，带轻微色调曲线；这里用 Rec.709 亮度，
///     与本仓库 <c>GrayscaleResampler</c> 的系数一致，保证跨组件一致而非逐像素复刻滤镜。
///   · CIColorInvert 反 RGB 不动 Alpha —— 这里同样不动 Alpha。
/// </summary>
public static class PinPixelFilters
{
    public const int BytesPerPixel = RgbaBitmap.BytesPerPixel;

    /// <summary>就地应用滤镜。像素格式为 RGBA、未预乘，与 <see cref="RgbaBitmap"/> 一致。</summary>
    public static void ApplyInPlace(Span<byte> pixels, PinFilterMode mode)
    {
        switch (mode)
        {
            case PinFilterMode.None:
                return;

            case PinFilterMode.Grayscale:
                for (var i = 0; i + 3 < pixels.Length; i += BytesPerPixel)
                {
                    // Rec.709：2126/7152/722，与 GrayscaleResampler 同系数。
                    var luminance = (2126 * pixels[i]) + (7152 * pixels[i + 1]) + (722 * pixels[i + 2]);
                    var gray = (byte)(luminance / 10_000);
                    pixels[i] = gray;
                    pixels[i + 1] = gray;
                    pixels[i + 2] = gray;
                }

                return;

            case PinFilterMode.Inverted:
                for (var i = 0; i + 3 < pixels.Length; i += BytesPerPixel)
                {
                    pixels[i] = (byte)(255 - pixels[i]);
                    pixels[i + 1] = (byte)(255 - pixels[i + 1]);
                    pixels[i + 2] = (byte)(255 - pixels[i + 2]);
                }

                return;
        }
    }

    /// <summary>返回应用滤镜后的新缓冲（原缓冲不变），供单测逐值断言。</summary>
    public static byte[] Apply(ReadOnlySpan<byte> pixels, PinFilterMode mode)
    {
        var copy = pixels.ToArray();
        ApplyInPlace(copy, mode);
        return copy;
    }

    /// <summary>对整张位图就地应用滤镜。</summary>
    public static void ApplyInPlace(RgbaBitmap bitmap, PinFilterMode mode) =>
        ApplyInPlace(bitmap.Pixels.AsSpan(), mode);

    /// <summary>对整张位图返回滤镜后的副本。</summary>
    public static RgbaBitmap Apply(RgbaBitmap bitmap, PinFilterMode mode)
    {
        if (mode == PinFilterMode.None)
        {
            return bitmap;
        }

        var copy = new RgbaBitmap(bitmap.Width, bitmap.Height);
        Array.Copy(bitmap.Pixels, copy.Pixels, bitmap.Pixels.Length);
        ApplyInPlace(copy.Pixels.AsSpan(), mode);
        return copy;
    }
}
