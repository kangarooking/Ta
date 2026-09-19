using System.Runtime.InteropServices;

namespace Ta.LongSession.Win32;

/// <summary>
/// 32bpp 自绘画布：GDI 绘制 + 可选 per-pixel alpha 合成到分层窗口。
///
/// 设计取舍（对应 Mac 版 .ultraThickMaterial 的近似方案）：
/// 整块面板用**统一**的 panelAlpha 混合到屏幕（acrylic 需要
/// SetWindowCompositionAttribute 的未文档化 API，风险高于收益）；
/// 只有圆角之外是逐像素 alpha=0。文字/按钮都画在同一张
/// 不透明 DIB 上，合成前统一预乘。
/// </summary>
internal sealed class PixelCanvas : IDisposable
{
    private IntPtr _memDc;
    private IntPtr _dib;
    private IntPtr _oldBitmap;
    private int _width;
    private int _height;
    private IntPtr _bits;
    private byte[]? _pixels;
    private bool _disposed;

    public PixelCanvas()
    {
        _memDc = Native.CreateCompatibleDC(IntPtr.Zero);
    }

    public int Width => _width;
    public int Height => _height;

    /// <summary>GDI 设备上下文（仅在窗口线程使用）。</summary>
    public IntPtr Hdc => _memDc;

    /// <summary>按需重建后备 DIB。</summary>
    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (_width == width && _height == height && _dib != IntPtr.Zero)
        {
            return;
        }

        ReleaseBitmap();
        _width = width;
        _height = height;

