namespace Ta.HotKeys;

/// <summary>
/// 用户可见的修饰键集合（与平台无关的表示）。
///
/// 取值刻意与 Win32 <c>MOD_ALT / MOD_CONTROL / MOD_SHIFT / MOD_WIN</c> 的低 4 位一致，
/// 但映射仍然显式写死在 <see cref="HotKeyShortcut.ToModifierWord"/>，
/// 避免「因为位值刚好相同」而隐式耦合。
///
/// ⚠️ **Mac 版刻意不含 controlKey**：默认六个快捷键一律 <c>cmdKey | optionKey | shiftKey</c>
/// （HotKeyPreferences.swift:61-77）。⌘ 在 Windows 上映射为 Ctrl，
/// 所以本工程的「Control」同时承载 Mac 的 ⌘ 与 ⌃ 两个来源。
/// </summary>
[Flags]
public enum HotKeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}

/// <summary>
/// Carbon modifierFlags 中的修饰位（<c>cmdKey</c> 等）。
///
/// 对应 macOS <c>Carbon/HIToolbox/Events.h</c>。
/// </summary>
[Flags]
internal enum CarbonModifiers : uint
{
    None = 0,
    /// <summary><c>cmdKey</c> = 256 = 0x100 —— ⌘。</summary>
    Command = 0x0100,
    /// <summary><c>shiftKey</c> = 512 = 0x200 —— ⇧。</summary>
    Shift = 0x0200,
    /// <summary><c>optionKey</c> = 2048 = 0x800 —— ⌥。</summary>
    Option = 0x0800,
    /// <summary><c>controlKey</c> = 4096 = 0x1000 —— ⌃。Mac 版默认快捷键**不使用**此位。</summary>
    Control = 0x1000,
}

/// <summary>
/// 修饰键的 Carbon ↔ Windows 双向转换。
///
/// ⌘ → Ctrl（Windows 上 ⌘ 不存在），⌥ → Alt，⇧ → Shift，⌃ → Ctrl（与 ⌘ 同落点）。
/// </summary>
public static class HotKeyModifierMapping
{
    /// <summary>Carbon modifierFlags → Windows 修饰键。</summary>
    public static HotKeyModifiers ToWindowsModifiers(uint carbonModifiers)
    {
        var flags = (CarbonModifiers)carbonModifiers;
        var result = HotKeyModifiers.None;
        if (flags.HasFlag(CarbonModifiers.Shift)) result |= HotKeyModifiers.Shift;
        if (flags.HasFlag(CarbonModifiers.Option)) result |= HotKeyModifiers.Alt;
        // ⌘ 与 ⌃ 在 Windows 上都落到 Ctrl；二者同时按下不叠加。
        if (flags.HasFlag(CarbonModifiers.Command) || flags.HasFlag(CarbonModifiers.Control))
        {
            result |= HotKeyModifiers.Control;
        }
        return result;
    }

    /// <summary>Windows 修饰键 → Carbon modifierFlags（用于导出回 Mac 或做 parity 断言）。</summary>
    public static uint ToCarbonModifiers(HotKeyModifiers modifiers)
    {
        var result = CarbonModifiers.None;
        if (modifiers.HasFlag(HotKeyModifiers.Shift)) result |= CarbonModifiers.Shift;
        if (modifiers.HasFlag(HotKeyModifiers.Alt)) result |= CarbonModifiers.Option;
        if (modifiers.HasFlag(HotKeyModifiers.Control)) result |= CarbonModifiers.Command;
        if (modifiers.HasFlag(HotKeyModifiers.Win)) result |= CarbonModifiers.Control; // 无 ⌘ 键，落到 ⌃
        return (uint)result;
    }
}
