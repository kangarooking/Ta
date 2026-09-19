namespace Ta.Settings.Core;

/// <summary>
/// 全局快捷键修饰键。对应 macOS 的 Carbon <c>cmdKey/optionKey/shiftKey/controlKey</c>
/// 位掩码（HotKeyPreferences.swift:11-14）。
/// Windows 上的等价物是 <c>MOD_ALT/MOD_CONTROL/MOD_SHIFT/MOD_WIN</c>。
/// </summary>
[Flags]
public enum HotKeyModifiers
{
    /// <summary>无修饰键。</summary>
    None = 0,

    /// <summary>Alt（对应 Mac 的 option）。</summary>
    Alt = 1,

    /// <summary>Ctrl。</summary>
    Control = 2,

    /// <summary>Shift。</summary>
    Shift = 4,

    /// <summary>Win（对应 Mac 的 command）。</summary>
    Win = 8,
}

/// <summary>
/// 一个快捷键 = 虚拟键码 + 修饰键 + 键面显示文本。
///
/// 对应 macOS <c>HotKeyShortcut</c>（HotKeyPreferences.swift:3-20）。
/// ⚠️ 集成时请用 <c>Ta.HotKeys.HotKeyShortcut</c>（另一路 agent 的实现）替换本类型；
/// 这里只声明结构，保证设置 UI 能独立编译与测试。
/// </summary>
/// <param name="VirtualKey">Windows VK 码。</param>
/// <param name="Modifiers">修饰键集合。</param>
/// <param name="KeyLabel">键面显示文本，如 <c>1</c>、<c>F5</c>、<c>Page Up</c>。</param>
public readonly record struct HotKeyShortcut(ushort VirtualKey, HotKeyModifiers Modifiers, string KeyLabel)
{
    /// <summary>
    /// Windows 风格显示文本，如 <c>Ctrl+Alt+Shift+1</c>。
    /// 修饰键顺序照抄 Mac 版 displayText 的相对次序（⌃⇧⌥⌘ → Ctrl,Shift,Alt,Win），
    /// 只是把符号换成 Windows 用户熟悉的单词。
    /// </summary>
    public string DisplayText
    {
        get
        {
            var parts = new List<string>(4);
            if (Modifiers.HasFlag(HotKeyModifiers.Control))
            {
                parts.Add("Ctrl");
            }

            if (Modifiers.HasFlag(HotKeyModifiers.Shift))
            {
                parts.Add("Shift");
            }

            if (Modifiers.HasFlag(HotKeyModifiers.Alt))
            {
                parts.Add("Alt");
            }

            if (Modifiers.HasFlag(HotKeyModifiers.Win))
            {
                parts.Add("Win");
            }

            parts.Add(KeyLabel);
            return string.Join("+", parts);
        }
    }

    /// <summary>
    /// 与 macOS <c>HotKeyShortcut.displayText</c>（HotKeyPreferences.swift:10-19）同构的符号串，
    /// 顺序也照抄：⌃ ⇧ ⌥ ⌘ + keyLabel。仅用于跨平台 parity 断言。
    /// </summary>
    public string MacParitySymbolicText
    {
        get
        {
            var result = string.Empty;
            if (Modifiers.HasFlag(HotKeyModifiers.Control))
            {
                result += "⌃";
            }

            if (Modifiers.HasFlag(HotKeyModifiers.Shift))
            {
                result += "⇧";
            }

            if (Modifiers.HasFlag(HotKeyModifiers.Alt))
            {
                result += "⌥";
            }

            if (Modifiers.HasFlag(HotKeyModifiers.Win))
            {
                result += "⌘";
            }

            return result + KeyLabel;
        }
    }
}

/// <summary>
/// 6 个全局截图快捷键动作。对应 macOS <c>GlobalHotKeyAction</c>
/// （HotKeyPreferences.swift:20-77）与参考文档 §4.2。
/// </summary>
public enum GlobalHotKeyAction
{
    /// <summary>极速识别内容。</summary>
    IntelligentCapture,

    /// <summary>通用截图。</summary>
    InteractiveCapture,

    /// <summary>截图图片。</summary>
    ImageCapture,

    /// <summary>截图并钉住。</summary>
    PinCapture,

    /// <summary>滚动长截图。</summary>
    LongCapture,

    /// <summary>截图翻译。</summary>
    TranslationCapture,
}

