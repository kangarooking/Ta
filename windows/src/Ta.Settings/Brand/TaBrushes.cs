using System.Windows;
using System.Windows.Media;

namespace Ta.Settings.Brand;

/// <summary>
/// 把 <see cref="TaPalette"/> 的颜色固化成 WPF 画刷。
/// 因为设置界面完全用代码构造（没有 XAML / ResourceDictionary），
/// 画刷必须在这里集中定义并冻结，才能被多个线程/元素安全复用。
/// </summary>
public static class TaBrushes
{
    /// <summary>ink #1A1A1A。</summary>
    public static SolidColorBrush Ink { get; } = Freeze(TaPalette.Ink);

    /// <summary>paper #FAF6EE。</summary>
    public static SolidColorBrush Paper { get; } = Freeze(TaPalette.Paper);

    /// <summary>elevatedPaper #FFFDF8。</summary>
    public static SolidColorBrush ElevatedPaper { get; } = Freeze(TaPalette.ElevatedPaper);

    /// <summary>cinnabar #D6402F（品牌主色）。</summary>
    public static SolidColorBrush Cinnabar { get; } = Freeze(TaPalette.Cinnabar);

    /// <summary>mutedInk #68645E。</summary>
    public static SolidColorBrush MutedInk { get; } = Freeze(TaPalette.MutedInk);

    /// <summary>hairline（ink @ 10%）。</summary>
    public static SolidColorBrush Hairline { get; } = Freeze(TaPalette.Hairline);

    /// <summary>成功色。</summary>
    public static SolidColorBrush Success { get; } = Freeze(TaPalette.Success);

    /// <summary>危险色。</summary>
    public static SolidColorBrush Danger { get; } = Freeze(TaPalette.Danger);

    /// <summary>警示色。</summary>
    public static SolidColorBrush Warning { get; } = Freeze(TaPalette.Warning);

    /// <summary>透明画刷。</summary>
    public static SolidColorBrush Transparent { get; } = Freeze(Colors.Transparent);

    // ---- 下面这些是 SwiftUI 里 <color>.opacity(x) 的等价画刷，按需创建后缓存 ----

    private static readonly Dictionary<string, SolidColorBrush> Cache = new(StringComparer.Ordinal);

    /// <summary>取得 <paramref name="color"/> 在 <paramref name="opacity"/> 下的画刷（带缓存）。</summary>
    public static SolidColorBrush Of(Color color, double opacity)
    {
        var key = $"{color}_{opacity:0.###}";
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var brush = Freeze(TaPalette.WithOpacity(color, opacity));
        Cache[key] = brush;
        return brush;
    }

    /// <summary>cinnabar 的 10% 淡色（标签选中底、推荐徽章底）。</summary>
    public static SolidColorBrush Cinnabar10 => Of(TaPalette.Cinnabar, 0.10);

    /// <summary>cinnabar 的 7% 淡色（服务商行选中底）。</summary>
    public static SolidColorBrush Cinnabar07 => Of(TaPalette.Cinnabar, 0.07);

    /// <summary>cinnabar 的 72% 描边。</summary>
    public static SolidColorBrush Cinnabar72 => Of(TaPalette.Cinnabar, 0.72);

    /// <summary>cinnabar 的 35% 描边。</summary>
    public static SolidColorBrush Cinnabar35 => Of(TaPalette.Cinnabar, 0.35);

    /// <summary>cinnabar 的 24%（步骤连接线已完成）。</summary>
    public static SolidColorBrush Cinnabar24 => Of(TaPalette.Cinnabar, 0.24);

    /// <summary>cinnabar 的 20%（欢迎页主卡片描边）。</summary>
    public static SolidColorBrush Cinnabar20 => Of(TaPalette.Cinnabar, 0.20);

    /// <summary>cinnabar 的 34%（快捷操作 hover 描边）。</summary>
    public static SolidColorBrush Cinnabar34 => Of(TaPalette.Cinnabar, 0.34);

    /// <summary>cinnabar 的 55% 背景（快捷操作 hover 底）。</summary>
    public static SolidColorBrush Cinnabar55 => Of(TaPalette.Cinnabar, 0.055);

