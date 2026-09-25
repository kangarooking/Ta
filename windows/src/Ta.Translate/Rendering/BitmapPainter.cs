using Ta.Core.Capture;
using Ta.Core.Imaging;

namespace Ta.Translate.Rendering;

/// <summary>
/// 位图绘制原语：纯托管地直接写 <see cref="RgbaBitmap.Pixels"/>。
///
/// 对应 Mac 版 NSGraphicsContext 上的基础绘制：
/// · <see cref="FillRect"/> ↔ NSRect.fill / NSColor.setFill
/// · <see cref="FillRoundedRect"/> ↔ NSBezierPath(roundedRect:xRadius:yRadius:).fill()
/// · 颜色一律按<b>直通 alpha</b> 混合（RgbaBitmap 为未预乘、源不透明），
///   对应 AppKit 在 opaque 位图上下文里的合成结果。
///
/// ⚠️ 坐标系（Windows移植参考文档 §5.1）：全部按<b>左上原点、Y 向下</b>的像素空间计算 ——
/// 与 Mac 的 CGContext（左下原点、Y 向上）不同，Mac 版那几处 Y 翻转在 Windows 上
/// 直接消失，本文件内没有任何翻转代码，这是刻意为之。
/// </summary>
internal static class BitmapPainter
{
    /// <summary>填充实心矩形（alpha 直通混合）。对应 Mac: NSColor.setFill + NSRect.fill。</summary>
    public static void FillRect(RgbaBitmap target, int left, int top, int width, int height, RgbaColor color)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var x0 = Math.Max(0, left);
        var y0 = Math.Max(0, top);
        var x1 = Math.Min(target.Width, left + width);
        var y1 = Math.Min(target.Height, top + height);

        for (var y = y0; y < y1; y++)
        {
            var row = y * target.Stride;
            for (var x = x0; x < x1; x++)
            {
                BlendPixel(target, row + (x * 4), color);
            }
        }
    }

    /// <summary>
    /// 填充圆角矩形。对应 Mac: NSBezierPath(roundedRect:xRadius:yRadius:).fill()（:46）。
    /// 圆角为四分之一椭圆（AppKit 的 roundedRect 语义），按像素中心做包含判定。
    /// </summary>
    public static void FillRoundedRect(
        RgbaBitmap target,
        RectD rect,
        RgbaColor color,
        double radiusX,
        double radiusY)
    {
        var left = (int)Math.Floor(rect.MinX);
        var top = (int)Math.Floor(rect.MinY);
        var right = (int)Math.Ceiling(rect.MaxX);
        var bottom = (int)Math.Ceiling(rect.MaxY);

        var x0 = Math.Max(0, left);
        var y0 = Math.Max(0, top);
        var x1 = Math.Min(target.Width, right);
        var y1 = Math.Min(target.Height, bottom);

        if (x1 <= x0 || y1 <= y0)
        {
            return;
        }

        var rx = Math.Min(radiusX, rect.Width / 2);
        var ry = Math.Min(radiusY, rect.Height / 2);

        for (var y = y0; y < y1; y++)
        {
            var py = y + 0.5;
            var row = y * target.Stride;

            for (var x = x0; x < x1; x++)
            {
                if (IsInsideRoundedRect(x + 0.5, py, rect, rx, ry))
                {
                    BlendPixel(target, row + (x * 4), color);
                }
            }
        }
    }

    /// <summary>像素中心 (px,py) 是否落在圆角矩形内（四角为椭圆弧）。</summary>
    private static bool IsInsideRoundedRect(double px, double py, RectD rect, double rx, double ry)
    {
        if (rx <= 0 || ry <= 0)
        {
            return px >= rect.MinX && px <= rect.MaxX && py >= rect.MinY && py <= rect.MaxY;
        }

        // 中间带（无圆角区域）直接判包含。
        if (px >= rect.MinX + rx && px <= rect.MaxX - rx)
        {
            return py >= rect.MinY && py <= rect.MaxY;
        }

        if (py >= rect.MinY + ry && py <= rect.MaxY - ry)
        {
            return px >= rect.MinX && px <= rect.MaxX;
        }

        // 落在某个角区：到对应角心的椭圆距离。
        var cornerX = px < rect.MinX + rx ? rect.MinX + rx : rect.MaxX - rx;
        var cornerY = py < rect.MinY + ry ? rect.MinY + ry : rect.MaxY - ry;
        var dx = (px - cornerX) / rx;
        var dy = (py - cornerY) / ry;
        return (dx * dx) + (dy * dy) <= 1;
    }

    /// <summary>
    /// 直通 alpha 混合一个像素（源为直通 alpha、目标不透明）：
    /// out = src + (color - src) * (color.A / 255)，带四舍五入。
    /// </summary>
    private static void BlendPixel(RgbaBitmap target, int index, RgbaColor color)
    {
        if (color.A >= 255)
        {
            target.Pixels[index] = color.R;
            target.Pixels[index + 1] = color.G;
            target.Pixels[index + 2] = color.B;
            target.Pixels[index + 3] = 255;
            return;
        }

        var sr = target.Pixels[index];
        var sg = target.Pixels[index + 1];
        var sb = target.Pixels[index + 2];
        var a = color.A;

        target.Pixels[index] = (byte)(((255 * sr) + ((color.R - sr) * a) + 127) / 255);
        target.Pixels[index + 1] = (byte)(((255 * sg) + ((color.G - sg) * a) + 127) / 255);
        target.Pixels[index + 2] = (byte)(((255 * sb) + ((color.B - sb) * a) + 127) / 255);
        target.Pixels[index + 3] = 255;
    }

    /// <summary>按 (族, 字重, 字号) 画一行文字。便捷入口（内部建引擎的场景用不到，保留给对称性）。</summary>
    public static void DrawTextLine(
        GdiTextEngine engine,
        RgbaBitmap target,
        string text,
        int x,
        int baselineY,
        string face,
        int weight,
        double pointSize,
        RgbaColor color)
    {
        var font = engine.GetFont(face, weight, pointSize);
        font.DrawLine(target, text, x, baselineY, color);
    }
}
