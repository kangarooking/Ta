using System.Diagnostics;
using System.Runtime.InteropServices;
using Ta.HotKeys;
using Xunit.Abstractions;

namespace Ta.HotKeys.Tests;

/// <summary>
/// 真机验证：真的调用 <c>RegisterHotKey</c>，真的合成一次按键，
/// 断言回调被 <c>WM_HOTKEY</c> 触发。
///
/// 只有整条链路都通才会通过：
/// 默认键 / 翻译表 → 修饰键字（含 MOD_NOREPEAT）→ RegisterHotKey →
/// 消息专属窗口 → 消息泵 → manager.Invoke(id) → 回调。
///
/// ⚠️ 两条 Win32 语义（都在本文件的探测里实测过，改代码前先读）：
/// 1. 全局热键是**系统级**资源，别的进程占用就注册不上（1409）。
///    本机跑着另一个拓 Ta 的 Windows 实例时会占掉部分默认键，
///    所以用例从候选池里挑**当前空闲**的组合来跑，不会假装通过。
/// 2. **同一线程里，同一个 (修饰键, VK) 只能挂一个 id**。换 id 重注册同一组合
///    会返回 1409，必须先 <c>UnregisterHotKey</c>。
///    这也是为什么不能「先用假 id 探一次再删」——必须注销干净。
/// </summary>
public sealed class LiveRegisterHotKeyTests
{
    private readonly ITestOutputHelper _output;

    public LiveRegisterHotKeyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>规格要求的默认组合排在最前，其余只是退路。</summary>
    private static readonly (HotKeyModifiers Modifiers, ushort VirtualKey, string Label)[] DefaultCombos =
    {
        (HotKeyModifiers.Control | HotKeyModifiers.Alt | HotKeyModifiers.Shift, 0x31, "1"),
        (HotKeyModifiers.Control | HotKeyModifiers.Alt | HotKeyModifiers.Shift, 0x32, "2"),
        (HotKeyModifiers.Control | HotKeyModifiers.Alt | HotKeyModifiers.Shift, 0x33, "3"),
        (HotKeyModifiers.Control | HotKeyModifiers.Alt | HotKeyModifiers.Shift, 0x34, "4"),
        (HotKeyModifiers.Control | HotKeyModifiers.Alt | HotKeyModifiers.Shift, 0x35, "5"),
        (HotKeyModifiers.Control | HotKeyModifiers.Alt | HotKeyModifiers.Shift, 0x36, "6"),
    };

    /// <summary>退路候选池：F 键区几乎没人抢。</summary>
    private static readonly (HotKeyModifiers Modifiers, ushort VirtualKey, string Label)[] FallbackCombos =
    {
        (HotKeyModifiers.Control | HotKeyModifiers.Alt | HotKeyModifiers.Shift, 0x70, "F1"),
        (HotKeyModifiers.Control | HotKeyModifiers.Alt | HotKeyModifiers.Shift, 0x71, "F2"),
        (HotKeyModifiers.Control | HotKeyModifiers.Alt | HotKeyModifiers.Shift, 0x72, "F3"),
        (HotKeyModifiers.Control | HotKeyModifiers.Alt | HotKeyModifiers.Shift, 0x73, "F4"),
        (HotKeyModifiers.Control | HotKeyModifiers.Alt | HotKeyModifiers.Shift, 0x74, "F5"),
        (HotKeyModifiers.Control | HotKeyModifiers.Alt | HotKeyModifiers.Shift, 0x75, "F6"),
        (HotKeyModifiers.Control | HotKeyModifiers.Alt | HotKeyModifiers.Shift, 0x76, "F7"),
        (HotKeyModifiers.Control | HotKeyModifiers.Alt | HotKeyModifiers.Shift, 0x77, "F8"),
        (HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x78, "F9"),
        (HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x79, "F10"),
        (HotKeyModifiers.Control | HotKeyModifiers.Alt, 0x7A, "F11"),
        (HotKeyModifiers.Control | HotKeyModifiers.Alt, 0x7B, "F12"),
    };

