using System.Runtime.InteropServices;

namespace Ta.HotKeys;

/// <summary>注册失败异常（冲突或其他系统错误）。</summary>
/// <remarks>
/// 对应 macOS <c>GlobalHotKeyError.registration(action:status:)</c>
/// （GlobalHotKeyManager.swift:6、:12-14）。
/// 文案在 Mac 版「“&lt;显示名&gt;”快捷键已被系统或其他应用占用（&lt;错误码&gt;）。」的基础上
/// 补上了具体组合 —— 用户改键时需要知道**哪个组合**被占了。
/// </remarks>
public sealed class HotKeyRegistrationException : Exception
{
    public HotKeyRegistrationException(
        GlobalHotKeyAction action,
        int errorCode,
        bool isConflict,
        HotKeyShortcut? shortcut = null)
        : base(BuildMessage(action, errorCode, isConflict, shortcut))
    {
        Action = action;
        ErrorCode = errorCode;
        IsConflict = isConflict;
        Shortcut = shortcut;
    }

    /// <summary>注册失败的动作。</summary>
    public GlobalHotKeyAction Action { get; }

    /// <summary>Win32 错误码（<c>GetLastError()</c>，1409 即 <c>ERROR_HOTKEY_ALREADY_REGISTERED</c>）。</summary>
    public int ErrorCode { get; }

    /// <summary>是否为 <c>ERROR_HOTKEY_ALREADY_REGISTERED</c> 冲突。</summary>
    public bool IsConflict { get; }

    /// <summary>被占用的组合（若调用方提供）。</summary>
    public HotKeyShortcut? Shortcut { get; }

    private static string BuildMessage(
        GlobalHotKeyAction action, int errorCode, bool isConflict, HotKeyShortcut? shortcut)
    {
        var combo = shortcut is null ? string.Empty : $" {shortcut.Value.DisplayText}";
        return isConflict
            ? $"“{action.DisplayName()}”快捷键{combo}已被系统或其他应用占用（{errorCode}）。"
            : $"“{action.DisplayName()}”快捷键{combo}注册失败（Win32 错误 {errorCode}）。";
    }
}

/// <summary>
/// 热键注册后端。抽出来是为了让「冲突回滚」这条关键行为可以在没有真实 Win32 的情况下被单测。
/// </summary>
public interface IHotKeyRegistrar
{
    /// <summary>注册一个热键；失败抛 <see cref="HotKeyRegistrationException"/>。</summary>
    void Register(int hotKeyId, HotKeyShortcut shortcut);

    /// <summary>
    /// 注销此前注册的全部热键（不存在的 id 静默忽略）。
    /// 对应 Mac 版 <c>unregisterHotKeys()</c>（GlobalHotKeyManager.swift:149-152）。
    /// </summary>
    void UnregisterAll();
}

/// <summary>
/// 基于 <c>RegisterHotKey</c> 的真实实现。
///
/// ⚠️ <c>RegisterHotKey</c> 属于「注册线程」：<c>WM_HOTKEY</c> 会投递到调用本方法的
/// 线程所拥有的窗口的消息队列，因此 <see cref="Register"/> 必须在将要运行消息循环的
/// 线程上被调用（正是 <see cref="HotKeyMessageHost"/> 的宿主线程）。
/// </summary>
public sealed class Win32HotKeyRegistrar : IHotKeyRegistrar
{
    private readonly IntPtr _hostWindow;
    private readonly List<int> _registeredIds = new();

    public Win32HotKeyRegistrar(IntPtr hostWindow)
    {
        if (hostWindow == IntPtr.Zero)
        {
            throw new ArgumentException("宿主窗口句柄不能为空。", nameof(hostWindow));
        }

        _hostWindow = hostWindow;
    }

    /// <summary>当前已注册的 id 列表（按注册顺序），供测试与诊断。</summary>
    public IReadOnlyList<int> RegisteredIds
    {
        get
        {
            lock (_registeredIds) return _registeredIds.ToArray();
        }
    }

    public void Register(int hotKeyId, HotKeyShortcut shortcut)
    {
        var modifierWord = shortcut.ToModifierWord();
        if (!HotKeyInterop.RegisterHotKey(_hostWindow, hotKeyId, modifierWord, shortcut.VirtualKey))
        {
            // ⚠️ 必须用 Marshal.GetLastWin32Error()，不能自己再 P/Invoke 一个 GetLastError()：
            // 第二次 P/Invoke 期间 CLR 内部会调别的 Win32 API，把线程的 last error 冲掉
            // （实测返回 0，冲突会被误判成「Win32 错误 0」）。
            var error = Marshal.GetLastWin32Error();
            throw new HotKeyRegistrationException(
                ResolveAction(hotKeyId),
                error,
                HotKeyConstants.IsConflict(error),
                shortcut);
        }

        lock (_registeredIds)
        {
            if (!_registeredIds.Contains(hotKeyId))
            {
                _registeredIds.Add(hotKeyId);
            }
        }
    }

    public void UnregisterAll()
    {
        List<int> ids;
        lock (_registeredIds)
        {
            ids = _registeredIds.ToList();
            _registeredIds.Clear();
        }

        foreach (var id in ids)
        {
            HotKeyInterop.UnregisterHotKey(_hostWindow, id);
        }
    }

    private static GlobalHotKeyAction ResolveAction(int hotKeyId)
    {
        foreach (var action in GlobalHotKeyActions.All)
        {
            if (action.HotKeyId() == hotKeyId)
            {
                return action;
            }
        }

        // id 不在 1..6 内：返回一个占位动作，仅用于错误文案。
        return GlobalHotKeyAction.InteractiveCapture;
    }
}

/// <summary>
/// 可编排的假注册器：按脚本决定第几次注册失败。
/// 只用于「冲突回滚」这类不依赖真实系统的单测。
/// </summary>
public sealed class ScriptedHotKeyRegistrar : IHotKeyRegistrar
{
    private readonly List<int> _registeredIds = new();

    /// <summary>失败计划：key = 第几次 <see cref="Register"/> 调用（从 1 开始）。</summary>
    public Dictionary<int, HotKeyRegistrationException> PlannedFailures { get; } = new();

    /// <summary>全部注册尝试的记录，用于断言顺序与内容。</summary>
    public List<(int HotKeyId, uint ModifierWord, ushort VirtualKey)> RegisterCalls { get; } = new();

    public void Register(int hotKeyId, HotKeyShortcut shortcut)
    {
        var attempt = RegisterCalls.Count + 1;
        RegisterCalls.Add((hotKeyId, shortcut.ToModifierWord(), shortcut.VirtualKey));

        if (PlannedFailures.TryGetValue(attempt, out var failure))
        {
            throw failure;
        }

        if (!_registeredIds.Contains(hotKeyId))
        {
            _registeredIds.Add(hotKeyId);
        }
    }

    public void UnregisterAll() => _registeredIds.Clear();

    /// <summary>当前仍处于注册态的 id。</summary>
    public IReadOnlyList<int> RegisteredIds => _registeredIds.ToArray();
}
