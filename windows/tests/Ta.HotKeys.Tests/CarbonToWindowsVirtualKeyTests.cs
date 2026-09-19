using Ta.HotKeys;

namespace Ta.HotKeys.Tests;

/// <summary>
/// Carbon <c>kVK_*</c> → Windows <c>VK_*</c> 翻译表。
///
/// 逐条断言，对应验收项 1 与移植参考文档 §14 风险 #7。
/// </summary>
public sealed class CarbonToWindowsVirtualKeyTests
{
    private static void AssertTranslation(ushort carbon, ushort expectedWindows, string why)
    {
        var translation = CarbonToWindowsVirtualKey.Translate(carbon);
        Assert.True(
            translation.IsUsable,
            $"Carbon 0x{carbon:X2} 应当可翻译（{why}），实际为 {translation.Kind}：{translation.Note}");
        Assert.Equal(expectedWindows, translation.WindowsVirtualKey);
    }

    [Fact]
    public void Digits1To6MapToWindowsDigitKeys()
    {
        // 数字键的 Carbon 码是物理键序，不是 18+n：ANSI_1=18 … ANSI_5=23、ANSI_6=22。
        AssertTranslation(CarbonVirtualKeyCodes.ANSI_1, 0x31, "kVK_ANSI_1(18) → VK_1(0x31)");
        AssertTranslation(CarbonVirtualKeyCodes.ANSI_2, 0x32, "kVK_ANSI_2(19) → VK_2(0x32)");
        AssertTranslation(CarbonVirtualKeyCodes.ANSI_3, 0x33, "kVK_ANSI_3(20) → VK_3(0x33)");
        AssertTranslation(CarbonVirtualKeyCodes.ANSI_4, 0x34, "kVK_ANSI_4(21) → VK_4(0x34)");
        AssertTranslation(CarbonVirtualKeyCodes.ANSI_5, 0x35, "kVK_ANSI_5(23) → VK_5(0x35)");
        AssertTranslation(CarbonVirtualKeyCodes.ANSI_6, 0x36, "kVK_ANSI_6(22) → VK_6(0x36)");

        // 关键不变量：Carbon 码值与 VK 码值绝不相同，证明真的需要翻译表。
        Assert.NotEqual(CarbonVirtualKeyCodes.ANSI_1, 0x31u);
        Assert.Equal(18, CarbonVirtualKeyCodes.ANSI_1);
    }

    [Fact]
    public void DigitRowOrderingIsPhysicalNotNumeric()
    {
        // 锁定「5 与 6 倒序」这个易踩坑点。
        Assert.Equal(23, CarbonVirtualKeyCodes.ANSI_5);
        Assert.Equal(22, CarbonVirtualKeyCodes.ANSI_6);
        Assert.True(CarbonVirtualKeyCodes.ANSI_5 > CarbonVirtualKeyCodes.ANSI_6);
    }

    [Fact]
    public void EscapeAndReturnMapAsExpected()
    {
        AssertTranslation(CarbonVirtualKeyCodes.Escape, 0x1B, "kVK_Escape(53) → VK_ESCAPE(0x1B)");
        AssertTranslation(CarbonVirtualKeyCodes.Return, 0x0D, "kVK_Return(36) → VK_RETURN(0x0D)");
        AssertTranslation(CarbonVirtualKeyCodes.Tab, 0x09, "kVK_Tab(48) → VK_TAB(0x09)");
        AssertTranslation(CarbonVirtualKeyCodes.Space, 0x20, "kVK_Space(49) → VK_SPACE(0x20)");

        // 退格 / 前删在两边名字是反的。
        var backspace = CarbonToWindowsVirtualKey.Translate(CarbonVirtualKeyCodes.Delete);
        Assert.Equal(0x08, backspace.WindowsVirtualKey);
        Assert.Equal(VirtualKeyTranslationKind.Renamed, backspace.Kind);

        var forwardDelete = CarbonToWindowsVirtualKey.Translate(CarbonVirtualKeyCodes.ForwardDelete);
        Assert.Equal(0x2E, forwardDelete.WindowsVirtualKey);
        Assert.Equal(VirtualKeyTranslationKind.Renamed, forwardDelete.Kind);
    }

