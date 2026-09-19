using Ta.Core.Imaging;

namespace Ta.Translate.Rendering;

/// <summary>
/// 一个 GDI 字体实例 + 度量缓存 + 单行栅格化。
///
/// 对应 Mac 的 NSFont 对象（systemFont(ofSize:weight:)）：
/// · <see cref="Ascent"/> / <see cref="LineHeight"/> 对应
///   <c>boundingRect(options: [.usesFontLeading])</c> 得到的行高语义 ——
///   Mac 的 usesFontLeading 使用字体的 ascent+descent+leading；GDI 侧用
///   <c>tmHeight + tmExternalLeading</c> 近似（tmHeight = ascent+descent）。
/// · <see cref="MeasureWidth"/> 对应 <c>NSString.boundingRect</c> 的单行宽度度量。
/// · <see cref="DrawLine"/> 对应 <c>NSString.draw</c>。
///
/// ⚠️ 非线程安全：GDI DC 句柄可重入使用会互相踩状态。渲染器在同一线程内串行调用，
/// 因此不做加锁。
/// </summary>
internal sealed class GdiFont : IDisposable
{
    private readonly IntPtr _handle;
    private bool _disposed;

    public string Face { get; }

    public int Weight { get; }

    /// <summary>Mac 的 NSFont.pointSize（在无缩放的位图上下文里 = 像素高度）。</summary>
    public double PointSize { get; }

    /// <summary>tmAscent —— 基线到行顶的距离。</summary>
    public double Ascent { get; }

    /// <summary>tmHeight + tmExternalLeading —— 行步进（对应 usesFontLeading 的行高）。</summary>
    public double LineHeight { get; }

    internal GdiFont(string face, int weight, double pointSize)
    {
        Face = face;
        Weight = weight;
        PointSize = pointSize;

        var height = -(int)Math.Round(pointSize, MidpointRounding.AwayFromZero);
        var logFont = new NativeMethods.LOGFONTW
        {
            lfHeight = height,

            // 负值表示按「字符高度」（em 尺寸）匹配 —— 与 NSFont.pointSize 语义最接近。
            lfWeight = weight,
            lfQuality = NativeMethods.ANTIALIASED_QUALITY,
            lfCharSet = NativeMethods.DEFAULT_CHARSET,
            lfFaceName = face,
        };

        _handle = NativeMethods.CreateFontIndirectW(ref logFont);
        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"创建字体失败：{face} {weight} {pointSize}pt。");
        }

        // 在临时 DC 上取度量后立即还原（DC 只是度量载体，字体句柄本身继续存活）。
        var dc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_handle);
            _handle = IntPtr.Zero;
            throw new InvalidOperationException("创建兼容 DC 失败，无法度量文字。");
        }

        try
        {
            var previous = NativeMethods.SelectObject(dc, _handle);
            NativeMethods.GetTextMetricsW(dc, out var metrics);
            NativeMethods.SelectObject(dc, previous);

            Ascent = metrics.tmAscent;
            LineHeight = metrics.tmHeight + metrics.tmExternalLeading;
            if (LineHeight <= 0)
            {
                LineHeight = Math.Max(1, pointSize);
            }
        }
        finally
        {
            NativeMethods.DeleteDC(dc);
        }
    }

    /// <summary>测量单行宽度（像素）。对应 Mac: boundingRect(with:…) 的单行宽度。</summary>
    public double MeasureWidth(GdiTextEngine engine, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return engine.MeasureWith(_handle, text);
    }

    /// <summary>
    /// 把一行文字以给定基线 y 画进位图（灰度抗锯齿遮罩 + alpha 混合）。
    /// 对应 Mac: NSString.draw（单行，左对齐）。
    /// </summary>
    public void DrawLine(RgbaBitmap target, string text, int x, int baselineY, RgbaColor color)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var widthPx = Math.Max(1, (int)Math.Ceiling(MeasureWidthText(text)) + 4);
        var heightPx = Math.Max(1, (int)Math.Ceiling(LineHeight) + 4);

        var surface = GdiMaskSurface.Create(widthPx, heightPx);
        try
        {
            surface.ClearWhite();

            // 文字画在遮罩原点 + (2, 2+Ascent)：留 2px 边距容纳上升部/下降部。
            const int originX = 2;
            var originY = 2 + (int)Math.Ceiling(Ascent);

            var previous = NativeMethods.SelectObject(surface.Dc, _handle);
            NativeMethods.SetTextColor(surface.Dc, 0x00000000u);
            NativeMethods.SetBkMode(surface.Dc, NativeMethods.TRANSPARENT);
            NativeMethods.TextOutW(surface.Dc, originX, originY, text, text.Length);
            NativeMethods.SelectObject(surface.Dc, previous);

            // 遮罩像素 (mx,my) → 目标像素 (x + mx - originX, baselineY + my - originY)
            surface.BlendInto(target, x - originX, baselineY - originY, color);
        }
        finally
        {
            surface.Dispose();
        }
    }

    /// <summary>不依赖 engine 的宽度测量（DrawLine 内部用；engine 与渲染在同一线程，此处自建一次性测量）。</summary>
    private double MeasureWidthText(string text)
    {
        var dc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            var previous = NativeMethods.SelectObject(dc, _handle);
            NativeMethods.GetTextExtentPoint32W(dc, text, text.Length, out var size);
            NativeMethods.SelectObject(dc, previous);
            return size.cx;
        }
        finally
        {
            NativeMethods.DeleteDC(dc);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_handle);
        }
    }
}

