using Ta.Core.Imaging;

namespace Ta.Encoding.Tests;

/// <summary>
/// WIC 图像编码器测试。
///
/// 验收核心思路：**只断言「字节数 &gt; 0」是无效的** —— 编码器完全可能返回
/// 一段合法但内容错误的字节（比如通道顺序错了、或者 alpha 被预乘了）。
/// 因此每个用例都走「编码 → 用 WIC 独立解码回 RGBA → 逐像素比对」。
/// </summary>
public sealed class WicImageEncoderTests
{
    private readonly WicImageEncoder _encoder = new();

    // ---------------------------------------------------------------- PNG 无损

    [Fact]
    public void EncodePng_用WIC解回来_像素逐字节一致()
    {
        var source = TestBitmaps.WithTransparency();

        var encoded = _encoder.EncodePng(source);

        Assert.NotEmpty(encoded);

        var (width, height, decoded) = WicDecoder.DecodeToRgba(encoded);

        Assert.Equal(source.Width, width);
        Assert.Equal(source.Height, height);
        Assert.Equal(source.Pixels.Length, decoded.Length);

        // PNG 是无损的：全图必须逐字节一致，容差为 0。
        var firstMismatch = FindFirstMismatch(source.Pixels, decoded);
        Assert.True(
            firstMismatch < 0,
            $"PNG 无损往返在第 {firstMismatch} 字节出现偏差" +
            $"（源 {(firstMismatch < 0 ? 0 : source.Pixels[firstMismatch])}，" +
            $"解回 {(firstMismatch < 0 ? 0 : decoded[firstMismatch])}）。" +
            "通道顺序错或 alpha 被预乘都会在此暴露。");
    }

    [Fact]
    public void EncodePng_半透明像素_不被预乘压暗()
    {
        // 这一条单独拎出来：预乘会做 RGB *= A/255，
        // 全不透明的截图看不出来，只有半透明处会发灰。
        var source = TestBitmaps.WithTransparency();

        var encoded = _encoder.EncodePng(source);

        var (width, _, decoded) = WicDecoder.DecodeToRgba(encoded);

        var halfRed = TestBitmaps.Get(decoded, width, 0, 0);
        Assert.Equal((255, 0, 0, 128), halfRed);

        var quarterBlue = TestBitmaps.Get(decoded, width, 1, 0);
        Assert.Equal((0, 0, 255, 64), quarterBlue);

        var gray = TestBitmaps.Get(decoded, width, 2, 0);
        Assert.Equal((200, 200, 200, 96), gray);

        var transparentWhite = TestBitmaps.Get(decoded, width, 3, 0);
        Assert.Equal((255, 255, 255, 0), transparentWhite);
    }

    [Fact]
    public void EncodePng_容器格式确实是PNG()
    {
        var source = TestBitmaps.OpaqueGradient();

        var encoded = _encoder.EncodePng(source);

        Assert.Equal(WicConstants.ContainerFormatPng, WicDecoder.DetectContainerFormat(encoded));

        // PNG 签名 89 50 4E 47 0D 0A 1A 0A
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        Assert.True(encoded.AsSpan(0, 8).SequenceEqual(signature), "PNG 文件签名不正确。");
    }

    // -------------------------------------------------------------- JPEG 有损

    [Fact]
    public void EncodeJpeg_用WIC解回来_像素在容差内()
    {
        var source = TestBitmaps.OpaqueGradient();

        var encoded = _encoder.EncodeJpeg(source, WicImageEncoder.VisionModelJpegQuality);

        Assert.NotEmpty(encoded);

        var (width, height, decoded) = WicDecoder.DecodeToRgba(encoded);

        Assert.Equal(source.Width, width);
        Assert.Equal(source.Height, height);

        // 有损格式，允许小幅偏差：逐通道上限 + 均值上限。
        var (maxError, meanError) = MeasureError(source.Pixels, decoded);
        Assert.True(maxError <= 20, $"JPEG 单通道最大偏差 {maxError} 超过容差 20。");
        Assert.True(meanError < 6.0, $"JPEG 平均偏差 {meanError:F2} 超过容差 6.0。");
    }

    [Fact]
    public void EncodeJpeg_容器格式确实是JPEG()
    {
        var source = TestBitmaps.OpaqueGradient();

        var encoded = _encoder.EncodeJpeg(source, WicImageEncoder.ExportJpegQuality);

        Assert.Equal(WicConstants.ContainerFormatJpeg, WicDecoder.DetectContainerFormat(encoded));

        // JPEG SOI：FF D8 FF
        Assert.True(encoded.Length >= 3 && encoded[0] == 0xFF && encoded[1] == 0xD8 && encoded[2] == 0xFF,
            "JPEG 起始标记不正确。");
    }

    [Fact]
    public void EncodeJpeg_质量参数真的生效_而非被忽略()
    {
        // 这条专门盯属性包的写入时机：ImageQuality 必须在 frame.Initialize 之前写入，
        // 写晚了编码器会用默认质量 1.0 —— 那时高低质量的字节数会几乎一样。
        // 噪声图不可压缩：低质量丢高频细节、高质量几乎全保留，字节数差异显著。
        // 平滑渐变在两种质量下都压得很小，×2 阈值对它没有判别力。
        var source = TestBitmaps.Noise();

        var low = _encoder.EncodeJpeg(source, 10);
        var high = _encoder.EncodeJpeg(source, 95);

        Assert.True(high.Length > low.Length * 2,
            $"质量参数疑似未生效：quality=95 得到 {high.Length} 字节，" +
            $"quality=10 得到 {low.Length} 字节。");
    }

