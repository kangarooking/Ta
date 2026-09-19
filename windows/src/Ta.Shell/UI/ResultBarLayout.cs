using System.Drawing;

namespace Ta.Shell.UI;

/// <summary>结果反馈条的四种状态。对应 Mac 版 <c>ResultBarKind</c>（ResultBarView.swift:4-27）。</summary>
public enum ResultBarKind
{
    Processing,
    Success,
    Warning,
    Failure,
}

public static class ResultBarKindExtensions
{
    /// <summary>
    /// 状态色。逐字对齐 Mac 版 <c>ResultBarKind.color</c>（ResultBarView.swift:19-26）：
    /// processing = mutedInk、success = rgb(65,143,61)、warning = orange、
    /// failure = cinnabar rgb(214,64,47)。
    /// </summary>
    public static Color Color(this ResultBarKind kind) => kind switch
    {
        ResultBarKind.Success => System.Drawing.Color.FromArgb(255, 65, 143, 61),
        ResultBarKind.Warning => System.Drawing.Color.Orange,
        ResultBarKind.Failure => TaPalette.Cinnabar,
        _ => TaPalette.MutedInk,
    };

    /// <summary>
    /// 状态图标字形。
    /// Mac 版用 SF Symbols（ResultBarView.swift:9-16）；
    /// Windows 用 Segoe MDL2 Assets / 通用 Unicode 字形替代。
    /// </summary>
    public static string Glyph(this ResultBarKind kind) => kind switch
    {
        // ⋯⋯ Mac: circle.dotted —— 处理中（配合旋转动画）。
        ResultBarKind.Processing => "◦",

        // ✓ Mac: checkmark.circle
        ResultBarKind.Success => "✓",

        // ⚠ Mac: exclamationmark.triangle.fill
        ResultBarKind.Warning => "⚠",

        // ✕ Mac: xmark.circle
        _ => "✕",
    };

    /// <summary>处理中状态不显示关闭按钮。对应 ResultBarView.swift:94。</summary>
    public static bool ShowsCloseButton(this ResultBarKind kind) => kind != ResultBarKind.Processing;
}

/// <summary>结果反馈条要显示的内容。对应 Mac 版 <c>ResultBarState</c>（ResultBarView.swift:29-33）。</summary>
public sealed record ResultBarState
{
    public ResultBarState(ResultBarKind kind, string title, string? detail = null)
    {
        Kind = kind;
        Title = title ?? string.Empty;
        Detail = detail;
    }

    public ResultBarKind Kind { get; }
    public string Title { get; }
    public string? Detail { get; }

    public override string ToString() =>
        Detail is null ? $"[{Kind}] {Title}" : $"[{Kind}] {Title} — {Detail}";
}

/// <summary>
/// 结果反馈条的度量。逐字对齐移植参考文档 §5.7
/// （Mac 版 <c>ResultBarLayout</c>，ResultBarView.swift:35-55）。
/// </summary>
public static class ResultBarLayout
{
    public const double MinimumWidth = 320;
    public const double MaximumWidth = 352;
    public const double Height = 58;
    public const double CornerRadius = 18;

    /// <summary>自动隐藏默认秒数。对应 UserDefaults "resultBarDuration" 默认 3。</summary>
    public const double DefaultAutoHideSeconds = 3;

    /// <summary>设置滑块下限。</summary>
    public const double MinimumDurationSeconds = 1.5;

    /// <summary>设置滑块上限。</summary>
    public const double MaximumDurationSeconds = 8;

    /// <summary>设置滑块步进。</summary>
    public const double DurationStep = 0.5;

    /// <summary>
    /// 非自动隐藏状态的最低展示秒数。
    /// 对应 Mac 版 <c>max(preferredSeconds, 6)</c>（ResultBarController.swift:39）——
    /// 让用户有时间读完一条错误。
    /// </summary>
    public const double StickyMinimumSeconds = 6;

    /// <summary>
    /// 面板距屏顶的偏移。对应 Mac 版 <c>visibleFrame.minY + 28</c>
    /// （ResultBarController.swift:83）。Windows 屏坐标 Y 向下，故为 <c>top + 28</c>。
    /// </summary>
    public const double TopInset = 28;

    /// <summary>状态图标尺寸。对应 ResultBarView.swift:67 的 frame(width: 24, height: 24)。</summary>
    public const double IconSize = 24;

