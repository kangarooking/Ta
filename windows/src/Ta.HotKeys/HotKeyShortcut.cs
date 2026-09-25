using System.Globalization;
using System.Text.Json.Serialization;

namespace Ta.HotKeys;

/// <summary>
/// 一个全局快捷键 = Windows VK + 修饰键 + 显示标签。
///
/// 对应 macOS <c>HotKeyShortcut</c>（HotKeyPreferences.swift:3-20），字段一一对应：
/// <c>keyCode → VirtualKey</c>、<c>modifiers → Modifiers</c>、<c>keyLabel → KeyLabel</c>。
///
/// ⚠️ <see cref="VirtualKey"/> 存的是 **Windows VK 码**，不是 Carbon kVK 码。
/// 只有迁移 Mac 侧数据时才需要 <see cref="CarbonToWindowsVirtualKey"/>。
/// </summary>
public readonly record struct HotKeyShortcut
{
    public HotKeyShortcut(ushort virtualKey, HotKeyModifiers modifiers, string keyLabel)
    {
        VirtualKey = virtualKey;
        Modifiers = modifiers;
        KeyLabel = keyLabel ?? throw new ArgumentNullException(nameof(keyLabel));
    }

    /// <summary>Windows 虚拟键码（VK_*）。</summary>
    public ushort VirtualKey { get; }

    /// <summary>修饰键集合。</summary>
    public HotKeyModifiers Modifiers { get; }

    /// <summary>键面显示文本，如 <c>1</c>、<c>F5</c>、<c>Page Up</c>。</summary>
    public string KeyLabel { get; }

    /// <summary>
    /// Windows 风格的显示文本，如 <c>Ctrl+Alt+Shift+1</c>。
    ///
    /// 方案说明：Windows 用户（以及 WPF/WinUI 的 <c>KeyGesture</c> 惯例）惯用
    /// <c>Ctrl+Alt+Shift</c> 顺序的可读名，而不是 macOS 的 ⇧⌥⌘ 符号串。
    /// 若需要与 Mac 侧断言做 parity 比对，用 <see cref="MacParitySymbolicText"/>。
    /// </summary>
    public string DisplayText
    {
        get
        {
            var parts = new List<string>(4);
            if (Modifiers.HasFlag(HotKeyModifiers.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(HotKeyModifiers.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(HotKeyModifiers.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(HotKeyModifiers.Win)) parts.Add("Win");
            parts.Add(KeyLabel);
            return string.Join("+", parts);
        }
    }

    /// <summary>
    /// 与 macOS <c>HotKeyShortcut.displayText</c>（HotKeyPreferences.swift:10-19）
    /// 完全同构的符号串，顺序也照抄 Mac 版：⌃ ⇧ ⌥ ⌘ + keyLabel。
    /// 用于跨平台 parity 断言，不用于 UI 展示。
    /// </summary>
    [JsonIgnore]
    public string MacParitySymbolicText
    {
        get
        {
            var result = string.Empty;
            if (Modifiers.HasFlag(HotKeyModifiers.Control)) result += "⌃";
            if (Modifiers.HasFlag(HotKeyModifiers.Shift)) result += "⇧";
            if (Modifiers.HasFlag(HotKeyModifiers.Alt)) result += "⌥";
            // Windows 无 ⌘，Ctrl 即 Mac 的 ⌘ 位；Win 键无符号表示，用 ❖。
            if (Modifiers.HasFlag(HotKeyModifiers.Win)) result += "❖";
            return result + KeyLabel;
        }
    }

    /// <summary>
    /// 传给 <c>RegisterHotKey</c> 的 fsModifiers。
    ///
    /// 默认带 <c>MOD_NOREPEAT</c>：macOS 的 <c>RegisterEventHotKey</c> 在按住时
    /// 只触发一次 <c>kEventHotKeyPressed</c>，<c>MOD_NOREPEAT</c> 是对应的语义
    /// （移植参考文档 §13）。
    /// </summary>
    public uint ToModifierWord(bool includeNoRepeat = true)
    {
        uint word = 0;
        if (Modifiers.HasFlag(HotKeyModifiers.Alt)) word |= HotKeyConstants.MOD_ALT;
        if (Modifiers.HasFlag(HotKeyModifiers.Control)) word |= HotKeyConstants.MOD_CONTROL;
        if (Modifiers.HasFlag(HotKeyModifiers.Shift)) word |= HotKeyConstants.MOD_SHIFT;
        if (Modifiers.HasFlag(HotKeyModifiers.Win)) word |= HotKeyConstants.MOD_WIN;
        if (includeNoRepeat) word |= HotKeyConstants.MOD_NOREPEAT;
        return word;
    }

    /// <summary>
    /// 用于重复检测的身份串：<c>modifiers:keyCode</c>。
    /// 对齐 Mac 版测试里 <c>"\($0.modifiers):\($0.keyCode)"</c>
    /// （HotKeyPreferencesTests.swift:9）的构造方式。
    /// </summary>
    [JsonIgnore]
    public string Identity => ToModifierWord(false).ToString(CultureInfo.InvariantCulture)
                             + ":"
                             + VirtualKey.ToString(CultureInfo.InvariantCulture);

    public override string ToString() => DisplayText;
}
