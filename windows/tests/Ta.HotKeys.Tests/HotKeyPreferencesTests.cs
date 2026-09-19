using Ta.HotKeys;

namespace Ta.HotKeys.Tests;

/// <summary>
/// 快捷键持久化。
///
/// 用例对齐 macOS Tests/AIScreenshotAppTests/HotKeyPreferencesTests.swift
/// （1:1 对应项在注释里标出）。
/// </summary>
public sealed class HotKeyPreferencesTests
{
    private static (HotKeyPreferences Preferences, InMemorySettingsStore Store) CreatePreferences(
        bool assumeCarbonKeyCodes = false)
    {
        var store = new InMemorySettingsStore();
        return (new HotKeyPreferences(store, assumeCarbonKeyCodes), store);
    }

    // Mac: testDefaultsAreUniqueAndKeepExpectedDisplayText()
    [Fact]
    public void DefaultsAreUniqueAndKeepExpectedDisplayText()
    {
        var shortcuts = new Dictionary<GlobalHotKeyAction, HotKeyShortcut>();
        foreach (var action in GlobalHotKeyActions.All)
        {
            shortcuts[action] = action.DefaultShortcut();
        }

        var identities = new HashSet<string>(shortcuts.Values.Select(shortcut => shortcut.Identity));
        Assert.Equal(GlobalHotKeyActions.All.Count, identities.Count);

        Assert.Equal("Ctrl+Alt+Shift+1", shortcuts[GlobalHotKeyAction.IntelligentCapture].DisplayText);
        Assert.Equal("Ctrl+Alt+Shift+6", shortcuts[GlobalHotKeyAction.TranslationCapture].DisplayText);
        Assert.Equal("Ctrl+Alt+Shift+2", shortcuts[GlobalHotKeyAction.InteractiveCapture].DisplayText);
    }

    // Mac: 上一条测试里的 displayText 断言，此处按 Mac 符号串再锁一次 parity。
    [Fact]
    public void DefaultsMatchMacOSSymbolicText()
    {
        var intelligent = GlobalHotKeyAction.IntelligentCapture.DefaultShortcut();
        var translation = GlobalHotKeyAction.TranslationCapture.DefaultShortcut();

        // Mac 版断言的是 "⇧⌥⌘1" / "⇧⌥⌘6"（顺序 ⇧⌥⌘）。Windows 上 Ctrl 承担 ⌘ 位。
        Assert.Equal("⌃⇧⌥1", intelligent.MacParitySymbolicText);
        Assert.Equal("⌃⇧⌥6", translation.MacParitySymbolicText);
    }

    [Fact]
    public void ActionIdsAndStorageKeysMatchSpecification()
    {
        // 移植参考文档 §4.2 的 hotKeyID 映射 + §10.6 的持久化 key。
        Assert.Equal(1, GlobalHotKeyAction.InteractiveCapture.HotKeyId());
        Assert.Equal(2, GlobalHotKeyAction.IntelligentCapture.HotKeyId());
        Assert.Equal(3, GlobalHotKeyAction.ImageCapture.HotKeyId());
        Assert.Equal(4, GlobalHotKeyAction.PinCapture.HotKeyId());
        Assert.Equal(5, GlobalHotKeyAction.LongCapture.HotKeyId());
        Assert.Equal(6, GlobalHotKeyAction.TranslationCapture.HotKeyId());

        Assert.Equal("globalHotKey.interactiveCapture", GlobalHotKeyAction.InteractiveCapture.StorageKey());
        Assert.Equal("globalHotKey.intelligentCapture", GlobalHotKeyAction.IntelligentCapture.StorageKey());
        Assert.Equal("globalHotKey.translationCapture", GlobalHotKeyAction.TranslationCapture.StorageKey());

        Assert.Equal("通用截图", GlobalHotKeyAction.InteractiveCapture.DisplayName());
        Assert.Equal("极速识别内容", GlobalHotKeyAction.IntelligentCapture.DisplayName());
        Assert.Equal("截图图片", GlobalHotKeyAction.ImageCapture.DisplayName());
        Assert.Equal("截图并钉住", GlobalHotKeyAction.PinCapture.DisplayName());
        Assert.Equal("滚动长截图", GlobalHotKeyAction.LongCapture.DisplayName());
        Assert.Equal("截图翻译", GlobalHotKeyAction.TranslationCapture.DisplayName());
    }

    [Fact]
    public void DefaultKeysUseWindowsVirtualKeysNotCarbon()
    {
        // ⌘⌥⇧2 → Ctrl+Alt+Shift+VK_2(0x32)，绝不能用 kVK_ANSI_2(19)。
        var interactive = GlobalHotKeyAction.InteractiveCapture.DefaultShortcut();
        Assert.Equal(0x32, interactive.VirtualKey);
        Assert.NotEqual((ushort)19, interactive.VirtualKey);
        Assert.Equal("2", interactive.KeyLabel);
    }

