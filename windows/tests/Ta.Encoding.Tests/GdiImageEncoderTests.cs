using System.Drawing;
using Ta.Core.Imaging;
using Ta.Encoding;
using Xunit;

namespace Ta.Encoding.Tests;

/// <summary>
/// GDI 编码器的往返验证。
///
/// 这是之前 WIC 实现失败的那类测试 —— 也是唯一能证明「编码出的图不是全黑」的办法。
/// 之前手写 WIC COM 产出的 PNG 结构合法但像素全零，光查签名查不出来。
/// </summary>
public sealed class GdiImageEncoderTests
{
    private static RgbaBitmap Solid(int w, int h, byte r, byte g, byte b, byte a = 255)
    {
        var bitmap = new RgbaBitmap(w, h);
        bitmap.Fill(r, g, b, a);
        return bitmap;
    }

    /// <summary>独立解码基准：用系统解码器读回像素，再与源图逐字节比对。</summary>
    private static byte[] DecodeRgba(byte[] encoded, out int width, out int height)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ta-gdi-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(path, encoded);
            using var image = Image.FromFile(path);
            width = image.Width;
            height = image.Height;

            using var bmp = new Bitmap(image);
            var rgba = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var p = bmp.GetPixel(x, y);
                    var i = ((y * width) + x) * 4;
                    rgba[i] = p.R;
                    rgba[i + 1] = p.G;
                    rgba[i + 2] = p.B;
                    rgba[i + 3] = p.A;
                }
            }

            return rgba;
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // 忽略清理失败。
            }
        }
    }

    [Fact]
    public void PNG往返_像素逐字节一致()
    {
        using var source = Solid(8, 6, 255, 0, 0);
        var encoded = GdiImageEncoder.Instance.EncodePng(source);
        var decoded = DecodeRgba(encoded, out var w, out var h);

        Assert.Equal(source.Width, w);
        Assert.Equal(source.Height, h);
        Assert.Equal(source.Pixels, decoded);
    }

    [Fact]
    public void PNG往返_绿色也一致()
    {
        using var source = Solid(5, 5, 0, 200, 0);
        var encoded = GdiImageEncoder.Instance.EncodePng(source);
        var decoded = DecodeRgba(encoded, out _, out _);

        Assert.Equal(source.Pixels, decoded);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(37, 1)]
    [InlineData(1, 37)]
    [InlineData(3, 5)]
    public void PNG往返_极端长宽比也精确(int width, int height)
    {
        using var source = Solid(width, height, 12, 34, 56);
        var encoded = GdiImageEncoder.Instance.EncodePng(source);
        var decoded = DecodeRgba(encoded, out var w, out var h);

        Assert.Equal(width, w);
        Assert.Equal(height, h);
        Assert.Equal(source.Pixels, decoded);
    }

    [Fact]
    public void JPEG往返_像素在容差内()
    {
        using var source = Solid(16, 16, 200, 100, 50);
        var encoded = GdiImageEncoder.Instance.EncodeJpeg(source, 92);
        var decoded = DecodeRgba(encoded, out var w, out var h);

        Assert.Equal(source.Width, w);
        Assert.Equal(source.Height, h);

        // JPEG 有损，逐通道允许小幅偏差。
        var maxDelta = 0;
        for (var i = 0; i < source.Pixels.Length; i++)
        {
            maxDelta = Math.Max(maxDelta, Math.Abs(source.Pixels[i] - decoded[i]));
        }

        Assert.True(maxDelta <= 24, $"最大通道偏差 {maxDelta} 应不超过 24");
    }

    [Fact]
    public void 质量参数真的生效()
    {
        using var bitmap = Solid(64, 64, 120, 60, 30);

        var high = GdiImageEncoder.Instance.EncodeJpeg(bitmap, 95).Length;
        var low = GdiImageEncoder.Instance.EncodeJpeg(bitmap, 20).Length;

        Assert.True(low < high, $"高质量 {high} 字节应多于低质量 {low} 字节");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(100)]
    public void JPEG边界质量值可用(int quality)
    {
        using var bitmap = Solid(4, 4, 10, 20, 30);
        var encoded = GdiImageEncoder.Instance.EncodeJpeg(bitmap, quality);

        Assert.NotEmpty(encoded);
        // JPEG SOI 标记
        Assert.Equal(0xFF, encoded[0]);
        Assert.Equal(0xD8, encoded[1]);
    }

    [Fact]
    public void PNG签名正确()
    {
        using var bitmap = Solid(2, 2, 1, 2, 3);
        var encoded = GdiImageEncoder.Instance.EncodePng(bitmap);

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, encoded[..8]);
    }
}
