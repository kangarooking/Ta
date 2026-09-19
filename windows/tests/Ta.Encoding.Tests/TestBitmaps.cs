using Ta.Core.Imaging;

namespace Ta.Encoding.Tests;

/// <summary>
/// 构造像素值已知的测试位图。
///
/// 关键：**必须**包含 alpha &lt; 255 的像素。RgbaBitmap 是未预乘 RGBA，
/// 而 WIC 有一个名字极像的预乘格式（32bppPBGRA / 32bppPRGBA）。选错格式时
/// 全不透明的截图看起来毫无异常，只有半透明处会被压暗 —— 没有半透明测试像素，
/// 这个 bug 会一路漏到线上。
/// </summary>
internal static class TestBitmaps
{
    /// <summary>小图 + 手写逐像素期望值，用于精确断言。</summary>
    public static RgbaBitmap WithTransparency(int width = 8, int height = 6)
    {
        var bitmap = new RgbaBitmap(width, height);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                Set(bitmap, x, y, (byte)(x * 16), (byte)(y * 32), (byte)(x + y * 8), 255);
            }
        }

        // 半透明纯红 / 纯蓝 / 中灰：这几个像素在预乘路径下会被明显压暗。
        // 小尺寸用例（如 1×37）时部分固定坐标会超出位图范围，仅在范围内写入。
        TrySet(bitmap, width, height, 0, 0, 255, 0, 0, 128);
        TrySet(bitmap, width, height, 1, 0, 0, 0, 255, 64);
        TrySet(bitmap, width, height, 2, 0, 200, 200, 200, 96);
        TrySet(bitmap, width, height, 3, 0, 255, 255, 255, 0);
        TrySet(bitmap, width, height, width - 1, height - 1, 0, 0, 0, 255);

        return bitmap;
    }

    /// <summary>
    /// 平滑渐变、全不透明。JPEG 是有损格式，用平滑图像才能让容差断言有意义 ——
    /// 在硬色边界上 4:2:0 色度抽样本身就允许几十级偏差，容差会失去判别力。
    /// </summary>
    public static RgbaBitmap OpaqueGradient(int width = 32, int height = 24)
    {
        var bitmap = new RgbaBitmap(width, height);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var r = (byte)(x * 255 / Math.Max(1, width - 1));
                var g = (byte)(y * 255 / Math.Max(1, height - 1));
                var b = (byte)((x + y) * 255 / Math.Max(1, width + height - 2));
                Set(bitmap, x, y, r, g, b, 255);
            }
        }

        return bitmap;
    }

    private static void TrySet(RgbaBitmap bitmap, int width, int height, int x, int y, byte r, byte g, byte b, byte a)
    {
        if (x >= 0 && x < width && y >= 0 && y < height)
        {
            Set(bitmap, x, y, r, g, b, a);
        }
    }

    /// <summary>
    /// 确定性伪随机噪声图（LCG，固定种子）。噪声不可压缩，
    /// JPEG 高低质量的字节数差异会非常显著 —— 供「质量参数生效」类断言使用。
    /// </summary>
    public static RgbaBitmap Noise(int width = 96, int height = 96)
    {
        var bitmap = new RgbaBitmap(width, height);
        var pixels = bitmap.Pixels;
        var state = 0x12345678u;
        for (var i = 0; i < width * height; i++)
        {
            state = (state * 1664525u) + 1013904223u;
            pixels[i * 4] = (byte)(state >> 24);
            pixels[(i * 4) + 1] = (byte)(state >> 16);
            pixels[(i * 4) + 2] = (byte)(state >> 8);
            pixels[(i * 4) + 3] = 255;
        }

        return bitmap;
    }

    public static void Set(RgbaBitmap bitmap, int x, int y, byte r, byte g, byte b, byte a)
    {
        var i = (y * bitmap.Stride) + (x * RgbaBitmap.BytesPerPixel);
        bitmap.Pixels[i] = r;
        bitmap.Pixels[i + 1] = g;
        bitmap.Pixels[i + 2] = b;
        bitmap.Pixels[i + 3] = a;
    }

    public static (byte R, byte G, byte B, byte A) Get(byte[] rgba, int width, int x, int y)
    {
        var i = (y * width * 4) + (x * 4);
        return (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
    }
}
