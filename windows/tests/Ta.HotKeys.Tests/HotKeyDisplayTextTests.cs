using Ta.HotKeys;

namespace Ta.HotKeys.Tests;

/// <summary>显示文本格式化。</summary>
public sealed class HotKeyDisplayTextTests
{
    private const uint ModNoRepeat = 0x4000;

    [Fact]
    public void DefaultShortcutsRenderAsWindowsStyleText()
    {
        // 选定的方案：Windows 侧显示用 "Ctrl+Alt+Shift+1" 这种可读名，
        // 而不是 macOS 的 ⇧⌥⌘1 符号串（符号串另存于 MacParitySymbolicText 供 parity 断言）。
        var defaults = new[]
        {
            (GlobalHotKeyAction.InteractiveCapture, "Ctrl+Alt+Shift+2"),
            (GlobalHotKeyAction.IntelligentCapture, "Ctrl+Alt+Shift+1"),
            (GlobalHotKeyAction.ImageCapture, "Ctrl+Alt+Shift+3"),
            (GlobalHotKeyAction.PinCapture, "Ctrl+Alt+Shift+4"),
            (GlobalHotKeyAction.LongCapture, "Ctrl+Alt+Shift+5"),
            (GlobalHotKeyAction.TranslationCapture, "Ctrl+Alt+Shift+6"),
        };

        foreach (var (action, expected) in defaults)
        {
            Assert.Equal(expected, action.DefaultShortcut().DisplayText);
        }
    }

    [Fact]
    public void ModifierOrderIsCtrlAltShiftWin()
    {
        var shortcut = new HotKeyShortcut(
            0x53,
            HotKeyModifiers.Win | HotKeyModifiers.Shift | HotKeyModifiers.Control | HotKeyModifiers.Alt,
            "S");

        Assert.Equal("Ctrl+Alt+Shift+Win+S", shortcut.DisplayText);
    }

    [Fact]
    public void SubsetsOmitAbsentModifiers()
    {
        Assert.Equal("Ctrl+F1", new HotKeyShortcut(0x70, HotKeyModifiers.Control, "F1").DisplayText);
        Assert.Equal("Alt+⇥", new HotKeyShortcut(0x09, HotKeyModifiers.Alt, "⇥").DisplayText);
        Assert.Equal("Shift+Win+P", new HotKeyShortcut(0x50, HotKeyModifiers.Shift | HotKeyModifiers.Win, "P").DisplayText);
        Assert.Equal("F5", new HotKeyShortcut(0x74, HotKeyModifiers.None, "F5").DisplayText);
    }

    [Fact]
    public void ToStringUsesDisplayText()
    {
        Assert.Equal(
            "Ctrl+Alt+Shift+1",
            GlobalHotKeyAction.IntelligentCapture.DefaultShortcut().ToString());
    }

    [Fact]
    public void ModifierWordAlwaysIncludesNoRepeat()
    {
        var shortcut = GlobalHotKeyAction.IntelligentCapture.DefaultShortcut();

        const uint expected = 0x0001 // MOD_ALT
                            | 0x0002 // MOD_CONTROL
                            | 0x0004 // MOD_SHIFT
                            | ModNoRepeat;

        Assert.Equal(expected, shortcut.ToModifierWord());
        Assert.NotEqual(0x0008u, shortcut.ToModifierWord() & 0x0008); // 不带 MOD_WIN

        // 显式关闭 NOREPEAT 时按纯修饰键比较（身份串用这条）。
        Assert.Equal(0x0007u, shortcut.ToModifierWord(includeNoRepeat: false));
    }

    [Fact]
    public void IdentityDistinguishesDifferentCombinations()
    {
        var a = new HotKeyShortcut(0x31, HotKeyModifiers.Control | HotKeyModifiers.Alt | HotKeyModifiers.Shift, "1");
        var b = new HotKeyShortcut(0x32, HotKeyModifiers.Control | HotKeyModifiers.Alt | HotKeyModifiers.Shift, "2");
        var c = new HotKeyShortcut(0x31, HotKeyModifiers.Control | HotKeyModifiers.Shift, "1");

        Assert.NotEqual(a.Identity, b.Identity);
        Assert.NotEqual(a.Identity, c.Identity);
        Assert.Equal(a.Identity, new HotKeyShortcut(0x31, HotKeyModifiers.Control | HotKeyModifiers.Alt | HotKeyModifiers.Shift, "1").Identity);
    }
}