    /// <summary>cinnabar 的 9%（快捷操作图标底）。</summary>
    public static SolidColorBrush Cinnabar09 => Of(TaPalette.Cinnabar, 0.09);

    /// <summary>cinnabar 的 14%（快捷操作 hover 图标底）。</summary>
    public static SolidColorBrush Cinnabar14 => Of(TaPalette.Cinnabar, 0.14);

    /// <summary>paper 的 42%（服务商行未选中底）。</summary>
    public static SolidColorBrush Paper42 => Of(TaPalette.Paper, 0.42);

    /// <summary>paper 的 40%（步骤左栏底）。</summary>
    public static SolidColorBrush Paper40 => Of(TaPalette.Paper, 0.40);

    /// <summary>paper 的 54%（AI 页紧凑卡片底）。</summary>
    public static SolidColorBrush Paper54 => Of(TaPalette.Paper, 0.54);

    /// <summary>paper 的 55%（Agent 安装面板底）。</summary>
    public static SolidColorBrush Paper55 => Of(TaPalette.Paper, 0.55);

    /// <summary>paper 的 72%（模型切换菜单底）。</summary>
    public static SolidColorBrush Paper72 => Of(TaPalette.Paper, 0.72);

    /// <summary>paper 的 82%（快捷键徽章底）。</summary>
    public static SolidColorBrush Paper82 => Of(TaPalette.Paper, 0.82);

    /// <summary>paper @ 20% —— 深色卡片上的浅色键帽底。</summary>
    public static SolidColorBrush Paper20 => Of(TaPalette.Paper, 0.20);

    /// <summary>elevatedPaper 的 72%（AI 页容器底）。</summary>
    public static SolidColorBrush ElevatedPaper72 => Of(TaPalette.ElevatedPaper, 0.72);

    /// <summary>elevatedPaper 的 78%。</summary>
    public static SolidColorBrush ElevatedPaper78 => Of(TaPalette.ElevatedPaper, 0.78);

    /// <summary>elevatedPaper 的 84%（翻译页卡片底）。</summary>
    public static SolidColorBrush ElevatedPaper84 => Of(TaPalette.ElevatedPaper, 0.84);

    /// <summary>elevatedPaper 的 96%（标签栏底）。</summary>
    public static SolidColorBrush ElevatedPaper96 => Of(TaPalette.ElevatedPaper, 0.96);

    /// <summary>ink 的 45%（命令块底）。</summary>
    public static SolidColorBrush Ink45 => Of(TaPalette.Ink, 0.045);

    /// <summary>ink 的 60%（安装徽章底）。</summary>
    public static SolidColorBrush Ink60 => Of(TaPalette.Ink, 0.60);

    /// <summary>ink 的 82%（命令文本）。</summary>
    public static SolidColorBrush Ink82 => Of(TaPalette.Ink, 0.82);

    /// <summary>ink 的 12%（主卡片阴影色）。</summary>
    public static SolidColorBrush Ink12 => Of(TaPalette.Ink, 0.12);

    /// <summary>ink 的 10%（主卡片阴影色）。</summary>
    public static SolidColorBrush Ink10 => Of(TaPalette.Ink, 0.10);

    /// <summary>ink 的 16%（主卡片 hover 阴影色）。</summary>
    public static SolidColorBrush Ink16 => Of(TaPalette.Ink, 0.16);

    /// <summary>paper 的 67%（主卡片副标题）。</summary>
    public static SolidColorBrush Paper67 => Of(TaPalette.Paper, 0.67);

    /// <summary>paper 的 94%（主卡片 hover 底）。</summary>
    public static SolidColorBrush Paper94 => Of(TaPalette.Paper, 0.94);

    /// <summary>成功色 10%（成功徽章底）。</summary>
    public static SolidColorBrush Success10 => Of(TaPalette.Success, 0.10);

    /// <summary>警示色 12%（欢迎页状态圆底）。</summary>
    public static SolidColorBrush Warning12 => Of(TaPalette.Warning, 0.12);

    /// <summary>成功色 12%（欢迎页状态圆底）。</summary>
    public static SolidColorBrush Success12 => Of(TaPalette.Success, 0.12);

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
