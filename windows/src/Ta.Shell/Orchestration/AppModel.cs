using Ta.Shell.Contracts;
using Ta.Shell.Models;
using Ta.Shell.UI;

namespace Ta.Shell.Orchestration;

/// <summary>
/// 应用层状态机：把托盘、快捷键、启动分流、捕获链路串起来。
///
/// 对应 Mac 版 <c>AppModel</c>（AppModel.swift, 245 行）。
///
/// ## 状态机
///
/// <code>
/// Idle ──startCapture(mode)──► Capturing ──outcome──► Idle
///   │                             │
///   │                             └── 重入保护：isCapturing 时忽略新的请求
///   └── statusText 只由 outcome / 事件驱动，UI 只读
/// </code>
///
/// 与 Mac 版的两处一致约束：
///   · <c>startCapture</c> 有重入保护（<c>guard !isCapturing</c>，AppModel.swift:169）；
///   · <c>showWelcome</c> 也有（<c>guard !isCapturing</c>，:147）——
///     截图进行中弹主界面会把 Ta 自己截进去。
///
/// ## 与 UI 的边界
///
/// 本类<b>不直接持有任何窗口</b>。欢迎页、设置页、钉图管理都通过
/// <see cref="AppModelHooks"/> 的委托外发，因此整条启动流程可被单测
/// （用一个记录调用的假 hooks 即可断言顺序与内容）。
/// </summary>
public sealed class AppModel
{
    private readonly IHotKeyService _hotKeys;
    private readonly CaptureCoordinator _coordinator;
    private readonly IOcrService _ocr;
    private readonly IAgentBridge? _agentBridge;
    private readonly ISettingsStore _settings;
    private readonly AppModelHooks _hooks;
    private readonly LaunchPlan _launchPlan;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private CancellationTokenSource? _lifecycle;
    private bool _hasStarted;

    public AppModel(
        IHotKeyService hotKeys,
        CaptureCoordinator coordinator,
        IOcrService ocr,
        ISettingsStore settings,
        AppModelHooks? hooks = null,
        IAgentBridge? agentBridge = null,
        IReadOnlyList<string>? launchArguments = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _hotKeys = hotKeys ?? throw new ArgumentNullException(nameof(hotKeys));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _hooks = hooks ?? new AppModelHooks();
        _agentBridge = agentBridge;
        _delay = delay ?? DefaultDelay;

        _launchPlan = AppLaunchPolicy.PlanFor(launchArguments ?? Array.Empty<string>());
    }

    /// <summary>初始状态文本。对应 AppModel.swift:26 的 @Published 初值。</summary>
    public const string ReadyStatusText = "本地识别就绪";

    /// <summary>快捷键注册失败时的状态文本（AppModel.swift:60）。</summary>
    public const string HotKeyRegistrationFailedStatus = "快捷键注册失败";

    /// <summary>
    /// 快捷键冲突并已回滚时的状态文本（AppModel.swift:69 的默认值）。
    /// </summary>
    public const string HotKeyConflictStatus = "快捷键冲突，已恢复上一组配置";

    /// <summary>Agent 桥启动失败（AppModel.swift:142）。</summary>
    public const string AgentBridgeFailedStatus = "Agent Bridge 启动失败";

    public string StatusText { get; private set; } = ReadyStatusText;

    public bool IsCapturing { get; private set; }

    /// <summary>启动参数解析结果。</summary>
    public LaunchPlan LaunchPlan => _launchPlan;

    /// <summary>状态变化。供托盘 / popover 刷新。</summary>
    public event Action? StateChanged;

    /// <summary>
    /// 启动。对应 Mac 版 <c>AppModel.start()</c>（AppModel.swift:45-127）。
    ///
    /// 有重入保护：重复调用是空操作。
    /// </summary>
    public async Task StartAsync()
    {
        if (_hasStarted)
        {
            return;
        }

        _hasStarted = true;
        _lifecycle = new CancellationTokenSource();
        var cancellationToken = _lifecycle.Token;

        // 1. 启动 Agent 桥（:48 / :129-144）。
        await StartAgentBridgeAsync(cancellationToken);

        // 2. 注册快捷键，失败则状态文本置「快捷键注册失败」（:50-61）。
        _hotKeys.RegistrationFailed += OnHotKeyRegistrationFailed;

        try
        {
            _hotKeys.Register(StartCapture);
        }
        catch (Exception)
        {
            SetStatusText(HotKeyRegistrationFailedStatus);
        }

        // 3. 预热 OCR（:73）。这一步放在分流之前 —— 无论走哪条启动路径都要预热。
        try
        {
            _ocr.Prewarm();
        }
        catch (Exception)
        {
            // 预热失败不阻塞启动：真到识别时报错即可，让用户能用其他功能。
        }

        // 4. 按启动参数分流（:75-126）。
        await RunLaunchActionAsync(cancellationToken);
    }

