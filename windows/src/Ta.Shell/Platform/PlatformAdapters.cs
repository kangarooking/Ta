using Ta.Platform.Overlay;
using Ta.Shell.Contracts;
using Ta.Shell.Fakes;
using Ta.Core.Capture;
using Ta.Shell.Models;

// Ta.Platform 与应用层各有一个同名 OverlayTrigger，取值一一对应。
// 本文件里未限定的 OverlayTrigger 一律指应用层的那个。
using OverlayTrigger = Ta.Shell.Contracts.OverlayTrigger;
using PlatformOverlayTrigger = Ta.Platform.Overlay.OverlayTrigger;
using OverlayOutcome = Ta.Platform.Overlay.OverlayOutcome;

namespace Ta.Shell.Platform;

/// <summary>
/// 把 <c>Ta.Platform.Overlay.SelectionOverlay</c>（真 Win32 覆盖层）
/// 适配成应用层的 <see cref="ISelectionOverlay"/>。
///
/// # 为什么需要适配
///
/// <c>SelectionOverlay</c> 是<b>一次性</b>实例：它在独立线程上创建窗口、跑消息循环，
/// 完成后窗口销毁、钩子卸载（见该类 <c>Complete()</c>）。
/// 因此每次框选会话都必须新建实例 —— <see cref="Factory"/> 就是这个「每次一个新」。
///
/// 另外两处形状差异：
///   · 真覆盖层返回 <c>OverlayOutcome</c>（自带 RectD + Trigger），
///     而应用层还需要「用户在操作栏选了哪个动作」—— 当前 <c>Ta.Platform</c> 尚未提供
///     操作栏（那是另一个模块的范围），所以 <see cref="SelectionResult.SelectedAction"/>
///     暂时恒为 null，由模式配置兜底。
///   · <c>PrepareStartContext</c> 需要鼠标所在屏 + 前台应用进程，这层 Win32 采样
///     放在适配器里（应用层不直接碰 Win32）。
/// </summary>
public sealed class PlatformSelectionOverlay : ISelectionOverlay, IDisposable
{
    private SelectionOverlay? _overlay;
    private bool _disposed;

    /// <summary>
    /// 覆盖层工厂。<b>每次调用返回一个新实例</b> —— 见类注释。
    /// </summary>
    public static Func<ISelectionOverlay> Factory =>
        () => new PlatformSelectionOverlay();

    public OverlayStartContext? PrepareStartContext()
    {
        var display = Ta.Shell.UI.ScreenInterop.DisplayUnderCursor();

        if (display is null)
        {
            // 拿不到鼠标所在屏时退回主屏，而不是直接失败 ——
            // 对应 Mac 版 NSScreen.main ?? NSScreen.screens.first 的兜底链。
            display = Ta.Shell.UI.ScreenInterop.EnumerateDisplays()
                .FirstOrDefault(d => d.IsPrimary);
        }

        if (display is null)
        {
            return null;
        }

        var (processId, processName) = Ta.Shell.UI.ScreenInterop.FrontmostApplication();

        return new OverlayStartContext(
            display,
            processName,
            sourceWindowTitle: null,

            // 窗口吸附（WindowSnapService）是独立子系统，此处留空：
            // 覆盖层在没有吸附目标时依然可用，只是不自动贴窗口边。
            snapTargets: Array.Empty<WindowInfo>());
    }

