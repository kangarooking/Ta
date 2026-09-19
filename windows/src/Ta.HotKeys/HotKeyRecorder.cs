namespace Ta.HotKeys;

/// <summary>一次录制的结果分类。对应 macOS <c>HotKeyRecorderButton.keyDown</c> 的三个分支。</summary>
public enum HotKeyRecorderOutcome
{
    /// <summary>捕获到可用组合键。</summary>
    Accepted,

    /// <summary>按 Esc 取消（macOS <c>kVK_Escape</c>，HotKeyRecorderView.swift:69-72）。</summary>
    Cancelled,

    /// <summary>无法识别的键；macOS 会 beep 并显示「无法识别这个按键」（:75-80）。</summary>
    Unrecognized,
}

/// <summary>录制结果。</summary>
/// <param name="Outcome">分类。</param>
/// <param name="Shortcut">仅 <see cref="HotKeyRecorderOutcome.Accepted"/> 时非 null。</param>
/// <param name="Message">Unrecognized 时给用户的提示文案。</param>
public readonly record struct HotKeyRecorderResult(
    HotKeyRecorderOutcome Outcome,
    HotKeyShortcut? Shortcut,
    string? Message)
{
    public bool IsAccepted => Outcome == HotKeyRecorderOutcome.Accepted;
}

/// <summary>
/// 按键录制逻辑（纯计算，不含窗口 / 键盘钩子）。
///
/// 对应 macOS <c>HotKeyRecorderView.swift:64-164</c>：
/// - Esc 取消；
/// - 拿不到键面标签 → 无法识别；
/// - 单键也允许（Mac 版 <c>modifiers: 0</c> 的快捷键能存下来，
///   测试锁定于 HotKeyPreferencesTests.swift:43-55）。
///
/// Windows 侧录制直接用 VK 码（不经过 <see cref="CarbonToWindowsVirtualKey"/>），
/// 因为 <c>WM_KEYDOWN</c> 送来的就是 VK。翻译表只用于读 Mac 版数据。
/// </summary>
public static class HotKeyRecorder
{
    /// <summary>取消录制的键（macOS <c>kVK_Escape</c>）。</summary>
    public const ushort CancelVirtualKey = VirtualKeys.VK_ESCAPE;

    /// <summary>无法识别时展示的文案，照抄 macOS（HotKeyRecorderView.swift:78）。</summary>
    public const string UnrecognizedMessage = "无法识别这个按键";

    /// <summary>未开始录制时按钮上的占位文案（macOS <c>“点击设置”</c>，:114）。</summary>
    public const string IdleButtonText = "点击设置";

    /// <summary>录制中的按钮文案（macOS <c>“请按新快捷键…”</c>，:59）。</summary>
    public const string RecordingButtonText = "请按新快捷键…";

    /// <summary>按钮 tooltip（macOS <c>“可设置单键或组合键；按 Escape 取消”</c>，:115）。</summary>
    public const string TooltipText = "可设置单键或组合键；按 Escape 取消";

    /// <summary>
    /// 捕获一次按键。
    /// </summary>
    /// <param name="virtualKey">按下的 VK 码。</param>
    /// <param name="control">Ctrl 是否按下。</param>
    /// <param name="alt">Alt 是否按下。</param>
    /// <param name="shift">Shift 是否按下。</param>
    /// <param name="win">Win 是否按下。</param>
    public static HotKeyRecorderResult Capture(
        ushort virtualKey,
        bool control,
        bool alt,
        bool shift,
        bool win)
    {
        if (virtualKey == CancelVirtualKey)
        {
            return new HotKeyRecorderResult(HotKeyRecorderOutcome.Cancelled, null, null);
        }

        var label = KeyLabelFor(virtualKey);
        if (string.IsNullOrEmpty(label))
        {
            return new HotKeyRecorderResult(HotKeyRecorderOutcome.Unrecognized, null, UnrecognizedMessage);
        }

        var modifiers = HotKeyModifiers.None;
        if (control) modifiers |= HotKeyModifiers.Control;
        if (alt) modifiers |= HotKeyModifiers.Alt;
        if (shift) modifiers |= HotKeyModifiers.Shift;
        if (win) modifiers |= HotKeyModifiers.Win;

        return new HotKeyRecorderResult(
            HotKeyRecorderOutcome.Accepted,
            new HotKeyShortcut(virtualKey, modifiers, label),
            null);
    }

    /// <summary>
    /// 键面标签表，与 macOS <c>keyLabel(for:)</c> 的 <c>special</c> 字典一一对应
    /// （HotKeyRecorderView.swift:128-155），只是键码换成了 Windows VK。
    /// </summary>
    public static string? KeyLabelFor(ushort virtualKey)
    {
        return virtualKey switch
        {
            VirtualKeys.VK_RETURN => "↩",
            VirtualKeys.VK_TAB => "⇥",
            VirtualKeys.VK_SPACE => "Space",
            VirtualKeys.VK_BACK => "⌫",
            VirtualKeys.VK_DELETE => "⌦",
            VirtualKeys.VK_HOME => "Home",
            VirtualKeys.VK_END => "End",
            VirtualKeys.VK_PRIOR => "Page Up",
            VirtualKeys.VK_NEXT => "Page Down",
            VirtualKeys.VK_LEFT => "←",
            VirtualKeys.VK_RIGHT => "→",
            VirtualKeys.VK_UP => "↑",
            VirtualKeys.VK_DOWN => "↓",
            VirtualKeys.VK_F1 => "F1",
            VirtualKeys.VK_F2 => "F2",
            VirtualKeys.VK_F3 => "F3",
            VirtualKeys.VK_F4 => "F4",
            VirtualKeys.VK_F5 => "F5",
            VirtualKeys.VK_F6 => "F6",
            VirtualKeys.VK_F7 => "F7",
            VirtualKeys.VK_F8 => "F8",
            VirtualKeys.VK_F9 => "F9",
            VirtualKeys.VK_F10 => "F10",
            VirtualKeys.VK_F11 => "F11",
            VirtualKeys.VK_F12 => "F12",
            _ => LabelForPrintable(virtualKey),
        };
    }

    /// <summary>
    /// 数字 / 字母走「字符即标签」，对齐 macOS 的
    /// <c>event.charactersIgnoringModifiers?.uppercased()</c> 单字符分支（:157-163）。
    /// 修饰键本身不作为热键主体。
    /// </summary>
    private static string? LabelForPrintable(ushort virtualKey)
    {
        // 修饰键 / 锁定键不能单独当热键（RegisterHotKey 会因 MOD 与 VK 重复而失败）。
        switch (virtualKey)
        {
            case VirtualKeys.VK_SHIFT:
            case VirtualKeys.VK_CONTROL:
            case VirtualKeys.VK_MENU:
            case VirtualKeys.VK_LWIN or VirtualKeys.VK_RWIN:
            case VirtualKeys.VK_CAPITAL:
            case VirtualKeys.VK_NUMLOCK:
                return null;
        }

        var character = VirtualKeys.ToPrintableCharacter(virtualKey);
        return character?.ToString();
    }
}
