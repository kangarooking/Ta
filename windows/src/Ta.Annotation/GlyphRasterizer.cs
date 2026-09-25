using System.Runtime.InteropServices;
using Ta.Core.Drawing;

namespace Ta.Annotation;

/// <summary>
/// 文字光栅化：Win32 <c>GetGlyphOutlineW</c>（GGO_NATIVE）取字形轮廓，
/// 交给 <see cref="SoftwareCanvas"/> 填充。
///
/// 对应 Mac: TaAgentAnnotationRenderer.drawText(_:height:context:)（:141-157）
/// 与 drawNumber(_:height:context:)（:159-179）—— 两者都用 CoreText 的
/// <c>CTFontCreateUIFontForLanguage(.system, size, "zh-Hans")</c> 拿系统 UI 字体，
/// 再画 CTLine。
///
/// ⚠️ 保真度差距（参考文档 §14 风险 #12）：
///   · macOS 侧实际用的是 **PingFang SC**（简体中文系统字体），Windows 上没有等价字体，
///     这里取系统消息框字体（简体中文系统上即「微软雅黑」）。**字形外形与度量都会不同**，
///     因此文字盒尺寸、换行位置与 Mac 不可能逐像素一致 —— 这是字体层面的硬差异，
///     不是算法差异。
///   · CoreText 会做**整形 + 字偶距**（shaping/kerning），GDI 的 GetGlyphOutlineW 逐码元
///     取轮廓、按 gmCellIncX 推进，**不做 kerning**。拉丁文本的字距会有个位数像素差异。
///   · 基线语义已精确复现：Mac 的 textPosition.y = height - origin.y - fontSize
///     在左上原点画布上等价于「第一条基线在 origin.y + fontSize」。
///   · 未实现复杂文本整形（阿拉伯文/天城文连字等）—— 逐码元取轮廓，与 Mac 的 CTLine
///     在这些脚本上结果不同。配方里的文字标注是 CJK/拉丁，不受影响。
///
/// 选 GDI 而非 System.Windows.Media 的原因：不引入 WPF 框架引用。
/// 加了 <c>UseWPF</c> 会让本程序集依赖 Microsoft.WindowsDesktop.App 共享框架，
/// 任何非 WPF 的宿主进程（CLI/桥接）都会在运行时加载失败。
/// </summary>
internal static class GlyphRasterizer
{
    /// <summary>GGO_NATIVE：返回 TrueType 二次贝塞尔轮廓。</summary>
    private const uint FormatNative = 2;

    /// <summary>TT_POLYGON_TYPE。</summary>
    private const int PolygonType = 24;

    private const ushort PrimitiveLine = 1;
    private const ushort PrimitiveQuadraticSpline = 2;

    private const byte DefaultCharset = 1;

    private const uint SpiGetNonClientMetrics = 0x0029;

    /// <summary>
    /// 中文回退链。GDI 的字体链接（font linking）通常已能自动回落，
    /// 这里是显式兜底：系统 UI 字体取不到该字形时依次尝试。
    /// </summary>
    private static readonly string[] ChineseFallbackFaces =
    [
        "Microsoft YaHei",
        "SimSun",
        "DengXian",
        "Microsoft JhengHei",
    ];

    private static readonly Lazy<string> SystemUiFace = new(ResolveSystemUiFace);

    /// <summary>字形轮廓：已摊平成折线的闭合轮廓 + 填充规则。</summary>
    public readonly record struct TextContour(PointD[] Points, FillRule Rule);

    /// <summary>文字度量。</summary>
    public readonly record struct TextMeasurement(double Width, double Height, double Ascent, double Descent);

    /// <summary>
    /// 多行行距系数。CoreText 无 CTParagraphStyle 时用字体默认行距（≈1.2em），
    /// 这里用同一量级的近似值 —— 配方文字标注不含换行，此路径仅作防御。
    /// </summary>
    private const double LineSpacingFactor = 1.2;

