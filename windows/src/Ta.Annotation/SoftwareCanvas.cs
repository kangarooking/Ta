using Ta.Core.Agent;
using Ta.Core.Drawing;
using Ta.Core.Imaging;

namespace Ta.Annotation;

/// <summary>
/// 填充规则。
///
/// CGContext.fillPath() 默认用非零环绕（nonzero winding），
/// 因此除文字轮廓外一律使用 <see cref="NonZero"/>。
/// 对应 Mac: TaAgentAnnotationRenderer.swift 的 fillPath / clip 调用。
/// </summary>
internal enum FillRule
{
    /// <summary>非零环绕。CGContext 默认，对应 fillPath()。</summary>
    NonZero,

    /// <summary>奇偶规则。文字轮廓由字形提供方决定，见 <see cref="TextContour.Rule"/>。</summary>
    EvenOdd,
}

/// <summary>
/// 直通（未预乘）颜色，分量取值 0..1。
///
/// RgbaBitmap 的字节序是未预乘 RGBA（见 RgbaBitmap 类注释），
/// 因此合成前**不能**先乘 alpha —— 预乘会让高亮笔的半透明叠加整体变暗。
/// </summary>
internal readonly record struct Rgba(double R, double G, double B, double A)
{
    /// <summary>从配方颜色转换。对应 Mac: TaAgentAnnotationRenderer.cgColor(_:)（:357-359）。</summary>
    public static Rgba From(AnnotationColor color) =>
        new(color.Red, color.Green, color.Blue, color.Alpha);

    /// <summary>编号气泡上的白色数字。对应 Mac: CGColor(gray: 1, alpha: 1)（:172）。</summary>
    public static readonly Rgba White = new(1, 1, 1, 1);
}

/// <summary>
/// 位图放置描述：把源图的一块矩形映射到画布的一块矩形。
///
/// 马赛克/模糊是「1:1 覆盖」（<see cref="IsIdentity"/> 为真），
/// 放大镜是「源区域 → 目标椭圆」的缩放映射。
/// </summary>
internal readonly record struct ImagePlacement(
    RgbaBitmap Source,
    double DestinationX,
    double DestinationY,
    double DestinationWidth,
    double DestinationHeight,
    double SourceX,
    double SourceY,
    double SourceWidth,
    double SourceHeight,
    bool NearestNeighbor)
{
    /// <summary>源矩形与目标矩形位置尺寸完全相同 —— 逐像素直拷，无重采样误差。</summary>
    public bool IsIdentity =>
        Math.Abs(DestinationX - SourceX) < 1e-9
        && Math.Abs(DestinationY - SourceY) < 1e-9
        && Math.Abs(DestinationWidth - SourceWidth) < 1e-9
        && Math.Abs(DestinationHeight - SourceHeight) < 1e-9;
}

/// <summary>
/// 纯托管软件光栅化画布。
///
/// 对应 Mac 版的 CGContext（TaAgentAnnotationRenderer.swift:16-28）：
///   · 8bpc RGBA、抗锯齿开启（context.setShouldAntialias(true)）
///   · 左上原点、Y 向下 —— **配方坐标在 Windows 上无需翻转**
///
/// 抗锯齿实现说明：Quartz 对 8bpc 位图上下文用 4×4 子像素网格
/// （每个分量 4 位子像素精度）。这里用 **16 条子扫描线**（y 方向 16 个采样），
/// x 方向按扫描线区间做**解析式**覆盖累加 —— x 精度优于点采样，
/// 因此水平/垂直边缘的覆盖值与 Quartz 4×4 点采样在舍入前基本一致，
/// 但不保证逐字节相同（Quartz 内部舍入不公开）。
///
/// 形状一律走「多边形 + 非零环绕」：
/// 描边由 <see cref="StrokeGeometry"/> 预先膨胀成单一闭合轮廓，
/// 这样重叠区不会被重复混合 —— 对 alpha &lt; 1 的高亮笔尤其重要
/// （若按「线段逐段填充」实现，重叠处会出现可见接缝）。
/// </summary>
internal sealed class SoftwareCanvas
{
    /// <summary>每条像素行的子扫描线数。Quartz 是 4×4 子像素，y 向 16 采样已足够。</summary>
    private const int SubScanlines = 16;

