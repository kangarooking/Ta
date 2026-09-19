namespace Ta.Core.Imaging;

/// <summary>
/// 内存 RGBA 位图。
///
/// 字节序与 Mac 版 CGImage 的 <c>premultipliedLast</c> 一致：每像素 4 字节，
/// 顺序 R,G,B,A。**未预乘** —— 截图输入本身不透明，预乘只会让合成结果失真。
///
/// 提供可复用缓冲而非每次新建：长截图输出可达 120,000,000 像素
/// （单张 RGBA 约 480 MB），反复分配会砸 LOH 并触发 GC（见参考文档 §15）。
/// </summary>
public sealed class RgbaBitmap : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public const int BytesPerPixel = 4;

    public RgbaBitmap(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Width = width;
        Height = height;
        Pixels = new byte[(long)width * height * BytesPerPixel];
    }

    private RgbaBitmap(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Stride => Width * BytesPerPixel;

    /// <summary>填充整个位图。</summary>
    public void Fill(byte r, byte g, byte b, byte a = 255)
    {
        for (var y = 0; y < Height; y++)
        {
            var rowStart = y * Stride;
            for (var x = 0; x < Width; x++)
            {
                var i = rowStart + (x * BytesPerPixel);
                Pixels[i] = r;
                Pixels[i + 1] = g;
                Pixels[i + 2] = b;
                Pixels[i + 3] = a;
            }
        }
    }

    /// <summary>把源位图的一行复制到本图的指定行。用于纵向拼接。</summary>
    public void CopyRowFrom(RgbaBitmap source, int sourceY, int destinationY)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceY);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(sourceY, source.Height);
        ArgumentOutOfRangeException.ThrowIfNegative(destinationY);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(destinationY, Height);

        if (source.Width != Width)
        {
            throw new ArgumentException("宽度必须一致才能逐行复制。", nameof(source));
        }

        Array.Copy(source.Pixels, sourceY * source.Stride, Pixels, destinationY * Stride, Stride);
    }

    /// <summary>从本图裁出子矩形。</summary>
    public RgbaBitmap Crop(int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || width <= 0 || height <= 0
            || x + width > Width || y + height > Height)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "裁剪区域越界。");
        }

        var result = new RgbaBitmap(width, height);
        for (var row = 0; row < height; row++)
        {
            Array.Copy(Pixels, ((y + row) * Stride) + (x * BytesPerPixel),
                result.Pixels, row * result.Stride, result.Stride);
        }

        return result;
    }

    public void Dispose()
    {
        // 位图数据由 GC 回收；此处保留以便未来切换为非托管缓冲时保持调用点不变。
    }

    /// <summary>灰度采样帧。用于把位图喂给长截图匹配器。</summary>
    public LongCapture.GrayscaleFrame ToGrayscale(int sampleWidth, int sampleHeight)
    {
        var gray = GrayscaleResampler.Sample(this, sampleWidth, sampleHeight);
        return new LongCapture.GrayscaleFrame(gray.Width, gray.Height, gray.Pixels);
    }
}

/// <summary>
/// 面积平均降采样。
///
/// 对应 Mac 版把 CGImage 画进灰度 CGContext、interpolationQuality = .medium
/// 的行为 —— 中等质量插值本质上就是盒式滤波，这里用面积平均显式实现，
/// 结果确定、可测，且不依赖任何图形库。
/// </summary>
public static class GrayscaleResampler
{
    /// <summary>
    /// 按 Rec.709 亮度加权转灰度，再面积平均缩放到目标尺寸。
    /// 目标高度会夹到不超过源高度 —— 与 Mac: min(image.height, sampleHeight) 一致。
    /// </summary>
    public static (int Width, int Height, byte[] Pixels) Sample(RgbaBitmap source, int targetWidth, int targetHeight)
    {
        ArgumentNullException.ThrowIfNull(source);

        var width = Math.Max(1, Math.Min(targetWidth, source.Width));
        // 关键：高度独立夹取，不从宽度推导 —— 与 Mac 一致（见参考文档 §8.2）。
        var height = Math.Max(1, Math.Min(targetHeight, source.Height));

        var pixels = new byte[width * height];

        // 每个目标像素对应的源区域尺寸。
        var scaleX = (double)source.Width / width;
        var scaleY = (double)source.Height / height;

        for (var ty = 0; ty < height; ty++)
        {
            var sourceYStart = (int)(ty * scaleY);
            var sourceYEnd = Math.Max(sourceYStart + 1, (int)((ty + 1) * scaleY));

            for (var tx = 0; tx < width; tx++)
            {
                var sourceXStart = (int)(tx * scaleX);
                var sourceXEnd = Math.Max(sourceXStart + 1, (int)((tx + 1) * scaleX));

                long luminanceSum = 0;
                long count = 0;

                for (var sy = sourceYStart; sy < sourceYEnd && sy < source.Height; sy++)
                {
                    var rowStart = sy * source.Stride;
                    for (var sx = sourceXStart; sx < sourceXEnd && sx < source.Width; sx++)
                    {
                        var i = rowStart + (sx * RgbaBitmap.BytesPerPixel);
                        var r = source.Pixels[i];
                        var g = source.Pixels[i + 1];
                        var b = source.Pixels[i + 2];

                        // Rec.709 —— 与 Mac 的 contrastingTextColor 用同一组系数。
                        luminanceSum += (2126L * r) + (7152L * g) + (722L * b);
                        count++;
                    }
                }

                pixels[(ty * width) + tx] = count == 0
                    ? (byte)0
                    : (byte)(luminanceSum / (count * 10_000));
            }
        }

        return (width, height, pixels);
    }
}