    /// <summary>
    /// 开始一次截图。对应 Mac 版 <c>startCapture(_:)</c>（AppModel.swift:168-193）。
    ///
    /// 流程：
    ///   1. 重入保护
    ///   2. 收起 Ta 自己的面板（否则会被截进图里）
    ///   3. 按模式设状态文本
    ///   4. 交给 CaptureCoordinator，完成后按 outcome 更新状态文本
    /// </summary>
    public async Task StartCaptureAsync(CaptureMode mode)
    {
        if (IsCapturing)
        {
            return;
        }

        // 对应 Mac 版 :170 的 NotificationCenter.post(.taMenuBarShouldClose)。
        // 这是任务书 E 项（不把自身截进图）的第一道防线。
        _hooks.CloseSelfPanels?.Invoke();

        IsCapturing = true;
        SetStatusText(ModeActionMapper.StatusTextFor(mode));
        RaiseStateChanged();

        CaptureOutcome outcome = CaptureOutcome.Cancelled();
        try
        {
            outcome = await _coordinator.StartAsync(mode, _lifecycle?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            outcome = CaptureOutcome.Cancelled();
        }
        catch (Exception error)
        {
            // 截图链任何一步异常都必须留痕 —— 曾经 GetDpiForMonitor 的错误
            // DllImport 让整条链「点击无反应」而毫无线索。
            Ta.Shell.Program.Log($"截图链路异常: {error}");
            outcome = CaptureOutcome.Failed(error.Message);
        }
        finally
        {
            IsCapturing = false;

            // 收起的面板在截图结束后恢复（结果反馈条除外 —— 它按自己的节奏出现）。
            _hooks.RestoreSelfPanels?.Invoke();
        }

        // 对应 Mac 版 :184-191：cancelled → 回到就绪态；completed / failed → 显示消息。
        SetStatusText(outcome.Kind switch
        {
            CaptureOutcomeKind.Cancelled => ReadyStatusText,
            _ => outcome.Message,
        });
    }

    /// <summary>同步入口，供托盘与快捷键回调直接调用。</summary>
    public void StartCapture(CaptureMode mode)
    {
        // 不 await —— 捕获链是长流程，调用方（消息循环线程）不能被阻塞。
        _ = StartCaptureAsync(mode);
    }

    public void ShowWelcome()
    {
        if (IsCapturing)
        {
            // 对应 Mac 版 :147 的重入保护。
            return;
        }

        _hooks.ShowWelcome?.Invoke();
    }

    public void ShowSettings()
    {
        _hooks.CloseSelfPanels?.Invoke();
        _hooks.ShowSettings?.Invoke();
    }

    /// <summary>钉图管理分发。对应 Mac 版 AppModel.pinClipboardContent/hideAllPins/...（:195-216）。</summary>
    public void HandlePinAction(string actionId)
    {
        switch (actionId)
        {
            case "pin-clipboard":
                SetStatusText(_hooks.PinClipboard?.Invoke() == true
                    ? "已从剪贴板生成钉图"
                    : "剪贴板中没有可钉住的内容");
                break;

            case "hide-all":
                _hooks.HideAllPins?.Invoke();
                SetStatusText("已隐藏全部钉图");
                break;

            case "show-all":
                _hooks.ShowAllPins?.Invoke();
                SetStatusText("已显示全部钉图");
                break;

            case "restore-interaction":
                _hooks.RestorePinInteraction?.Invoke();
                SetStatusText("已恢复钉图鼠标交互");
                break;

            case "restore-last":
                SetStatusText(_hooks.RestoreLastClosedPin?.Invoke() == true
                    ? "已恢复最近关闭的钉图"
                    : "没有可恢复的钉图");
                break;
        }
    }

    /// <summary>取消当前生命周期（应用退出时调用）。</summary>
    public void Shutdown()
    {
        try
        {
            _lifecycle?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经释放过了。
        }

        _lifecycle?.Dispose();
        _lifecycle = null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 内部
    // ─────────────────────────────────────────────────────────────────────────

    private async Task StartAgentBridgeAsync(CancellationToken cancellationToken)
    {
        if (_agentBridge is null)
        {
            return;
        }

        try
        {
            await _agentBridge.StartAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 对应 Mac 版 :141-143。
            SetStatusText(AgentBridgeFailedStatus);
        }
    }

    /// <summary>
    /// 快捷键冲突回滚提示。对应 Mac 版 AppModel.swift:62-71 的
    /// <c>registrationFailedNotification</c> 观察者 —— 通知里带 message 就用它，
    /// 否则用默认文案。
    /// </summary>
    private void OnHotKeyRegistrationFailed(object? sender, HotKeyRegistrationFailure failure)
    {
        SetStatusText(string.IsNullOrWhiteSpace(failure.Message)
            ? HotKeyConflictStatus
            : failure.Message);
    }

    private async Task RunLaunchActionAsync(CancellationToken cancellationToken)
    {
        var plan = _launchPlan;

        // agent-bridge 模式提前返回：不显示欢迎页、不排截图（:76-77）。
        if (plan.Mode == AppLaunchMode.AgentBridge)
        {
            return;
        }

        if (plan.Delay > TimeSpan.Zero)
        {
            await _delay(plan.Delay, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();

        switch (plan.Action)
        {
            case LaunchAction.ScheduleWelcome:
                // Mac 版 :122-124 额外检查 isCapturing —— 延迟期间用户可能已经手动开了一次截图。
                if (!IsCapturing)
                {
                    _hooks.ShowWelcome?.Invoke();
                }

                break;

            case LaunchAction.ScheduleCapture when plan.CaptureMode is { } mode:
                StartCapture(mode);
                break;

            case LaunchAction.ScheduleFixedCapture:
                await _coordinator.StartFixedTestRegionAsync(
                    CaptureMode.Intelligent, cancellationToken);
                break;

            case LaunchAction.SmokeCaptureToolbar:
                await _coordinator.OpenCaptureToolbarSmokeAsync(cancellationToken);
                SetStatusText(ReadyStatusText);
                break;

            case LaunchAction.SmokeResultBarSuccess:
                await _coordinator.OpenResultBarSmokeAsync(ResultBarKind.Success, cancellationToken);
                break;

            case LaunchAction.SmokeResultBarFailure:
                await _coordinator.OpenResultBarSmokeAsync(ResultBarKind.Failure, cancellationToken);
                break;

            case LaunchAction.SmokeEditor:
                await _coordinator.OpenEditorSmokeAsync(cancellationToken);
                break;

            case LaunchAction.SmokeInlineEditor:
                IsCapturing = true;
                RaiseStateChanged();
                await _coordinator.OpenInlineEditorSmokeAsync(cancellationToken);
                IsCapturing = false;
                SetStatusText(ReadyStatusText);
                break;

            case LaunchAction.None:
            default:
                break;
        }
    }

    private void SetStatusText(string value)
    {
        if (StatusText == value)
        {
            return;
        }

        StatusText = value;
        RaiseStateChanged();
    }

    private void RaiseStateChanged() => StateChanged?.Invoke();

    /// <summary>默认延迟实现。可被测试替换成瞬时返回。</summary>
    private static Task DefaultDelay(TimeSpan duration, CancellationToken cancellationToken) =>
        duration <= TimeSpan.Zero
            ? Task.CompletedTask
            : Task.Delay(duration, cancellationToken);

    /// <summary>当前设置里结果反馈条的时长（秒），供 UI 构造时读取。</summary>
    public double ResultBarDuration =>
        _settings.ReadDouble(SettingKeys.ResultBarDuration, Ta.Shell.UI.ResultBarLayout.DefaultAutoHideSeconds);
}

/// <summary>
/// 应用层与外发 UI 的接缝。
///
/// Mac 版直接持有 <c>WelcomeWindowController</c> / <c>SettingsWindowController</c>
/// 等具体控制器（AppModel.swift:31-32），因此在没有窗口的环境里无法单测。
/// 这里把同样的外发动作收成一组委托，宿主（<c>Program</c>）注入真实现，
/// 测试注入记录型假实现。
/// </summary>
public sealed class AppModelHooks
{
    /// <summary>收起 Ta 自己的面板（popover / 结果条），避免被截进图里。</summary>
    public Action? CloseSelfPanels { get; init; }

    /// <summary>截图结束后恢复面板。</summary>
    public Action? RestoreSelfPanels { get; init; }

    public Action? ShowWelcome { get; init; }
    public Action? ShowSettings { get; init; }

    public Func<bool>? PinClipboard { get; init; }
    public Action? HideAllPins { get; init; }
    public Action? ShowAllPins { get; init; }
    public Action? RestorePinInteraction { get; init; }
    public Func<bool>? RestoreLastClosedPin { get; init; }
}