    private readonly RgbaBitmap _bitmap;
    private readonly int _stride;
    private readonly int _width;
    private readonly int _height;

    /// <summary>逐像素覆盖度暂存，长度 = 当前行的包围盒宽度。</summary>
    private double[] _coverage = new double[256];

    /// <summary>扫描线交点，按需增长（数量 = 2 × 边数）。</summary>
    private Crossing[] _crossings = new Crossing[64];

    private int _crossingCount;

    public SoftwareCanvas(RgbaBitmap bitmap)
    {
        _bitmap = bitmap;
        _stride = bitmap.Stride;
        _width = bitmap.Width;
        _height = bitmap.Height;
    }

    public RgbaBitmap Bitmap => _bitmap;

    /// <summary>填充多边形。对应 Mac: addPath + fillPath()。</summary>
    public void FillPolygon(IReadOnlyList<PointD> polygon, Rgba color, FillRule rule = FillRule.NonZero)
    {
        if (polygon.Count < 3 || color.A <= 0)
        {
            return;
        }

        Rasterize(polygon, rule, (y, coverage, firstPixelX, count) =>
        {
            for (var i = 0; i < count; i++)
            {
                var c = coverage[i];
                if (c <= 0)
                {
                    continue;
                }

                BlendPixel(firstPixelX + i, y, color, c > 1 ? 1 : c);
            }
        });
    }

    /// <summary>
    /// 用图像填充多边形（带覆盖度）。对应 Mac 的 clip + draw(image, in:)。
    /// </summary>
    public void FillPolygonWithImage(
        IReadOnlyList<PointD> polygon,
        ImagePlacement placement,
        FillRule rule = FillRule.NonZero)
    {
        if (polygon.Count < 3)
        {
            return;
        }

        var source = placement.Source;
        var identity = placement.IsIdentity;

        Rasterize(polygon, rule, (y, coverage, firstPixelX, count) =>
        {
            for (var i = 0; i < count; i++)
            {
                var c = coverage[i];
                if (c <= 0)
                {
                    continue;
                }

                if (c > 1)
                {
                    c = 1;
                }

                var x = firstPixelX + i;
                var u = ((x + 0.5) - placement.DestinationX) / placement.DestinationWidth;
                var v = ((y + 0.5) - placement.DestinationY) / placement.DestinationHeight;

                // 多边形可能伸出目标矩形（放大镜的椭圆是内接的，不会发生），
                // 越界处保持画布原样。
                if (u < 0 || u >= 1 || v < 0 || v >= 1)
                {
                    continue;
                }

                var index = (y * _stride) + (x * RgbaBitmap.BytesPerPixel);

                if (identity)
                {
                    // 1:1 覆盖：直接逐字节搬运，不过采样器，保证滤镜结果零损耗。
                    var sourceIndex = (y * source.Stride) + (x * RgbaBitmap.BytesPerPixel);
                    if (c >= 1)
                    {
                        _bitmap.Pixels[index] = source.Pixels[sourceIndex];
                        _bitmap.Pixels[index + 1] = source.Pixels[sourceIndex + 1];
                        _bitmap.Pixels[index + 2] = source.Pixels[sourceIndex + 2];
                        _bitmap.Pixels[index + 3] = source.Pixels[sourceIndex + 3];
                        continue;
                    }
                }

                var (r, g, b, a) = Sample(source, placement, u, v);
                BlendPixel(index, new Rgba(r, g, b, a), c);
            }
        });
    }