/// <summary>
/// 文字栅格化遮罩：32bpp 自上而下 DIB section。
///
/// 用「白底黑字」得到灰度覆盖度（coverage = 255 - 绿通道），再按 alpha 混合进位图 ——
/// 这样合成只依赖位图像素数组，完全托管，不引入任何图形框架。
/// </summary>
internal sealed class GdiMaskSurface : IDisposable
{
    public IntPtr Dc { get; }

    public IntPtr Bits { get; }

    public int Width { get; }

    public int Height { get; }

    private readonly IntPtr _dibSection;

    private GdiMaskSurface(IntPtr dc, IntPtr dib, IntPtr bits, int width, int height)
    {
        Dc = dc;
        _dibSection = dib;
        Bits = bits;
        Width = width;
        Height = height;
    }

    public static GdiMaskSurface Create(int width, int height)
    {
        var info = new NativeMethods.BITMAPINFO
        {
            bmiHeader = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = 40,

                // 负高度 = 自上而下（第 0 行在缓冲区开头），与 RgbaBitmap 的行序一致。
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BI_RGB,
            },
        };

        var screenDc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw new InvalidOperationException("创建内存 DC 失败，无法栅格化文字。");
        }

        var dib = NativeMethods.CreateDIBSection(
            screenDc,
            ref info,
            NativeMethods.DIB_RGB_COLORS,
            out var bits,
            IntPtr.Zero,
            0);
        NativeMethods.DeleteDC(screenDc);

        if (dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            if (dib != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(dib);
            }

            throw new InvalidOperationException("创建 DIB section 失败，无法栅格化文字。");
        }

        var dc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            NativeMethods.DeleteObject(dib);
            throw new InvalidOperationException("创建内存 DC 失败，无法栅格化文字。");
        }

        NativeMethods.SelectObject(dc, dib);
        return new GdiMaskSurface(dc, dib, bits, width, height);
    }

    /// <summary>把整块遮罩刷成白色（黑字画上去即得到灰度覆盖度）。</summary>
    public void ClearWhite()
    {
        NativeMethods.PatBlt(Dc, 0, 0, Width, Height, NativeMethods.WHITENESS);
    }

    /// <summary>
    /// 把遮罩按覆盖度 alpha 混合进目标位图。
    /// 遮罩像素 (mx,my) 对应目标像素 (originX+mx, originY+my)。
    /// </summary>
    public unsafe void BlendInto(RgbaBitmap target, int originX, int originY, RgbaColor color)
    {
        var stride = Width * 4;
        for (var my = 0; my < Height; my++)
        {
            var ty = originY + my;
            if ((uint)ty >= (uint)target.Height)
            {
                continue;
            }

            var maskRow = (byte*)Bits + (my * stride);
            var targetRow = ty * target.Stride;

            for (var mx = 0; mx < Width; mx++)
            {
                var tx = originX + mx;
                if ((uint)tx >= (uint)target.Width)
                {
                    continue;
                }

                // 白底(255)黑字(0)：绿通道即「未覆盖量」，覆盖度 = 255 - 绿。
                var coverage = 255 - maskRow[(mx * 4) + 1];
                if (coverage <= 0)
                {
                    continue;
                }

                var di = targetRow + (tx * 4);
                var sr = target.Pixels[di];
                var sg = target.Pixels[di + 1];
                var sb = target.Pixels[di + 2];

                // out = src + (color - src) * cov/255，带四舍五入。
                target.Pixels[di] = (byte)(((255 * sr) + ((color.R - sr) * coverage) + 127) / 255);
                target.Pixels[di + 1] = (byte)(((255 * sg) + ((color.G - sg) * coverage) + 127) / 255);
                target.Pixels[di + 2] = (byte)(((255 * sb) + ((color.B - sb) * coverage) + 127) / 255);
            }
        }
    }

    public void Dispose()
    {
        if (Dc != IntPtr.Zero)
        {
            NativeMethods.DeleteDC(Dc);
        }

        if (_dibSection != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_dibSection);
        }
    }
}

