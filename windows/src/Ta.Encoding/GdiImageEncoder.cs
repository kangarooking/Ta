using Ta.Core.Imaging;

namespace Ta.Encoding;

/// <summary>
/// 图像编码器 —— <see cref="IImageEncoder"/> 的实现。
///
/// ## 为什么是 System.Drawing 而不是手写 WIC COM
///
/// 原实现手写了整套 WIC COM 声明（WicInterop.cs）。它有三个 vtable 缺陷：
///   · IWICBitmapSource 多声明了 CopyPalette（属 IWICPalette）
///   · IWICFormatConverter 漏声明 Convert
///   · IWicImagingFactory 多了一个**不存在**的 CreateDecoderFromFileHandle，
///     使其后所有槽位错位 +1
/// 最后一个导致「编码器」实际是格式转换器，产出的 PNG 结构合法但**像素全黑** ——
/// 截图会存成黑图。且这类缺陷编译器不报、只能靠往返测试抓。
///
/// 选择 System.Drawing 的理由：
///   · 它在本机**已实测可用**（同一会话里成功解码出正确的 4×4 Argb）
///   · 一条调用即完成编码，没有 vtable 需要维护
///   · 先把功能跑通，比追求架构纯净更重要
///
/// 代价与后续：GDI+ 性能不如 WIC/Direct2D，且已停止演进。
/// 480 MB 长截图场景下应改用 ImageSharp（纯托管）或 Direct2D + WIC。
/// 属已知待优化项，**不影响当前功能正确性**。
/// </summary>
public sealed class GdiImageEncoder : IImageEncoder
{
    public static readonly GdiImageEncoder Instance = new();

    public byte[] EncodePng(RgbaBitmap bitmap)
    {
        using var gdi = ToBitmap(bitmap);
        using var stream = new MemoryStream();
        gdi.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }

    public byte[] EncodeJpeg(RgbaBitmap bitmap, int quality)
    {
        using var gdi = ToBitmap(bitmap);

        var encoder = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
            .First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);

        using var parameters = new System.Drawing.Imaging.EncoderParameters(1);
        parameters.Param[0] = new System.Drawing.Imaging.EncoderParameter(
            System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(quality, 0, 100));

        using var stream = new MemoryStream();
        gdi.Save(stream, encoder, parameters);
        return stream.ToArray();
    }

    /// <summary>
    /// RgbaBitmap（RGBA，未预乘）→ GDI+ Bitmap。
    ///
    /// ⚠️ GDI+ 的 Format32bppArgb 在内存里是 **B,G,R,A**，不是 R,G,B,A。
    /// 直接整块拷贝会让 R 与 B 对调（往返测试会抓到：期望 [12,34,56,255]、
    /// 实际 [56,34,12,255]）。所以这里显式做通道交换。
    /// </summary>
    private static System.Drawing.Bitmap ToBitmap(RgbaBitmap source)
    {
        var bitmap = new System.Drawing.Bitmap(source.Width, source.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        // 先换成 GDI 要的 B,G,R,A 顺序，再整块拷。
        var bgra = new byte[source.Pixels.Length];
        for (var i = 0; i < source.Pixels.Length; i += 4)
        {
            bgra[i] = source.Pixels[i + 2];
            bgra[i + 1] = source.Pixels[i + 1];
            bgra[i + 2] = source.Pixels[i];
            bgra[i + 3] = source.Pixels[i + 3];
        }

        var data = bitmap.LockBits(
            new System.Drawing.Rectangle(0, 0, source.Width, source.Height),
            System.Drawing.Imaging.ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        try
        {
            System.Runtime.InteropServices.Marshal.Copy(bgra, 0, data.Scan0, bgra.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }
}
