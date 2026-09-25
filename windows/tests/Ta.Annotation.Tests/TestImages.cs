using Ta.Core.Agent;
using Ta.Core.Drawing;
using Ta.Core.Imaging;

namespace Ta.Annotation.Tests;

/// <summary>
/// 测试辅助：造图与比对。
///
/// 图案与 Mac 版测试的 fixtureImage(width:height:) 完全一致
/// （Tests/AIScreenshotAppTests/TaAgentAnnotationRendererTests.swift），
/// 这样「每个可见操作都改变像素」这条断言在两个平台上跑的是同一张输入。
/// </summary>
internal static class TestImages
{
    /// <summary>8×8 棋盘格：亮 #F0F5FC / 暗 #6B8CB8。</summary>
    public static RgbaBitmap Checker(int width, int height)
    {
        var bitmap = new RgbaBitmap(width, height);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var light = ((x / 8) + (y / 8)) % 2 == 0;
                var index = (y * bitmap.Stride) + (x * RgbaBitmap.BytesPerPixel);
                if (light)
                {
                    bitmap.Pixels[index] = 240;
                    bitmap.Pixels[index + 1] = 245;
                    bitmap.Pixels[index + 2] = 252;
                }
                else
                {
                    bitmap.Pixels[index] = 107;
                    bitmap.Pixels[index + 1] = 140;
                    bitmap.Pixels[index + 2] = 184;
                }

                bitmap.Pixels[index + 3] = 255;
            }
        }

        return bitmap;
    }

    /// <summary>纯色图。</summary>
    public static RgbaBitmap Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var bitmap = new RgbaBitmap(width, height);
        bitmap.Fill(r, g, b, a);
        return bitmap;
    }

    /// <summary>水平灰度渐变（每像素 R 递增 1），用于验证模糊/马赛克。</summary>
    public static RgbaBitmap HorizontalGradient(int width, int height, byte start = 0)
    {
        var bitmap = new RgbaBitmap(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = (byte)Math.Min(255, start + x);
                var index = (y * bitmap.Stride) + (x * RgbaBitmap.BytesPerPixel);
                bitmap.Pixels[index] = value;
                bitmap.Pixels[index + 1] = value;
                bitmap.Pixels[index + 2] = value;
                bitmap.Pixels[index + 3] = 255;
            }
        }

        return bitmap;
    }

    /// <summary>左黑右白的硬边图（用于验证边缘扩展夹取）。</summary>
    public static RgbaBitmap SplitVertical(int width, int height, int splitX)
    {
        var bitmap = new RgbaBitmap(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = x < splitX ? (byte)0 : (byte)255;
                var index = (y * bitmap.Stride) + (x * RgbaBitmap.BytesPerPixel);
                bitmap.Pixels[index] = value;
                bitmap.Pixels[index + 1] = value;
                bitmap.Pixels[index + 2] = value;
                bitmap.Pixels[index + 3] = 255;
            }
        }

        return bitmap;
    }

    /// <summary>FNV-1a 摘要。与 Mac 测试的 pixelDigest 同一算法。</summary>
    public static ulong Digest(RgbaBitmap bitmap)
    {
        const ulong offset = 1469598103934665603;
        const ulong prime = 1099511628211;

        var hash = offset;
        foreach (var value in bitmap.Pixels)
        {
            hash = (hash ^ value) * prime;
        }

        return hash;
    }

    /// <summary>有多少像素不同。</summary>
    public static int ChangedPixelCount(RgbaBitmap a, RgbaBitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            return int.MaxValue;
        }

        var count = 0;
        for (var i = 0; i < a.Pixels.Length; i += RgbaBitmap.BytesPerPixel)
        {
            if (a.Pixels[i] != b.Pixels[i]
                || a.Pixels[i + 1] != b.Pixels[i + 1]
                || a.Pixels[i + 2] != b.Pixels[i + 2]
                || a.Pixels[i + 3] != b.Pixels[i + 3])
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>取某像素的 RGB（0..255）。</summary>
    public static (byte R, byte G, byte B) Pixel(RgbaBitmap bitmap, int x, int y)
    {
        var index = (y * bitmap.Stride) + (x * RgbaBitmap.BytesPerPixel);
        return (bitmap.Pixels[index], bitmap.Pixels[index + 1], bitmap.Pixels[index + 2]);
    }

    /// <summary>与参考图相比，被改动的像素的包围盒（含边界 1px 容差判定用）。</summary>
    public static (int MinX, int MinY, int MaxX, int MaxY, bool Empty) ChangedBounds(
        RgbaBitmap rendered,
        RgbaBitmap reference)
    {
        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = int.MinValue;
        var maxY = int.MinValue;

        for (var y = 0; y < rendered.Height; y++)
        {
            for (var x = 0; x < rendered.Width; x++)
            {
                var a = Pixel(rendered, x, y);
                var b = Pixel(reference, x, y);
                if (a.R == b.R && a.G == b.G && a.B == b.B)
                {
                    continue;
                }

                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        return (minX, minY, maxX, maxY, minX == int.MaxValue);
    }

    /// <summary>
    /// 点到多边形边界的最短距离（0 表示在边上）。
    /// 用于把「抗锯齿精度极限内的亚像素碎片」与「真的漏填」区分开。
    /// </summary>
    public static double DistanceToPolygon(double x, double y, IReadOnlyList<PointD> polygon)
    {
        var best = double.PositiveInfinity;
        var count = polygon.Count;

        for (var i = 0; i < count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % count];
            best = Math.Min(best, DistanceToSegment(x, y, a, b));
        }

        return best;
    }

    private static double DistanceToSegment(double px, double py, PointD a, PointD b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSquared = (dx * dx) + (dy * dy);

        if (lengthSquared <= 0)
        {
            var ex = px - a.X;
            var ey = py - a.Y;
            return Math.Sqrt((ex * ex) + (ey * ey));
        }

        var t = Math.Clamp((((px - a.X) * dx) + ((py - a.Y) * dy)) / lengthSquared, 0, 1);
        var cx = a.X + (t * dx);
        var cy = a.Y + (t * dy);
        var ox = px - cx;
        var oy = py - cy;
        return Math.Sqrt((ox * ox) + (oy * oy));
    }

    /// <summary>
    /// 测试侧的独立「点在多边形内」判定（射线法）。
    /// 用来交叉验证渲染结果确实等于 <see cref="TaperedArrowGeometry"/> 给出的 7 点，
    /// 而不是渲染器自己又算了一遍。
    /// </summary>
    public static bool PointInPolygon(double x, double y, IReadOnlyList<PointD> polygon)
    {
        var inside = false;
        var count = polygon.Count;

        var j = count - 1;
        for (var i = 0; i < count; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];

            if ((a.Y > y) != (b.Y > y)
                && x < (((b.X - a.X) * (y - a.Y)) / (b.Y - a.Y)) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }
}

/// <summary>配方构造辅助。</summary>
internal static class RecipeHelper
{
    public static AnnotationRecipe Of(params AnnotationOperation[] operations) =>
        new(1, operations);

    public static AnnotationRectOperation Rect(string id, AnnotationRect rect, AnnotationColor? color = null, double lineWidth = 5, bool dashed = false) =>
        new(id, rect, color ?? AnnotationColor.Red_, lineWidth, dashed);
}