    // Mac: testCustomShortcutPersistsAndResetRestoresDefault()
    [Fact]
    public void CustomShortcutPersistsAndResetRestoresDefault()
    {
        var (preferences, _) = CreatePreferences();
        var custom = new HotKeyShortcut(0x41, HotKeyModifiers.Control | HotKeyModifiers.Alt, "A");

        preferences.Save(custom, GlobalHotKeyAction.InteractiveCapture);
        Assert.Equal(custom, preferences.ShortcutFor(GlobalHotKeyAction.InteractiveCapture));

        preferences.ResetAll();
        Assert.Equal(
            GlobalHotKeyAction.InteractiveCapture.DefaultShortcut(),
            preferences.ShortcutFor(GlobalHotKeyAction.InteractiveCapture));
    }

    // Mac: testDuplicateShortcutIsRejectedWithoutOverwriting()
    [Fact]
    public void DuplicateShortcutIsRejectedWithoutOverwriting()
    {
        var (preferences, _) = CreatePreferences();
        var original = preferences.ShortcutFor(GlobalHotKeyAction.InteractiveCapture);

        // intelligentCapture 的默认键是 ⌘⌥⇧1；把它安到 interactiveCapture（默认 ⌘⌥⇧2）上 → 冲突。
        var exception = Assert.Throws<HotKeyDuplicateException>(() =>
            preferences.Save(
                GlobalHotKeyAction.IntelligentCapture.DefaultShortcut(),
                GlobalHotKeyAction.InteractiveCapture));

        Assert.Equal(GlobalHotKeyAction.IntelligentCapture, exception.ConflictingAction);
        Assert.Contains("极速识别内容", exception.Message);
        Assert.Equal(original, preferences.ShortcutFor(GlobalHotKeyAction.InteractiveCapture));
    }

    // Mac: 上面那条的「两个动作用同一组合」变体 —— 自定义组合同样会被拒。
    [Fact]
    public void TwoActionsCannotShareTheSameCombination()
    {
        var (preferences, _) = CreatePreferences();
        var shared = new HotKeyShortcut(0x70, HotKeyModifiers.Control | HotKeyModifiers.Shift, "F1");

        preferences.Save(shared, GlobalHotKeyAction.ImageCapture);
        Assert.Equal(shared, preferences.ShortcutFor(GlobalHotKeyAction.ImageCapture));

        var exception = Assert.Throws<HotKeyDuplicateException>(() =>
            preferences.Save(shared, GlobalHotKeyAction.PinCapture));
        Assert.Equal(GlobalHotKeyAction.ImageCapture, exception.ConflictingAction);

        // pinCapture 仍是自己的默认键。
        Assert.Equal(
            GlobalHotKeyAction.PinCapture.DefaultShortcut(),
            preferences.ShortcutFor(GlobalHotKeyAction.PinCapture));
    }

    [Fact]
    public void SavingTheSameCombinationForTheSameActionIsAllowed()
    {
        var (preferences, _) = CreatePreferences();
        var shortcut = new HotKeyShortcut(0x70, HotKeyModifiers.Control, "F1");

        preferences.Save(shortcut, GlobalHotKeyAction.LongCapture);
        preferences.Save(shortcut, GlobalHotKeyAction.LongCapture);

        Assert.Equal(shortcut, preferences.ShortcutFor(GlobalHotKeyAction.LongCapture));
    }

    // Mac: testSingleKeyShortcutPersistsWithoutModifier()
    [Fact]
    public void SingleKeyShortcutPersistsWithoutModifier()
    {
        var (preferences, _) = CreatePreferences();
        var singleKey = new HotKeyShortcut(0x41, HotKeyModifiers.None, "A");

        preferences.Save(singleKey, GlobalHotKeyAction.InteractiveCapture);

        Assert.Equal(singleKey, preferences.ShortcutFor(GlobalHotKeyAction.InteractiveCapture));
        Assert.Equal("A", preferences.ShortcutFor(GlobalHotKeyAction.InteractiveCapture).DisplayText);
    }

    [Fact]
    public void SaveRaisesDidChangeAndResetAllRaisesDidChange()
    {
        var (preferences, _) = CreatePreferences();
        var changeCount = 0;
        preferences.DidChange += (_, _) => changeCount++;

        preferences.Save(new HotKeyShortcut(0x41, HotKeyModifiers.None, "A"), GlobalHotKeyAction.PinCapture);
        Assert.Equal(1, changeCount);

        preferences.ResetAll();
        Assert.Equal(2, changeCount);
    }

