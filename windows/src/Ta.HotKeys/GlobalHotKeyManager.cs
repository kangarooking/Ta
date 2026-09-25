namespace Ta.HotKeys;

/// <summary>注册失败并已回滚的事件参数。</summary>
public sealed class HotKeyRegistrationFailedEventArgs : EventArgs
{
    public HotKeyRegistrationFailedEventArgs(string message)
    {
        Message = message;
    }

    /// <summary>给用户看的提示文案，对应 Mac 版 <c>errorMessageUserInfoKey</c> 的内容。</summary>
    public string Message { get; }
}

/// <summary>
/// 全局快捷键管理器：注册、派发、冲突回滚。
///
/// 1:1 移植 macOS <c>GlobalHotKeyManager</c>（GlobalHotKeyManager.swift:20-153）。
///
/// 关键行为 <see cref="ReloadFromPreferences"/> 必须与 Mac 版逐行对应：
/// <code>
/// let requested = preferences.allShortcuts()
/// unregisterHotKeys()
/// do { try register(requested); lastSuccessfulShortcuts = requested }
/// catch {
///     unregisterHotKeys()
///     if !lastSuccessfulShortcuts.isEmpty {
///         try? preferences.replaceAll(lastSuccessfulShortcuts, notify: false)
///         try? register(lastSuccessfulShortcuts)
///     }
///     post registrationFailedNotification(message)
/// }
/// </code>
/// 行为锁定于 Tests/AIScreenshotAppTests/HotKeyPreferencesTests.swift。
/// </summary>
public sealed class GlobalHotKeyManager : IDisposable
{
    private readonly HotKeyPreferences _preferences;
    private readonly IHotKeyRegistrar _registrar;
    private readonly Dictionary<int, Action> _actions = new();
    private readonly object _gate = new();
    private IReadOnlyDictionary<GlobalHotKeyAction, HotKeyShortcut> _lastSuccessful = new Dictionary<GlobalHotKeyAction, HotKeyShortcut>();
    private bool _disposed;

    /// <param name="preferences">持久化来源。</param>
    /// <param name="registrar">
    /// 注册后端。传 null 表示「尚无宿主窗口」——此时 <see cref="Register"/> 会抛
    /// <see cref="InvalidOperationException"/>；真实场景应先建好
    /// <see cref="HotKeyMessageHost"/> 再包成 <see cref="Win32HotKeyRegistrar"/>。
    /// </param>
    public GlobalHotKeyManager(HotKeyPreferences preferences, IHotKeyRegistrar? registrar = null)
    {
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _registrar = registrar ?? new DeferredHotKeyRegistrar();
    }

    /// <summary>注册失败且已回滚。对应 Mac 版 <c>registrationFailedNotification</c>（:100）。</summary>
    public event EventHandler<HotKeyRegistrationFailedEventArgs>? RegistrationFailed;

    /// <summary>当前生效的快捷键组合（即 <c>lastSuccessfulShortcuts</c>）。</summary>
    public IReadOnlyDictionary<GlobalHotKeyAction, HotKeyShortcut> ActiveShortcuts
    {
        get
        {
            lock (_gate) return _lastSuccessful;
        }
    }

    /// <summary>
    /// 绑定动作回调并首次注册。对应 Mac 版 <c>registerDefaults(...)</c>（:29-51）。
    /// </summary>
    public void Register(Action<GlobalHotKeyAction> handler)
    {
        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        lock (_gate)
        {
            _actions.Clear();
            foreach (var action in GlobalHotKeyActions.All)
            {
                _actions[action.HotKeyId()] = () => handler(action);
            }
        }

        _preferences.DidChange -= OnPreferencesDidChange;
        _preferences.DidChange += OnPreferencesDidChange;

        var shortcuts = _preferences.AllShortcuts();
        RegisterShortcuts(shortcuts);
        lock (_gate) _lastSuccessful = shortcuts;
    }

    /// <summary>
    /// 处理一次 <c>WM_HOTKEY</c>。对应 Mac 版 <c>invoke(id:)</c>（:145-147）。
    /// </summary>
    public void Invoke(int hotKeyId)
    {
        Action? action;
        lock (_gate)
        {
            _actions.TryGetValue(hotKeyId, out action);
        }

        action?.Invoke();
    }

    /// <summary>
    /// 偏好变更后的重载 + 冲突回滚。对应 Mac 版 <c>reloadFromPreferences()</c>（:104-123）。
    /// </summary>
    public void ReloadFromPreferences()
    {
        var requested = _preferences.AllShortcuts();
        _registrar.UnregisterAll();

        try
        {
            RegisterShortcuts(requested);
            lock (_gate) _lastSuccessful = requested;
        }
        catch (HotKeyRegistrationException error)
        {
            // ── 回滚：注销 → 恢复上一组成功配置 → 重新注册 → 通知失败 ──
            _registrar.UnregisterAll();

            IReadOnlyDictionary<GlobalHotKeyAction, HotKeyShortcut> previous;
            lock (_gate) previous = _lastSuccessful;

            if (previous.Count > 0)
            {
                _preferences.ReplaceAll(previous, notify: false);
                try
                {
                    RegisterShortcuts(previous);
                }
                catch (HotKeyRegistrationException)
                {
                    // 恢复注册也失败：保持未注册状态，不向 UI 抛异常
                    // （对应 Mac 版的 `try? register(lastSuccessfulShortcuts)`）。
                }
            }

            RaiseRegistrationFailed(error.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _preferences.DidChange -= OnPreferencesDidChange;
        _registrar.UnregisterAll();
        GC.SuppressFinalize(this);
    }

    private void RegisterShortcuts(IReadOnlyDictionary<GlobalHotKeyAction, HotKeyShortcut> shortcuts)
    {
        foreach (var action in GlobalHotKeyActions.All)
        {
            if (!shortcuts.TryGetValue(action, out var shortcut))
            {
                // Mac 版 `guard let shortcut = shortcuts[action] else { continue }`。
                continue;
            }

            // MOD_NOREPEAT 在 ToModifierWord 里默认带上。
            _registrar.Register(action.HotKeyId(), shortcut);
        }
    }

    private void OnPreferencesDidChange(object? sender, EventArgs e) => ReloadFromPreferences();

    private void RaiseRegistrationFailed(string message)
    {
        RegistrationFailed?.Invoke(this, new HotKeyRegistrationFailedEventArgs(message));
        _preferences.RaiseRegistrationFailed(message);
    }

    /// <summary>
    /// 尚未拿到宿主窗口时的占位后端：任何注册都明确报错，避免静默成功。
    /// </summary>
    private sealed class DeferredHotKeyRegistrar : IHotKeyRegistrar
    {
        public void Register(int hotKeyId, HotKeyShortcut shortcut)
        {
            throw new InvalidOperationException(
                "尚未提供消息宿主：先创建 HotKeyMessageHost，再用 Win32HotKeyRegistrar 构造 GlobalHotKeyManager。");
        }

        public void UnregisterAll()
        {
        }
    }
}