/// <summary>按键录制。</summary>
public sealed class HotKeyRecorderTests
{
    [Fact]
    public void EscapeCancelsRecording()
    {
        var result = HotKeyRecorder.Capture(HotKeyRecorder.CancelVirtualKey, control: true, alt: true, shift: true, win: false);

        Assert.Equal(HotKeyRecorderOutcome.Cancelled, result.Outcome);
        Assert.Null(result.Shortcut);
    }

    [Fact]
    public void DigitWithModifiersIsAccepted()
    {
        var result = HotKeyRecorder.Capture(0x31, control: true, alt: true, shift: true, win: false);

        Assert.True(result.IsAccepted);
        Assert.NotNull(result.Shortcut);
        Assert.Equal("Ctrl+Alt+Shift+1", result.Shortcut.Value.DisplayText);
        Assert.Equal(0x31, result.Shortcut.Value.VirtualKey);
    }

    [Fact]
    public void SingleKeyWithoutModifiersIsAccepted()
    {
        var result = HotKeyRecorder.Capture(0x41, control: false, alt: false, shift: false, win: false);

        Assert.True(result.IsAccepted);
        Assert.Equal("A", result.Shortcut!.Value.DisplayText);
    }

    [Fact]
    public void ModifierKeysAloneAreRejected()
    {
        foreach (var virtualKey in new ushort[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x14, 0x90 })
        {
            var result = HotKeyRecorder.Capture(virtualKey, control: false, alt: false, shift: false, win: false);
            Assert.Equal(HotKeyRecorderOutcome.Unrecognized, result.Outcome);
            Assert.Equal(HotKeyRecorder.UnrecognizedMessage, result.Message);
        }
    }

    [Fact]
    public void UnknownKeyIsReportedAsUnrecognized()
    {
        var result = HotKeyRecorder.Capture(0xFE, control: true, alt: false, shift: false, win: false);

        Assert.Equal(HotKeyRecorderOutcome.Unrecognized, result.Outcome);
        Assert.Null(result.Shortcut);
        Assert.Equal("无法识别这个按键", result.Message);
    }

    [Fact]
    public void SpecialKeyLabelsMirrorMacOSRecorder()
    {
        // HotKeyRecorderView.swift:128-155 的 special 字典，键码换成 Windows VK。
        Assert.Equal("↩", HotKeyRecorder.KeyLabelFor(0x0D));
        Assert.Equal("⇥", HotKeyRecorder.KeyLabelFor(0x09));
        Assert.Equal("Space", HotKeyRecorder.KeyLabelFor(0x20));
        Assert.Equal("⌫", HotKeyRecorder.KeyLabelFor(0x08));
        Assert.Equal("⌦", HotKeyRecorder.KeyLabelFor(0x2E));
        Assert.Equal("Home", HotKeyRecorder.KeyLabelFor(0x24));
        Assert.Equal("End", HotKeyRecorder.KeyLabelFor(0x23));
        Assert.Equal("Page Up", HotKeyRecorder.KeyLabelFor(0x21));
        Assert.Equal("Page Down", HotKeyRecorder.KeyLabelFor(0x22));
        Assert.Equal("←", HotKeyRecorder.KeyLabelFor(0x25));
        Assert.Equal("→", HotKeyRecorder.KeyLabelFor(0x27));
        Assert.Equal("↑", HotKeyRecorder.KeyLabelFor(0x26));
        Assert.Equal("↓", HotKeyRecorder.KeyLabelFor(0x28));
        Assert.Equal("F1", HotKeyRecorder.KeyLabelFor(0x70));
        Assert.Equal("F12", HotKeyRecorder.KeyLabelFor(0x7B));
        Assert.Equal("7", HotKeyRecorder.KeyLabelFor(0x37));
    }

    [Fact]
    public void RecorderOutputCanBePersisted()
    {
        var (preferences, _) = CreatePreferences();
        var result = HotKeyRecorder.Capture(0x70, control: true, alt: true, shift: false, win: false);

        preferences.Save(result.Shortcut!.Value, GlobalHotKeyAction.TranslationCapture);
        Assert.Equal("Ctrl+Alt+F1", preferences.ShortcutFor(GlobalHotKeyAction.TranslationCapture).DisplayText);
    }

    private static (HotKeyPreferences Preferences, InMemorySettingsStore Store) CreatePreferences()
    {
        var store = new InMemorySettingsStore();
        return (new HotKeyPreferences(store), store);
    }
}