    private static HotKeyShortcut ToShortcut(
        (HotKeyModifiers Modifiers, ushort VirtualKey, string Label) combo) =>
        new(combo.VirtualKey, combo.Modifiers, combo.Label);

    /// <summary>
    /// 探出 <paramref name="count"/> 个当前空闲的组合。
    /// 先按规格默认键挑（Ctrl+Alt+Shift+1 能抢到就用它），不够再用退路池补齐。
    /// 探测用一次性注册器，成功立即注销。
    /// </summary>
    private static List<HotKeyShortcut> FindFreeCombos(IntPtr hostWindow, int count)
    {
        var probe = new Win32HotKeyRegistrar(hostWindow);
        var free = new List<HotKeyShortcut>();

        foreach (var combo in DefaultCombos.Concat(FallbackCombos))
        {
            var shortcut = ToShortcut(combo);
            try
            {
                probe.Register(9100, shortcut);
            }
            catch (HotKeyRegistrationException)
            {
                continue;
            }

            probe.UnregisterAll();
            free.Add(shortcut);
            if (free.Count == count)
            {
                break;
            }
        }

        return free;
    }

    private static void ReplaceAllSix(
        HotKeyPreferences preferences, IReadOnlyList<HotKeyShortcut> combos)
    {
        var map = new Dictionary<GlobalHotKeyAction, HotKeyShortcut>();
        for (var index = 0; index < GlobalHotKeyActions.All.Count && index < combos.Count; index++)
        {
            map[GlobalHotKeyActions.All[index]] = combos[index];
        }

        preferences.ReplaceAll(map, notify: false);
    }