    /// <summary>
    /// 取一段文字的字形轮廓，坐标已换算到「左上原点、Y 向下」的画布空间，
    /// 第一条基线落在 y = 0（调用方再平移到 origin.y + fontSize）。
    /// </summary>
    public static IReadOnlyList<TextContour> Outlines(string text, double fontSize, bool bold)
    {
        if (string.IsNullOrEmpty(text) || fontSize <= 0)
        {
            return [];
        }

        var face = SystemUiFace.Value;
        var contours = new List<TextContour>();
        var lineAdvance = fontSize * LineSpacingFactor;
        var lineIndex = 0;

        foreach (var line in SplitLines(text))
        {
            var baselineY = lineIndex * lineAdvance;
            var penX = 0.0;

            foreach (var unit in line)
            {
                var glyph = TryGetGlyph(face, fontSize, bold, unit);
                if (glyph is null)
                {
                    continue;
                }

                var (metrics, points) = glyph.Value;

                // GDI 轮廓 Y 轴向上、原点在基线上的字形原点 → 翻转到画布坐标。
                foreach (var contour in points)
                {
                    var converted = new PointD[contour.Length];
                    for (var i = 0; i < contour.Length; i++)
                    {
                        converted[i] = new PointD(
                            penX + contour[i].X,
                            baselineY - contour[i].Y);
                    }

                    contours.Add(new TextContour(converted, FillRule.NonZero));
                }

                penX += metrics.CellIncX;
            }

            lineIndex++;
        }

        return contours;
    }

    /// <summary>
    /// 度量一段文字。用 <c>GetTextExtentPoint32W</c>（参考任务要求），
    /// 高度取 tmAscent + tmDescent。
    /// </summary>
    public static TextMeasurement Measure(string text, double fontSize, bool bold)
    {
        if (string.IsNullOrEmpty(text) || fontSize <= 0)
        {
            return new TextMeasurement(0, fontSize, fontSize * 0.8, fontSize * 0.2);
        }

        return WithFont(SystemUiFace.Value, fontSize, bold, dc =>
        {
            GetTextMetricsW(dc, out var metrics);
            var line = FirstLine(text);
            var width = GetTextExtentPoint32W(dc, line, line.Length, out var size) ? size.Cx : 0;
            return new TextMeasurement(
                width,
                metrics.Ascent + metrics.Descent,
                metrics.Ascent,
                metrics.Descent);
        });
    }

    /// <summary>供编号气泡做「字形包围盒居中」使用。对应 Mac: CTLineGetBoundsWithOptions(.useGlyphPathBounds)。</summary>
    public static (double MinX, double MinY, double MaxX, double MaxY, bool Empty) ContourBounds(IReadOnlyList<TextContour> contours)
    {
        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;

        foreach (var contour in contours)
        {
            foreach (var point in contour.Points)
            {
                if (point.X < minX) minX = point.X;
                if (point.X > maxX) maxX = point.X;
                if (point.Y < minY) minY = point.Y;
                if (point.Y > maxY) maxY = point.Y;
            }
        }

        return double.IsPositiveInfinity(minX)
            ? (0, 0, 0, 0, true)
            : (minX, minY, maxX, maxY, false);
    }

    // ── 字形获取 ─────────────────────────────────────────────────

