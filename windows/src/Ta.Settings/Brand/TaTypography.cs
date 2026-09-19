using System.Windows;
using System.Windows.Media;

namespace Ta.Settings.Brand;

/// <summary>
/// 字体与版式。
///
/// 对应 macOS <c>TaDesignSystem.swift</c> + 参考文档 §12.3「字体与版式」：
/// - 标题：<c>.system(.headline, design: .serif, weight: .bold)</c> → **中文衬线体**
/// - 正文：系统默认无衬线
/// - 编号数字：system **bold**
///
/// ## 为什么选思源宋体（Noto Serif SC）
/// macOS 的 <c>design: .serif</c> 会走 Apple 的 Songti SC。Windows 上没有等价系统字体，
/// 候选有三类：
/// 1. **Noto Serif SC / Source Han Serif SC（思源宋体）** —— 与 Songti 同样是为屏幕
///    优化的现代宋体，字重齐全（本机已装），是最接近的替代；
/// 2. SimSun（中易宋体）—— 系统必装，但笔画细、小字号下发虚，且没有 Semibold；
/// 3. STSong（华文宋体）—— 只有部分 Windows 版本随 Office 附带。
///
/// 因此取 **思源宋体优先、SimSun 兜底** 的顺序（见 <see cref="SerifFamily"/>），
/// 既保证观感接近 Mac 版，又保证在任何简体中文 Windows 上都能显示衬线。
/// </summary>
public static class TaTypography
{
    /// <summary>
    /// SwiftUI 的磅值（pt）→ WPF 的设备无关像素（DIP）。
    /// 1pt = 1/72 inch，1 DIP = 1/96 inch，所以 1pt = 4/3 DIP。
    /// 这样 20pt 的 title3 在 780 DIP 宽的窗口里的比例，与 Mac 上 20pt 在 780pt 窗口里一致。
    /// </summary>
    public static double Pt(double points) => points * 4.0 / 3.0;

    /// <summary>Windows 11 的默认 UI 字体。</summary>
    public const string SansFamily = "Microsoft YaHei UI";

    /// <summary>标题衬线体候选，按优先级排列。</summary>
    public static readonly IReadOnlyList<string> SerifCandidates = new[]
    {
        "Noto Serif SC",          // 思源宋体（首选）
        "Source Han Serif SC",    // 思源宋体的另一套安装名
        "STSong",                 // 华文宋体
        "SimSun",                 // 中易宋体（系统兜底）
    };

    /// <summary>
    /// 实际解析出的标题衬线体。构造时按 <see cref="SerifCandidates"/> 顺序探测系统已安装字体，
    /// 全部缺失时回落 SimSun（简体中文 Windows 必装）。
    /// </summary>
    public static FontFamily SerifFamily { get; } = ResolveSerif();

    /// <summary>解析标题衬线体（本机已安装的第一个候选）。</summary>
    public static FontFamily ResolveSerif()
    {
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in Fonts.SystemFontFamilies)
        {
            installed.Add(family.Source);
        }

        foreach (var candidate in SerifCandidates)
        {
            if (installed.Contains(candidate))
            {
                return new FontFamily(candidate);
            }
        }

        return new FontFamily("SimSun");
    }

    /// <summary>衬线 + 加粗的标题字体（对应 <c>.headline</c> serif bold / 欢迎页 25pt serif bold）。</summary>
    public static Typeface SerifTitle => new(SerifFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    /// <summary>衬线常规（用于「拓」字回退图标）。</summary>
    public static Typeface SerifBold => SerifTitle;

    // ---- SwiftUI 文本样式 → DIP（对应参考文档 §12.3 与各视图里出现过的字号）----

    /// <summary><c>.largeTitle</c>（34pt）。</summary>
    public const double LargeTitle = 34 * 4.0 / 3.0;

    /// <summary><c>.title2</c>（22pt）。</summary>
    public const double Title2 = 22 * 4.0 / 3.0;

    /// <summary><c>.title3.weight(.semibold)</c>（20pt）—— 步骤页大标题。</summary>
    public const double Title3 = 20 * 4.0 / 3.0;

    /// <summary><c>.headline</c>（13pt semibold）—— 卡片主标题。</summary>
    public const double Headline = 13 * 4.0 / 3.0;

    /// <summary><c>.body</c>（13pt）。</summary>
    public const double Body = 13 * 4.0 / 3.0;

    /// <summary><c>.callout</c>（12pt）。</summary>
    public const double Callout = 12 * 4.0 / 3.0;

    /// <summary><c>.subheadline</c>（11pt）。</summary>
    public const double Subheadline = 11 * 4.0 / 3.0;

    /// <summary><c>.footnote</c>（13pt）。</summary>
    public const double Footnote = 13 * 4.0 / 3.0;

    /// <summary><c>.caption</c>（11pt）。</summary>
    public const double Caption = 11 * 4.0 / 3.0;

    /// <summary><c>.caption2</c>（10pt）—— 最常用的一行说明文字。</summary>
    public const double Caption2 = 10 * 4.0 / 3.0;

    /// <summary>标签栏图标（SwiftUI <c>.system(size: 17)</c>）。</summary>
    public const double TabIcon = 17 * 4.0 / 3.0;

    /// <summary>标签栏标题（SwiftUI <c>.system(size: 12)</c>）。</summary>
    public const double TabTitle = 12 * 4.0 / 3.0;

    /// <summary>等宽字体（对应 SwiftUI <c>design: .monospaced</c>）。</summary>
    public const string MonoFamily = "Cascadia Mono, Consolas";
}