    /// <summary>按放置关系采样源图像素，返回 0..1 的直通颜色。</summary>
    private static (double R, double G, double B, double A) Sample(
        RgbaBitmap source,
        ImagePlacement placement,
        double u,
        double v)
    {
        var sx = placement.SourceX + (u * placement.SourceWidth);
        var sy = placement.SourceY + (v * placement.SourceHeight);

        if (placement.NearestNeighbor)
        {
            var ix = ClampIndex((int)Math.Floor(sx), placement.SourceX, placement.SourceWidth);
            var iy = ClampIndex((int)Math.Floor(sy), placement.SourceY, placement.SourceHeight);
            var index = (iy * source.Stride) + (ix * RgbaBitmap.BytesPerPixel);
            return (
                source.Pixels[index] / 255.0,
                source.Pixels[index + 1] / 255.0,
                source.Pixels[index + 2] / 255.0,
                source.Pixels[index + 3] / 255.0);
        }

        // 双线性。
        var x0 = ClampIndex((int)Math.Floor(sx), placement.SourceX, placement.SourceWidth);
        var y0 = ClampIndex((int)Math.Floor(sy), placement.SourceY, placement.SourceHeight);
        var x1 = ClampIndex(x0 + 1, placement.SourceX, placement.SourceWidth);
        var y1 = ClampIndex(y0 + 1, placement.SourceY, placement.SourceHeight);

        var fx = Math.Clamp(sx - Math.Floor(sx), 0, 1);
        var fy = Math.Clamp(sy - Math.Floor(sy), 0, 1);

        Span<double> result = stackalloc double[4];
        ReadOnlySpan<(int X, int Y, double W)> taps =
        [
            (x0, y0, (1 - fx) * (1 - fy)),
            (x1, y0, fx * (1 - fy)),
            (x0, y1, (1 - fx) * fy),
            (x1, y1, fx * fy),
        ];

        foreach (var (tapX, tapY, weight) in taps)
        {
            var index = (tapY * source.Stride) + (tapX * RgbaBitmap.BytesPerPixel);
            for (var channel = 0; channel < 4; channel++)
            {
                result[channel] += (source.Pixels[index + channel] / 255.0) * weight;
            }
        }

        return (result[0], result[1], result[2], result[3]);
    }

    private static int ClampIndex(int value, double origin, double length)
    {
        var upper = origin + length;
        if (value < origin)
        {
            value = (int)Math.Ceiling(origin);
        }

        if (value > upper - 1)
        {
            value = (int)Math.Ceiling(upper) - 1;
        }

        return value;
    }

    /// <summary>source-over 混合单像素（直通 alpha）。</summary>
    private void BlendPixel(int x, int y, Rgba color, double coverage)
    {
        BlendPixel((y * _stride) + (x * RgbaBitmap.BytesPerPixel), color, coverage);
    }

    private void BlendPixel(int index, Rgba color, double coverage)
    {
        var sourceAlpha = color.A * coverage;
        if (sourceAlpha <= 0)
        {
            return;
        }

        var pixels = _bitmap.Pixels;
        var destinationAlpha = pixels[index + 3] / 255.0;
        var inverse = 1 - sourceAlpha;
        var outAlpha = sourceAlpha + (destinationAlpha * inverse);

        if (outAlpha <= 0)
        {
            pixels[index] = 0;
            pixels[index + 1] = 0;
            pixels[index + 2] = 0;
            pixels[index + 3] = 0;
            return;
        }

        for (var channel = 0; channel < 3; channel++)
        {
            var source = channel switch
            {
                0 => color.R,
                1 => color.G,
                _ => color.B,
            };
            var destination = pixels[index + channel] / 255.0;
            var value = ((source * sourceAlpha) + (destination * destinationAlpha * inverse)) / outAlpha;
            pixels[index + channel] = ToByte(value);
        }

        pixels[index + 3] = ToByte(outAlpha);
    }

    private static byte ToByte(double value) =>
        (byte)Math.Clamp((int)((value * 255.0) + 0.5), 0, 255);

    /// <summary>行回调：给定像素行的覆盖度数组、起始列与有效列数。</summary>
    private delegate void RowAction(int y, double[] coverage, int firstPixelX, int count);

