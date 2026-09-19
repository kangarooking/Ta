namespace Ta.HotKeys;

/// <summary>
/// Carbon VK → Windows VK 的映射质量分级。
/// </summary>
public enum VirtualKeyTranslationKind
{
    /// <summary>码值不同但语义一对一，可直接迁移。</summary>
    Exact,

    /// <summary>语义一对一，但 Windows 侧改名（如 kVK_Delete → VK_BACK）。</summary>
    Renamed,

    /// <summary>多个 Carbon 码落到同一个 Windows VK —— 迁移会丢失区分度。</summary>
    ManyToOne,

    /// <summary>Windows 无等价键，无法迁移。</summary>
    Unmapped,
}

/// <summary>一条翻译结果。</summary>
/// <param name="WindowsVirtualKey">Windows VK；<see cref="VirtualKeyTranslationKind.Unmapped"/> 时为 0。</param>
/// <param name="Kind">映射质量。</param>
/// <param name="Note">迁移注意事项（非 1:1 时必有内容）。</param>
public readonly record struct VirtualKeyTranslation(
    ushort WindowsVirtualKey,
    VirtualKeyTranslationKind Kind,
    string? Note)
{
    public bool IsUsable => Kind != VirtualKeyTranslationKind.Unmapped;

    public static readonly VirtualKeyTranslation NotMapped =
        new(0, VirtualKeyTranslationKind.Unmapped, null);
}

/// <summary>
/// Carbon <c>kVK_*</c> → Windows <c>VK_*</c> 翻译表。
///
/// 这是移植参考文档 §14 风险 #7 点名的必建件：
/// 「Carbon VK 码（<c>kVK_ANSI_1</c> = 18）不是 Windows VK 码（<c>VK_1</c> = 0x31）——
/// 翻译表必须自建，且部分键非 1:1」。
///
/// 本表是**纯逻辑**，不碰 Win32，因此可被单测逐条断言。
/// 用途：读取 / 迁移 Mac 版写入的 <c>globalHotKey.&lt;rawValue&gt;</c> 持久化数据。
/// Windows 侧原生录制与持久化一律使用 VK 码，不再经过本表。
/// </summary>
public static class CarbonToWindowsVirtualKey
{
    private static readonly Dictionary<ushort, VirtualKeyTranslation> Table = BuildTable();

    /// <summary>已覆盖的 Carbon 码总数（含 Unmapped）。</summary>
    public static int Count => Table.Count;

    /// <summary>
    /// 翻译单个 Carbon 键码。未收录的码返回 <see cref="VirtualKeyTranslationKind.Unmapped"/>。
    /// </summary>
    public static VirtualKeyTranslation Translate(ushort carbonVirtualKey)
    {
        return Table.TryGetValue(carbonVirtualKey, out var translation)
            ? translation
            : new VirtualKeyTranslation(0, VirtualKeyTranslationKind.Unmapped, "未收录的 Carbon 键码");
    }

    /// <summary>尝试翻译，只有产出可用 VK 时返回 true。</summary>
    public static bool TryTranslate(ushort carbonVirtualKey, out ushort windowsVirtualKey)
    {
        var translation = Translate(carbonVirtualKey);
        windowsVirtualKey = translation.WindowsVirtualKey;
        return translation.IsUsable;
    }

    /// <summary>该 Carbon 码是否被多个键共享同一个 Windows VK。</summary>
    public static bool IsManyToOne(ushort carbonVirtualKey)
    {
        return Translate(carbonVirtualKey).Kind == VirtualKeyTranslationKind.ManyToOne;
    }

    /// <summary>
    /// 该 Carbon 码是否已被收录（收录 ≠ 可翻译：Unmapped 的条目同样算已收录，
    /// 用来把「刻意标注为无等价物」与「忘了加进表里」区分开）。
    /// </summary>
    public static bool Contains(ushort carbonVirtualKey) => Table.ContainsKey(carbonVirtualKey);

    /// <summary>枚举所有非 1:1 的条目，用于回归报告。</summary>
    public static IReadOnlyList<(ushort Carbon, VirtualKeyTranslation Translation)> NonOneToOne()
    {
        return Table
            .Where(pair => pair.Value.Kind != VirtualKeyTranslationKind.Exact)
            .OrderBy(pair => pair.Key)
            .Select(pair => (pair.Key, pair.Value))
            .ToArray();
    }