        var info = new Native.BITMAPINFO
        {
            bmiHeader = new Native.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,     // 自上而下
                biPlanes = 1,
                biBitCount = 32,
                biCompression = Native.BI_RGB,
            },
        };

        _dib = Native.CreateDIBSection(_memDc, ref info, Native.DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"CreateDIBSection 失败，Win32 错误 {Marshal.GetLastWin32Error()}");
        }

        _oldBitmap = Native.SelectObject(_memDc, _dib);
    }

    /// <summary>整块清成纯色（同时清 GDI 笔刷状态之外的画布内容）。</summary>
    public void Clear(uint bgrColor)
    {
        if (_memDc == IntPtr.Zero)
        {
            return;
        }

        var brush = Native.CreateSolidBrush(bgrColor);
        var old = Native.SelectObject(_memDc, brush);
        var rect = new Native.RECT(0, 0, _width, _height);
        Native.FillRect(_memDc, in rect, brush);
        Native.SelectObject(_memDc, old);
        Native.DeleteObject(brush);
    }

    /// <summary>GDI 圆角矩形填充（画在 DIB 上，合成时统一预乘）。</summary>
    public void FillRoundRect(Native.RECT rect, int radius, uint bgrColor)
    {
        var brush = Native.CreateSolidBrush(bgrColor);
        var old = Native.SelectObject(_memDc, brush);
        Native.RoundRect(_memDc, rect.left, rect.top, rect.right, rect.bottom, radius * 2, radius * 2);
        Native.SelectObject(_memDc, old);
        Native.DeleteObject(brush);
    }

    /// <summary>GDI 矩形填充。</summary>
    public void FillRect(Native.RECT rect, uint bgrColor)
    {
        var brush = Native.CreateSolidBrush(bgrColor);
        Native.FillRect(_memDc, in rect, brush);
        Native.DeleteObject(brush);
    }

    /// <summary>GDI 描边矩形。</summary>
    public void FrameRect(Native.RECT rect, uint bgrColor, int width)
    {
        var pen = Native.CreatePen(Native.PS_SOLID, width, bgrColor);
        var oldPen = Native.SelectObject(_memDc, pen);
        var oldBrush = Native.SelectObject(_memDc, Native.GetStockObject(Native.HOLLOW_BRUSH));
        Native.Rectangle(_memDc, rect.left, rect.top, rect.right, rect.bottom);
        Native.SelectObject(_memDc, oldPen);
        Native.SelectObject(_memDc, oldBrush);
        Native.DeleteObject(pen);
    }

    /// <summary>
    /// 以 <paramref name="panelAlpha"/> 为统一不透明度合成到分层窗口
    /// （圆角之外 alpha=0，带 1px 抗锯齿）。
    /// </summary>
    public void CommitLayered(IntPtr hwnd, int radius, byte panelAlpha)
    {
        if (_dib == IntPtr.Zero || _bits == IntPtr.Zero)
        {
            return;
        }

        EnsurePixels();
        var pixels = _pixels!;

        for (var y = 0; y < _height; y++)
        {
            var rowOffset = y * _width * 4;
            for (var x = 0; x < _width; x++)
            {
                var i = rowOffset + x * 4;
                var coverage = CornerCoverage(x, y, _width, _height, radius);
                var alpha = (byte)Math.Round(panelAlpha * coverage / 255.0);

                // 预乘（AC_SRC_ALPHA 要求）。
                pixels[i] = (byte)Math.Round(pixels[i] * alpha / 255.0);
                pixels[i + 1] = (byte)Math.Round(pixels[i + 1] * alpha / 255.0);
                pixels[i + 2] = (byte)Math.Round(pixels[i + 2] * alpha / 255.0);
                pixels[i + 3] = alpha;
            }
        }

        Marshal.Copy(pixels, 0, _bits, pixels.Length);

        var dst = new Native.POINT(WindowLeft(hwnd), WindowTop(hwnd));
        var size = new Native.SIZE { cx = _width, cy = _height };
        var src = new Native.POINT(0, 0);
        var blend = new Native.BLENDFUNCTION
        {
            BlendOp = 0,               // AC_SRC_OVER
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = 1,           // AC_SRC_ALPHA
        };

        Native.UpdateLayeredWindow(hwnd, IntPtr.Zero, ref dst, ref size, _memDc, ref src, 0, ref blend, 2);
    }

    /// <summary>把画布 BitBlt 到窗口 DC（普通窗口的 WM_PAINT 路径）。</summary>
    public void BlitTo(IntPtr hdc, Native.RECT destination)
    {
        if (_memDc == IntPtr.Zero)
        {
            return;
        }

        Native.BitBlt(hdc, destination.left, destination.top, _width, _height, _memDc, 0, 0, Native.SRCCOPY);
    }

    /// <summary>把一张位图缩放画到画布上（≈ .interpolation(.high)）。</summary>
    public void DrawBitmapScaled(IntPtr bitmapDc, int srcWidth, int srcHeight, Native.RECT dest)
    {
        if (_memDc == IntPtr.Zero || bitmapDc == IntPtr.Zero)
        {
            return;
        }

        var oldMode = Native.SetStretchBltMode(_memDc, Native.HALFTONE);
        Native.StretchBlt(
            _memDc, dest.left, dest.top, dest.Width, dest.Height,
            bitmapDc, 0, 0, srcWidth, srcHeight, Native.SRCCOPY);
        Native.SetStretchBltMode(_memDc, oldMode);
    }

    private static int WindowLeft(IntPtr hwnd)
    {
        Native.GetWindowRect(hwnd, out var rect);
        return rect.left;
    }

    private static int WindowTop(IntPtr hwnd)
    {
        Native.GetWindowRect(hwnd, out var rect);
        return rect.top;
    }

    /// <summary>圆角覆盖度（SDF 式 1px 抗锯齿，仅四角参与计算）。</summary>
    private static double CornerCoverage(int x, int y, int width, int height, int radius)
    {
        if (radius <= 0)
        {
            return 1;
        }

        var r = Math.Min(radius, Math.Min(width, height) / 2);
        var inLeft = x < r;
        var inRight = x >= width - r;
        var inTop = y < r;
        var inBottom = y >= height - r;

        if ((inLeft || inRight) && (inTop || inBottom))
        {
            var cx = inLeft ? r : width - r - 1;
            var cy = inTop ? r : height - r - 1;
            var dx = x + 0.5 - cx;
            var dy = y + 0.5 - cy;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            return Math.Clamp(r - distance + 0.5, 0, 1);
        }

        return 1;
    }

    private void EnsurePixels()
    {
        var length = _width * _height * 4;
        if (_pixels is null || _pixels.Length != length)
        {
            _pixels = new byte[length];
        }

        if (_bits != IntPtr.Zero)
        {
            Marshal.Copy(_bits, _pixels, 0, length);
        }
    }

    private void ReleaseBitmap()
    {
        if (_oldBitmap != IntPtr.Zero)
        {
            Native.SelectObject(_memDc, _oldBitmap);
            _oldBitmap = IntPtr.Zero;
        }

        if (_dib != IntPtr.Zero)
        {
            Native.DeleteObject(_dib);
            _dib = IntPtr.Zero;
        }

        _bits = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseBitmap();
        if (_memDc != IntPtr.Zero)
        {
            Native.DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
        }
    }
}
