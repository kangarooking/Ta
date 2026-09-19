using System.Runtime.InteropServices;
using Ta.Core.Imaging;
using Ta.Pinning.Interop;

namespace Ta.Pinning;

/// <summary>
/// 文字卡片的**尺寸**算法（纯逻辑部分）。
///
/// 对应 Mac 版 <c>renderText(_:)</c>（PinnedImageWindowController.swift:216-235）：
/// <code>
/// let width: CGFloat = 520
/// let textRect = attributed.boundingRect(with: CGSize(width: width - 48, height: 1200), …)
/// let height = min(1200, max(120, textRect.height + 48))
/// </code>
/// </summary>
public static class PinnedImageTextCard
{
    /// <summary>卡片宽。Mac: <c>let width: CGFloat = 520</c>（:217）。</summary>
    public const double CardWidth = 520;

    /// <summary>内边距。Mac: 文本在 (24, 24, width−48, height−48) 内绘制（:232）。</summary>
    public const double Padding = 24;

    /// <summary>文本可用宽。Mac: <c>width - 48</c>（:219）。</summary>
    public static double TextWidth => CardWidth - (Padding * 2);

    /// <summary>测量上限。Mac: <c>CGSize(width: width - 48, height: 1200)</c>（:219）。</summary>
    public const double MeasurementHeightLimit = 1200;

    /// <summary>卡片高上限。Mac: <c>min(1200, …)</c>（:222）。</summary>
    public const double MaxCardHeight = 1200;

    /// <summary>卡片高下限。Mac: <c>max(120, …)</c>（:222）。</summary>
    public const double MinCardHeight = 120;

    /// <summary>垂直留白。Mac: <c>textRect.height + 48</c>（:222）。</summary>
    public const double VerticalPadding = 48;

    /// <summary>
    /// 由测得的文本高度算出卡片尺寸。
    /// </summary>
    public static PinSizeD CardSize(double textHeight) =>
        new(CardWidth, Math.Min(MaxCardHeight, Math.Max(MinCardHeight, textHeight + VerticalPadding)));
}

/// <summary>
/// 用 GDI 把文本渲染成位图，产出与 Mac <c>renderText</c> 同尺寸的卡片。
///
/// Mac 用 NSAttributedString + NSGraphicsContext（:228-234），背景
/// <c>NSColor.textBackgroundColor</c>、文字 <c>NSColor.labelColor</c>。
/// Windows 上取**亮色模式**的等价物：白底黑字（Segoe UI）。
/// ⚠️ 这是参考文档 §14 风险 #11（字体度量）的直接体现：换成 Segoe UI 后
/// 每个文字盒的行高与换行位置都会与 Mac 的 SF 不同 —— 尺寸公式一致，像素不复刻。
///
/// 注：Mac 的 <c>makeTextCard(title:detail:icon:)</c>（:237-242）其实把 icon
/// **丢掉了**（只把标题与详情拼进 attributed string），因此这里同样不画图标。
/// </summary>
internal static class PinTextCard
{
    private const string FontFace = "Segoe UI";

    /// <summary>
    /// 纯文本卡片。对应 Mac: NSAttributedString(string:text, attributes: 18pt + labelColor)（:85-88）。
    /// </summary>
    /// <returns>位图与其**逻辑尺寸**（520 × min(1200, max(120, h+48))）。</returns>
    public static (RgbaBitmap Bitmap, PinSizeD LogicalSize) RenderTextCard(string text, double scale)
    {
        var textHeightPx = Measure(text, fontPoints: 18, fontWeight: PinInterop.FW_NORMAL, scale);
        var size = PinnedImageTextCard.CardSize(textHeightPx / scale);

        // 位图按 DPI 放大以保文字清晰，但**逻辑尺寸**始终是 520 宽 ——
        // 由调用方作为 preferredLogicalSize 传下去，钉图因此正好 520pt 宽。
        var width = Math.Max(1, (int)Math.Round(size.Width * scale));
        var height = Math.Max(1, (int)Math.Round(size.Height * scale));

        var surface = CreateSurface(width, height);
        if (surface is null)
        {
            // 兜底：至少给一张 1×1 的图，调用方再按逻辑尺寸钉出去。
            var fallback = new RgbaBitmap(1, 1);
            fallback.Fill(255, 255, 255);
            return (fallback, size);
        }

        var (dc, bitmap, bits) = surface.Value;
        try
        {
            var padding = (int)Math.Round(PinnedImageTextCard.Padding * scale);
            var textWidth = (int)Math.Round(PinnedImageTextCard.TextWidth * scale);
            var rect = new PinInterop.RECT(padding, padding, padding + textWidth, height - padding);
            Draw(dc, text, fontPoints: 18, fontWeight: PinInterop.FW_NORMAL, scale: scale, rect: rect);
            return (ReadBack(bits, width, height), size);
        }
        finally
        {
            PinInterop.SelectObject(dc, bitmap);
            PinInterop.DeleteObject(bitmap);
            PinInterop.DeleteDC(dc);
        }
    }