    [Fact]
    public void ArrowKeysMapAsExpected()
    {
        AssertTranslation(CarbonVirtualKeyCodes.LeftArrow, 0x25, "kVK_LeftArrow(123) → VK_LEFT");
        AssertTranslation(CarbonVirtualKeyCodes.UpArrow, 0x26, "kVK_UpArrow(126) → VK_UP");
        AssertTranslation(CarbonVirtualKeyCodes.RightArrow, 0x27, "kVK_RightArrow(124) → VK_RIGHT");
        AssertTranslation(CarbonVirtualKeyCodes.DownArrow, 0x28, "kVK_DownArrow(125) → VK_DOWN");

        AssertTranslation(CarbonVirtualKeyCodes.Home, 0x24, "kVK_Home(115) → VK_HOME");
        AssertTranslation(CarbonVirtualKeyCodes.End, 0x23, "kVK_End(119) → VK_END");
        AssertTranslation(CarbonVirtualKeyCodes.PageUp, 0x21, "kVK_PageUp(116) → VK_PRIOR");
        AssertTranslation(CarbonVirtualKeyCodes.PageDown, 0x22, "kVK_PageDown(121) → VK_NEXT");
    }

    [Fact]
    public void FunctionKeysF1ToF12MapAsExpected()
    {
        // Carbon 的 F 键码是乱序的，逐条列出。
        AssertTranslation(CarbonVirtualKeyCodes.F1, 0x70, "kVK_F1(122) → VK_F1");
        AssertTranslation(CarbonVirtualKeyCodes.F2, 0x71, "kVK_F2(120) → VK_F2");
        AssertTranslation(CarbonVirtualKeyCodes.F3, 0x72, "kVK_F3(99) → VK_F3");
        AssertTranslation(CarbonVirtualKeyCodes.F4, 0x73, "kVK_F4(118) → VK_F4");
        AssertTranslation(CarbonVirtualKeyCodes.F5, 0x74, "kVK_F5(96) → VK_F5");
        AssertTranslation(CarbonVirtualKeyCodes.F6, 0x75, "kVK_F6(97) → VK_F6");
        AssertTranslation(CarbonVirtualKeyCodes.F7, 0x76, "kVK_F7(98) → VK_F7");
        AssertTranslation(CarbonVirtualKeyCodes.F8, 0x77, "kVK_F8(100) → VK_F8");
        AssertTranslation(CarbonVirtualKeyCodes.F9, 0x78, "kVK_F9(101) → VK_F9");
        AssertTranslation(CarbonVirtualKeyCodes.F10, 0x79, "kVK_F10(109) → VK_F10");
        AssertTranslation(CarbonVirtualKeyCodes.F11, 0x7A, "kVK_F11(103) → VK_F11");
        AssertTranslation(CarbonVirtualKeyCodes.F12, 0x7B, "kVK_F12(111) → VK_F12");
    }

    [Fact]
    public void LettersMapOneToOne()
    {
        AssertTranslation(CarbonVirtualKeyCodes.ANSI_A, 0x41, "kVK_ANSI_A(0) → VK_A");
        AssertTranslation(CarbonVirtualKeyCodes.ANSI_Z, 0x5A, "kVK_ANSI_Z(6) → VK_Z");
        AssertTranslation(CarbonVirtualKeyCodes.ANSI_S, 0x53, "kVK_ANSI_S(1) → VK_S");
    }

    [Fact]
    public void KeypadEnterCollidesWithReturn()
    {
        var keypadEnter = CarbonToWindowsVirtualKey.Translate(CarbonVirtualKeyCodes.ANSI_KeypadEnter);
        Assert.Equal(0x0D, keypadEnter.WindowsVirtualKey);
        Assert.Equal(VirtualKeyTranslationKind.ManyToOne, keypadEnter.Kind);
        Assert.True(CarbonToWindowsVirtualKey.IsManyToOne(CarbonVirtualKeyCodes.ANSI_KeypadEnter));
        Assert.False(string.IsNullOrEmpty(keypadEnter.Note));
    }

