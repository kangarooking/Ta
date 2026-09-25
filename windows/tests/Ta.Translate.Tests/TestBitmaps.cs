using Ta.Core.Capture;
using Ta.Core.Imaging;
using Ta.Translate;
using Ta.Translate.Rendering;

namespace Ta.Translate.Tests;

/// <summary>测试用的小工具：构造已知内容的位图、取像素。</summary>
internal static class TestBitmaps
{
    /// <summary>纯色位图。</summary>
    public static RgbaBitmap Solid(int width, int height, RgbaColor color)
    {
        var bitmap = new RgbaBitmap(width, height);
        bitmap.Fill(color.R, color.G, color.B, color.A);
        return bitmap;
    }

    /// <summary>取像素（越界返回黑色，避免测试里到处写边界判断）。</summary>
    public static RgbaColor Pixel(RgbaBitmap bitmap, int x, int y)
    {
        if (x < 0 || y < 0 || x >= bitmap.Width || y >= bitmap.Height)
        {
            return RgbaColor.Black;
        }

        var i = (y * bitmap.Stride) + (x * RgbaBitmap.BytesPerPixel);
        return new RgbaColor(bitmap.Pixels[i], bitmap.Pixels[i + 1], bitmap.Pixels[i + 2], bitmap.Pixels[i + 3]);
    }

    /// <summary>一条 OCR 文字行（归一化 bbox，左上原点、Y 向下）。</summary>
    public static OcrTextLine OcrLine(
        string text,
        float confidence,
        double x,
        double y,
        double w,
        double h) =>
        new(text, confidence, new RectD(x, y, w, h));

    /// <summary>一条已翻译的文字行。</summary>
    public static TranslatedOcrLine TranslatedLine(
        string source,
        string translated,
        double x,
        double y,
        double w,
        double h,
        float confidence = 0.95f) =>
        new(source, translated, new RectD(x, y, w, h), confidence);
}