    [Fact]
    public void EncodeJpeg_质量越高质量越好_字节也越多()
    {
        var source = TestBitmaps.OpaqueGradient();

        var lengths = new[]
        {
            _encoder.EncodeJpeg(source, 20).Length,
            _encoder.EncodeJpeg(source, 50).Length,
            _encoder.EncodeJpeg(source, 80).Length,
        };

        Assert.True(lengths[0] < lengths[1], $"quality 20→50 未变长：{lengths[0]} vs {lengths[1]}。");
        Assert.True(lengths[1] < lengths[2], $"quality 50→80 未变长：{lengths[1]} vs {lengths[2]}。");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(100)]
    public void EncodeJpeg_边界质量值可用(int quality)
    {
        var source = TestBitmaps.OpaqueGradient();

        var encoded = _encoder.EncodeJpeg(source, quality);

        Assert.NotEmpty(encoded);
        var (width, height, _) = WicDecoder.DecodeToRgba(encoded);
        Assert.Equal(source.Width, width);
        Assert.Equal(source.Height, height);
    }

    // ------------------------------------------------- 文件名后缀分派（Mac:67）

    [Theory]
    [InlineData("shot.png", ImageFileFormat.Png)]
    [InlineData("shot.PNG", ImageFileFormat.Png)]
    [InlineData("shot.jpg", ImageFileFormat.Jpeg)]
    [InlineData("shot.jpeg", ImageFileFormat.Jpeg)]
    [InlineData("shot.JPG", ImageFileFormat.Jpeg)]
    [InlineData("shot.JPEG", ImageFileFormat.Jpeg)]
    [InlineData("shot.JpEg", ImageFileFormat.Jpeg)]
    [InlineData("shot", ImageFileFormat.Png)]
    [InlineData("shot.bmp", ImageFileFormat.Png)]
    [InlineData("shot.tiff", ImageFileFormat.Png)]
    [InlineData(@"C:\Users\me\AI-Screenshot-2026-09-17-10-20-30.png", ImageFileFormat.Png)]
    [InlineData(@"C:\Users\me\AI-Screenshot-2026-09-17-10-20-30.jpeg", ImageFileFormat.Jpeg)]
    public void ResolveFileFormat_只认jpg和jpeg_其余一律PNG(string fileName, ImageFileFormat expected)
    {
        Assert.Equal(expected, WicImageEncoder.ResolveFileFormat(fileName));
    }

    [Fact]
    public void EncodeForFile_按后缀走对应编码器()
    {
        var source = TestBitmaps.OpaqueGradient();

        var asPng = _encoder.EncodeForFile("shot.png", source);
        var asJpeg = _encoder.EncodeForFile("shot.JPEG", source);
        var unknownExtension = _encoder.EncodeForFile("shot.dat", source);

        Assert.Equal(WicConstants.ContainerFormatPng, WicDecoder.DetectContainerFormat(asPng));
        Assert.Equal(WicConstants.ContainerFormatJpeg, WicDecoder.DetectContainerFormat(asJpeg));

        // Mac 的「其余一律 PNG」：未知后缀不能报错，而是当 PNG 处理。
        Assert.Equal(WicConstants.ContainerFormatPng, WicDecoder.DetectContainerFormat(unknownExtension));
        Assert.Equal(asPng, unknownExtension);
    }

    // ------------------------------------------------------------- 保存到文件

    [Fact]
    public void Save_按后缀写文件_内容与内存编码一致()
    {
        var source = TestBitmaps.OpaqueGradient();
        var directory = CreateTempDirectory();

        try
        {
            var pngPath = Path.Combine(directory, "shot.png");
            _encoder.Save(source, pngPath);

            Assert.True(File.Exists(pngPath));
            Assert.Equal(_encoder.EncodeForFile("shot.png", source), File.ReadAllBytes(pngPath));

            var jpegPath = Path.Combine(directory, "shot.jpeg");
            _encoder.Save(source, jpegPath);

            Assert.True(File.Exists(jpegPath));
            Assert.Equal(_encoder.EncodeForFile("shot.jpeg", source), File.ReadAllBytes(jpegPath));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void Save_覆盖已存在文件_不残留临时文件()
    {
        var source = TestBitmaps.OpaqueGradient();
        var directory = CreateTempDirectory();

        try
        {
            var path = Path.Combine(directory, "overwrite.png");
            _encoder.Save(source, path);
            var first = File.ReadAllBytes(path);

            _encoder.Save(source, path);

            Assert.Equal(first, File.ReadAllBytes(path));

            // 原子写入用的是「临时文件 + 替换」；替换失败/成功后都不该留下 .ta-tmp-* 垃圾。
            Assert.Empty(Directory.GetFiles(directory, "*.ta-tmp-*"));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    // --------------------------------------------------------------- 尺寸边界

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 37)]
    [InlineData(37, 1)]
    [InlineData(3, 5)]
    public void Encode_极端长宽比_PNG往返仍精确(int width, int height)
    {
        var source = TestBitmaps.WithTransparency(width, height);

        var (decodedWidth, decodedHeight, decoded) =
            WicDecoder.DecodeToRgba(_encoder.EncodePng(source));

        Assert.Equal(width, decodedWidth);
        Assert.Equal(height, decodedHeight);
        Assert.Equal(source.Pixels, decoded);
    }

    // ------------------------------------------------------------------- 工具

    private static int FindFirstMismatch(byte[] expected, byte[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
            {
                return i;
            }
        }

        return -1;
    }

    private static (int Max, double Mean) MeasureError(byte[] expected, byte[] actual)
    {
        long total = 0;
        var max = 0;
        for (var i = 0; i < expected.Length; i++)
        {
            var delta = Math.Abs(expected[i] - actual[i]);
            total += delta;
            if (delta > max)
            {
                max = delta;
            }
        }

        return (max, (double)total / expected.Length);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ta-encoding-tests-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // 清理失败不影响断言结果。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }
}