    /// <summary>文件卡片。对应 Mac: makeTextCard —— 标题 20pt semibold + 详情 13pt（:239-240）。</summary>
    public static (RgbaBitmap Bitmap, PinSizeD LogicalSize) RenderFileCard(string title, string detail, double scale)
    {
        var titleHeightPx = Measure($"{title}\n", fontPoints: 20, fontWeight: PinInterop.FW_SEMIBOLD, scale);
        var detailHeightPx = Measure(detail, fontPoints: 13, fontWeight: PinInterop.FW_NORMAL, scale);
        var textHeightPt = (titleHeightPx + detailHeightPx) / scale;
        var size = PinnedImageTextCard.CardSize(textHeightPt);

        var width = Math.Max(1, (int)Math.Round(size.Width * scale));
        var height = Math.Max(1, (int)Math.Round(size.Height * scale));

        var surface = CreateSurface(width, height);
        if (surface is null)
        {
            var fallback = new RgbaBitmap(1, 1);
            fallback.Fill(255, 255, 255);
            return (fallback, size);
        }

        var (dc, bitmap, bits) = surface.Value;
        try
        {
            var padding = (int)Math.Round(PinnedImageTextCard.Padding * scale);
            var textWidth = (int)Math.Round(PinnedImageTextCard.TextWidth * scale);
            var titleRect = new PinInterop.RECT(padding, padding, padding + textWidth, height - padding);
            Draw(dc, $"{title}\n", fontPoints: 20, fontWeight: PinInterop.FW_SEMIBOLD, scale: scale, rect: titleRect);

            var detailRect = new PinInterop.RECT(
                padding,
                padding + (int)Math.Round((double)titleHeightPx),
                padding + textWidth,
                height - padding);
            Draw(dc, detail, fontPoints: 13, fontWeight: PinInterop.FW_NORMAL, scale: scale, rect: detailRect);

            return (ReadBack(bits, width, height), size);
        }
        finally
        {
            PinInterop.SelectObject(dc, bitmap);
            PinInterop.DeleteObject(bitmap);
            PinInterop.DeleteDC(dc);
        }
    }

