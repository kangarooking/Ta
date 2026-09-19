namespace Ta.Pinning;

/// <summary>
/// 右键菜单的命令枚举。对应 Mac 版 <c>PinnedImageView</c> 里的那组 @objc 选择器
/// （PinnedImageWindowController.swift:320-346）。
/// </summary>
public enum PinCommand
{
    CopyImage = 1,
    BeginCrop,
    ResetCrop,
    RotateLeft,
    RotateRight,
    MirrorHorizontally,
    MirrorVertically,
    ToggleGrayscale,
    ToggleInversion,
    ToggleBorder,
    ToggleShadow,
    ToggleTopmost,
    ResetAppearance,
    ToggleThumbnail,
    EnableClickThrough,
    GroupVisiblePins,
    HideGroup,
    ClosePin,
}

/// <summary>一个菜单项。对应 Mac 的 NSMenuItem（标题 + 键等价 + 勾选态）。</summary>
/// <param name="Command">命令。</param>
/// <param name="Title">标题。Mac 版为中文，1:1 保留。</param>
/// <param name="KeyEquivalent">Mac 版键等价（小写单字母，不带修饰键）。</param>
/// <param name="DefaultChecked">初始勾选态。Mac: 边框/阴影/保持最前默认 .on（:333-337）。</param>
public sealed record PinMenuItem(
    PinCommand Command,
    string Title,
    string KeyEquivalent = "",
    bool DefaultChecked = false);

/// <summary>
/// 右键菜单的完整规格 —— 顺序、标题、键等价、分隔符全部 1:1 对应 Mac 版
/// <c>PinnedImageView.init</c> 里那段 NSMenu 构造（:320-346）。
///
/// 抽成纯数据是为了让「菜单有没有漏项、顺序对不对、键等价有没有丢」
/// 能被单测锁定，而不是只能靠人眼看窗口。
/// </summary>
public static class PinMenuSpec
{
    /// <summary>
    /// 菜单项与加速键的命令 ID 基址。
    /// 集中在此，避免窗口类与宿主类各自定义出不同的映射。
    /// </summary>
    public const int CommandIdBase = 1000;

    /// <summary>菜单项标题常量。对应 Mac 的 <c>PinnedImageDecoration</c>（:275-279）。</summary>
    public const string BorderTitle = "显示边框";
    public const string ShadowTitle = "窗口阴影";

    /// <summary>命令 ID → 菜单命令。对应 TrackPopupMenuEx(TPM_RETURNCMD) 的返回值。</summary>
    public static PinCommand? CommandFromId(int id)
    {
        var command = (PinCommand)(id - CommandIdBase);
        return Enum.IsDefined(command) ? command : null;
    }

    /// <summary>
    /// 菜单项序列。分隔符用 <see cref="PinCommand.None"/>? 不用 ——
    /// 分隔符没有命令，用 <see cref="IsSeparator"/> 表达，与 Mac 的 <c>NSMenuItem.separator()</c> 对应。
    /// </summary>
    public static IReadOnlyList<PinMenuItem> Items { get; } = new PinMenuItem[]
    {
        new(PinCommand.CopyImage, "复制图片", "c"),
        new(PinCommand.BeginCrop, "裁剪…"),
        new(PinCommand.ResetCrop, "重置裁剪"),
        new(PinCommand.RotateLeft, "向左旋转", "["),
        new(PinCommand.RotateRight, "向右旋转", "]"),
        new(PinCommand.MirrorHorizontally, "水平翻转"),
        new(PinCommand.MirrorVertically, "垂直翻转"),
        new(PinCommand.ToggleGrayscale, "灰度显示"),
        new(PinCommand.ToggleInversion, "反色显示"),
        new(PinCommand.ToggleBorder, BorderTitle, "", true),
        new(PinCommand.ToggleShadow, ShadowTitle, "", true),
        new(PinCommand.ToggleTopmost, "保持最前", "", true),
        new(PinCommand.ResetAppearance, "恢复显示", "0"),
        new(PinCommand.ToggleThumbnail, "缩略图模式"),
        new(PinCommand.EnableClickThrough, "鼠标穿透"),
        new(PinCommand.GroupVisiblePins, "将可见钉图编为一组"),
        new(PinCommand.HideGroup, "隐藏本组"),
        new(PinCommand.ClosePin, "关闭钉图", "w"),
    };

    /// <summary>
    /// 分隔符插入位置（以「其后插分隔符的命令」表达），对应 Mac 的 4 个 <c>menu.addItem(.separator())</c>：
    /// 在 重置裁剪 / 垂直翻转 / 恢复显示 / 隐藏本组 之后各一条（:324, :329, :339, :344）。
    /// </summary>
    public static IReadOnlySet<PinCommand> SeparatorAfter { get; } = new HashSet<PinCommand>
    {
        PinCommand.ResetCrop,
        PinCommand.MirrorVertically,
        PinCommand.ResetAppearance,
        PinCommand.HideGroup,
    };

    /// <summary>灰度与反色互斥（Mac: :515, :522）。</summary>
    public static IReadOnlySet<PinCommand> MutuallyExclusiveFilters { get; } = new HashSet<PinCommand>
    {
        PinCommand.ToggleGrayscale,
        PinCommand.ToggleInversion,
    };

    /// <summary>
    /// 一个命令对应哪种滤镜。互斥规则本身实现在 <see cref="PinFilterModeMath.Toggle"/>，
    /// 由 <see cref="PinViewState"/> 持有状态，菜单只是它的投影。
    /// </summary>
    public static PinFilterMode FilterFor(PinCommand command) => command switch
    {
        PinCommand.ToggleGrayscale => PinFilterMode.Grayscale,
        PinCommand.ToggleInversion => PinFilterMode.Inverted,
        _ => PinFilterMode.None,
    };
}
