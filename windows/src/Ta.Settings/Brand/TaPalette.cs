using System.Windows.Media;

namespace Ta.Settings.Brand;

/// <summary>
/// 拓 Ta 的品牌调色板。
///
/// 逐值对应 macOS <c>TaPalette</c>（TaDesignSystem.swift:11-18），
/// 以及移植参考文档 §12.2「调色板（TaPalette，:11-18）—— ⚠️ 精确 RGB」。
///
/// macOS 的 <c>hairline</c> 是 <c>ink.opacity(0.10)</c>，即一个 **带 alpha 的颜色**，
/// 而不是合成后的实色。WPF 里必须保留 alpha 才能在不同底色上得到同样的叠加效果，
/// 所以 <see cref="Hairline"/> 也是 <c>#1A1A1A @ 10%</c>（alpha = 26/255）。
/// 需要实色时用 <see cref="Flatten"/> 合成（例如叠加在 paper 上得到 #E3E0D8）。
/// </summary>
public static class TaPalette
{
    /// <summary>ink = rgb(26, 26, 26) = #1A1A1A —— 主文字色。</summary>
    public static readonly Color Ink = Color.FromRgb(0x1A, 0x1A, 0x1A);

    /// <summary>paper = rgb(250, 246, 238) = #FAF6EE —— 页面底色。</summary>
    public static readonly Color Paper = Color.FromRgb(0xFA, 0xF6, 0xEE);

    /// <summary>elevatedPaper = rgb(255, 253, 248) = #FFFDF8 —— 抬升卡片底色。</summary>
    public static readonly Color ElevatedPaper = Color.FromRgb(0xFF, 0xFD, 0xF8);

    /// <summary>cinnabar = rgb(214, 64, 47) = #D6402F —— 品牌主色（朱砂）。</summary>
    public static readonly Color Cinnabar = Color.FromRgb(0xD6, 0x40, 0x2F);

    /// <summary>mutedInk = rgb(104, 100, 94) = #68645E —— 次要文字色。</summary>
    public static readonly Color MutedInk = Color.FromRgb(0x68, 0x64, 0x5E);

    /// <summary>hairline = ink @ 0.10 —— 分隔线 / 描边。</summary>
    public static readonly Color Hairline = Color.FromArgb(0x1A, 0x1A, 0x1A, 0x1A);

    /// <summary>
    /// macOS <c>Color.green</c> / <c>Color.red</c> / <c>Color.orange</c> 在
    /// 浅色模式下的近似值，用于状态图标（SwiftUI 用的是语义系统色）。
    /// </summary>
    public static readonly Color Success = Color.FromRgb(0x2E, 0x8B, 0x57);

    public static readonly Color Danger = Color.FromRgb(0xC0, 0x2B, 0x2B);

    public static readonly Color Warning = Color.FromRgb(0xC8, 0x7A, 0x1E);

    /// <summary>给颜色加上指定不透明度（对应 SwiftUI 的 <c>.opacity(_:)</c>）。</summary>
    public static Color WithOpacity(Color color, double opacity)
    {
        var clamped = opacity < 0 ? 0 : opacity > 1 ? 1 : opacity;
        return Color.FromArgb((byte)Math.Round(clamped * 255.0), color.R, color.G, color.B);
    }

    /// <summary>
    /// 把带 alpha 的前景色合成到背景色上（source-over）。
    /// 用于把 <see cref="Hairline"/> 之类的半透明色转成实色做断言或截图比对。
    /// </summary>
    public static Color Flatten(Color foreground, Color background)
    {
        var a = foreground.A / 255.0;
        var ba = background.A / 255.0;
        var outA = a + ba * (1 - a);
        byte Blend(byte f, byte b)
        {
            var value = (f * a + b * ba * (1 - a)) / outA;
            return (byte)Math.Clamp((int)Math.Round(value), 0, 255);
        }

        return Color.FromArgb(
            (byte)Math.Clamp((int)Math.Round(outA * 255), 0, 255),
            Blend(foreground.R, background.R),
            Blend(foreground.G, background.G),
            Blend(foreground.B, background.B));
    }
}
