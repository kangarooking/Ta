using Ta.HotKeys;

namespace Ta.HotKeys.Tests;

/// <summary>
/// 冲突回滚。
///
/// 复现 macOS <c>GlobalHotKeyManager.reloadFromPreferences</c>
/// （GlobalHotKeyManager.swift:104-123）：注销全部 → 尝试注册 → 失败则注销、
/// 恢复 <c>lastSuccessfulShortcuts</c>、重新注册、发 <c>registrationFailedNotification</c>。
/// </summary>
public sealed class GlobalHotKeyManagerRollbackTests
{
    private const int ConflictErrorCode = 1409; // ERROR_HOTKEY_ALREADY_REGISTERED

    private static (GlobalHotKeyManager Manager, HotKeyPreferences Preferences, ScriptedHotKeyRegistrar Registrar)
        CreateManager()
    {
        var store = new InMemorySettingsStore();
        var preferences = new HotKeyPreferences(store);
        var registrar = new ScriptedHotKeyRegistrar();
        var manager = new GlobalHotKeyManager(preferences, registrar);
        return (manager, preferences, registrar);
    }

    [Fact]
    public void FirstRegistrationPassesModifierWordWithNoRepeat()
    {
        var (manager, _, registrar) = CreateManager();
        manager.Register(_ => { });

        // 六个动作按 hotKeyID 升序注册。
        Assert.Equal(6, registrar.RegisterCalls.Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, registrar.RegisterCalls.Select(call => call.HotKeyId));

        // intelligentCapture = hotKeyID 2 → VK_1，修饰键含 MOD_NOREPEAT。
        var intelligent = registrar.RegisterCalls.Single(call => call.HotKeyId == 2);
        Assert.Equal(0x31, intelligent.VirtualKey);
        Assert.Equal(0x4007u, intelligent.ModifierWord); // MOD_CONTROL|MOD_ALT|MOD_SHIFT|MOD_NOREPEAT

        // interactiveCapture = hotKeyID 1 → VK_2。
        var interactive = registrar.RegisterCalls.Single(call => call.HotKeyId == 1);
        Assert.Equal(0x32, interactive.VirtualKey);

        Assert.Equal(6, registrar.RegisteredIds.Count);
        Assert.Equal(GlobalHotKeyActions.All.Count, manager.ActiveShortcuts.Count);
        Assert.Equal("Ctrl+Alt+Shift+2", manager.ActiveShortcuts[GlobalHotKeyAction.InteractiveCapture].DisplayText);
    }

    [Fact]
    public void SecondRegistrationFailureRollsBackToPreviousGroupAndRaisesEvent()
    {
        var (manager, preferences, registrar) = CreateManager();

        var handled = new List<GlobalHotKeyAction>();
        manager.Register(action => handled.Add(action));

        var previous = manager.ActiveShortcuts;

        var failures = new List<string>();
        manager.RegistrationFailed += (_, args) => failures.Add(args.Message);

        // 用户把 interactiveCapture 改成一个合法的新组合（Ctrl+F4）并保存。
        // Save → DidChange → ReloadFromPreferences：
        //   第 1 个动作（interactiveCapture）注册成功，
        //   第 2 个（intelligentCapture，⌘⌥⇧1）被别的程序占用 → 抛错 → 回滚。
        registrar.PlannedFailures[8] = new HotKeyRegistrationException(
            GlobalHotKeyAction.IntelligentCapture, ConflictErrorCode, isConflict: true);

        preferences.Save(
            new HotKeyShortcut(0x73, HotKeyModifiers.Control, "F4"),
            GlobalHotKeyAction.InteractiveCapture);

        // ① 发出了失败事件，文案带动作显示名与错误码。
        Assert.Single(failures);
        Assert.Contains("极速识别内容", failures[0]);
        Assert.Contains(ConflictErrorCode.ToString(), failures[0]);

        // ② 生效组合仍是上一组成功配置（未被半途而废的新配置污染）。
        Assert.Equal(previous[GlobalHotKeyAction.InteractiveCapture], manager.ActiveShortcuts[GlobalHotKeyAction.InteractiveCapture]);
        Assert.Equal(previous[GlobalHotKeyAction.IntelligentCapture], manager.ActiveShortcuts[GlobalHotKeyAction.IntelligentCapture]);

        // ③ 存储里的值也回滚了（设置界面重新打开时看到的仍是生效值）。
        Assert.Equal(previous[GlobalHotKeyAction.InteractiveCapture], preferences.ShortcutFor(GlobalHotKeyAction.InteractiveCapture));

        // ④ 六个热键重新注册成功：首批 6 + 重载 2（含失败的）+ 恢复 6。
        Assert.Equal(14, registrar.RegisterCalls.Count);
        Assert.Equal(6, registrar.RegisteredIds.Count);
    }

