using Ta.Core.Imaging;
using Ta.Core.LongCapture;
using Xunit;

namespace Ta.Core.Tests.Imaging;

/// <summary>位图与合成测试。</summary>
public class RgbaBitmapTests
{
    [Fact]
    public void 尺寸决定像素数组长度()
    {
        var bitmap = new RgbaBitmap(4, 3);

        Assert.Equal(4, bitmap.Width);
        Assert.Equal(3, bitmap.Height);
        Assert.Equal(4 * 3 * 4, bitmap.Pixels.Length);
        Assert.Equal(16, bitmap.Stride);
    }

    [Fact]
    public void 非正尺寸被拒绝()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RgbaBitmap(0, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RgbaBitmap(4, -1));
    }

    [Fact]
    public void 填充写入全部像素()
    {
        var bitmap = new RgbaBitmap(3, 2);
        bitmap.Fill(10, 20, 30, 40);

        for (var i = 0; i < bitmap.Pixels.Length; i += 4)
        {
            Assert.Equal(10, bitmap.Pixels[i]);
            Assert.Equal(20, bitmap.Pixels[i + 1]);
            Assert.Equal(30, bitmap.Pixels[i + 2]);
            Assert.Equal(40, bitmap.Pixels[i + 3]);
        }
    }

    [Fact]
    public void 裁剪按区域取出并保持左上原点()
    {
        var bitmap = new RgbaBitmap(4, 4);
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var i = (y * bitmap.Stride) + (x * 4);
                bitmap.Pixels[i] = (byte)(y * 10 + x);   // 用行号编码便于断言
                bitmap.Pixels[i + 3] = 255;
            }
        }

        var cropped = bitmap.Crop(1, 2, 2, 2);

        Assert.Equal(2, cropped.Width);
        Assert.Equal(2, cropped.Height);
        // 左上角应为源图 (1,2) 处，即 2*10+1 = 21。
        Assert.Equal(21, cropped.Pixels[0]);
        Assert.Equal(22, cropped.Pixels[4]);
        Assert.Equal(31, cropped.Pixels[cropped.Stride]);
    }

    [Fact]
    public void 裁剪越界被拒绝()
    {
        var bitmap = new RgbaBitmap(4, 4);

        Assert.Throws<ArgumentOutOfRangeException>(() => bitmap.Crop(0, 0, 5, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => bitmap.Crop(-1, 0, 2, 2));
    }

    [Fact]
    public void 逐行复制保留行的内容与顺序()
    {
        var source = new RgbaBitmap(2, 3);
        for (var y = 0; y < 3; y++)
        {
            for (var x = 0; x < 2; x++)
            {
                var i = (y * source.Stride) + (x * 4);
                source.Pixels[i] = (byte)(y + 1);
                source.Pixels[i + 3] = 255;
            }
        }

        var destination = new RgbaBitmap(2, 3);
        destination.CopyRowFrom(source, 1, 2);

        Assert.Equal(2, destination.Pixels[2 * destination.Stride]);
    }
}

/// <summary>灰度重采样测试。</summary>
public class GrayscaleResamplerTests
{
    private static RgbaBitmap Solid(int width, int height, byte r, byte g, byte b)
    {
        var bitmap = new RgbaBitmap(width, height);
        bitmap.Fill(r, g, b);
        return bitmap;
    }

    [Fact]
    public void 纯色图采样后灰度值符合亮度加权()
    {
        // 纯白应得 255。
        var white = GrayscaleResampler.Sample(Solid(4, 4, 255, 255, 255), 2, 2);
        Assert.All(white.Pixels, p => Assert.Equal(255, p));
    }

    [Fact]
    public void 纯黑采样后为0()
    {
        var black = GrayscaleResampler.Sample(Solid(4, 4, 0, 0, 0), 2, 2);
        Assert.All(black.Pixels, p => Assert.Equal(0, p));
    }

