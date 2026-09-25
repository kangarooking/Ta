namespace Ta.HotKeys;

/// <summary>
/// 六个可绑定的全局快捷键动作。
///
/// 对应 macOS <c>GlobalHotKeyAction</c>（HotKeyPreferences.swift:22-81）。
/// rawValue 沿用 Mac 版的 camelCase 字符串，持久化 key 因此与 Mac 一致
/// （<c>globalHotKey.&lt;rawValue&gt;</c>，移植参考文档 §10.6）。
/// </summary>
public enum GlobalHotKeyAction
{
    /// <summary>通用截图（hotKeyID 1，默认 ⌘⌥⇧2）。</summary>
    InteractiveCapture = 1,

    /// <summary>极速识别内容（hotKeyID 2，默认 ⌘⌥⇧1）。</summary>
    IntelligentCapture = 2,

    /// <summary>截图图片（hotKeyID 3，默认 ⌘⌥⇧3）。</summary>
    ImageCapture = 3,

    /// <summary>截图并钉住（hotKeyID 4，默认 ⌘⌥⇧4）。</summary>
    PinCapture = 4,

    /// <summary>滚动长截图（hotKeyID 5，默认 ⌘⌥⇧5）。</summary>
    LongCapture = 5,

    /// <summary>截图翻译（hotKeyID 6，默认 ⌘⌥⇧6）。</summary>
    TranslationCapture = 6,
}

/// <summary>动作的元数据：id、显示名、默认快捷键、持久化 key。</summary>
public static class GlobalHotKeyActions
{
    /// <summary>
    /// 全部动作，顺序与 <c>GlobalHotKeyAction.hotKeyID</c> 一致
    /// （Mac 版注册时按 <c>allCases</c> 遍历，顺序影响「哪个动作先注册」——
    /// 回滚时先注册成功的会先拿到系统热键，顺序必须稳定）。
    /// </summary>
    public static IReadOnlyList<GlobalHotKeyAction> All { get; } = new[]
    {
        GlobalHotKeyAction.InteractiveCapture,
        GlobalHotKeyAction.IntelligentCapture,
        GlobalHotKeyAction.ImageCapture,
        GlobalHotKeyAction.PinCapture,
        GlobalHotKeyAction.LongCapture,
        GlobalHotKeyAction.TranslationCapture,
    };

    /// <summary>
    /// 传回 <c>WM_HOTKEY</c> wParam 的 id，与 Mac 版 <c>hotKeyID</c> 逐一对齐。
    /// 见移植参考文档 §4.2 的映射表。
    /// </summary>
    public static int HotKeyId(this GlobalHotKeyAction action) => (int)action;

    /// <summary>设置界面显示名（Mac 版 <c>displayName</c>，HotKeyPreferences.swift:35-46）。</summary>
    public static string DisplayName(this GlobalHotKeyAction action) => action switch
    {
        GlobalHotKeyAction.InteractiveCapture => "通用截图",
        GlobalHotKeyAction.IntelligentCapture => "极速识别内容",
        GlobalHotKeyAction.ImageCapture => "截图图片",
        GlobalHotKeyAction.PinCapture => "截图并钉住",
        GlobalHotKeyAction.LongCapture => "滚动长截图",
        GlobalHotKeyAction.TranslationCapture => "截图翻译",
        _ => action.ToString(),
    };

    /// <summary>设置界面副标题（Mac 版 <c>detail</c>，HotKeyPreferences.swift:48-59）。</summary>
    public static string Detail(this GlobalHotKeyAction action) => action switch
    {
        GlobalHotKeyAction.InteractiveCapture => "框选后再选择取字、翻译、复制或编辑",
        GlobalHotKeyAction.IntelligentCapture => "按识别设置直接获取内容",
        GlobalHotKeyAction.ImageCapture => "框选后直接复制图片",
        GlobalHotKeyAction.PinCapture => "框选后直接钉在屏幕上",
        GlobalHotKeyAction.LongCapture => "进入滚动长截图模式",
        GlobalHotKeyAction.TranslationCapture => "识别、翻译并复制文字",
        _ => string.Empty,
    };

    /// <summary>
    /// Mac 版持久化用的 camelCase rawValue。
    /// </summary>
    public static string RawValue(this GlobalHotKeyAction action) => action switch
    {
        GlobalHotKeyAction.InteractiveCapture => "interactiveCapture",
        GlobalHotKeyAction.IntelligentCapture => "intelligentCapture",
        GlobalHotKeyAction.ImageCapture => "imageCapture",
        GlobalHotKeyAction.PinCapture => "pinCapture",
        GlobalHotKeyAction.LongCapture => "longCapture",
        GlobalHotKeyAction.TranslationCapture => "translationCapture",
        _ => action.ToString(),
    };

    /// <summary>
    /// 持久化 key：<c>globalHotKey.&lt;rawValue&gt;</c>。
    /// 对应 Mac 版 <c>HotKeyPreferences.storageKey(for:)</c>（HotKeyPreferences.swift:145）
    /// 与移植参考文档 §10.6。
    /// </summary>
    public static string StorageKey(this GlobalHotKeyAction action) => $"globalHotKey.{action.RawValue()}";

    /// <summary>
    /// 默认快捷键。
    ///
    /// Mac 版是 <c>cmdKey | optionKey | shiftKey</c> + <c>kVK_ANSI_&lt;n&gt;</c>
    /// （HotKeyPreferences.swift:61-77）。Windows 侧 ⌘ → Ctrl，
    /// 且 <c>kVK_ANSI_&lt;n&gt;</c> 必须过翻译表变成 <c>VK_&lt;n&gt;</c>：
    /// 数字键的 Carbon 码是物理键序（<c>kVK_ANSI_1</c> = 18），**不是** 18+n。
    /// </summary>
    public static HotKeyShortcut DefaultShortcut(this GlobalHotKeyAction action)
    {
        // 单一来源：先按 Mac 版给出 Carbon 码，再过翻译表，确保两平台键面一致。
        var (carbonKey, label) = action switch
        {
            GlobalHotKeyAction.InteractiveCapture => (CarbonVirtualKeyCodes.ANSI_2, "2"),
            GlobalHotKeyAction.IntelligentCapture => (CarbonVirtualKeyCodes.ANSI_1, "1"),
            GlobalHotKeyAction.ImageCapture => (CarbonVirtualKeyCodes.ANSI_3, "3"),
            GlobalHotKeyAction.PinCapture => (CarbonVirtualKeyCodes.ANSI_4, "4"),
            GlobalHotKeyAction.LongCapture => (CarbonVirtualKeyCodes.ANSI_5, "5"),
            GlobalHotKeyAction.TranslationCapture => (CarbonVirtualKeyCodes.ANSI_6, "6"),
            _ => (CarbonVirtualKeyCodes.ANSI_1, "1"),
        };

        if (!CarbonToWindowsVirtualKey.TryTranslate(carbonKey, out var windowsKey))
        {
            // 默认键一定在翻译表里；真走到这里说明表被改坏了，属于开发期错误。
            throw new InvalidOperationException($"默认快捷键的 Carbon 键码 {carbonKey} 无法翻译到 Windows VK。");
        }

        var modifiers = HotKeyModifierMapping.ToWindowsModifiers((uint)CarbonModifiers.Command
                                                                 | (uint)CarbonModifiers.Option
                                                                 | (uint)CarbonModifiers.Shift);
        return new HotKeyShortcut(windowsKey, modifiers, label);
    }
}
