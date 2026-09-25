using Ta.Shell.Contracts;
using Ta.Shell.Models;
using HotKeysSettingsStore = Ta.HotKeys.ISettingsStore;
using HotKeyManager = Ta.HotKeys.GlobalHotKeyManager;
using HotKeyPreferences = Ta.HotKeys.HotKeyPreferences;
using HotKeyMessageHost = Ta.HotKeys.HotKeyMessageHost;

namespace Ta.Shell.Platform;

/// <summary>
/// 把 <c>Ta.HotKeys.GlobalHotKeyManager</c> 适配成应用层的 <see cref="IHotKeyService"/>。
///
/// 对应 Mac 版 <c>AppModel</c> 里那几行（AppModel.swift:29-31、:50-71）：
/// <code>
/// hotKeyManager.registerDefaults(
///     interactiveCapture:  { self?.startCapture(.interactive) },
///     intelligentCapture:  { self?.startCapture(.intelligent) },
///     translationCapture:  { self?.startCapture(.translation) },
///     imageCapture:        { self?.startCapture(.image) },
///     pinCapture:          { self?.startCapture(.pin) },
///     longCapture:         { self?.startCapture(.long) })
/// catch { statusText = "快捷键注册失败" }
/// hotKeyFailureObserver = NotificationCenter.default.addObserver(
///     forName: HotKeyPreferences.registrationFailedNotification, ...)
/// </code>
///
/// 快捷键的<b>注册、冲突回滚、持久化</b>都由 <c>Ta.HotKeys</c> 负责（那是独立子系统）。
/// 本适配器只做三件事：
///   · 把「六个动作」翻译成「六种截图模式」；
///   · 把 <c>RegistrationFailed</c> 事件转成应用层的 <see cref="HotKeyRegistrationFailure"/>；
///   · 持有 <c>HotKeyMessageHost</c> 并暴露 <see cref="Pump"/> 供消息循环泵活。
///
/// # ⚠️ 线程模型（必须遵守）
///
/// <c>RegisterHotKey</c> 属于「注册线程」：<c>WM_HOTKEY</c> 会投递到调用注册方法的
/// 线程所拥有窗口的消息队列。因此：
///   · 本类的<b>构造函数</b>必须在将要运行消息循环的那个 STA 线程上被调用；
///   · 消息循环里必须周期调用 <see cref="Pump"/>，否则快捷键收不到。
/// 这两条由 <c>Program.Main</c> 保证 —— 它在 Main 的 STA 线程上构造本类，
/// 并在同一线程的循环里调用 <see cref="Pump"/>。
/// </summary>
public sealed class PlatformHotKeyService : IHotKeyService, IDisposable
{
    private readonly HotKeyMessageHost _host;
    private readonly HotKeyManager _manager;
    private bool _disposed;

    public PlatformHotKeyService(HotKeysSettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        // 先建消息宿主 —— RegisterHotKey 的 WM_HOTKEY 要投到它的队列里。
        _host = new HotKeyMessageHost();
        if (!_host.IsAlive || _host.WindowHandle == IntPtr.Zero)
        {
            _host.Dispose();
            throw new InvalidOperationException("创建快捷键消息宿主失败。");
        }

        // assumeCarbonKeyCodes: false —— 迁移 Mac 侧配置时才需要按 Carbon 解释。
        var preferences = new HotKeyPreferences(store, assumeCarbonKeyCodes: false);
        _manager = new HotKeyManager(preferences, new Ta.HotKeys.Win32HotKeyRegistrar(_host.WindowHandle));

        // WM_HOTKEY → manager.invoke(id) → 应用层的截图模式回调。
        _host.HotKeyPressed += OnHotKeyPressed;

        // 对应 Mac 版 registrationFailedNotification 观察者（AppModel.swift:62-71）。
        _manager.RegistrationFailed += OnManagerRegistrationFailed;
    }

    public event EventHandler<HotKeyRegistrationFailure>? RegistrationFailed;

    public IReadOnlyDictionary<Ta.HotKeys.GlobalHotKeyAction, Ta.HotKeys.HotKeyShortcut> ActiveShortcuts =>
        _manager.ActiveShortcuts;

    /// <summary>
    /// 注册六个默认快捷键。
    ///
    /// ⚠️ 注册失败会抛异常，由 <c>AppModel.StartAsync</c> 捕获并把状态文本置为
    /// 「快捷键注册失败」（对应 AppModel.swift:59-61）。
    /// </summary>
    public void Register(Action<CaptureMode> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ThrowIfDisposed();

        _manager.Register(action =>
        {
            // 动作 → 模式。对照参考文档 §4.2 的映射表。
            var mode = action switch
            {
                Ta.HotKeys.GlobalHotKeyAction.InteractiveCapture => CaptureMode.Interactive,
                Ta.HotKeys.GlobalHotKeyAction.IntelligentCapture => CaptureMode.Intelligent,
                Ta.HotKeys.GlobalHotKeyAction.TranslationCapture => CaptureMode.Translation,
                Ta.HotKeys.GlobalHotKeyAction.ImageCapture => CaptureMode.Image,
                Ta.HotKeys.GlobalHotKeyAction.PinCapture => CaptureMode.Pin,
                Ta.HotKeys.GlobalHotKeyAction.LongCapture => CaptureMode.Long,
                _ => CaptureMode.Interactive,
            };

            handler(mode);
        });
    }

    public void Reload()
    {
        ThrowIfDisposed();
        _manager.ReloadFromPreferences();
    }

    /// <summary>
    /// 抽干宿主窗口的消息队列。必须由消息循环<b>周期</b>调用。
    /// 超时给 0 —— 循环自己已经 Sleep 过了，这里只负责把已排队的消息取完。
    /// </summary>
    public void Pump() => _host.PumpPending(0);

    private void OnHotKeyPressed(object? sender, int hotKeyId) => _manager.Invoke(hotKeyId);

    private void OnManagerRegistrationFailed(object? sender, Ta.HotKeys.HotKeyRegistrationFailedEventArgs args)
    {
        // 冲突时 Ta.HotKeys 已自动回滚到上一组成功配置（参考文档 §4.2），
        // 因此这里只负责把消息带给应用层改状态文本。
        RegistrationFailed?.Invoke(this, new HotKeyRegistrationFailure(args.Message, IsConflict: true));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _host.HotKeyPressed -= OnHotKeyPressed;
        _manager.Dispose();
        _host.Dispose();
    }
}