    private static (GlyphMetrics Metrics, List<PointD[]> Contours)? TryGetGlyph(
        string face,
        double fontSize,
        bool bold,
        uint codeUnit)
    {
        var primary = WithFont(face, fontSize, bold, dc => ReadGlyph(dc, codeUnit));
        if (primary is not null)
        {
            return primary;
        }

        foreach (var fallback in ChineseFallbackFaces)
        {
            if (string.Equals(fallback, face, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var result = WithFont(fallback, fontSize, bold, dc => ReadGlyph(dc, codeUnit));
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    /// <summary>
    /// 单位矩阵的 GetGlyphOutlineW 包装。矩阵必须是局部变量 ——
    /// 静态只读字段不能作为 ref 实参传递。
    /// </summary>
    private static uint GetGlyphOutline(
        IntPtr dc,
        uint codeUnit,
        ref GlyphMetrics metrics,
        uint bufferSize,
        IntPtr buffer)
    {
        var matrix = new MAT2
        {
            E11 = new Fixed { Value = 1 },
            E12 = new Fixed { Value = 0 },
            E21 = new Fixed { Value = 0 },
            E22 = new Fixed { Value = 1 },
        };

        return GetGlyphOutlineW(dc, codeUnit, FormatNative, ref metrics, bufferSize, buffer, ref matrix);
    }

    private static (GlyphMetrics Metrics, List<PointD[]> Contours)? ReadGlyph(IntPtr dc, uint codeUnit)
    {
        var metrics = default(GlyphMetrics);
        var required = GetGlyphOutline(dc, codeUnit, ref metrics, 0, IntPtr.Zero);

        if (required == 0xFFFFFFFF)
        {
            // GDI_ERROR —— 该字体没有这个字形。
            return null;
        }

        var contours = new List<PointD[]>();
        if (required == 0)
        {
            // 空格等无轮廓字符：只推进，不产出轮廓。
            return (metrics, contours);
        }

        var buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            var written = GetGlyphOutline(dc, codeUnit, ref metrics, required, buffer);
            if (written == 0xFFFFFFFF)
            {
                return null;
            }

            ParseNativeBuffer(buffer, (int)written, contours);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return (metrics, contours);
    }

    /// <summary>
    /// 解析 GGO_NATIVE 缓冲区。
    ///
    /// 布局（全部手工按偏移读取，避免结构体对齐歧义）：
    ///   [TTPOLYGONHEADER] cb:4, dwType:4, pfxStart:8
    ///   [TTPOLYCURVE]*   wType:2, cpfx:2, apfx[cpfx]:8 each
    /// 每个 TTPOLYGONHEADER 是一条闭合轮廓；TTPOLYCURVE 是其中的一段。
    /// </summary>
    private static void ParseNativeBuffer(IntPtr buffer, int totalBytes, List<PointD[]> contours)
    {
        var offset = 0;

        while (offset + 16 <= totalBytes)
        {
            var headerBytes = Marshal.ReadInt32(buffer, offset);
            var headerType = Marshal.ReadInt32(buffer, offset + 4);
            if (headerType != PolygonType || headerBytes <= 16)
            {
                break;
            }

            var points = new List<PointD>
            {
                ReadPointFx(buffer, offset + 8),
            };

            var curveOffset = offset + 16;
            var headerEnd = offset + headerBytes;

            while (curveOffset + 4 <= headerEnd)
            {
                var primitive = (ushort)Marshal.ReadInt16(buffer, curveOffset);
                var count = (ushort)Marshal.ReadInt16(buffer, curveOffset + 2);
                if (count == 0)
                {
                    break;
                }

                var pointOffset = curveOffset + 4;

                if (primitive == PrimitiveLine)
                {
                    for (var i = 0; i < count; i++)
                    {
                        points.Add(ReadPointFx(buffer, pointOffset + (i * 8)));
                    }
                }
                else if (primitive == PrimitiveQuadraticSpline)
                {
                    // TrueType 二次贝塞尔：最后一个点是 on-curve，
                    // 中间点是 off-curve 控制点（相邻控制点之间隐含 on-curve 中点）。
                    var onCurve = new PointD[count];
                    for (var i = 0; i < count; i++)
                    {
                        onCurve[i] = ReadPointFx(buffer, pointOffset + (i * 8));
                    }

                    var start = points[^1];
                    for (var i = 0; i < count - 1; i++)
                    {
                        var control = onCurve[i];
                        var end = i == count - 2 ? onCurve[count - 1] : Midpoint(onCurve[i], onCurve[i + 1]);
                        AppendQuadratic(points, start, control, end);
                        start = end;
                    }

                    points.Add(onCurve[count - 1]);
                }

                curveOffset = pointOffset + (count * 8);
            }

            if (points.Count >= 3)
            {
                contours.Add([.. points]);
            }

            offset += headerBytes;
        }
    }

    /// <summary>二次贝塞尔按控制多边形长度自适应细分（弦高 ≈ 0.1px）。</summary>
    private static void AppendQuadratic(List<PointD> target, PointD start, PointD control, PointD end)
    {
        var steps = Math.Clamp(
            (int)Math.Ceiling((Distance(start, control) + Distance(control, end)) / 2.0),
            2,
            24);

        for (var step = 1; step <= steps; step++)
        {
            var t = (double)step / steps;
            var u = 1 - t;
            target.Add(new PointD(
                (u * u * start.X) + ((2 * u * t) * control.X) + (t * t * end.X),
                (u * u * start.Y) + ((2 * u * t) * control.Y) + (t * t * end.Y)));
        }
    }

    private static PointD Midpoint(PointD a, PointD b) =>
        new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    private static double Distance(PointD a, PointD b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>
    /// 读 POINTFX（两个 FIXED，各 16.16 定点数）。
    ///
    /// ⚠️ FIXED 的 <c>value</c> 是**有符号** 16 位 —— 拆成高低字分别解读会把
    /// 基线下方的小负坐标（如 -0.86）读成 +65535，字形整体错位到画布外。
    /// 直接把整个 32 位当有符号 16.16 定点数除以 65536 即可。
    /// </summary>
    private static PointD ReadPointFx(IntPtr buffer, int offset)
    {
        var x = Marshal.ReadInt32(buffer, offset);
        var y = Marshal.ReadInt32(buffer, offset + 4);
        return new PointD(FromFixed(x), FromFixed(y));
    }

    private static double FromFixed(int bits) => bits / 65536.0;

    // ── 换行与码元 ───────────────────────────────────────────────

    /// <summary>按换行切分。行距见 <see cref="LineSpacingFactor"/>。</summary>
    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static string FirstLine(string text) => SplitLines(text)[0];

    // ── Win32 ────────────────────────────────────────────────────

    /// <summary>在临时字体 DC 上执行动作，用完即销毁（GDI 对象有线程亲和性，故不复用）。</summary>
    private static T WithFont<T>(string face, double fontSize, bool bold, Func<IntPtr, T> action)
    {
        var (dc, font) = CreateCalibratedDc(face, fontSize, bold);
        if (dc == IntPtr.Zero)
        {
            return default!;
        }

        try
        {
            return action(dc);
        }
        finally
        {
            // 先把字体从 DC 里摘出来再销毁 —— GDI 对象在选中状态下删除会失败。
            SelectObject(dc, font);
            DeleteObject(font);
            DeleteDC(dc);
        }
    }

    /// <summary>
    /// 按 **em 尺寸**校准后创建字体 DC。
    ///
    /// ⚠️ 关键：GDI 的 <c>lfHeight</c> 是「字符高度」(cell = ascent + descent)，
    /// 而 macOS 的 <c>CTFontCreateUIFontForLanguage(.system, size)</c> 用的是 **em 尺寸**。
    /// 对中文字体（微软雅黑）两者相差约 1.3 倍 —— 直接拿 fontSize 当 lfHeight
    /// 会让所有中文字形整体放大约 30%，基线上方的墨迹溢出到画布外。
    ///
    /// 校准用 TrueType 的经典关系 <c>emPx = tmHeight - tmInternalLeading</c>
    /// 迭代收敛（最多 6 次，通常 2 次内命中）。
    /// </summary>
    private static (IntPtr Dc, IntPtr Font) CreateCalibratedDc(string face, double fontSize, bool bold)
    {
        var dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            return (IntPtr.Zero, IntPtr.Zero);
        }

        var height = -(int)Math.Max(1, Math.Round(fontSize, MidpointRounding.AwayFromZero));
        var font = IntPtr.Zero;

        for (var attempt = 0; attempt < 6; attempt++)
        {
            var logFont = LogFont(height, face, bold);
            var candidate = CreateFontIndirectW(ref logFont);
            if (candidate == IntPtr.Zero)
            {
                break;
            }

            // 选中新字体并销毁上一轮的旧字体。
            var previous = SelectObject(dc, candidate);
            if (previous != IntPtr.Zero)
            {
                DeleteObject(previous);
            }

            font = candidate;

            GetTextMetricsW(dc, out var metrics);
            var emPixels = metrics.tmHeight - metrics.tmInternalLeading;
            if (emPixels <= 0)
            {
                break;
            }

            if (Math.Abs(emPixels - fontSize) <= 0.5)
            {
                break;
            }

            var next = (int)Math.Round(height * (fontSize / emPixels));
            if (next == 0 || next == height)
            {
                break;
            }

            height = next;
        }

        return (dc, font);
    }

    private static LOGFONTW LogFont(int height, string face, bool bold) =>
        new()
        {
            // 负值 = 字符高度（像素）。
            lfHeight = height,
            lfWeight = bold ? 700 : 400,
            lfCharSet = DefaultCharset,
            lfQuality = 4, // CLEARTYPE_QUALITY —— 只影响光栅化，不影响轮廓
            lfFaceName = face,
        };

    private static string ResolveSystemUiFace()
    {
        try
        {
            var metrics = new NonClientMetricsW
            {
                cbSize = Marshal.SizeOf<NonClientMetricsW>(),
            };

            if (SystemParametersInfoW(SpiGetNonClientMetrics, (uint)metrics.cbSize, ref metrics, 0)
                && !string.IsNullOrWhiteSpace(metrics.lfMessageFont.lfFaceName))
            {
                return metrics.lfMessageFont.lfFaceName;
            }
        }
        catch
        {
            // 取不到就回落默认字体名。
        }

        return "Segoe UI";
    }

    [DllImport("gdi32.dll", EntryPoint = "CreateCompatibleDC", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", EntryPoint = "CreateFontIndirectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFontIndirectW(ref LOGFONTW logFont);

    [DllImport("gdi32.dll", EntryPoint = "SelectObject", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [DllImport("gdi32.dll", EntryPoint = "DeleteObject", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", EntryPoint = "DeleteDC", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", EntryPoint = "GetGlyphOutlineW", SetLastError = true)]
    private static extern uint GetGlyphOutlineW(
        IntPtr hdc,
        uint uChar,
        uint fuFormat,
        ref GlyphMetrics lpgm,
        uint cjBuffer,
        IntPtr pvBuffer,
        ref MAT2 lpmat2);

    [DllImport("gdi32.dll", EntryPoint = "GetTextExtentPoint32W", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetTextExtentPoint32W(IntPtr hdc, string text, int length, out Size size);

    [DllImport("gdi32.dll", EntryPoint = "GetTextMetricsW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetTextMetricsW(IntPtr hdc, out TextMetrics metrics);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfoW(uint action, uint uiParam, ref NonClientMetricsW data, uint winIni);

    [StructLayout(LayoutKind.Sequential)]
    private struct Fixed
    {
        public ushort Fract;
        public ushort Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MAT2
    {
        public Fixed E11;
        public Fixed E12;
        public Fixed E21;
        public Fixed E22;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GlyphMetrics
    {
        public int gmBlackBoxX;
        public int gmBlackBoxY;
        public PointL gmptGlyphOrigin;
        public short gmCellIncX;
        public short gmCellIncY;

        public int CellIncX => gmCellIncX;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointL
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size
    {
        public int Cx;
        public int Cy;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LOGFONTW
    {
        public int lfHeight;
        public int lfWidth;
        public int lfEscapement;
        public int lfOrientation;
        public int lfWeight;
        public byte lfItalic;
        public byte lfUnderline;
        public byte lfStrikeOut;
        public byte lfCharSet;
        public byte lfOutPrecision;
        public byte lfClipPrecision;
        public byte lfQuality;
        public byte lfPitchAndFamily;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string lfFaceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TextMetrics
    {
        public int tmHeight;
        public int tmAscent;
        public int tmDescent;
        public int tmInternalLeading;
        public int tmExternalLeading;
        public int tmAveCharWidth;
        public int tmMaxCharWidth;
        public int tmWeight;
        public int tmOverhang;
        public int tmDigitizedAspectX;
        public int tmDigitizedAspectY;
        public char tmFirstChar;
        public char tmLastChar;
        public char tmDefaultChar;
        public char tmBreakChar;
        public byte tmItalic;
        public byte tmUnderlined;
        public byte tmStruckOut;
        public byte tmPitchAndFamily;
        public byte tmCharSet;

        public int Ascent => tmAscent;
        public int Descent => tmDescent;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NonClientMetricsW
    {
        public int cbSize;
        public int iBorderWidth;
        public int iScrollWidth;
        public int iScrollHeight;
        public int iCaptionWidth;
        public int iCaptionHeight;

        public LOGFONTW lfCaptionFont;

        public int iSmCaptionWidth;
        public int iSmCaptionHeight;

        public LOGFONTW lfMenuFont;

        public LOGFONTW lfMessageFont;

        public int iPaddedBorderWidth;
    }
}
