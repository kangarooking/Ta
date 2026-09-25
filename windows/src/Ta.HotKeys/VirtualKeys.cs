namespace Ta.HotKeys;

/// <summary>
/// Windows 虚拟键码（VK_*）常量子集。
///
/// 与 Carbon 的 kVK_* **完全不同**：<c>VK_1</c> = 0x31，而 <c>kVK_ANSI_1</c> = 18。
/// 两个命名空间之间必须过 <see cref="CarbonToWindowsVirtualKey"/> 的翻译表，
/// 严禁直接套用同一个整数。
/// </summary>
internal static class VirtualKeys
{
    public const ushort VK_LBUTTON = 0x01;
    public const ushort VK_BACK = 0x08;         // ⌫ 退格（macOS 的 kVK_Delete）
    public const ushort VK_TAB = 0x09;
    public const ushort VK_RETURN = 0x0D;       // macOS 的 kVK_Return 与小键盘 Enter 都落这里
    public const ushort VK_SHIFT = 0x10;
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_MENU = 0x12;         // Alt
    public const ushort VK_LWIN = 0x5B;
    public const ushort VK_RWIN = 0x5C;
    public const ushort VK_ESCAPE = 0x1B;       // 录制器取消键
    public const ushort VK_SPACE = 0x20;
    public const ushort VK_PRIOR = 0x21;        // Page Up
    public const ushort VK_NEXT = 0x22;         // Page Down
    public const ushort VK_END = 0x23;
    public const ushort VK_HOME = 0x24;
    public const ushort VK_LEFT = 0x25;
    public const ushort VK_UP = 0x26;
    public const ushort VK_RIGHT = 0x27;
    public const ushort VK_DOWN = 0x28;
    public const ushort VK_INSERT = 0x2D;
    public const ushort VK_DELETE = 0x2E;       // ⌦（macOS 的 kVK_ForwardDelete）

    public const ushort VK_0 = 0x30;
    public const ushort VK_1 = 0x31;
    public const ushort VK_2 = 0x32;
    public const ushort VK_3 = 0x33;
    public const ushort VK_4 = 0x34;
    public const ushort VK_5 = 0x35;
    public const ushort VK_6 = 0x36;
    public const ushort VK_7 = 0x37;
    public const ushort VK_8 = 0x38;
    public const ushort VK_9 = 0x39;

    public const ushort VK_A = 0x41;
    public const ushort VK_B = 0x42;
    public const ushort VK_C = 0x43;
    public const ushort VK_D = 0x44;
    public const ushort VK_E = 0x45;
    public const ushort VK_F = 0x46;
    public const ushort VK_G = 0x47;
    public const ushort VK_H = 0x48;
    public const ushort VK_I = 0x49;
    public const ushort VK_J = 0x4A;
    public const ushort VK_K = 0x4B;
    public const ushort VK_L = 0x4C;
    public const ushort VK_M = 0x4D;
    public const ushort VK_N = 0x4E;
    public const ushort VK_O = 0x4F;
    public const ushort VK_P = 0x50;
    public const ushort VK_Q = 0x51;
    public const ushort VK_R = 0x52;
    public const ushort VK_S = 0x53;
    public const ushort VK_T = 0x54;
    public const ushort VK_U = 0x55;
    public const ushort VK_V = 0x56;
    public const ushort VK_W = 0x57;
    public const ushort VK_X = 0x58;
    public const ushort VK_Y = 0x59;
    public const ushort VK_Z = 0x5A;

    public const ushort VK_NUMPAD0 = 0x60;
    public const ushort VK_NUMPAD1 = 0x61;
    public const ushort VK_NUMPAD2 = 0x62;
    public const ushort VK_NUMPAD3 = 0x63;
    public const ushort VK_NUMPAD4 = 0x64;
    public const ushort VK_NUMPAD5 = 0x65;
    public const ushort VK_NUMPAD6 = 0x66;
    public const ushort VK_NUMPAD7 = 0x67;
    public const ushort VK_NUMPAD8 = 0x68;
    public const ushort VK_NUMPAD9 = 0x69;
    public const ushort VK_MULTIPLY = 0x6A;
    public const ushort VK_ADD = 0x6B;
    public const ushort VK_SEPARATOR = 0x6C;
    public const ushort VK_SUBTRACT = 0x6D;
    public const ushort VK_DECIMAL = 0x6E;
    public const ushort VK_DIVIDE = 0x6F;
    public const ushort VK_F1 = 0x70;
    public const ushort VK_F2 = 0x71;
    public const ushort VK_F3 = 0x72;
    public const ushort VK_F4 = 0x73;
    public const ushort VK_F5 = 0x74;
    public const ushort VK_F6 = 0x75;
    public const ushort VK_F7 = 0x76;
    public const ushort VK_F8 = 0x77;
    public const ushort VK_F9 = 0x78;
    public const ushort VK_F10 = 0x79;
    public const ushort VK_F11 = 0x7A;
    public const ushort VK_F12 = 0x7B;
    public const ushort VK_CAPITAL = 0x14;
    public const ushort VK_NUMLOCK = 0x90;
    public const ushort VK_VOLUME_MUTE = 0xAD;
    public const ushort VK_VOLUME_DOWN = 0xAE;
    public const ushort VK_VOLUME_UP = 0xAF;

    public const ushort VK_OEM_1 = 0xBA;        // ';:'
    public const ushort VK_OEM_PLUS = 0xBB;     // '+' / '='（ISO 键盘）
    public const ushort VK_OEM_COMMA = 0xBC;
    public const ushort VK_OEM_MINUS = 0xBD;
    public const ushort VK_OEM_PERIOD = 0xBE;
    public const ushort VK_OEM_2 = 0xBF;        // '/?'
    public const ushort VK_OEM_3 = 0xC0;        // '`~'
    public const ushort VK_OEM_4 = 0xDB;        // '[{'
    public const ushort VK_OEM_5 = 0xDC;        // '\|'
    public const ushort VK_OEM_6 = 0xDD;        // ']}'
    public const ushort VK_OEM_7 = 0xDE;        // ''"'
    public const ushort VK_OEM_102 = 0xE2;      // ISO 键盘 '\|'（美式键盘上无此键）

    /// <summary>把 VK_A..VK_Z / VK_0..VK_9 解析为对应的字符；失败返回 null。</summary>
    public static char? ToPrintableCharacter(ushort virtualKey)
    {
        if (virtualKey >= VK_0 && virtualKey <= VK_9) return (char)('0' + (virtualKey - VK_0));
        if (virtualKey >= VK_A && virtualKey <= VK_Z) return (char)('A' + (virtualKey - VK_A));
        return null;
    }
}