    [Fact]
    public void ReplaceAllWithoutNotifyDoesNotRaiseDidChange()
    {
        var (preferences, store) = CreatePreferences();
        var changeCount = 0;
        preferences.DidChange += (_, _) => changeCount++;

        var replacement = new Dictionary<GlobalHotKeyAction, HotKeyShortcut>
        {
            [GlobalHotKeyAction.InteractiveCapture] = new HotKeyShortcut(0x73, HotKeyModifiers.Control, "F4"),
        };

        preferences.ReplaceAll(replacement, notify: false);
        Assert.Equal(0, changeCount);
        Assert.Equal(
            replacement[GlobalHotKeyAction.InteractiveCapture],
            preferences.ShortcutFor(GlobalHotKeyAction.InteractiveCapture));

        preferences.ReplaceAll(replacement, notify: true);
        Assert.Equal(1, changeCount);

        // 其余动作不受影响。
        Assert.Equal(
            GlobalHotKeyAction.IntelligentCapture.DefaultShortcut(),
            preferences.ShortcutFor(GlobalHotKeyAction.IntelligentCapture));
        Assert.NotNull(store);
    }

    [Fact]
    public void StorageIsWrittenUnderTheExpectedKey()
    {
        var store = new InMemorySettingsStore();
        var preferences = new HotKeyPreferences(store);
        var shortcut = new HotKeyShortcut(0x41, HotKeyModifiers.Control, "A");

        preferences.Save(shortcut, GlobalHotKeyAction.TranslationCapture);

        var raw = store.Read("globalHotKey.translationCapture");
        Assert.NotNull(raw);
        Assert.Contains("\"keyCode\":65", raw.Replace(" ", string.Empty));
        Assert.Contains("\"keyLabel\":\"A\"", raw.Replace(" ", string.Empty));
    }

    [Fact]
    public void CorruptedEntryFallsBackToDefault()
    {
        var store = new InMemorySettingsStore();
        store.Write("globalHotKey.longCapture", "{ 这不是 JSON");

        var preferences = new HotKeyPreferences(store);
        Assert.Equal(
            GlobalHotKeyAction.LongCapture.DefaultShortcut(),
            preferences.ShortcutFor(GlobalHotKeyAction.LongCapture));
    }

    [Fact]
    public void CarbonEncodedEntriesAreTranslatedOnRead()
    {
        // Mac 版写出的 JSON：keyCode 是 kVK_ANSI_2 = 19，modifiers 是 cmd|option|shift = 0x0B00。
        const string macJson = """{"keyCode":19,"modifiers":2816,"keyLabel":"2"}""";

        var parsed = HotKeyShortcutJson.TryParseCarbon(macJson);
        Assert.NotNull(parsed);

        Assert.Equal(0x32, parsed.Value.VirtualKey);
        Assert.Equal(
            HotKeyModifiers.Control | HotKeyModifiers.Alt | HotKeyModifiers.Shift,
            parsed.Value.Modifiers);
        Assert.Equal("Ctrl+Alt+Shift+2", parsed.Value.DisplayText);

        var store = new InMemorySettingsStore();
        store.Write("globalHotKey.interactiveCapture", macJson);
        var preferences = new HotKeyPreferences(store, assumeCarbonKeyCodes: true);
        Assert.Equal(0x32, preferences.ShortcutFor(GlobalHotKeyAction.InteractiveCapture).VirtualKey);
    }

    [Fact]
    public void CarbonEntryForUnmappableKeyFallsBackToDefault()
    {
        // kVK_F13 = 105：Windows 上无等价键，应丢弃并回落默认键。
        const string macJson = """{"keyCode":105,"modifiers":2816,"keyLabel":"F13"}""";

        var store = new InMemorySettingsStore();
        store.Write("globalHotKey.imageCapture", macJson);

        var preferences = new HotKeyPreferences(store, assumeCarbonKeyCodes: true);
        Assert.Equal(
            GlobalHotKeyAction.ImageCapture.DefaultShortcut(),
            preferences.ShortcutFor(GlobalHotKeyAction.ImageCapture));
    }

    [Fact]
    public void JsonFileStoreRoundTripsAcrossInstances()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ta-hotkeys-{Guid.NewGuid():N}.json");
        try
        {
            var shortcut = new HotKeyShortcut(0x70, HotKeyModifiers.Alt, "F1");
            var first = new HotKeyPreferences(new JsonFileSettingsStore(path));
            first.Save(shortcut, GlobalHotKeyAction.PinCapture);

            var second = new HotKeyPreferences(new JsonFileSettingsStore(path));
            Assert.Equal(shortcut, second.ShortcutFor(GlobalHotKeyAction.PinCapture));

            second.ResetAll();
            var third = new HotKeyPreferences(new JsonFileSettingsStore(path));
            Assert.Equal(
                GlobalHotKeyAction.PinCapture.DefaultShortcut(),
                third.ShortcutFor(GlobalHotKeyAction.PinCapture));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