    [Fact]
    public void KeysWithoutWindowsEquivalentAreUnmapped()
    {
        foreach (var carbon in new ushort[]
                 {
                     CarbonVirtualKeyCodes.F13, CarbonVirtualKeyCodes.F14, CarbonVirtualKeyCodes.F15,
                     CarbonVirtualKeyCodes.F16, CarbonVirtualKeyCodes.F17, CarbonVirtualKeyCodes.F18,
                     CarbonVirtualKeyCodes.F19, CarbonVirtualKeyCodes.F20, CarbonVirtualKeyCodes.Function,
                 })
        {
            Assert.False(CarbonToWindowsVirtualKey.TryTranslate(carbon, out _), $"Carbon 0x{carbon:X2} 应当无等价键");
            Assert.Equal(VirtualKeyTranslationKind.Unmapped, CarbonToWindowsVirtualKey.Translate(carbon).Kind);
        }
    }

    [Fact]
    public void ModifierKeysAreNeverHotKeySubjects()
    {
        foreach (var carbon in new ushort[]
                 {
                     CarbonVirtualKeyCodes.Command, CarbonVirtualKeyCodes.Shift, CarbonVirtualKeyCodes.Option,
                     CarbonVirtualKeyCodes.Control, CarbonVirtualKeyCodes.RightShift,
                     CarbonVirtualKeyCodes.RightOption, CarbonVirtualKeyCodes.RightControl,
                 })
        {
            Assert.False(CarbonToWindowsVirtualKey.TryTranslate(carbon, out _), $"修饰键 0x{carbon:X2} 不能当热键主体");
        }
    }

    [Fact]
    public void UnknownCodesAreReportedAsUnmapped()
    {
        Assert.Equal(VirtualKeyTranslationKind.Unmapped, CarbonToWindowsVirtualKey.Translate(0xFE).Kind);
        Assert.False(CarbonToWindowsVirtualKey.TryTranslate(0xFE, out _));
    }

    [Fact]
    public void TableCoversEveryDeclaredCarbonCode()
    {
        // 防止有人加了常量却忘了进翻译表（收录但标 Unmapped 也算收录）。
        var declared = typeof(CarbonVirtualKeyCodes)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.FieldType == typeof(ushort))
            .Select(field => (ushort)field.GetValue(null)!)
            .ToArray();

        Assert.NotEmpty(declared);
        Assert.Equal(declared.Length, CarbonToWindowsVirtualKey.Count);

        foreach (var code in declared)
        {
            Assert.True(
                CarbonToWindowsVirtualKey.Contains(code),
                $"Carbon 0x{code:X2} 未收录进翻译表");
        }

        // 未收录的码必须被识别为「未知」，而不是悄悄返回某个 VK。
        Assert.False(CarbonToWindowsVirtualKey.Contains(0xFE));
    }

    [Fact]
    public void NonOneToOneEntriesAreExplicitlyTagged()
    {
        var entries = CarbonToWindowsVirtualKey.NonOneToOne();
        Assert.NotEmpty(entries);

        // 每条非 1:1 都必须带说明，方便后续维护者知道坑在哪。
        foreach (var (carbon, translation) in entries)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(translation.Note),
                $"Carbon 0x{carbon:X2}（{translation.Kind}）缺少迁移说明");
        }
    }

    [Fact]
    public void ModifierBitsTranslateAcrossNamespaces()
    {
        // ⌘⌥⇧ → Ctrl+Alt+Shift，且**不含** Win。
        var windows = HotKeyModifierMapping.ToWindowsModifiers(
            (uint)CarbonModifiers.Command | (uint)CarbonModifiers.Option | (uint)CarbonModifiers.Shift);

        Assert.Equal(HotKeyModifiers.Control | HotKeyModifiers.Alt | HotKeyModifiers.Shift, windows);
        Assert.False(windows.HasFlag(HotKeyModifiers.Win));

        // ⌃ 也落到 Ctrl（macOS 侧默认不含 controlKey，这里是反向校验）。
        var withControl = HotKeyModifierMapping.ToWindowsModifiers((uint)CarbonModifiers.Control);
        Assert.Equal(HotKeyModifiers.Control, withControl);
    }
}