/// <summary><see cref="GlobalHotKeyAction"/> 的派生数据。</summary>
public static class GlobalHotKeyActions
{
    /// <summary>全部动作，顺序同 Mac 版 <c>CaseIterable</c>。</summary>
    public static IReadOnlyList<GlobalHotKeyAction> All { get; } = new[]
    {
        GlobalHotKeyAction.IntelligentCapture,
        GlobalHotKeyAction.InteractiveCapture,
        GlobalHotKeyAction.ImageCapture,
        GlobalHotKeyAction.PinCapture,
        GlobalHotKeyAction.LongCapture,
        GlobalHotKeyAction.TranslationCapture,
    };

    /// <summary>rawValue（持久化到 <c>globalHotKey.&lt;rawValue&gt;</c>）。</summary>
    public static string Raw(GlobalHotKeyAction action) => action switch
    {
        GlobalHotKeyAction.InteractiveCapture => "interactiveCapture",
        GlobalHotKeyAction.ImageCapture => "imageCapture",
        GlobalHotKeyAction.PinCapture => "pinCapture",
        GlobalHotKeyAction.LongCapture => "longCapture",
        GlobalHotKeyAction.TranslationCapture => "translationCapture",
        _ => "intelligentCapture",
    };

    /// <summary>显示名（HotKeyPreferences.swift:39-48）。</summary>
    public static string DisplayName(GlobalHotKeyAction action) => action switch
    {
        GlobalHotKeyAction.IntelligentCapture => "极速识别内容",
        GlobalHotKeyAction.InteractiveCapture => "通用截图",
        GlobalHotKeyAction.ImageCapture => "截图图片",
        GlobalHotKeyAction.PinCapture => "截图并钉住",
        GlobalHotKeyAction.LongCapture => "滚动长截图",
        GlobalHotKeyAction.TranslationCapture => "截图翻译",
        _ => string.Empty,
    };

    /// <summary>详情说明（HotKeyPreferences.swift:50-59）。</summary>
    public static string Detail(GlobalHotKeyAction action) => action switch
    {
        GlobalHotKeyAction.IntelligentCapture => "按识别设置直接获取内容",
        GlobalHotKeyAction.InteractiveCapture => "框选后再选择取字、翻译、复制或编辑",
        GlobalHotKeyAction.ImageCapture => "框选后直接复制图片",
        GlobalHotKeyAction.PinCapture => "框选后直接钉在屏幕上",
        GlobalHotKeyAction.LongCapture => "进入滚动长截图模式",
        GlobalHotKeyAction.TranslationCapture => "识别、翻译并复制文字",
        _ => string.Empty,
    };

    /// <summary>
    /// 默认快捷键。Mac 版统一用 <c>cmd|option|shift + 数字 1..6</c>（参考文档 §4.2，
    /// **无 control**）；Windows 上对应 <c>Win|Alt|Shift</c>。
    /// </summary>
    public static HotKeyShortcut DefaultShortcut(GlobalHotKeyAction action)
    {
        const HotKeyModifiers modifiers = HotKeyModifiers.Win | HotKeyModifiers.Alt | HotKeyModifiers.Shift;
        return action switch
        {
            GlobalHotKeyAction.IntelligentCapture => new HotKeyShortcut(0x31, modifiers, "1"),
            GlobalHotKeyAction.InteractiveCapture => new HotKeyShortcut(0x32, modifiers, "2"),
            GlobalHotKeyAction.ImageCapture => new HotKeyShortcut(0x33, modifiers, "3"),
            GlobalHotKeyAction.PinCapture => new HotKeyShortcut(0x34, modifiers, "4"),
            GlobalHotKeyAction.LongCapture => new HotKeyShortcut(0x35, modifiers, "5"),
            GlobalHotKeyAction.TranslationCapture => new HotKeyShortcut(0x36, modifiers, "6"),
            _ => new HotKeyShortcut(0x32, modifiers, "2"),
        };
    }
}

/// <summary>
/// 6 种截图模式。对应 macOS <c>CaptureMode</c>（CaptureModels.swift:3-10）。
/// </summary>
public enum CaptureMode
{
    /// <summary>通用（框选后选择动作）。</summary>
    Interactive,

    /// <summary>极速识别。</summary>
    Intelligent,

    /// <summary>截图翻译。</summary>
    Translation,

    /// <summary>截图图片。</summary>
    Image,

    /// <summary>截图并钉住。</summary>
    Pin,

    /// <summary>滚动长截图。</summary>
    Long,
}