    /// <summary>
    /// 扫描线光栅化主循环。
    ///
    /// 每像素 16 条子扫描线；每条子扫描线收集与多边形边的交点，
    /// 按 x 排序后用**环绕数**判定内部区间，区间内按解析宽度累加覆盖度。
    /// </summary>
    private void Rasterize(IReadOnlyList<PointD> polygon, FillRule rule, RowAction action)
    {
        var minX = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var minY = double.PositiveInfinity;
        var maxY = double.NegativeInfinity;

        foreach (var point in polygon)
        {
            if (point.X < minX) minX = point.X;
            if (point.X > maxX) maxX = point.X;
            if (point.Y < minY) minY = point.Y;
            if (point.Y > maxY) maxY = point.Y;
        }

        var rowStart = Math.Max(0, (int)Math.Floor(minY));
        var rowEnd = Math.Min(_height - 1, (int)Math.Ceiling(maxY));
        var columnStart = Math.Max(0, (int)Math.Floor(minX));
        var columnEnd = Math.Min(_width - 1, (int)Math.Ceiling(maxX));

        if (rowEnd < rowStart || columnEnd < columnStart)
        {
            return;
        }

        var rowWidth = (columnEnd - columnStart) + 1;
        if (_coverage.Length < rowWidth)
        {
            _coverage = new double[rowWidth];
        }

        var edgeCount = polygon.Count;
        var step = 1.0 / SubScanlines;

        for (var y = rowStart; y <= rowEnd; y++)
        {
            Array.Clear(_coverage, 0, rowWidth);

            for (var sub = 0; sub < SubScanlines; sub++)
            {
                var scanY = y + ((sub + 0.5) * step);

                _crossingCount = 0;
                for (var edge = 0; edge < edgeCount; edge++)
                {
                    var a = polygon[edge];
                    var b = polygon[(edge + 1) % edgeCount];
                    var ay = a.Y;
                    var by = b.Y;

                    // 半开区间约定：顶点恰好落在扫描线上只计一次。
                    if ((ay <= scanY && by > scanY) || (by <= scanY && ay > scanY))
                    {
                        var t = (scanY - ay) / (by - ay);
                        AddCrossing(a.X + (t * (b.X - a.X)), by > ay ? 1 : -1);
                    }
                }

                if (_crossingCount < 2)
                {
                    continue;
                }

                _crossings.AsSpan(0, _crossingCount).Sort();

                var winding = 0;
                for (var k = 0; k + 1 < _crossingCount; k++)
                {
                    winding += _crossings[k].Winding;
                    if (IsInside(winding, rule))
                    {
                        AccumulateSpan(y, columnStart, rowWidth, _crossings[k].X, _crossings[k + 1].X);
                    }
                }
            }

            action(y, _coverage, columnStart, rowWidth);
        }
    }

    private static bool IsInside(int winding, FillRule rule) =>
        rule == FillRule.NonZero ? winding != 0 : (winding & 1) != 0;

    /// <summary>把区间 [spanStart, spanEnd) 的解析宽度累加进逐像素覆盖度。</summary>
    private void AccumulateSpan(int y, int columnStart, int rowWidth, double spanStart, double spanEnd)
    {
        if (spanEnd <= spanStart)
        {
            return;
        }

        var first = Math.Max((int)Math.Floor(spanStart), columnStart);
        var last = Math.Min((int)Math.Ceiling(spanEnd) - 1, columnStart + rowWidth - 1);

        for (var x = first; x <= last; x++)
        {
            var left = Math.Max(spanStart, x);
            var right = Math.Min(spanEnd, x + 1);
            if (right <= left)
            {
                continue;
            }

            _coverage[x - columnStart] += (right - left) / SubScanlines;
        }
    }

    private void AddCrossing(double x, int winding)
    {
        if (_crossingCount == _crossings.Length)
        {
            Array.Resize(ref _crossings, _crossingCount * 2);
        }

        _crossings[_crossingCount++] = new Crossing(x, winding);
    }

    /// <summary>扫描线交点：x 坐标与该边贡献的环绕数符号。</summary>
    private readonly record struct Crossing(double X, int Winding) : IComparable<Crossing>
    {
        public int CompareTo(Crossing other) => X.CompareTo(other.X);
    }
}
