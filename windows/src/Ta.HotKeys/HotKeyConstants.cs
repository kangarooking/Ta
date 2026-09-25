namespace Ta.HotKeys;

/// <summary>
/// <c>RegisterHotKey</c> 与 <c>WM_HOTKEY</c> 的 Win32 常量。
///
/// 集中放在纯常量类里（而非混在 <see cref="HotKeyInterop"/>），
/// 这样 <see cref="HotKeyShortcut.ToModifierWord"/> 这类纯逻辑不依赖 P/Invoke 文件，
/// 单测也不必加载任何原生库。
/// </summary>
internal static class HotKeyConstants
{
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    /// <summary>
    /// 按住时不重复上报。
    ///
    /// macOS 的 <c>RegisterEventHotKey</c> 天然只触发一次 <c>kEventHotKeyPressed</c>；
    /// Windows 默认会持续发 <c>WM_HOTKEY</c>，因此**必须**显式传此位
    /// （移植参考文档 §13 与 §14 风险 #7）。
    /// </summary>
    public const uint MOD_NOREPEAT = 0x4000;

    public const uint WM_HOTKEY = 0x0312;

    /// <summary><c>RegisterHotKey</c> 冲突错误码：该组合已被本进程或其他进程占用。</summary>
    public const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    /// <summary><c>RegisterHotKey</c> 的 hWnd 无效。</summary>
    public const int ERROR_INVALID_WINDOW_HANDLE = 1400;

    /// <summary>热键标识无效（例如 id 为 0，Windows 保留 0x0000–0xBFFF）。</summary>
    public const int ERROR_INVALID_PARAMETER = 87;

    /// <summary>
    /// <c>RegisterHotKey</c> 的 id 合法区间：文档要求 0x0000–0xBFFF。
    /// 本工程沿用 Mac 版的 <see cref="GlobalHotKeyAction.HotKeyId"/>（1..6），落在区间内，
    /// 因此 id 与 Mac 完全一致，<c>WM_HOTKEY</c> 的 wParam 可直接查回动作。
    /// </summary>
    public const int GlobalHotKeyIdUpperBound = 0xBFFF;

    public static bool IsConflict(int lastError) => lastError == ERROR_HOTKEY_ALREADY_REGISTERED;
}