    [Fact]
    [Trait("Category", "Live")]
    public void PressingTheHotKeyInvokesTheCallback()
    {
        Assert.True(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "真机热键用例需要 Windows。");

        using var host = new HotKeyMessageHost();
        Assert.True(host.IsAlive, "消息宿主窗口创建失败。");
        Assert.NotEqual(IntPtr.Zero, host.WindowHandle);

        var combos = FindFreeCombos(host.WindowHandle, GlobalHotKeyActions.All.Count);
        Assert.True(
            combos.Count == GlobalHotKeyActions.All.Count,
            $"只找到 {combos.Count} 个空闲组合，无法整组注册。" +
            $"（已探测：{string.Join("，", combos.Select(c => c.DisplayText))}）");

        var preferences = new HotKeyPreferences(new InMemorySettingsStore());
        ReplaceAllSix(preferences, combos);

        _output.WriteLine($"[真机] 消息宿主 hwnd=0x{host.WindowHandle.ToInt64():X}，线程 {host.HostThreadId}");
        for (var index = 0; index < GlobalHotKeyActions.All.Count; index++)
        {
            var action = GlobalHotKeyActions.All[index];
            _output.WriteLine(
                $"[真机] hotKeyID={action.HotKeyId()} {action.DisplayName(),-8} → " +
                $"{combos[index].DisplayText} (VK=0x{combos[index].VirtualKey:X2}, fsModifiers=0x{combos[index].ToModifierWord():X4})");
        }

        var invocations = new List<(GlobalHotKeyAction Action, long Milliseconds)>();
        var failures = new List<string>();
        var stopwatch = Stopwatch.StartNew();

        var registrar = new Win32HotKeyRegistrar(host.WindowHandle);
        using var manager = new GlobalHotKeyManager(preferences, registrar);
        manager.RegistrationFailed += (_, args) => failures.Add(args.Message);
        host.HotKeyPressed += (_, id) => manager.Invoke(id);

        // 整组注册（manager.Register 与 Mac 版 registerDefaults 一致：一次注册六个）。
        manager.Register(action => invocations.Add((action, stopwatch.ElapsedMilliseconds)));
        Assert.Empty(failures);
        Assert.Equal(GlobalHotKeyActions.All.Count, registrar.RegisteredIds.Count);
        _output.WriteLine($"[真机] RegisterHotKey 全部成功，已注册 id：{string.Join(",", registrar.RegisteredIds)}");

        // intelligentCapture 是 All 里的第 2 个，对应 combos[1]。
        var intelligentSlot = combos[1];
        Assert.Equal("极速识别内容", GlobalHotKeyActions.All[1].DisplayName());

        // ① 真按下这个组合。
        _output.WriteLine($"[真机] 合成按键 {intelligentSlot.DisplayText} …");
        SendKeyCombo(intelligentSlot.Modifiers, intelligentSlot.VirtualKey);
        var triggered = WaitForHotKey(host, TimeSpan.FromSeconds(5));

        Assert.True(
            triggered,
            $"按下 {intelligentSlot.DisplayText} 后 5 秒内没有收到 WM_HOTKEY，注册或消息泵链路有问题。");

        Assert.Empty(failures);
        var fired = Assert.Single(invocations);
        Assert.Equal(GlobalHotKeyAction.IntelligentCapture, fired.Action);
        Assert.True(fired.Milliseconds >= 0);
        _output.WriteLine(
            $"[真机] 收到 WM_HOTKEY → Invoke({GlobalHotKeyAction.IntelligentCapture.HotKeyId()}) → " +
            $"{fired.Action} 回调触发（{fired.Milliseconds} ms）");

        // ② 另一个已注册的组合也要触发（顺带证明 id 派发没串）。
        _output.WriteLine($"[真机] 合成按键 {combos[0].DisplayText} …");
        SendKeyCombo(combos[0].Modifiers, combos[0].VirtualKey);
        Assert.True(WaitForHotKey(host, TimeSpan.FromSeconds(5)), $"按下 {combos[0].DisplayText} 未触发。");
        Assert.Equal(2, invocations.Count);
        Assert.Equal(GlobalHotKeyAction.InteractiveCapture, invocations[1].Action);
        _output.WriteLine(
            $"[真机] 收到 WM_HOTKEY → Invoke({GlobalHotKeyAction.InteractiveCapture.HotKeyId()}) → " +
            $"{invocations[1].Action} 回调触发（{invocations[1].Milliseconds} ms）");

        // ③ 未注册的组合不应触发。
        invocations.Clear();
        SendKeyCombo(HotKeyModifiers.Control | HotKeyModifiers.Alt | HotKeyModifiers.Shift, 0x39);
        Assert.False(
            WaitForHotKey(host, TimeSpan.FromMilliseconds(700)),
            "未注册的组合不应触发回调。");
        Assert.Empty(invocations);
        _output.WriteLine("[真机] 未注册的 Ctrl+Alt+Shift+9 未触发回调，符合预期");
    }

    [Fact]
    [Trait("Category", "Live")]
    public void DefaultIntelligentCaptureBindingIsCtrlAltShiftVk1()
    {
        Assert.True(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "真机热键用例需要 Windows。");

        // 规格锁：intelligentCapture = ⌘⌥⇧1 → Ctrl+Alt+Shift+VK_1(0x31) + MOD_NOREPEAT。
        var intelligent = GlobalHotKeyAction.IntelligentCapture.DefaultShortcut();
        Assert.Equal(0x31, intelligent.VirtualKey);
        Assert.Equal(
            HotKeyModifiers.Control | HotKeyModifiers.Alt | HotKeyModifiers.Shift,
            intelligent.Modifiers);
        Assert.Equal("Ctrl+Alt+Shift+1", intelligent.DisplayText);
        Assert.Equal(0x4007u, intelligent.ToModifierWord());

        using var host = new HotKeyMessageHost();
        var registrar = new Win32HotKeyRegistrar(host.WindowHandle);

        try
        {
            // 用 interactiveCapture 的 id 挂 intelligentCapture 的组合，只为真调一次 RegisterHotKey。
            registrar.Register(GlobalHotKeyAction.InteractiveCapture.HotKeyId(), intelligent);
            Assert.Contains(GlobalHotKeyAction.InteractiveCapture.HotKeyId(), registrar.RegisteredIds);
        }
        catch (HotKeyRegistrationException conflict)
        {
            // 组合正被别的进程占用：冲突码必须是 1409，且报的正是 Ctrl+Alt+Shift+1。
            Assert.True(conflict.IsConflict, $"错误码 {conflict.ErrorCode} 不是冲突码 1409。");
            Assert.Equal(1409, conflict.ErrorCode);
            Assert.Contains("Ctrl+Alt+Shift+1", conflict.Message);
        }
        finally
        {
            registrar.UnregisterAll();
        }
    }