/// <summary>
/// 纵向条带合成器。
///
/// 对应 Mac 版 ScrollingImageStitcher.render(strips:startY:height:)：
/// 把若干条带按顺序纵向拼成一张图。
///
/// 差异说明：Mac 用 CGContext 绘制并做 Y 翻转（CG 原点在左下），
/// 本实现全部在**左上原点**的字节数组上操作，与 Windows 屏幕坐标约定一致，
/// 因此不需要翻转（详见参考文档 §5.1 关于 Y 翻转移除的说明）。
/// </summary>
public static class VerticalCompositor
{
    /// <summary>
    /// 把所有条带按顺序纵向拼接。
    /// 条带宽度必须一致，否则无法拼接。
    /// </summary>
    public static RgbaBitmap Compose(IReadOnlyList<(RgbaBitmap Frame, int SourceY, int Height)> strips)
    {
        ArgumentNullException.ThrowIfNull(strips);

        if (strips.Count == 0)
        {
            throw new ArgumentException("至少需要一条条带。", nameof(strips));
        }

        var width = strips[0].Frame.Width;
        if (strips.Any(s => s.Frame.Width != width))
        {
            throw new ArgumentException("所有条带宽度必须一致。", nameof(strips));
        }

        var totalHeight = strips.Sum(s => s.Height);
        var result = new RgbaBitmap(width, totalHeight);

        var destinationY = 0;
        foreach (var (frame, sourceY, height) in strips)
        {
            if (sourceY < 0 || height <= 0 || sourceY + height > frame.Height)
            {
                throw new ArgumentOutOfRangeException(nameof(strips), "条带区域越界。");
            }

            for (var row = 0; row < height; row++)
            {
                result.CopyRowFrom(frame, sourceY + row, destinationY + row);
            }

            destinationY += height;
        }

        return result;
    }

    /// <summary>
    /// 只合成请求的纵向区间 [startY, startY+height)。
    /// 用于超长输出的分段导出 —— 避免为了取一段而合成整张 480 MB 的图。
    /// </summary>
    public static RgbaBitmap ComposeRange(
        IReadOnlyList<(RgbaBitmap Frame, int SourceY, int Height)> strips,
        int startY,
        int height)
    {
        ArgumentNullException.ThrowIfNull(strips);

        if (strips.Count == 0 || height <= 0)
        {
            throw new ArgumentException("条带与高度必须有效。");
        }

        var width = strips[0].Frame.Width;
        var result = new RgbaBitmap(width, height);

        var requestedEnd = startY + height;
        var globalY = 0;

        foreach (var (frame, sourceY, stripHeight) in strips)
        {
            var stripStart = globalY;
            var stripEnd = globalY + stripHeight;

            var lower = Math.Max(startY, stripStart);
            var upper = Math.Min(requestedEnd, stripEnd);

            if (lower < upper)
            {
                var offset = lower - stripStart;
                for (var row = 0; row < upper - lower; row++)
                {
                    result.CopyRowFrom(frame, sourceY + offset + row, lower - startY + row);
                }
            }

            globalY = stripEnd;
        }

        return result;
    }
}