    /// <summary>「拓」字徽章尺寸。对应 ResultBarView.swift:87 的 frame(width: 22, height: 22)。</summary>
    public const double BrandBadgeSize = 22;

    /// <summary>关闭按钮尺寸。对应 ResultBarView.swift:99。</summary>
    public const double CloseButtonSize = 24;

    /// <summary>水平内边距。对应 ResultBarView.swift:106 的 padding(.horizontal, 14)。</summary>
    public const double HorizontalPadding = 14;

    /// <summary>
    /// 图标与固定控件的预留宽度。
    /// 对应 Mac 版 <c>fixedControlsAndSpacing</c>（ResultBarView.swift:52）：
    /// processing 无关闭按钮故 116，其余 144。
    /// </summary>
    public static double FixedControlsAndSpacingFor(ResultBarKind kind) =>
        kind == ResultBarKind.Processing ? 116 : 144;

    /// <summary>
    /// 算出首选宽度。
    ///
    /// 逐字对齐 Mac 版 <c>ResultBarLayout.preferredWidth(for:)</c>
    /// （ResultBarView.swift:43-54）：
    /// <code>
    /// textWidth = max(titleWidth, detailWidth)
    /// return min(352, max(320, ceil(textWidth + fixedControlsAndSpacing)))
    /// </code>
    ///
    /// 文字宽度通过 <paramref name="measure"/> 注入（GDI 的 TextRenderer.MeasureText），
    /// 因此本函数本身可单测。
    /// </summary>
    /// <param name="measure">测宽函数：(文本, 字号磅) → 像素宽。</param>
    public static double PreferredWidthFor(
        ResultBarState state,
        Func<string, double, double> measure)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(measure);

        // 标题 13pt semibold、说明 11pt regular —— 与 Mac 版字号一致（:46-50）。
        var titleWidth = measure(state.Title, 13);
        var detailWidth = measure(state.Detail ?? string.Empty, 11);
        var textWidth = Math.Max(titleWidth, detailWidth);

        return Math.Min(
            MaximumWidth,
            Math.Max(MinimumWidth, Math.Ceiling(textWidth + FixedControlsAndSpacingFor(state.Kind))));
    }

    /// <summary>
    /// 是否应当自动隐藏。
    ///
    /// 对应 Mac 版 ResultBarController.swift:35 的
    /// <code>guard autoHide || state.kind != .processing else { return }</code>：
    /// <b>processing 永不自动隐藏</b>，它一直留在屏幕上直到被下一个状态替换。
    /// </summary>
    public static bool ShouldAutoHide(ResultBarKind kind, bool autoHide) =>
        autoHide || kind != ResultBarKind.Processing;

    /// <summary>
    /// 解析自动隐藏时长。
    ///
    /// 逐字对齐 Mac 版 ResultBarController.swift:36-39：
    /// <code>
    /// let preferredSeconds = duration > 0 ? duration : 3
    /// let seconds = dismissalOverrideSeconds ?? (autoHide ? preferredSeconds : max(preferredSeconds, 6))
    /// </code>
    /// </summary>
    public static double ResolveAutoHideSeconds(
        double storedDuration,
        bool autoHide,
        double? overrideSeconds)
    {
        if (overrideSeconds is { } explicitSeconds)
        {
            return explicitSeconds;
        }

        var preferred = storedDuration > 0 ? storedDuration : DefaultAutoHideSeconds;
        return autoHide ? preferred : Math.Max(preferred, StickyMinimumSeconds);
    }

    /// <summary>
    /// 把设置里的时长夹到合法区间并吸附到步进网格。
    /// 对应设置滑块 1.5…8、步进 0.5（参考文档 §5.7）。
    ///
    /// Mac 版靠 NSSlider 保证取值合法；Windows 侧存储可能被人手工改坏，
    /// 因此这里做一次防御性归一 —— 避免出现 0 或 99 这种会让提示一闪而过的值。
    /// </summary>
    public static double NormalizeDuration(double value)
    {
        if (double.IsNaN(value) || value <= 0)
        {
            return DefaultAutoHideSeconds;
        }

        var clamped = Math.Clamp(value, MinimumDurationSeconds, MaximumDurationSeconds);
        var steps = Math.Round((clamped - MinimumDurationSeconds) / DurationStep);
        return MinimumDurationSeconds + (steps * DurationStep);
    }
}
