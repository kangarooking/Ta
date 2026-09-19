using Ta.Core.Imaging;

namespace Ta.Annotation;

/// <summary>
/// 配方版滤镜。
///
/// ⚠️ **不要与编辑器版混淆**（参考文档 §14 风险 #8）：
///   编辑器版用 CoreImage 的 <c>CIPixellate(scale 14)</c> / <c>CIGaussianBlur(radius 12)</c>；
///   本类是 Agent 配方版，对应 Mac: TaAgentAnnotationRenderer.pixelated(_:scale:)（:252-286）
///   与 blurred(image,radius:)（:288-344）。两者**同名参数下输出不同**，
///   这里忠实移植的是配方版。
///
/// 两个滤镜都从**裁剪后的原图**生成（不采样已画上标注的像素）——
/// 对应 Mac:181-344 里 filtered 图始终来自 base。
/// </summary>
internal static class ImageFilters
{
    /// <summary>
    /// 两级缩放像素化。对应 Mac:252-286 的 pixelated(_:scale:)。
    ///
    ///   blockSize = max(2, Int(scale.rounded()))        （Mac:253）
    ///   小图尺寸 = ceil(源尺寸 / blockSize)              （Mac:254-255）
    ///   降采样插值 = .low，升采样插值 = .none（最近邻）    （Mac:265、277）
    ///
    /// 差异说明：Quartz 的 <c>.low</c> 插值不是纯盒式平均，因此**每个块的均值会有
    /// 数级（个位数量级）差异**；但「块内同色、块间跳变」的结构完全一致 ——
    /// 这正是马赛克的视觉契约（验收项 4）。升采样用最近邻是精确对齐的。
    ///
    /// 实现上不真的分配中间小图，而是直接按块求面积平均再回填，
    /// 与「降采样成 byte 图 → 最近邻升采样」等价（省一次全图分配）。
    /// </summary>
    public static RgbaBitmap Pixelate(RgbaBitmap source, double scale)
    {
        var blockSize = Math.Max(2, (int)Math.Round(scale, MidpointRounding.AwayFromZero));
        var width = source.Width;
        var height = source.Height;
        var result = new RgbaBitmap(width, height);

        var blocksX = (width + blockSize - 1) / blockSize;
        var blocksY = (height + blockSize - 1) / blockSize;

        for (var blockY = 0; blockY < blocksY; blockY++)
        {
            var startY = blockY * blockSize;
            var endY = Math.Min(startY + blockSize, height);

            for (var blockX = 0; blockX < blocksX; blockX++)
            {
                var startX = blockX * blockSize;
                var endX = Math.Min(startX + blockSize, width);

                long sumR = 0;
                long sumG = 0;
                long sumB = 0;
                long sumA = 0;
                var count = 0;

                for (var y = startY; y < endY; y++)
                {
                    var rowStart = y * source.Stride;
                    for (var x = startX; x < endX; x++)
                    {
                        var index = rowStart + (x * RgbaBitmap.BytesPerPixel);
                        sumR += source.Pixels[index];
                        sumG += source.Pixels[index + 1];
                        sumB += source.Pixels[index + 2];
                        sumA += source.Pixels[index + 3];
                        count++;
                    }
                }

                var r = (byte)((sumR + (count / 2)) / count);
                var g = (byte)((sumG + (count / 2)) / count);
                var b = (byte)((sumB + (count / 2)) / count);
                var a = (byte)((sumA + (count / 2)) / count);

                for (var y = startY; y < endY; y++)
                {
                    var rowStart = y * result.Stride;
                    for (var x = startX; x < endX; x++)
                    {
                        var index = rowStart + (x * RgbaBitmap.BytesPerPixel);
                        result.Pixels[index] = r;
                        result.Pixels[index + 1] = g;
                        result.Pixels[index + 2] = b;
                        result.Pixels[index + 3] = a;
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 盒式卷积模糊。对应 Mac:288-344 的 blurred(image,radius:)。
    ///
    ///   kernel = max(3, Int((radius * 2 + 1).rounded()))，且强制为奇数（Mac:317-318）
    ///   抽头居中、边缘扩展夹取（kvImageEdgeExtend）（Mac:328）
    ///   **单遍盒式**，不是真高斯 —— 这是配方版的既定行为（风险 #8）
    ///
    /// 提速做法：二维盒式核可分离，先横向后纵向各做一遍一维盒式，
    /// 数学上与单遍二维盒式**等价**（同一核的两次一维卷积 = 一次二维卷积）。
    /// 唯一差异是中间结果按 byte 舍入，累计每通道最多 ±1 —— 已通过测试确认。
    ///
    /// 注意 Mac 在 premultipliedLast 缓冲上卷积；本实现按直通 alpha 逐通道卷积。
    /// 截图输入 alpha 恒为 255（预乘与非预乘完全相同），
    /// 半透明输入才会有 ≤1 的舍入差异。
    /// </summary>
    public static RgbaBitmap BoxBlur(RgbaBitmap source, double radius)
    {
        var kernel = Math.Max(3, (int)Math.Round((radius * 2) + 1, MidpointRounding.AwayFromZero));
        if (kernel % 2 == 0)
        {
            kernel++;
        }

        var width = source.Width;
        var height = source.Height;

        // 核宽不小于图像尺寸时，横向/纵向各退化为整行/整列平均 —— 结果与截断核一致。
        var horizontal = new RgbaBitmap(width, height);
        var result = new RgbaBitmap(width, height);
        var half = kernel / 2;

        // 横向一维盒式。
        for (var y = 0; y < height; y++)
        {
            var sourceRow = y * source.Stride;
            var targetRow = y * horizontal.Stride;

            for (var x = 0; x < width; x++)
            {
                long sumR = 0;
                long sumG = 0;
                long sumB = 0;
                long sumA = 0;

                for (var tap = -half; tap <= half; tap++)
                {
                    var sx = Math.Clamp(x + tap, 0, width - 1);
                    var index = sourceRow + (sx * RgbaBitmap.BytesPerPixel);
                    sumR += source.Pixels[index];
                    sumG += source.Pixels[index + 1];
                    sumB += source.Pixels[index + 2];
                    sumA += source.Pixels[index + 3];
                }

                var index2 = targetRow + (x * RgbaBitmap.BytesPerPixel);
                horizontal.Pixels[index2] = Average(sumR, kernel);
                horizontal.Pixels[index2 + 1] = Average(sumG, kernel);
                horizontal.Pixels[index2 + 2] = Average(sumB, kernel);
                horizontal.Pixels[index2 + 3] = Average(sumA, kernel);
            }
        }

        // 纵向一维盒式。
        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
            {
                long sumR = 0;
                long sumG = 0;
                long sumB = 0;
                long sumA = 0;

                for (var tap = -half; tap <= half; tap++)
                {
                    var sy = Math.Clamp(y + tap, 0, height - 1);
                    var index = (sy * horizontal.Stride) + (x * RgbaBitmap.BytesPerPixel);
                    sumR += horizontal.Pixels[index];
                    sumG += horizontal.Pixels[index + 1];
                    sumB += horizontal.Pixels[index + 2];
                    sumA += horizontal.Pixels[index + 3];
                }

                var index2 = (y * result.Stride) + (x * RgbaBitmap.BytesPerPixel);
                result.Pixels[index2] = Average(sumR, kernel);
                result.Pixels[index2 + 1] = Average(sumG, kernel);
                result.Pixels[index2 + 2] = Average(sumB, kernel);
                result.Pixels[index2 + 3] = Average(sumA, kernel);
            }
        }

        return result;
    }

    /// <summary>四舍五入的平均（整数域，避免浮点）。</summary>
    private static byte Average(long sum, int count) =>
        (byte)((sum + (count / 2)) / count);
}