    public async Task<SelectionResult> BeginAsync(
        SelectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _overlay = new SelectionOverlay(new OverlayOptions
        {
            // 遮罩不透明度：Mac 版黑色 0.42 → 255 × 0.42 ≈ 107。
            // Ta.Platform 的 DimAlpha 默认 190（约 0.75），这里按 Mac 规格覆盖。
            DimAlpha = 107,

            ShowsActionToolbar = request.ShowsActionToolbar,
            ExcludeSelfFromCapture = true,
            PresetSelection = request.PresetSelection,

            // 操作栏动作表：标签顺序对应 Mac 版工具栏（CopyToolbarView）。
            // 动作索引 = CaptureQuickAction 枚举值，完成时原样回传由下方映射。
            ToolbarActions = request.ShowsActionToolbar
                ? new (string Label, int Action)[]
                  {
                      ("复制", (int)CaptureQuickAction.CopyImage),
                      ("贴图", (int)CaptureQuickAction.Pin),
                      ("取字", (int)CaptureQuickAction.LocalOcr),
                      ("识图", (int)CaptureQuickAction.Multimodal),
                      ("翻译", (int)CaptureQuickAction.TranslateText),
                      ("编辑", (int)CaptureQuickAction.Edit),
                      ("保存", (int)CaptureQuickAction.Save),
                      ("美化", (int)CaptureQuickAction.Beautify),
                  }
                : null,
        });

        OverlayOutcome outcome;
        try
        {
            // SelectionOverlay 自己跑消息循环，ShowAsync 不阻塞调用方线程。
            outcome = await _overlay.ShowAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return SelectionResult.Cancelled();
        }

        // 触发方式需要跨命名空间转换：Ta.Platform 有自己的 OverlayTrigger，
        // 应用层用 Ta.Shell.Contracts.OverlayTrigger。两者取值一一对应。
        var trigger = outcome.Trigger switch
        {
            PlatformOverlayTrigger.EnterKey => OverlayTrigger.EnterKey,
            PlatformOverlayTrigger.Drag => OverlayTrigger.Drag,
            _ => OverlayTrigger.Cancelled,
        };

        if (outcome.WasCancelled)
        {
            return SelectionResult.Cancelled();
        }

        // 操作栏动作索引 → CaptureQuickAction（Platform 端只传索引，语义在本层还原）。
        CaptureQuickAction? selectedAction = outcome.ToolbarActionIndex is { } index
            ? (CaptureQuickAction)index
            : null;
        Ta.Shell.Program.Log(
            $"[toolbar] outcome 动作索引={outcome.ToolbarActionIndex?.ToString() ?? "null"} "
            + $"映射={selectedAction?.ToString() ?? "null"}");

        return SelectionResult.Selected(outcome.Selection, selectedAction, trigger);
    }

    public void Dismiss()
    {
        if (_overlay is null)
        {
            return;
        }

        try
        {
            _overlay.Cancel();
        }
        finally
        {
            _overlay.Dispose();
            _overlay = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Dismiss();
    }
}

/// <summary>
/// 脚本化覆盖层假实现：按队列依次返回预设结果。
///
/// 用于「跑完整条链路」的单测 —— 不需要真窗口就能验证
/// 冻结 → 覆盖层 → 裁剪 → 分派 的顺序与内容。
/// </summary>
public sealed class ScriptedSelectionOverlay : ISelectionOverlay
{
    private readonly Queue<SelectionResult> _results = new();
    private readonly Queue<SelectionRequest> _requests = new();

    public OverlayStartContext Context { get; set; } = new(new DisplayInfo
    {
        Id = 1,
        Frame = new RectD(0, 0, 1920, 1080),
        PixelScale = 1,
        IsPrimary = true,
    });

    /// <summary>BeginAsync 被调用的次数。</summary>
    public int BeginCount => _requests.Count;

    /// <summary>Dismiss 被调用的次数。</summary>
    public int DismissCount { get; private set; }

    /// <summary>Dismiss 是否被调用过。</summary>
    public bool WasDismissed => DismissCount > 0;

    /// <summary>入队一个要返回的结果。</summary>
    public ScriptedSelectionOverlay Enqueue(SelectionResult result)
    {
        _results.Enqueue(result);
        return this;
    }

    /// <summary>入队一次成功框选（可附带用户在操作栏选的动作）。</summary>
    public ScriptedSelectionOverlay EnqueueSelection(
        RectD rect,
        CaptureQuickAction? selectedAction = null)
    {
        _results.Enqueue(SelectionResult.Selected(rect, selectedAction));
        return this;
    }

    /// <summary>入队一次取消。</summary>
    public ScriptedSelectionOverlay EnqueueCancel()
    {
        _results.Enqueue(SelectionResult.Cancelled());
        return this;
    }

    /// <summary>最近一次 BeginAsync 收到的请求；没有则为 null。</summary>
    public SelectionRequest? LastRequest => _requests.Count > 0 ? _requests.ToArray()[^1] : null;

    public OverlayStartContext? PrepareStartContext() => Context;

    public Task<SelectionResult> BeginAsync(
        SelectionRequest request,
        CancellationToken cancellationToken = default)
    {
        _requests.Enqueue(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : SelectionResult.Cancelled());
    }

    public void Dismiss() => DismissCount++;
}