/// <summary>
/// 字体与度量的持有者（含缓存）。
///
/// 对应 Mac 侧的 NSFont 度量路径：本引擎集中「选字体 → 量宽度 → 量行高 → 栅格化」，
/// 渲染器只跟它打交道。每次渲染创建一个实例，用完整体释放（DC/字体句柄不跨渲染复用，
/// 避免与宿主 UI 线程争用 GDI）。
/// </summary>
internal sealed class GdiTextEngine : IDisposable
{
    private readonly IntPtr _measurementDc;
    private readonly Dictionary<(string Face, int Weight, int HeightPx), GdiFont> _fonts = new();
    private bool _disposed;

    public GdiTextEngine()
    {
        _measurementDc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
        if (_measurementDc == IntPtr.Zero)
        {
            throw new InvalidOperationException("创建兼容 DC 失败，无法度量文字。");
        }
    }

    /// <summary>按 (族, 字重, 字号) 取字体（带缓存）。</summary>
    public GdiFont GetFont(string face, int weight, double pointSize)
    {
        var key = (face, weight, (int)Math.Round(pointSize, MidpointRounding.AwayFromZero));
        if (!_fonts.TryGetValue(key, out var font))
        {
            font = new GdiFont(face, weight, key.Item3);
            _fonts[key] = font;
        }

        return font;
    }

    /// <summary>测量单行宽度（像素）。内部把字体临时选入度量 DC。</summary>
    public double MeasureWith(IntPtr fontHandle, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var previous = NativeMethods.SelectObject(_measurementDc, fontHandle);
        NativeMethods.GetTextExtentPoint32W(_measurementDc, text, text.Length, out var size);
        NativeMethods.SelectObject(_measurementDc, previous);
        return size.cx;
    }

    /// <summary>便捷方法：按 (族, 字重, 字号) 量一行宽度。</summary>
    public double MeasureWidth(string face, int weight, double pointSize, string text)
    {
        var font = GetFont(face, weight, pointSize);
        return font.MeasureWidth(this, text);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var font in _fonts.Values)
        {
            font.Dispose();
        }

        _fonts.Clear();

        if (_measurementDc != IntPtr.Zero)
        {
            NativeMethods.DeleteDC(_measurementDc);
        }
    }
}