    [Fact]
    public void RollbackKeepsManagerUnregisteredWhenPreviousGroupAlsoFails()
    {
        var (manager, _, registrar) = CreateManager();
        manager.Register(_ => { });

        var failures = new List<string>();
        manager.RegistrationFailed += (_, args) => failures.Add(args.Message);

        // 让重载与随后的恢复注册都失败。
        registrar.PlannedFailures[7] = new HotKeyRegistrationException(
            GlobalHotKeyAction.InteractiveCapture, ConflictErrorCode, isConflict: true);
        registrar.PlannedFailures[8] = new HotKeyRegistrationException(
            GlobalHotKeyAction.InteractiveCapture, ConflictErrorCode, isConflict: true);
        registrar.PlannedFailures[9] = new HotKeyRegistrationException(
            GlobalHotKeyAction.InteractiveCapture, ConflictErrorCode, isConflict: true);

        manager.ReloadFromPreferences();

        Assert.Single(failures);
        Assert.Empty(registrar.RegisteredIds);
        // 仍记住上一组配置，供下次重载恢复。
        Assert.Equal(GlobalHotKeyActions.All.Count, manager.ActiveShortcuts.Count);
    }

    [Fact]
    public void SuccessfulReloadReplacesActiveShortcuts()
    {
        var (manager, preferences, registrar) = CreateManager();
        manager.Register(_ => { });

        var requested = new Dictionary<GlobalHotKeyAction, HotKeyShortcut>
        {
            [GlobalHotKeyAction.InteractiveCapture] = new HotKeyShortcut(0x74, HotKeyModifiers.Control, "F5"),
        };
        preferences.ReplaceAll(requested, notify: false);

        manager.ReloadFromPreferences();

        Assert.Equal(12, registrar.RegisterCalls.Count);
        Assert.Equal("Ctrl+F5", manager.ActiveShortcuts[GlobalHotKeyAction.InteractiveCapture].DisplayText);
        // 未指定的动作沿用默认键。
        Assert.Equal("Ctrl+Alt+Shift+1", manager.ActiveShortcuts[GlobalHotKeyAction.IntelligentCapture].DisplayText);
    }

    [Fact]
    public void PreferencesRegistrationFailedEventAlsoFires()
    {
        var (manager, preferences, registrar) = CreateManager();
        manager.Register(_ => { });

        var failures = new List<string>();
        preferences.RegistrationFailed += (_, message) => failures.Add(message);

        registrar.PlannedFailures[7] = new HotKeyRegistrationException(
            GlobalHotKeyAction.InteractiveCapture, ConflictErrorCode, isConflict: true);

        manager.ReloadFromPreferences();

        Assert.Single(failures);
    }

    [Fact]
    public void InvokeDispatchesToMatchingActionOnly()
    {
        var (manager, _, _) = CreateManager();
        var handled = new List<GlobalHotKeyAction>();
        manager.Register(handled.Add);

        manager.Invoke(2);
        manager.Invoke(6);
        manager.Invoke(99); // 未注册的 id 静默忽略

        Assert.Equal(new[] { GlobalHotKeyAction.IntelligentCapture, GlobalHotKeyAction.TranslationCapture }, handled);
    }

    [Fact]
    public void DisposeUnregistersEverything()
    {
        var (manager, _, registrar) = CreateManager();
        manager.Register(_ => { });

        manager.Dispose();

        Assert.Empty(registrar.RegisteredIds);
    }

    [Fact]
    public void NonConflictRegistrationErrorStillTriggersRollback()
    {
        var (manager, _, registrar) = CreateManager();
        manager.Register(_ => { });

        var failures = new List<string>();
        manager.RegistrationFailed += (_, args) => failures.Add(args.Message);

        // ERROR_INVALID_PARAMETER(87) 之类的非冲突错误同样要走回滚路径。
        registrar.PlannedFailures[7] = new HotKeyRegistrationException(
            GlobalHotKeyAction.InteractiveCapture, 87, isConflict: false);

        manager.ReloadFromPreferences();

        Assert.Single(failures);
        Assert.Contains("注册失败", failures[0]);
        Assert.Equal(6, registrar.RegisteredIds.Count);
    }
}
