using System.Runtime.InteropServices;
using System.Text;

namespace Ta.Translate.Rendering;

/// <summary>
/// 文字渲染所需的 Win32 GDI 互操作（<b>纯 GDI，不使用 System.Drawing / GDI+</b>）。
///
/// 只用三组能力：
/// 1. 字体创建与度量（CreateFontIndirectW / GetTextMetricsW / GetTextExtentPoint32W）——
///    对应 Mac 的 NSFont 度量（boundingRect + usesFontLeading）；
/// 2. 文字栅格化（TextOutW 画进 32bpp DIB section 得到灰度遮罩）——
///    对应 Mac 的 NSString.draw；
/// 3. 像素填充（PatBlt）。
///
/// ⚠️ 中文字体回退是保真难点：GDI 按字体名取字，没有 CoreText 那种
/// <c>CTFontCreateUIFontForLanguage(.system, size, "zh-Hans")</c> 的语言感知级联。
/// 本实现按<b>文字内容</b>选字体族（见 <see cref="FontFaces"/>）：含 CJK 码位走微软雅黑，
/// 纯拉丁走 Segoe UI —— 与 Mac 的系统字体（亮色 PingFang SC / SF Pro）在字宽、
/// 行高、字重上都不一致，属于近似映射，详见报告的保真度说明。
/// </summary>
internal static class NativeMethods
{
    private const string Gdi32 = "gdi32.dll";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct LOGFONTW
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

    [StructLayout(LayoutKind.Sequential)]
    public struct TEXTMETRICW
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
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColorsR;
        public uint bmiColorsG;
        public uint bmiColorsB;
    }

    public const uint WHITENESS = 0x00FF0062;
    public const int TRANSPARENT = 1;
    public const int ANTIALIASED_QUALITY = 4;
    public const byte DEFAULT_CHARSET = 1;
    public const uint BI_RGB = 0;
    public const uint DIB_RGB_COLORS = 0;

    [DllImport(Gdi32, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport(Gdi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport(Gdi32, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateFontIndirectW(ref LOGFONTW lplf);

    [DllImport(Gdi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport(Gdi32, SetLastError = true)]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport(Gdi32, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetTextMetricsW(IntPtr hdc, out TEXTMETRICW lptm);

    [DllImport(Gdi32, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetTextExtentPoint32W(
        IntPtr hdc,
        [MarshalAs(UnmanagedType.LPWStr)] string lpString,
        int c,
        out SIZE lpSize);

    [DllImport(Gdi32, SetLastError = true)]
    public static extern IntPtr CreateDIBSection(
        IntPtr hdc,
        ref BITMAPINFO pbmi,
        uint usage,
        out IntPtr ppvBits,
        IntPtr hSection,
        uint offset);

    [DllImport(Gdi32, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TextOutW(
        IntPtr hdc,
        int x,
        int y,
        [MarshalAs(UnmanagedType.LPWStr)] string lpString,
        int c);

    [DllImport(Gdi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PatBlt(IntPtr hdc, int x, int y, int w, int h, uint rop);

    [DllImport(Gdi32, SetLastError = true)]
    public static extern uint SetTextColor(IntPtr hdc, uint color);

    [DllImport(Gdi32, SetLastError = true)]
    public static extern int SetBkMode(IntPtr hdc, int mode);
}

/// <summary>
/// 字体族选择。
///
/// Mac 版用 <c>NSFont.systemFont(ofSize:weight:)</c> + CoreText 的语言感知字体级联；
/// Windows/GDI 没有等价机制，只能按内容显式选族。这是移植的主要保真度差距之一。
/// </summary>
internal static class FontFaces
{
    /// <summary>纯拉丁文本（对应 Mac 的 SF Pro / Segoe UI 系）。</summary>
    public const string Latin = "Segoe UI";

    /// <summary>含 CJK 码位的文本（对应 Mac 的 PingFang SC / 微软雅黑）。</summary>
    public const string Cjk = "Microsoft YaHei";

    /// <summary>GDI 字重。medium=400、semibold=600、bold=700 —— 对应 NSFont.Weight。</summary>
    public const int WeightMedium = 400;
    public const int WeightSemibold = 600;
    public const int WeightBold = 700;

    /// <summary>按文字内容选字体族：含 CJK 码位即走中文族（含 fullwidth/谚文/假名）。</summary>
    public static string ResolveFace(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsCjkCodePoint(rune.Value))
            {
                return Cjk;
            }
        }

        return Latin;
    }

    /// <summary>CJK / 兼容区段判定（Unicode 常用 CJK 区间的合集）。分词器与字体族选择共用。</summary>
    internal static bool IsCjkCodePoint(int cp) =>
        (cp >= 0x1100 && cp <= 0x11FF)      // Hangul Jamo
        || (cp >= 0x2E80 && cp <= 0x9FFF)   // CJK 部首 → 统一表意文字（含标点 0x3000-0x303F）
        || (cp >= 0xA000 && cp <= 0xA4CF)   // 彝文
        || (cp >= 0xAC00 && cp <= 0xD7AF)   // 谚文音节
        || (cp >= 0xF900 && cp <= 0xFAFF)   // 兼容表意文字
        || (cp >= 0xFE30 && cp <= 0xFE4F)   // CJK 兼容形式
        || (cp >= 0xFF00 && cp <= 0xFF60)   // 全角形式
        || (cp >= 0xFFE0 && cp <= 0xFFE6)   // 全角符号
        || (cp >= 0x20000 && cp <= 0x2FA1F) // 扩展表意文字
        || (cp >= 0x3040 && cp <= 0x30FF);  // 平假名 / 片假名
}
