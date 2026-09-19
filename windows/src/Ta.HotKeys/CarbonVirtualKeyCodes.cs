namespace Ta.HotKeys;

/// <summary>
/// Carbon <c>kVK_*</c> 虚拟键码常量。
///
/// 对应 macOS 头文件 <c>Carbon/HIToolbox/Events.h</c>。
/// 这些值**不是** Windows VK 码（见 <see cref="CarbonToWindowsVirtualKey"/>），
/// 仅在读取 / 迁移 Mac 版持久化快捷键时使用。
///
/// 参考移植参考文档 §13、§14 风险 #7。
/// </summary>
/// <remarks>
/// 两处著名陷阱（都写死在下面的值里）：
/// 1. <see cref="ANSI5"/> = 23 而 <see cref="ANSI6"/> = 22 —— 数字键的码值与键面数字**倒序**。
/// 2. 小键盘 Enter（<see cref="ANSIKeypadEnter"/> = 76）与主回车（<see cref="Return"/> = 36）
///    在 Carbon 里是两个码，在 Windows 里都落在 VK_RETURN 上。
/// </remarks>
internal static class CarbonVirtualKeyCodes
{
    // ── 字母（Carbon 顺序是键盘物理位置，非字母序）─────────────────
    public const ushort ANSI_A = 0x00;
    public const ushort ANSI_S = 0x01;
    public const ushort ANSI_D = 0x02;
    public const ushort ANSI_F = 0x03;
    public const ushort ANSI_H = 0x04;
    public const ushort ANSI_G = 0x05;
    public const ushort ANSI_Z = 0x06;
    public const ushort ANSI_X = 0x07;
    public const ushort ANSI_C = 0x08;
    public const ushort ANSI_V = 0x09;
    public const ushort ANSI_B = 0x0B;
    public const ushort ANSI_Q = 0x0C;
    public const ushort ANSI_W = 0x0D;
    public const ushort ANSI_E = 0x0E;
    public const ushort ANSI_R = 0x0F;
    public const ushort ANSI_Y = 0x10;
    public const ushort ANSI_T = 0x11;
    public const ushort ANSI_I = 0x22;
    public const ushort ANSI_J = 0x26;
    public const ushort ANSI_K = 0x28;
    public const ushort ANSI_L = 0x25;
    public const ushort ANSI_M = 0x2E;
    public const ushort ANSI_N = 0x2D;
    public const ushort ANSI_O = 0x1F;
    public const ushort ANSI_P = 0x23;
    public const ushort ANSI_U = 0x20;

    // ── 数字行（注意 5 / 6 倒序）──────────────────────────────────
    public const ushort ANSI_1 = 0x12; // 18
    public const ushort ANSI_2 = 0x13; // 19
    public const ushort ANSI_3 = 0x14; // 20
    public const ushort ANSI_4 = 0x15; // 21
    public const ushort ANSI_5 = 0x17; // 23  ← 与 ANSI_6 对调
    public const ushort ANSI_6 = 0x16; // 22  ← 与 ANSI_5 对调
    public const ushort ANSI_7 = 0x1A; // 26
    public const ushort ANSI_8 = 0x1C; // 28
    public const ushort ANSI_9 = 0x19; // 25
    public const ushort ANSI_0 = 0x1D; // 29

    // ── 数字行符号 ────────────────────────────────────────────────
    public const ushort ANSI_Equal = 0x18;          // 24
    public const ushort ANSI_Minus = 0x1B;          // 27
    public const ushort ANSI_RightBracket = 0x1E;   // 30
    public const ushort ANSI_LeftBracket = 0x21;    // 33
    public const ushort ANSI_Quote = 0x27;          // 39
    public const ushort ANSI_Backslash = 0x2A;      // 42
    public const ushort ANSI_Semicolon = 0x29;      // 41
    public const ushort ANSI_Comma = 0x2B;          // 43
    public const ushort ANSI_Slash = 0x2C;          // 44
    public const ushort ANSI_Period = 0x2F;         // 47
    public const ushort ANSI_Grave = 0x32;          // 50
    public const ushort ANSI_KeypadDecimal = 0x41;  // 65
    public const ushort ANSI_KeypadMultiply = 0x43; // 67
    public const ushort ANSI_KeypadPlus = 0x45;     // 69
    public const ushort ANSI_KeypadClear = 0x47;    // 71
    public const ushort ANSI_KeypadDivide = 0x4B;   // 75
    public const ushort ANSI_KeypadEnter = 0x4C;    // 76
    public const ushort ANSI_KeypadMinus = 0x4E;    // 78
    public const ushort ANSI_KeypadEquals = 0x51;   // 81