    [Fact]
    public void 绿色比红色亮符合Rec709权重()
    {
        var red = GrayscaleResampler.Sample(Solid(4, 4, 255, 0, 0), 2, 2);
        var green = GrayscaleResampler.Sample(Solid(4, 4, 0, 255, 0), 2, 2);

        // 0.7152 > 0.2126
        Assert.True(green.Pixels[0] > red.Pixels[0],
            $"绿 {green.Pixels[0]} 应亮于红 {red.Pixels[0]}");
    }

    [Fact]
    public void 采样高度独立夹取不从宽度推导()
    {
        // ⚠️ 这是 Mac 版刻意保留的不对称性：宽 2× 的帧若从宽度推导高度，
        // 会因采样过矮而察觉不到小幅滚动。
        var bitmap = Solid(200, 1000, 128, 128, 128);
        var sample = GrayscaleResampler.Sample(bitmap, 96, 720);

        Assert.Equal(96, sample.Width);
        Assert.Equal(720, sample.Height);
    }

    [Fact]
    public void 源比目标小时不放大()
    {
        var sample = GrayscaleResampler.Sample(Solid(10, 20, 200, 200, 200), 96, 720);

        // 应与源尺寸相同 —— 与 Mac: min(image.height, sampleHeight) 一致。
        Assert.Equal(10, sample.Width);
        Assert.Equal(20, sample.Height);
    }

    [Fact]
    public void 面积平均会平滑条纹()
    {
        // 左右半黑半白的图，降采样后应接近中灰。
        var bitmap = new RgbaBitmap(4, 4);
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var i = (y * bitmap.Stride) + (x * 4);
                var value = x < 2 ? (byte)0 : (byte)255;
                bitmap.Pixels[i] = value;
                bitmap.Pixels[i + 1] = value;
                bitmap.Pixels[i + 2] = value;
                bitmap.Pixels[i + 3] = 255;
            }
        }

        var sample = GrayscaleResampler.Sample(bitmap, 1, 1);

        Assert.InRange(sample.Pixels[0], 120, 136);   // 期望约 128
    }
}

/// <summary>纵向合成测试。</summary>
public class VerticalCompositorTests
{
    private static RgbaBitmap RowBand(int width, int height, byte fill)
    {
        var bitmap = new RgbaBitmap(width, height);
        bitmap.Fill(fill, fill, fill);
        return bitmap;
    }

    [Fact]
    public void 拼接后的高度等于各条带之和()
    {
        var strips = new List<(RgbaBitmap, int, int)>
        {
            (RowBand(4, 3, 10), 0, 3),
            (RowBand(4, 5, 20), 0, 5),
        };

        var result = VerticalCompositor.Compose(strips);

        Assert.Equal(8, result.Height);
        Assert.Equal(4, result.Width);
    }

    [Fact]
    public void 拼接顺序保持条带内容()
    {
        var strips = new List<(RgbaBitmap, int, int)>
        {
            (RowBand(2, 2, 10), 0, 2),
            (RowBand(2, 2, 200), 0, 2),
        };

        var result = VerticalCompositor.Compose(strips);

        // 第一段应为 10，第二段应为 200。
        Assert.Equal(10, result.Pixels[0]);
        Assert.Equal(200, result.Pixels[2 * result.Stride]);
    }

    [Fact]
    public void 只取条带的一部分进行拼接()
    {
        var band = RowBand(2, 10, 77);
        var strips = new List<(RgbaBitmap, int, int)> { (band, 3, 4) };

        var result = VerticalCompositor.Compose(strips);

        Assert.Equal(4, result.Height);
    }

    [Fact]
    public void 宽度不一致的条带无法拼接()
    {
        var strips = new List<(RgbaBitmap, int, int)>
        {
            (RowBand(4, 2, 10), 0, 2),
            (RowBand(8, 2, 20), 0, 2),
        };

        Assert.Throws<ArgumentException>(() => VerticalCompositor.Compose(strips));
    }

    [Fact]
    public void 空条带列表被拒绝()
    {
        Assert.Throws<ArgumentException>(() => VerticalCompositor.Compose([]));
    }