    /// <summary>测文本高度（物理像素）。对应 Mac 的 boundingRect(with:options:.usesLineFragmentOrigin)（:218-221）。</summary>
    private static int Measure(string text, double fontPoints, int fontWeight, double scale)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        var dc = PinInterop.CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            return 0;
        }

        var font = CreateFont(fontPoints, fontWeight, scale);
        var previous = PinInterop.SelectObject(dc, font);
        try
        {
            var padding = (int)Math.Round(PinnedImageTextCard.Padding * scale);
            var textWidth = (int)Math.Round(PinnedImageTextCard.TextWidth * scale);
            var limit = (int)Math.Round(PinnedImageTextCard.MeasurementHeightLimit * scale);
            var rect = new PinInterop.RECT(0, 0, textWidth, limit);
            PinInterop.DrawText(dc, text, -1, ref rect,
                PinInterop.DT_LEFT | PinInterop.DT_TOP | PinInterop.DT_WORDBREAK
                | PinInterop.DT_NOPREFIX | PinInterop.DT_CALCRECT);
            return rect.Height;
        }
        finally
        {
            PinInterop.SelectObject(dc, previous);
            PinInterop.DeleteObject(font);
            PinInterop.DeleteDC(dc);
        }
    }

    private static (IntPtr Dc, IntPtr Bitmap, IntPtr Bits)? CreateSurface(int width, int height)
    {
        var screenDc = PinInterop.GetDC(IntPtr.Zero);
        try
        {
            var dc = PinInterop.CreateCompatibleDC(screenDc);
            if (dc == IntPtr.Zero)
            {
                return null;
            }

            var info = new PinInterop.BITMAPINFO();
            info.bmiHeader.biSize = (uint)Marshal.SizeOf<PinInterop.BITMAPINFOHEADER>();
            info.bmiHeader.biWidth = width;
            info.bmiHeader.biHeight = -height;   // 自上而下，DrawText 才不倒置
            info.bmiHeader.biPlanes = 1;
            info.bmiHeader.biBitCount = 32;
            info.bmiHeader.biCompression = PinInterop.BI_RGB;

            var bitmap = PinInterop.CreateDIBSection(
                screenDc, ref info, PinInterop.DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero)
            {
                PinInterop.DeleteDC(dc);
                return null;
            }

            PinInterop.SelectObject(dc, bitmap);

            // Mac 用 textBackgroundColor 铺底（:230-231）—— 取亮色模式的白色。
            var white = new byte[width * height * 4];
            for (var i = 0; i < white.Length; i += 4)
            {
                white[i] = 255;      // B
                white[i + 1] = 255;  // G
                white[i + 2] = 255;  // R
                white[i + 3] = 255;  // A
            }

            Marshal.Copy(white, 0, bits, white.Length);
            PinInterop.SetBkMode(dc, PinInterop.TRANSPARENT);
            PinInterop.SetTextColor(dc, 0x00000000);   // labelColor 的亮色模式等价物：黑

            return (dc, bitmap, bits);
        }
        finally
        {
            PinInterop.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static void Draw(IntPtr dc, string text, double fontPoints, int fontWeight, double scale, PinInterop.RECT rect)
    {
        var font = CreateFont(fontPoints, fontWeight, scale);
        var previous = PinInterop.SelectObject(dc, font);
        try
        {
            PinInterop.DrawText(dc, text, -1, ref rect,
                PinInterop.DT_LEFT | PinInterop.DT_TOP | PinInterop.DT_WORDBREAK | PinInterop.DT_NOPREFIX);
        }
        finally
        {
            PinInterop.SelectObject(dc, previous);
            PinInterop.DeleteObject(font);
        }
    }

    /// <summary>pt → 像素字高（96dpi 下 1pt = 4/3 px），负值表示取字符高度而非单元格高度。</summary>
    private static IntPtr CreateFont(double fontPoints, int fontWeight, double scale)
    {
        var pixels = (int)Math.Round(fontPoints * (4.0 / 3.0) * scale);
        return PinInterop.CreateFont(
            -pixels, 0, 0, 0, fontWeight,
            0, 0, 0,
            PinInterop.DEFAULT_CHARSET, PinInterop.OUT_TT_PRECIS, PinInterop.CLIP_DEFAULT_PRECIS,
            PinInterop.PROOF_QUALITY, PinInterop.DEFAULT_PITCH | PinInterop.FF_DONTCARE, FontFace);
    }

    private static RgbaBitmap ReadBack(IntPtr bits, int width, int height)
    {
        var buffer = new byte[width * height * 4];
        Marshal.Copy(bits, buffer, 0, buffer.Length);

        var bitmap = new RgbaBitmap(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var from = ((y * width) + x) * 4;
                var to = (y * bitmap.Stride) + (x * RgbaBitmap.BytesPerPixel);
                bitmap.Pixels[to] = buffer[from + 2];
                bitmap.Pixels[to + 1] = buffer[from + 1];
                bitmap.Pixels[to + 2] = buffer[from];
                bitmap.Pixels[to + 3] = 255;
            }
        }

        return bitmap;
    }
}