    private static Dictionary<ushort, VirtualKeyTranslation> BuildTable()
    {
        var table = new Dictionary<ushort, VirtualKeyTranslation>();

        // ── 主键盘数字行 ──────────────────────────────────────────
        // ⚠️ 数字键的 Carbon 码是物理键序（5 与 6 倒序），不是 18+n。
        Add(table, CarbonVirtualKeyCodes.ANSI_1, VirtualKeys.VK_1);
        Add(table, CarbonVirtualKeyCodes.ANSI_2, VirtualKeys.VK_2);
        Add(table, CarbonVirtualKeyCodes.ANSI_3, VirtualKeys.VK_3);
        Add(table, CarbonVirtualKeyCodes.ANSI_4, VirtualKeys.VK_4);
        Add(table, CarbonVirtualKeyCodes.ANSI_5, VirtualKeys.VK_5);
        Add(table, CarbonVirtualKeyCodes.ANSI_6, VirtualKeys.VK_6);
        Add(table, CarbonVirtualKeyCodes.ANSI_7, VirtualKeys.VK_7);
        Add(table, CarbonVirtualKeyCodes.ANSI_8, VirtualKeys.VK_8);
        Add(table, CarbonVirtualKeyCodes.ANSI_9, VirtualKeys.VK_9);
        Add(table, CarbonVirtualKeyCodes.ANSI_0, VirtualKeys.VK_0);

        // ── 字母 ──────────────────────────────────────────────────
        Add(table, CarbonVirtualKeyCodes.ANSI_A, VirtualKeys.VK_A);
        Add(table, CarbonVirtualKeyCodes.ANSI_B, VirtualKeys.VK_B);
        Add(table, CarbonVirtualKeyCodes.ANSI_C, VirtualKeys.VK_C);
        Add(table, CarbonVirtualKeyCodes.ANSI_D, VirtualKeys.VK_D);
        Add(table, CarbonVirtualKeyCodes.ANSI_E, VirtualKeys.VK_E);
        Add(table, CarbonVirtualKeyCodes.ANSI_F, VirtualKeys.VK_F);
        Add(table, CarbonVirtualKeyCodes.ANSI_G, VirtualKeys.VK_G);
        Add(table, CarbonVirtualKeyCodes.ANSI_H, VirtualKeys.VK_H);
        Add(table, CarbonVirtualKeyCodes.ANSI_I, VirtualKeys.VK_I);
        Add(table, CarbonVirtualKeyCodes.ANSI_J, VirtualKeys.VK_J);
        Add(table, CarbonVirtualKeyCodes.ANSI_K, VirtualKeys.VK_K);
        Add(table, CarbonVirtualKeyCodes.ANSI_L, VirtualKeys.VK_L);
        Add(table, CarbonVirtualKeyCodes.ANSI_M, VirtualKeys.VK_M);
        Add(table, CarbonVirtualKeyCodes.ANSI_N, VirtualKeys.VK_N);
        Add(table, CarbonVirtualKeyCodes.ANSI_O, VirtualKeys.VK_O);
        Add(table, CarbonVirtualKeyCodes.ANSI_P, VirtualKeys.VK_P);
        Add(table, CarbonVirtualKeyCodes.ANSI_Q, VirtualKeys.VK_Q);
        Add(table, CarbonVirtualKeyCodes.ANSI_R, VirtualKeys.VK_R);
        Add(table, CarbonVirtualKeyCodes.ANSI_S, VirtualKeys.VK_S);
        Add(table, CarbonVirtualKeyCodes.ANSI_T, VirtualKeys.VK_T);
        Add(table, CarbonVirtualKeyCodes.ANSI_U, VirtualKeys.VK_U);
        Add(table, CarbonVirtualKeyCodes.ANSI_V, VirtualKeys.VK_V);
        Add(table, CarbonVirtualKeyCodes.ANSI_W, VirtualKeys.VK_W);
        Add(table, CarbonVirtualKeyCodes.ANSI_X, VirtualKeys.VK_X);
        Add(table, CarbonVirtualKeyCodes.ANSI_Y, VirtualKeys.VK_Y);
        Add(table, CarbonVirtualKeyCodes.ANSI_Z, VirtualKeys.VK_Z);

        // ── 数字行符号（码值与语义都变，属 1:1）───────────────────
        Add(table, CarbonVirtualKeyCodes.ANSI_Minus, VirtualKeys.VK_OEM_MINUS);
        Add(table, CarbonVirtualKeyCodes.ANSI_Equal, VirtualKeys.VK_OEM_PLUS);
        Add(table, CarbonVirtualKeyCodes.ANSI_LeftBracket, VirtualKeys.VK_OEM_4);
        Add(table, CarbonVirtualKeyCodes.ANSI_RightBracket, VirtualKeys.VK_OEM_6);
        Add(table, CarbonVirtualKeyCodes.ANSI_Backslash, VirtualKeys.VK_OEM_5);
        Add(table, CarbonVirtualKeyCodes.ANSI_Semicolon, VirtualKeys.VK_OEM_1);
        Add(table, CarbonVirtualKeyCodes.ANSI_Quote, VirtualKeys.VK_OEM_7);
        Add(table, CarbonVirtualKeyCodes.ANSI_Comma, VirtualKeys.VK_OEM_COMMA);
        Add(table, CarbonVirtualKeyCodes.ANSI_Period, VirtualKeys.VK_OEM_PERIOD);
        Add(table, CarbonVirtualKeyCodes.ANSI_Slash, VirtualKeys.VK_OEM_2);
        Add(table, CarbonVirtualKeyCodes.ANSI_Grave, VirtualKeys.VK_OEM_3);

        // ── 控制 / 导航键 ─────────────────────────────────────────
        // macOS 的 kVK_Delete 是「退格」，Windows 退格是 VK_BACK —— 名字反了，必须 Renamed。
        Add(table,
            CarbonVirtualKeyCodes.Return,
            VirtualKeys.VK_RETURN,
            VirtualKeyTranslationKind.Exact);
        Add(table,
            CarbonVirtualKeyCodes.Tab,
            VirtualKeys.VK_TAB,
            VirtualKeyTranslationKind.Exact);
        Add(table,
            CarbonVirtualKeyCodes.Space,
            VirtualKeys.VK_SPACE,
            VirtualKeyTranslationKind.Exact);
        Add(table,
            CarbonVirtualKeyCodes.Delete,
            VirtualKeys.VK_BACK,
            VirtualKeyTranslationKind.Renamed,
            "macOS kVK_Delete 是 ⌫ 退格；Windows 退格是 VK_BACK，VK_DELETE 是 ⌦");
        Add(table,
            CarbonVirtualKeyCodes.ForwardDelete,
            VirtualKeys.VK_DELETE,
            VirtualKeyTranslationKind.Renamed,
            "macOS kVK_ForwardDelete 是 ⌦；Windows 上是 VK_DELETE");
        Add(table,
            CarbonVirtualKeyCodes.Escape,
            VirtualKeys.VK_ESCAPE,
            VirtualKeyTranslationKind.Exact);
        Add(table,
            CarbonVirtualKeyCodes.Home,
            VirtualKeys.VK_HOME,
            VirtualKeyTranslationKind.Exact);
        Add(table,
            CarbonVirtualKeyCodes.End,
            VirtualKeys.VK_END,
            VirtualKeyTranslationKind.Exact);
        Add(table,
            CarbonVirtualKeyCodes.PageUp,
            VirtualKeys.VK_PRIOR,
            VirtualKeyTranslationKind.Renamed,
            "Windows 用 VK_PRIOR 表示 Page Up");
        Add(table,
            CarbonVirtualKeyCodes.PageDown,
            VirtualKeys.VK_NEXT,
            VirtualKeyTranslationKind.Renamed,
            "Windows 用 VK_NEXT 表示 Page Down");
        Add(table,
            CarbonVirtualKeyCodes.LeftArrow,
            VirtualKeys.VK_LEFT,
            VirtualKeyTranslationKind.Exact);
        Add(table,
            CarbonVirtualKeyCodes.RightArrow,
            VirtualKeys.VK_RIGHT,
            VirtualKeyTranslationKind.Exact);
        Add(table,
            CarbonVirtualKeyCodes.UpArrow,
            VirtualKeys.VK_UP,
            VirtualKeyTranslationKind.Exact);
        Add(table,
            CarbonVirtualKeyCodes.DownArrow,
            VirtualKeys.VK_DOWN,
            VirtualKeyTranslationKind.Exact);

        // ── F1..F12 ───────────────────────────────────────────────
        // Carbon 的 F 键码是乱序的（F5=96、F3=99、F12=111…），逐个列出而非 +0x??。
        Add(table, CarbonVirtualKeyCodes.F1, VirtualKeys.VK_F1);
        Add(table, CarbonVirtualKeyCodes.F2, VirtualKeys.VK_F2);
        Add(table, CarbonVirtualKeyCodes.F3, VirtualKeys.VK_F3);
        Add(table, CarbonVirtualKeyCodes.F4, VirtualKeys.VK_F4);
        Add(table, CarbonVirtualKeyCodes.F5, VirtualKeys.VK_F5);
        Add(table, CarbonVirtualKeyCodes.F6, VirtualKeys.VK_F6);
        Add(table, CarbonVirtualKeyCodes.F7, VirtualKeys.VK_F7);
        Add(table, CarbonVirtualKeyCodes.F8, VirtualKeys.VK_F8);
        Add(table, CarbonVirtualKeyCodes.F9, VirtualKeys.VK_F9);
        Add(table, CarbonVirtualKeyCodes.F10, VirtualKeys.VK_F10);
        Add(table, CarbonVirtualKeyCodes.F11, VirtualKeys.VK_F11);
        Add(table, CarbonVirtualKeyCodes.F12, VirtualKeys.VK_F12);

        // ── 小键盘 ────────────────────────────────────────────────
        Add(table, CarbonVirtualKeyCodes.ANSI_Keypad0, VirtualKeys.VK_NUMPAD0);
        Add(table, CarbonVirtualKeyCodes.ANSI_Keypad1, VirtualKeys.VK_NUMPAD1);
        Add(table, CarbonVirtualKeyCodes.ANSI_Keypad2, VirtualKeys.VK_NUMPAD2);
        Add(table, CarbonVirtualKeyCodes.ANSI_Keypad3, VirtualKeys.VK_NUMPAD3);
        Add(table, CarbonVirtualKeyCodes.ANSI_Keypad4, VirtualKeys.VK_NUMPAD4);
        Add(table, CarbonVirtualKeyCodes.ANSI_Keypad5, VirtualKeys.VK_NUMPAD5);
        Add(table, CarbonVirtualKeyCodes.ANSI_Keypad6, VirtualKeys.VK_NUMPAD6);
        Add(table, CarbonVirtualKeyCodes.ANSI_Keypad7, VirtualKeys.VK_NUMPAD7);
        Add(table, CarbonVirtualKeyCodes.ANSI_Keypad8, VirtualKeys.VK_NUMPAD8);
        Add(table, CarbonVirtualKeyCodes.ANSI_Keypad9, VirtualKeys.VK_NUMPAD9);
        Add(table, CarbonVirtualKeyCodes.ANSI_KeypadDecimal, VirtualKeys.VK_DECIMAL);
        Add(table, CarbonVirtualKeyCodes.ANSI_KeypadMultiply, VirtualKeys.VK_MULTIPLY);
        Add(table, CarbonVirtualKeyCodes.ANSI_KeypadPlus, VirtualKeys.VK_ADD);
        Add(table, CarbonVirtualKeyCodes.ANSI_KeypadDivide, VirtualKeys.VK_DIVIDE);
        Add(table, CarbonVirtualKeyCodes.ANSI_KeypadMinus, VirtualKeys.VK_SUBTRACT);

        // ⚠️ 两个 ManyToOne：迁移后无法区分主回车 / 小键盘回车、主 = / 小键盘 =。
        Add(table,
            CarbonVirtualKeyCodes.ANSI_KeypadEnter,
            VirtualKeys.VK_RETURN,
            VirtualKeyTranslationKind.ManyToOne,
            "小键盘 Enter 与主回车都落在 VK_RETURN，迁移后无法区分");
        Add(table,
            CarbonVirtualKeyCodes.ANSI_KeypadEquals,
            VirtualKeys.VK_OEM_PLUS,
            VirtualKeyTranslationKind.ManyToOne,
            "小键盘 = 与主 = 都落在 VK_OEM_PLUS，迁移后无法区分");
        Add(table,
            CarbonVirtualKeyCodes.ANSI_KeypadClear,
            0x0C, // VK_CLEAR —— Windows 上几乎不存在的键
            VirtualKeyTranslationKind.Renamed,
            "kVK_ANSI_KeypadClear 映射到 VK_CLEAR(0x0C)，多数键盘无此键，建议迁移时丢弃");

        // ── JIS / ISO 专有键（同样非 1:1）─────────────────────────
        Add(table,
            CarbonVirtualKeyCodes.ISO_Section,
            VirtualKeys.VK_OEM_102,
            VirtualKeyTranslationKind.ManyToOne,
            "ISO_Section 与 JIS_Underscore 都落在 VK_OEM_102");
        Add(table,
            CarbonVirtualKeyCodes.JIS_Underscore,
            VirtualKeys.VK_OEM_102,
            VirtualKeyTranslationKind.ManyToOne,
            "JIS_Underscore 与 ISO_Section 都落在 VK_OEM_102");
        Add(table,
            CarbonVirtualKeyCodes.JIS_Yen,
            VirtualKeys.VK_OEM_5,
            VirtualKeyTranslationKind.ManyToOne,
            "JIS_Yen 与 ANSI_Backslash 都落在 VK_OEM_5");
        Add(table,
            CarbonVirtualKeyCodes.JIS_KeypadComma,
            VirtualKeys.VK_SEPARATOR,
            VirtualKeyTranslationKind.Renamed,
            "JIS 小键盘逗号映射到 VK_SEPARATOR(0x6C)，与 VK_DECIMAL 语义不同");

        // ── 音量 / 锁定（语义一致，只是码不同）────────────────────
        Add(table, CarbonVirtualKeyCodes.VolumeUp, VirtualKeys.VK_VOLUME_UP);
        Add(table, CarbonVirtualKeyCodes.VolumeDown, VirtualKeys.VK_VOLUME_DOWN);
        Add(table, CarbonVirtualKeyCodes.Mute, VirtualKeys.VK_VOLUME_MUTE);
        Add(table, CarbonVirtualKeyCodes.CapsLock, VirtualKeys.VK_CAPITAL);

        // ── 语义漂移：macOS 的 Help 键在 PC 键盘上是 Insert ───────
        Add(table,
            CarbonVirtualKeyCodes.Help,
            VirtualKeys.VK_INSERT,
            VirtualKeyTranslationKind.ManyToOne,
            "macOS Help 键对应 PC 的 Insert，键面语义不同");

        // ── Windows 上完全无等价物 ───────────────────────────────
        Add(table, CarbonVirtualKeyCodes.F13, 0, VirtualKeyTranslationKind.Unmapped, "Windows 无 F13");
        Add(table, CarbonVirtualKeyCodes.F14, 0, VirtualKeyTranslationKind.Unmapped, "Windows 无 F14");
        Add(table, CarbonVirtualKeyCodes.F15, 0, VirtualKeyTranslationKind.Unmapped, "Windows 无 F15");
        Add(table, CarbonVirtualKeyCodes.F16, 0, VirtualKeyTranslationKind.Unmapped, "Windows 无 F16");
        Add(table, CarbonVirtualKeyCodes.F17, 0, VirtualKeyTranslationKind.Unmapped, "Windows 无 F17");
        Add(table, CarbonVirtualKeyCodes.F18, 0, VirtualKeyTranslationKind.Unmapped, "Windows 无 F18");
        Add(table, CarbonVirtualKeyCodes.F19, 0, VirtualKeyTranslationKind.Unmapped, "Windows 无 F19");
        Add(table, CarbonVirtualKeyCodes.F20, 0, VirtualKeyTranslationKind.Unmapped, "Windows 无 F20");
        Add(table,
            CarbonVirtualKeyCodes.Function,
            0,
            VirtualKeyTranslationKind.Unmapped,
            "macOS 的 fn 键是修饰键，RegisterHotKey 不接受修饰键作为热键 VK");
        Add(table,
            CarbonVirtualKeyCodes.Command,
            0,
            VirtualKeyTranslationKind.Unmapped,
            "⌘ 是修饰键；Windows 上对应 Ctrl，由 MOD_CONTROL 表达，不能作为热键 VK");
        Add(table,
            CarbonVirtualKeyCodes.Shift,
            0,
            VirtualKeyTranslationKind.Unmapped,
            "⇧ 是修饰键，由 MOD_SHIFT 表达");
        Add(table,
            CarbonVirtualKeyCodes.Option,
            0,
            VirtualKeyTranslationKind.Unmapped,
            "⌥ 是修饰键，由 MOD_ALT 表达");
        Add(table,
            CarbonVirtualKeyCodes.Control,
            0,
            VirtualKeyTranslationKind.Unmapped,
            "⌃ 是修饰键，由 MOD_CONTROL 表达");
        Add(table,
            CarbonVirtualKeyCodes.RightShift,
            0,
            VirtualKeyTranslationKind.Unmapped,
            "右 ⇧ 是修饰键，RegisterHotKey 不区分左右");
        Add(table,
            CarbonVirtualKeyCodes.RightOption,
            0,
            VirtualKeyTranslationKind.Unmapped,
            "右 ⌥ 是修饰键，RegisterHotKey 不区分左右");
        Add(table,
            CarbonVirtualKeyCodes.RightControl,
            0,
            VirtualKeyTranslationKind.Unmapped,
            "右 ⌃ 是修饰键，RegisterHotKey 不区分左右");

        return table;
    }

    private static void Add(
        Dictionary<ushort, VirtualKeyTranslation> table,
        ushort carbonVirtualKey,
        ushort windowsVirtualKey,
        VirtualKeyTranslationKind kind = VirtualKeyTranslationKind.Exact,
        string? note = null)
    {
        table[carbonVirtualKey] = new VirtualKeyTranslation(windowsVirtualKey, kind, note);
    }
}