    [Fact]
    [Trait("Category", "Live")]
    public void OccupiedCombinationIsReportedAsConflict()
    {
        Assert.True(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "真机热键用例需要 Windows。");

        using var host = new HotKeyMessageHost();
        var combos = FindFreeCombos(host.WindowHandle, GlobalHotKeyActions.All.Count);
        Assert.True(
            combos.Count == GlobalHotKeyActions.All.Count,
            $"只找到 {combos.Count} 个空闲组合，无法复现冲突。");

        var preferences = new HotKeyPreferences(new InMemorySettingsStore());
        ReplaceAllSix(preferences, combos);

        // 占掉第 3 个动作（imageCapture）的组合，模拟「被别的程序占用」。
        var victimIndex = 2;
        var victimAction = GlobalHotKeyActions.All[victimIndex];
        var squatter = new Win32HotKeyRegistrar(host.WindowHandle);
        squatter.Register(9001, combos[victimIndex]);

        HotKeyRegistrationException? conflict = null;
        var manager = new GlobalHotKeyManager(preferences, new Win32HotKeyRegistrar(host.WindowHandle));
        try
        {
            manager.Register(_ => { });
        }
        catch (HotKeyRegistrationException raised)
        {
            conflict = raised;
        }
        finally
        {
            squatter.UnregisterAll();
            manager.Dispose();
        }

        Assert.NotNull(conflict);
        Assert.True(conflict.IsConflict, $"错误码 {conflict.ErrorCode} 不是冲突码 1409。");
        Assert.Equal(1409, conflict.ErrorCode);
        Assert.Equal(victimAction, conflict.Action);
        Assert.Contains(combos[victimIndex].DisplayText, conflict.Message);
        Assert.Contains(victimAction.DisplayName(), conflict.Message);
        Assert.Contains("已被系统或其他应用占用", conflict.Message);
    }

    private static bool WaitForHotKey(HotKeyMessageHost host, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            if (host.PumpPending(100))
            {
                return true;
            }
        }

        return false;
    }

    private static void SendKeyCombo(HotKeyModifiers modifiers, ushort key)
    {
        if (modifiers.HasFlag(HotKeyModifiers.Control)) Key(0x11, down: true);
        if (modifiers.HasFlag(HotKeyModifiers.Alt)) Key(0x12, down: true);
        if (modifiers.HasFlag(HotKeyModifiers.Shift)) Key(0x10, down: true);
        if (modifiers.HasFlag(HotKeyModifiers.Win)) Key(0x5B, down: true);
        Key((byte)key, down: true);
        Key((byte)key, down: false);
        if (modifiers.HasFlag(HotKeyModifiers.Win)) Key(0x5B, down: false);
        if (modifiers.HasFlag(HotKeyModifiers.Shift)) Key(0x10, down: false);
        if (modifiers.HasFlag(HotKeyModifiers.Alt)) Key(0x12, down: false);
        if (modifiers.HasFlag(HotKeyModifiers.Control)) Key(0x11, down: false);
    }

    [DllImport("user32.dll", EntryPoint = "keybd_event")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

    private static void Key(byte virtualKey, bool down)
    {
        keybd_event(virtualKey, 0, down ? 0u : 0x0002u, IntPtr.Zero);
        Thread.Sleep(15);
    }
}