    // ── 小键盘数字（0x5A 是 F20，小键盘从这里跳开，没有 keypad 0x5A）──
    public const ushort ANSI_Keypad0 = 0x52; // 82
    public const ushort ANSI_Keypad1 = 0x53; // 83
    public const ushort ANSI_Keypad2 = 0x54; // 84
    public const ushort ANSI_Keypad3 = 0x55; // 85
    public const ushort ANSI_Keypad4 = 0x56; // 86
    public const ushort ANSI_Keypad5 = 0x57; // 87
    public const ushort ANSI_Keypad6 = 0x58; // 88
    public const ushort ANSI_Keypad7 = 0x59; // 89
    public const ushort ANSI_Keypad8 = 0x5B; // 91
    public const ushort ANSI_Keypad9 = 0x5C; // 92

    // ── 控制与导航 ────────────────────────────────────────────────
    public const ushort Return = 0x24;         // 36   → VK_RETURN
    public const ushort Tab = 0x30;            // 48   → VK_TAB
    public const ushort Space = 0x31;          // 49   → VK_SPACE
    public const ushort Delete = 0x33;         // 51   → VK_BACK（⌫）
    public const ushort Escape = 0x35;         // 53   → VK_ESCAPE
    public const ushort Command = 0x37;        // 55
    public const ushort Shift = 0x38;          // 56
    public const ushort CapsLock = 0x39;       // 57
    public const ushort Option = 0x3A;         // 58
    public const ushort Control = 0x3B;        // 59
    public const ushort RightShift = 0x3C;     // 60
    public const ushort RightOption = 0x3D;    // 61
    public const ushort RightControl = 0x3E;   // 62
    public const ushort Function = 0x3F;       // 63   → Windows 无等价物
    public const ushort F17 = 0x40;            // 64
    public const ushort VolumeUp = 0x48;       // 72
    public const ushort VolumeDown = 0x49;     // 73
    public const ushort Mute = 0x4A;           // 74
    public const ushort F18 = 0x4F;            // 79
    public const ushort F19 = 0x50;            // 80
    public const ushort F20 = 0x5A;            // 90
    public const ushort F5 = 0x60;             // 96
    public const ushort F6 = 0x61;             // 97
    public const ushort F7 = 0x62;             // 98
    public const ushort F3 = 0x63;             // 99
    public const ushort F8 = 0x64;             // 100
    public const ushort F9 = 0x65;             // 101
    public const ushort F11 = 0x67;            // 103
    public const ushort F13 = 0x69;            // 105  → Windows 无等价物
    public const ushort F16 = 0x6A;            // 106  → Windows 无等价物
    public const ushort F14 = 0x6B;            // 107  → Windows 无等价物
    public const ushort F10 = 0x6D;            // 109
    public const ushort F12 = 0x6F;            // 111
    public const ushort F15 = 0x71;            // 113  → Windows 无等价物
    public const ushort Help = 0x72;           // 114  → VK_INSERT（语义不同！）
    public const ushort Home = 0x73;           // 115
    public const ushort PageUp = 0x74;         // 116
    public const ushort ForwardDelete = 0x75;  // 117  → VK_DELETE（⌦）
    public const ushort F4 = 0x76;             // 118
    public const ushort End = 0x77;            // 119
    public const ushort F2 = 0x78;             // 120
    public const ushort PageDown = 0x79;       // 121
    public const ushort F1 = 0x7A;             // 122
    public const ushort LeftArrow = 0x7B;      // 123
    public const ushort RightArrow = 0x7C;     // 124
    public const ushort DownArrow = 0x7D;      // 125
    public const ushort UpArrow = 0x7E;        // 126

    // ── JIS / ISO 专有键（均非 1:1）──────────────────────────────
    public const ushort JIS_Yen = 0x5D;              // 93
    public const ushort JIS_Underscore = 0x5E;       // 94
    public const ushort JIS_KeypadComma = 0x5F;      // 95
    public const ushort ISO_Section = 0x0A;          // 10
}