    [Fact]
    public void 条带越界被拒绝()
    {
        var strips = new List<(RgbaBitmap, int, int)> { (RowBand(4, 2, 10), 0, 5) };

        Assert.Throws<ArgumentOutOfRangeException>(() => VerticalCompositor.Compose(strips));
    }

    [Fact]
    public void 区间合成只生成请求的部分()
    {
        // 关键：取一小段不应合成整张图。
        var strips = new List<(RgbaBitmap, int, int)>
        {
            (RowBand(4, 100, 50), 0, 100),
            (RowBand(4, 100, 150), 0, 100),
        };

        var part = VerticalCompositor.ComposeRange(strips, 50, 20);

        Assert.Equal(20, part.Height);
        // 前 50 行属于第一条带（值 50），故本段应全为 50。
        Assert.Equal(50, part.Pixels[0]);
    }

    [Fact]
    public void 区间合成可跨越条带边界()
    {
        var strips = new List<(RgbaBitmap, int, int)>
        {
            (RowBand(2, 4, 10), 0, 4),
            (RowBand(2, 4, 200), 0, 4),
        };

        // 请求跨越接缝的区间。
        var part = VerticalCompositor.ComposeRange(strips, 2, 4);

        Assert.Equal(4, part.Height);
        Assert.Equal(10, part.Pixels[0]);                    // 第一条带尾部
        Assert.Equal(200, part.Pixels[2 * part.Stride]);     // 第二条带开头
    }
}

/// <summary>位图长截图端到端测试 —— 验证记账与像素合成已接上。</summary>
public class BitmapLongCaptureTests
{
    /// <summary>构造一张宽 96、高足够的"世界图"，再按偏移裁出帧。</summary>
    private static RgbaBitmap MakeFrame(int offset, int width = 96, int height = 160)
    {
        // 用确定性图案，使匹配器能稳定找到位移。
        var bitmap = new RgbaBitmap(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var documentY = y + offset;
                var value = (byte)((x * 17 + documentY * 31 + (documentY / 3) * 47 + (x * documentY) % 83) % 256);
                var i = (y * bitmap.Stride) + (x * 4);
                bitmap.Pixels[i] = value;
                bitmap.Pixels[i + 1] = value;
                bitmap.Pixels[i + 2] = value;
                bitmap.Pixels[i + 3] = 255;
            }
        }

        return bitmap;
    }

    [Fact]
    public void 多帧滚动后合成出累加高度的长图()
    {
        var capture = new BitmapLongCapture();

        capture.Append(MakeFrame(0));
        var second = capture.Append(MakeFrame(40));

        Assert.Equal(FrameDisposition.Appended, second);

        var composed = capture.Compose();

        Assert.Equal(160 + 40, composed.Height);
        Assert.Equal(96, composed.Width);
    }

    [Fact]
    public void 合成结果的顶端等于首帧顶端()
    {
        var capture = new BitmapLongCapture();
        var first = MakeFrame(0);
        capture.Append(first);
        capture.Append(MakeFrame(40));

        var composed = capture.Compose();

        // 首帧首行应原样出现在合成图顶部（无固定区域时）。
        Assert.Equal(first.Pixels[0], composed.Pixels[0]);
        Assert.Equal(first.Pixels[1], composed.Pixels[1]);
        Assert.Equal(first.Pixels[2], composed.Pixels[2]);
    }

    [Fact]
    public void 分段合成的各段拼起来等于整图高度()
    {
        var capture = new BitmapLongCapture();
        capture.Append(MakeFrame(0));
        capture.AppendFallback(MakeFrame(200), 300);

        var full = capture.Compose();
        var parts = capture.ComposeParts(maximumPixelHeight: 100);

        Assert.Equal(full.Height, parts.Sum(p => p.Height));
        Assert.True(parts.Count > 1, "应产生多段");
    }

    [Fact]
    public void 单帧时合成结果就是该帧本身()
    {
        var capture = new BitmapLongCapture();
        var first = MakeFrame(0);
        capture.Append(first);

        var composed = capture.Compose();

        Assert.Equal(first.Height, composed.Height);
        Assert.Equal(first.Pixels, composed.Pixels);
    }
}
