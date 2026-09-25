namespace Ta.Translate.Rendering;

/// <summary>
/// RGBA 颜色。字节序与 <see cref="Ta.Core.Imaging.RgbaBitmap"/> 的像素布局一致（R,G,B,A）。
/// </summary>
public readonly record struct RgbaColor(byte R, byte G, byte B, byte A = 255)
{
    public static RgbaColor Black { get; } = new(0, 0, 0);

    public static RgbaColor White { get; } = new(255, 255, 255);

    /// <summary>
    /// Rec.709 相对亮度（0…1）。系数与 Mac 的 contrastingTextColor 完全一致（:189）——
    /// Mac 那边用的是 NSColor 的 0…1 分量，这里字节分量需先除以 255。
    /// </summary>
    public double Luminance => ((0.2126 * R) + (0.7152 * G) + (0.0722 * B)) / 255.0;

    /// <summary>按对比度选文字颜色：亮度 &gt; 0.54 用黑，否则用白。对应 Mac: contrastingTextColor（:187-191）。</summary>
    public static RgbaColor ContrastingTextColor(RgbaColor background) =>
        background.Luminance > 0.54 ? Black : White;
}

/// <summary>
/// 渲染用的固定色值。
///
/// ⚠️ Mac 版这里用 <c>NSColor.labelColor / secondaryLabelColor / separatorColor</c> 等
/// <b>动态系统色</b>（亮/暗色两套值，且会随强调色与外观变化）。
/// Windows 移植无法 1:1 跟随系统外观（本渲染器只产出位图、不持有窗口上下文），
/// 因此按 <b>亮色模式</b> 的近似值固化。数值为公开参考值的整数化：
/// labelColor ≈ 纯黑；secondaryLabelColor ≈ 黑 60% 叠加在白底上 ≈ (138,138,141)；
/// separatorColor ≈ 黑约 22% 叠加在白底上 ≈ (198,198,198)。
/// 后续若要支持暗色，需要宿主把外观传入（本次移植未做，见报告）。
/// </summary>
public static class RenderColors
{
    /// <summary>面板底色：Mac: NSColor(calibratedWhite: 0.97) → 247。</summary>
    public static RgbaColor PanelBackground { get; } = new(247, 247, 247);

    /// <summary>接缝 / 行分隔线：NSColor.separatorColor 亮色近似。</summary>
    public static RgbaColor Separator { get; } = new(198, 198, 198);

    /// <summary>源文字色：NSColor.secondaryLabelColor 亮色近似。</summary>
    public static RgbaColor SecondaryLabel { get; } = new(138, 138, 141);

    /// <summary>译文 / 标题色：NSColor.labelColor 亮色近似（≈ 纯黑）。</summary>
    public static RgbaColor Label { get; } = new(0, 0, 0);
}
