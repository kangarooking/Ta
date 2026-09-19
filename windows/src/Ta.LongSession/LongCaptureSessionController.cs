using Ta.Core.Capture;
using Ta.Core.Imaging;
using Ta.Core.LongCapture;

namespace Ta.LongSession;

/// <summary>会话选项。</summary>
public sealed class LongCaptureSessionOptions
{
    /// <summary>源应用进程 ID（对应 Mac: CaptureSelection.sourceApplicationProcessID）。</summary>
    public int? SourceProcessId { get; init; }

    /// <summary>目标显示器 ID；为空时按选区所在显示器解析。</summary>
    public int? DisplayId { get; init; }

    /// <summary>滚动驱动策略。</summary>
    public ScrollStrategy ScrollStrategy { get; init; } = ScrollStrategy.Auto;
}

/// <summary>会话完成结果。对应 Mac: ScrollingCaptureSessionOutcome.completed。</summary>
public sealed record LongCaptureSessionResult(
    IReadOnlyList<RgbaBitmap> Images,
    int AcceptedFrames,
    int SkippedFrames,
    int ReviewedSeams);

/// <summary>
/// 滚动长截图会话控制器。
///
/// 逐行对应 Mac 版 ScrollingCaptureSessionController.swift：
/// 状态机判定全部委托给 <see cref="AutoScrollSessionLogic"/>（纯逻辑、可单测），
/// 本类只负责 I/O 编排 —— 捕获、驱动滚动、更新 HUD、走接缝复查。
///
/// 所有可变状态都在 <c>_lock</c> 保护下；HUD 按钮回调（HUD 线程）与
/// 捕获循环（后台 Task）都经由该锁同步。
/// </summary>
public sealed class LongCaptureSessionController : IDisposable
{
    private readonly object _lock = new();

    // 依赖（可注入，便于测试）
    private readonly IScreenCapture _capture;
    private readonly IScrollStitchEngine _stitcher;
    private readonly IAutoScrollDriver _driver;
    private readonly ILongCaptureHud _hud;
    private readonly ISeamReviewView _seamReview;
    private readonly IClock _clock;
    private readonly IDelay _delay;

    // 会话状态
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private CaptureSelection _selection;
    private LongCaptureSessionOptions _options = new();
    private ScrollTarget? _scrollTarget;
    private ScrollProgress? _pendingScrollProgress;
    private int _expectedScrollDistancePoints;
    private readonly AutoScrollProgressTracker _tracker = new(AutoScrollSessionLogic.MaximumAttemptsWithoutProgress);
    private double _nextAutoScrollDate = AutoScrollSessionLogic.DistantPast;
    private double _autoScrollSettleDeadline = AutoScrollSessionLogic.DistantPast;
    private int _bottomConfirmationCount;
    private int _acceptedFrames;
    private int _skippedFrames;
    private bool _isPaused;
    private bool _isFinishing;
    private bool _isAutoScrolling;
    private bool _started;
    private bool _disposed;
    private string _hudStatus = "正在采集第一帧…";
    private bool _hudStatusSticky;
    private readonly ViewportMotionDetector _viewportMotion = new();
    private readonly ViewportMotionDetector _bottomFrameMotion = new();
    private int _displayId;

    public LongCaptureSessionController(
        IScreenCapture capture,
        IScrollStitchEngine? stitcher = null,
        IAutoScrollDriver? driver = null,
        ILongCaptureHud? hud = null,
        ISeamReviewView? seamReview = null,
        IClock? clock = null,
        IDelay? delay = null)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _stitcher = stitcher ?? new BitmapStitchEngine();
        _driver = driver ?? new UiaAutoScrollDriver();
        _hud = hud ?? new LongCaptureHud();
        _seamReview = seamReview ?? new SeamReviewWindow();
        _clock = clock ?? new StopwatchClock();
        _delay = delay ?? new TaskDelay();
    }

    /// <summary>完成（含接缝复查后的导出）。</summary>
    public event Action<LongCaptureSessionResult>? Completed;

    /// <summary>用户取消。</summary>
    public event Action? Cancelled;

    /// <summary>失败（捕获/拼接异常）。</summary>
    public event Action<string>? Failed;

    /// <summary>开始会话。对应 Mac: start(selection:completion:)（:46-79）。</summary>
    public void Start(CaptureSelection selection, LongCaptureSessionOptions? options = null)
    {
        // 先取消旧会话（不持锁 —— CancelLoop 要等旧循环任务退出，而循环任务需要锁）。
        if (_started)
        {
            CancelCore();
        }

        lock (_lock)
        {
            _started = true;
            _stitcher.Reset();
            _viewportMotion.Reset();
            _bottomFrameMotion.Reset();
            _acceptedFrames = 0;
            _skippedFrames = 0;
            _isPaused = false;
            _isFinishing = false;
            _isAutoScrolling = false;
            _tracker.Begin();
            _selection = selection;
            _options = options ?? new LongCaptureSessionOptions();
            _scrollTarget = null;
            _pendingScrollProgress = null;
            _expectedScrollDistancePoints = AutoScrollSessionLogic.RecommendedScrollDistance(selection.GlobalRect.Height);
            _bottomConfirmationCount = 0;
            _nextAutoScrollDate = AutoScrollSessionLogic.DistantPast;
            _autoScrollSettleDeadline = AutoScrollSessionLogic.DistantPast;
            _hudStatus = "正在采集第一帧…";
            _hudStatusSticky = false;
            _displayId = ResolveDisplayId(selection, _options);
        }

        _hud.ButtonPressed -= OnHudButton;
        _hud.ButtonPressed += OnHudButton;
        _hud.Start(selection);

        // Mac 版在 HUD 显示后立即尝试 beginAutoScroll（:69-73）。
        BeginAutoScrollCore();

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => CaptureLoopAsync(_cts.Token), _cts.Token);
    }

    /// <summary>取消会话。对应 Mac: cancel(notify:)（:520-533）。</summary>
    public void Cancel()
    {
        lock (_lock)
        {
            if (!_started || _isFinishing)
            {
                return;
            }

            _isFinishing = true;
        }

        CancelCore();
        Raise(Cancelled);
    }

    /// <summary>完成并保存：走接缝复查，或直接导出。对应 Mac: finish()（:467-494）。</summary>
    public void Finish()
    {
        lock (_lock)
        {
            if (!_started || _isFinishing)
            {
                return;
            }

            _isFinishing = true;
        }

        CancelLoop();
        _hud.Close();

        bool hasSegments;
        lock (_lock)
        {
            hasSegments = _stitcher.Segments.Count > 0;
        }

        if (hasSegments)
        {
            PresentSeamReview();
            return;
        }

        IReadOnlyList<RgbaBitmap> images;
        lock (_lock)
        {
            try
            {
                images = _stitcher.MakeImages(SeamReviewWindow.ExportMaximumPixelHeight);
            }
            catch (StitchException ex)
            {
                FailCore(ex.Message);
                return;
            }
        }

        Complete(images, 0);
    }

    /// <summary>暂停 / 继续。对应 Mac: togglePause()（:422-431）。</summary>
    public void TogglePause()
    {
        lock (_lock)
        {
            if (!_started || _isFinishing)
            {
                return;
            }

            _isPaused = !_isPaused;
            if (!_isPaused && _isAutoScrolling)
            {
                _tracker.Begin();
                _nextAutoScrollDate = _clock.Now();
                _autoScrollSettleDeadline = AutoScrollSessionLogic.DistantPast;
            }

            SetHudStatus(_isPaused ? "已暂停，可检查内容" : "继续上下滚动", sticky: true);
            PushHudStateLocked();
        }
    }

    /// <summary>自动滚动开关。对应 Mac: toggleAutoScroll()（:433-451）。</summary>
    public void ToggleAutoScroll()
    {
        lock (_lock)
        {
            if (!_started || _isFinishing)
            {
                return;
            }

            if (_isAutoScrolling)
            {
                _isAutoScrolling = false;
                _tracker.Begin();
                SetHudStatus("自动滚动已关闭，可继续手动滚动", sticky: true);
                PushHudStateLocked();
                return;
            }

            BeginAutoScrollCore();
            PushHudStateLocked();
        }
    }

    // ── 捕获循环（逐行对应 Mac: captureLoop :81-267） ──────────────

    private async Task CaptureLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            bool paused;
            bool finishing;
            lock (_lock)
            {
                paused = _isPaused;
                finishing = _isFinishing;
            }

            if (!paused && !finishing)
            {
                try
                {
                    await CaptureTickAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    FailCore(ex.Message);
                    return;
                }
            }

            bool autoScrolling;
            lock (_lock)
            {
                autoScrolling = _isAutoScrolling;
            }

            var loopDelay = autoScrolling
                ? AutoScrollSessionLogic.StandardAutoScrollDelayMilliseconds
                : AutoScrollSessionLogic.ManualCaptureLoopDelayMilliseconds;

            try
            {
                await _delay.Delay(loopDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task CaptureTickAsync(CancellationToken cancellationToken)
    {
        var selection = _selection;

        // 每 tick 重新冻结整屏（页面在滚动，缓存帧无意义）。
        var frozen = _capture.CaptureDisplay(_displayId, selection.BackingScaleFactor);
        var image = _capture.CropFrozen(frozen, selection);

        // 灰度采样只算一次 —— 匹配器、视口运动、底部运动检测共用。
        var sample = image.ToGrayscale(
            BitmapStitchFrame.DefaultSampleWidth,
            BitmapStitchFrame.DefaultSampleHeight);

        var motion = _viewportMotion.Compare(sample);

        int requestedPixelShift;
        ScrollConstraint constraint;
        int? preferredShift;
        lock (_lock)
        {
            requestedPixelShift = (int)Math.Round(
                Math.Max(1, _expectedScrollDistancePoints) * selection.BackingScaleFactor);
            constraint = _isAutoScrolling ? ScrollConstraint.DownwardOnly : ScrollConstraint.Any;
            preferredShift = _isAutoScrolling ? requestedPixelShift : null;
        }

        var disposition = _stitcher.Append(image, constraint, preferredShift);

        bool autoScrolling;
        lock (_lock)
        {
            autoScrolling = _isAutoScrolling;
        }

        if (autoScrolling)
        {
            disposition = await AutoScrollStepAsync(image, sample, disposition, motion, requestedPixelShift)
                .ConfigureAwait(false);
        }

        lock (_lock)
        {
            switch (disposition)
            {
                case FrameDisposition.FirstFrame:
                    _acceptedFrames = 1;
                    _viewportMotion.Commit(sample);
                    break;
                case FrameDisposition.Appended:
                    _acceptedFrames++;
                    _viewportMotion.Commit(sample);
                    break;
                case FrameDisposition.Duplicate:
                    break;
                case FrameDisposition.Rejected:
                    _skippedFrames++;
                    break;
            }

            UpdateHudLocked();
        }
    }

    /// <summary>
    /// 自动滚动单步。逐行对应 Mac captureLoop 的 isAutoScrolling 分支（:97-243）。
    /// </summary>
    private async Task<FrameDisposition> AutoScrollStepAsync(
        RgbaBitmap image,
        GrayscaleFrame sample,
        FrameDisposition disposition,
        ViewportMotionMeasurement motion,
        int requestedPixelShift)
    {
        var now = _clock.Now();

        ScrollProgress? currentProgress;
        ScrollTarget? target;
        lock (_lock)
        {
            target = _scrollTarget;
        }

        currentProgress = target is { } t ? _driver.ReadProgress(t) : null;

        // 底部帧运动检测：只有 AX 报到底才启用（对应 Mac: :102-109）。
        ViewportMotionMeasurement? bottomMotion;
        if (currentProgress?.IsAtEnd == true)
        {
            bottomMotion = _bottomFrameMotion.Compare(sample);
            _bottomFrameMotion.Commit(sample);
        }
        else
        {
            _bottomFrameMotion.Reset();
            bottomMotion = null;
        }

        var observation = AutoScrollSessionLogic.AutoScrollObservation(disposition, motion);

        ScrollProgress? progressBefore;
        double settleDeadline;
        lock (_lock)
        {
            _tracker.Observe(observation);
            progressBefore = _pendingScrollProgress;
            settleDeadline = _autoScrollSettleDeadline;

            var resolution = AutoScrollSessionLogic.ResolveAutoScrollAttempt(
                _tracker,
                observation,
                motion,
                progressBefore,
                currentProgress,
                now,
                settleDeadline);

            switch (resolution)
            {
                case AutoScrollAttemptResolution.Waiting:
                    break;

                case AutoScrollAttemptResolution.Progress:
                    // 滚动条/视口运动已独立证明手势成功；接缝含糊时保留一帧
                    // 保守的低置信度帧继续走，绝不因拼接质量停掉驱动。
                    if (_tracker.HasPendingScroll)
                    {
                        switch (disposition)
                        {
                            case FrameDisposition.FirstFrame:
                            case FrameDisposition.Appended:
                                break;
                            case FrameDisposition.Duplicate:
                            case FrameDisposition.Rejected:
                                // 最后一次手势常短于常规步长。等底部视口稳定后
                                // 再测那段尾巴，而不是插入整幅回退帧。
                                if (currentProgress?.IsAtEnd != true)
                                {
                                    disposition = _stitcher.AppendFallback(image, requestedPixelShift);
                                }

                                break;
                        }
                    }

                    _tracker.FinishPendingWithProgress();
                    _pendingScrollProgress = null;
                    _expectedScrollDistancePoints =
                        AutoScrollSessionLogic.RecommendedScrollDistance(_selection.GlobalRect.Height);
                    _nextAutoScrollDate = now + AutoScrollSessionLogic.StandardAutoScrollDelay;
                    break;

                case AutoScrollAttemptResolution.NoMovement:
                    _tracker.FinishPendingWithoutProgress();
                    _pendingScrollProgress = null;
                    break;
            }

            // AX 可能先于平滑滚动/懒加载/布局完成就报最大值。连续两个底部帧
            // 稳定后提交该终端视口 —— 保下那次短于常规步长的末次滚动。
            if (currentProgress?.IsAtEnd == true
                && bottomMotion?.HasReference == true
                && bottomMotion?.IsStationary == true)
            {
                switch (disposition)
                {
                    case FrameDisposition.FirstFrame:
                    case FrameDisposition.Appended:
                        break;
                    case FrameDisposition.Duplicate:
                    case FrameDisposition.Rejected:
                        disposition = _stitcher.AppendTerminal(image, requestedPixelShift);
                        break;
                }
            }

            _bottomConfirmationCount = AutoScrollSessionLogic.AdvanceBottomConfirmation(
                _bottomConfirmationCount,
                disposition,
                bottomMotion,
                currentProgress);

            if (_isAutoScrolling
                && _bottomConfirmationCount >= AutoScrollSessionLogic.RequiredBottomConfirmationFrames)
            {
                StopAutoScrollAtBottomLocked();
            }
        }

        // ── 新手势 / 收尾决策（对应 Mac: :187-242） ──
        bool shouldSendScroll = false;
        bool retryWithSmallerStep = false;
        bool stopAtBottom = false;
        int scrollPixels = 0;
        ScrollStrategy strategy;

        lock (_lock)
        {
            strategy = _options.ScrollStrategy;

            if (_isAutoScrolling
                && !_tracker.HasPendingScroll
                && now >= _nextAutoScrollDate)
            {
                var mode = _scrollTarget?.Mode ?? ScrollTargetMode.EventFallback;

                if (_tracker.ShouldStop)
                {
                    if (AutoScrollSessionLogic.ShouldCompleteAfterNoProgress(_tracker, mode, currentProgress))
                    {
                        stopAtBottom = true;
                    }
                    else
                    {
                        // 视口看着静止，但被跟踪的滚动条仍说下面有内容。
                        // 重新解析嵌套滚动区域、用更小步长重试，
                        // 而不是错误地宣布采集完成。
                        retryWithSmallerStep = true;
                    }
                }
                else if (currentProgress?.IsAtEnd == true)
                {
                    // 底部信号之后不再开新手势 —— 完成由像素稳定性控制。
                    _nextAutoScrollDate = AutoScrollSessionLogic.DistantFuture;
                }
                else
                {
                    scrollPixels = Math.Min(
                        AutoScrollSessionLogic.RecommendedScrollDistance(_selection.GlobalRect.Height),
                        Math.Max(AutoScrollSessionLogic.RetryScrollDistanceFloor, _expectedScrollDistancePoints));
                    shouldSendScroll = true;
                }
            }
        }

        if (stopAtBottom)
        {
            lock (_lock)
            {
                StopAutoScrollAtBottomLocked();
            }
        }
        else if (retryWithSmallerStep)
        {
            lock (_lock)
            {
                _scrollTarget = _driver.ResolveTarget(_selection, _options.SourceProcessId);
                _tracker.Begin();
                _pendingScrollProgress = null;
                _expectedScrollDistancePoints = Math.Max(
                    AutoScrollSessionLogic.RetryScrollDistanceFloor,
                    AutoScrollSessionLogic.RecommendedScrollDistance(_selection.GlobalRect.Height) / 2);
                _nextAutoScrollDate = now + AutoScrollSessionLogic.StandardAutoScrollDelay;
                SetHudStatus("滚动区域仍未到底，正在重新锁定并继续", sticky: true);
                PushHudStateLocked();
            }
        }
        else if (shouldSendScroll)
        {
            ScrollProgress? before = null;
            ScrollTarget? targetForScroll;
            lock (_lock)
            {
                targetForScroll = _scrollTarget;
            }

            before = targetForScroll is { } t2 ? _driver.ReadProgress(t2) : null;

            var posted = await _driver.PostDownwardScrollAsync(
                _selection, targetForScroll, scrollPixels, strategy, CancellationToken.None)
                .ConfigureAwait(false);

            lock (_lock)
            {
                if (posted)
                {
                    _tracker.DidSendScroll();
                    _pendingScrollProgress = before;
                    _expectedScrollDistancePoints = scrollPixels;
                    _nextAutoScrollDate = now + AutoScrollSessionLogic.StandardAutoScrollDelay;
                    _autoScrollSettleDeadline = now + AutoScrollSessionLogic.MaximumAutoScrollSettleTime;
                }
                else
                {
                    _isAutoScrolling = false;
                    SetHudStatus("无法向截图目标发送滚动事件，请检查目标窗口与辅助功能权限", sticky: true);
                    PushHudStateLocked();
                }
            }
        }

        return disposition;
    }

    // ── 内部状态转换 ──────────────────────────────────────────────

    private void BeginAutoScrollCore()
    {
        lock (_lock)
        {
            var target = _driver.ResolveTarget(_selection, _options.SourceProcessId);
            _scrollTarget = target;
            _isAutoScrolling = true;
            _tracker.Begin();
            _nextAutoScrollDate = _clock.Now();
            _autoScrollSettleDeadline = AutoScrollSessionLogic.DistantPast;
            SetHudStatus(
                target?.Mode == ScrollTargetMode.AccessibilityTracked
                    ? "已锁定滚动区域，自动采集到真实底部"
                    : "通用滚动模式，自动识别新增内容直到画面稳定",
                sticky: true);
            PushHudStateLocked();
        }
    }

    private void StopAutoScrollAtBottomLocked()
    {
        _isAutoScrolling = false;
        _tracker.Begin();
        _pendingScrollProgress = null;
        _bottomConfirmationCount = 0;
        SetHudStatus("已到达滚动区域底部，内容采集完成", sticky: true);
        PushHudStateLocked();
    }

    private void UpdateHudLocked()
    {
        var stickyFragments = new[]
        {
            "内容采集完成",
            "已暂停",
            "无法向截图目标发送滚动事件",
            "滚动区域仍未到底，正在重新锁定并继续",
            "自动滚动已关闭，可继续手动滚动",
            "自动滚动需要辅助功能权限",
            "截图区域已经失效，请重新开始长截图",
        };

        var hasSticky = _hudStatusSticky
            && stickyFragments.Any(f => _hudStatus.Contains(f, StringComparison.Ordinal));

        if (hasSticky)
        {
            PushHudStateLocked();
            return;
        }

        _hudStatusSticky = false;
        if (_acceptedFrames <= 1 && !_isAutoScrolling)
        {
            _hudStatus = "可手动上下滚动，也可开启自动滚动";
        }
        else if (_isAutoScrolling && _bottomConfirmationCount > 0)
        {
            _hudStatus = $"已到底，正在确认最终画面 · {_bottomConfirmationCount} / {AutoScrollSessionLogic.RequiredBottomConfirmationFrames}";
        }
        else if (_isAutoScrolling
            && _tracker.NeedsMoreSettlingTime
            && _clock.Now() < _autoScrollSettleDeadline)
        {
            _hudStatus = "页面仍在变化，等待稳定后继续";
        }
        else if (_isAutoScrolling && _tracker.AttemptsWithoutProgress > 0)
        {
            _hudStatus = $"等待页面响应 · 已重试 {_tracker.AttemptsWithoutProgress} / {_tracker.MaximumAttemptsWithoutProgress} 次";
        }
        else
        {
            _hudStatus = _isAutoScrolling
                ? "自动滚动中，正在识别新增内容"
                : "正在识别重叠区域与固定输入框";
        }

        PushHudStateLocked();
    }

    private void SetHudStatus(string status, bool sticky)
    {
        _hudStatus = status;
        _hudStatusSticky = sticky;
    }

    private void PushHudStateLocked()
    {
        _hud.Update(new LongCaptureHudState
        {
            IsPaused = _isPaused,
            IsAutoScrolling = _isAutoScrolling,
            AcceptedFrames = _acceptedFrames,
            SkippedFrames = _skippedFrames,
            PixelHeight = _stitcher.OutputPixelHeight,
            Status = _hudStatus,
            FinishEnabled = _acceptedFrames > 0,
        });
    }

    private void PresentSeamReview()
    {
        var segments = _stitcher.Segments
            .Select(s => new SeamReviewRow(
                s.Id, s.Direction, s.NewPixelHeight, s.Confidence, s.StableTopHeight, s.StableBottomHeight))
            .ToList();

        IReadOnlyList<RgbaBitmap> previewParts;
        try
        {
            previewParts = _stitcher.MakeImages(SeamReviewWindow.PreviewMaximumPixelHeight);
        }
        catch (StitchException ex)
        {
            FailCore(ex.Message);
            return;
        }

        var model = new SeamReviewModel
        {
            Segments = segments,
            PreviewParts = previewParts,
            OutputPixelHeight = _stitcher.OutputPixelHeight,
            LowConfidenceCount = _stitcher.LowConfidenceCount,
        };

        _seamReview.Present(
            model,
            onAdjust: (id, delta) =>
            {
                lock (_lock)
                {
                    if (!_stitcher.AdjustSegment(id, delta))
                    {
                        return;
                    }
                }

                RefreshSeamReview();
            },
            onConfirm: () =>
            {
                IReadOnlyList<RgbaBitmap> images;
                lock (_lock)
                {
                    try
                    {
                        images = _stitcher.MakeImages(SeamReviewWindow.ExportMaximumPixelHeight);
                    }
                    catch (StitchException ex)
                    {
                        FailCore(ex.Message);
                        return;
                    }
                }

                Complete(images, segments.Count(s => s.IsLowConfidence));
            },
            onCancel: () =>
            {
                lock (_lock)
                {
                    _stitcher.Reset();
                    _started = false;
                }

                Raise(Cancelled);
            });
    }

    private void RefreshSeamReview()
    {
        SeamReviewModel model;
        lock (_lock)
        {
            IReadOnlyList<RgbaBitmap> previewParts;
            try
            {
                previewParts = _stitcher.MakeImages(SeamReviewWindow.PreviewMaximumPixelHeight);
            }
            catch (StitchException ex)
            {
                model = new SeamReviewModel { ErrorMessage = ex.Message };
                _seamReview.Refresh(model);
                return;
            }

            model = new SeamReviewModel
            {
                Segments = _stitcher.Segments
                    .Select(s => new SeamReviewRow(
                        s.Id, s.Direction, s.NewPixelHeight, s.Confidence,
                        s.StableTopHeight, s.StableBottomHeight))
                    .ToList(),
                PreviewParts = previewParts,
                OutputPixelHeight = _stitcher.OutputPixelHeight,
                LowConfidenceCount = _stitcher.LowConfidenceCount,
            };
        }

        _seamReview.Refresh(model);
    }

    private void Complete(IReadOnlyList<RgbaBitmap> images, int reviewedSeams)
    {
        int accepted;
        int skipped;
        lock (_lock)
        {
            accepted = _acceptedFrames;
            skipped = _skippedFrames;
            ReleaseCapturedFramesLocked();
            _started = false;
        }

        Raise(() => Completed?.Invoke(
            new LongCaptureSessionResult(images, accepted, skipped, reviewedSeams)));
    }

    private void FailCore(string message)
    {
        CancelCore();
        Raise(() => Failed?.Invoke(message));
    }

    private void CancelLoop()
    {
        var cts = _cts;
        _cts = null;
        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }

        try
        {
            _loopTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 取消路径上的任务异常全部忽略。
        }

        _loopTask = null;
    }

    private void CancelCore()
    {
        CancelLoop();
        _hud.Close();
        _seamReview.Close();
        ReleaseCapturedFramesLocked();
    }

    private void ReleaseCapturedFramesLocked()
    {
        _stitcher.Reset();
        _viewportMotion.Reset();
        _bottomFrameMotion.Reset();
        _selection = default;
        _scrollTarget = null;
        _pendingScrollProgress = null;
    }

    private void OnHudButton(HudButton button)
    {
        switch (button)
        {
            case HudButton.AutoScroll:
                ToggleAutoScroll();
                break;
            case HudButton.Pause:
                TogglePause();
                break;
            case HudButton.Cancel:
                Cancel();
                break;
            case HudButton.Finish:
                Finish();
                break;
        }
    }

    private int ResolveDisplayId(CaptureSelection selection, LongCaptureSessionOptions options)
    {
        if (options.DisplayId is { } explicitId)
        {
            return explicitId;
        }

        // 按选区中心找所在显示器。
        var centerX = (int)Math.Round(selection.GlobalRect.MinX + selection.GlobalRect.Width / 2);
        var centerY = (int)Math.Round(selection.GlobalRect.MinY + selection.GlobalRect.Height / 2);

        foreach (var display in _capture.Displays)
        {
            var frame = display.Frame;
            if (centerX >= frame.MinX && centerX < frame.MaxX
                && centerY >= frame.MinY && centerY < frame.MaxY)
            {
                return display.Id;
            }
        }

        // 退化：主显示器。
        return _capture.Displays.FirstOrDefault(d => d.IsPrimary)?.Id ?? 0;
    }

    /// <summary>
    /// 事件统一在专用线程上抛出，避免 HUD/循环线程上的重入。
    /// </summary>
    private static void Raise(Action? action)
    {
        if (action is null)
        {
            return;
        }

        try
        {
            Task.Run(action);
        }
        catch
        {
            // 事件订阅方异常不回流到调用线程。
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelCore();
        if (_hud is IDisposable disposableHud)
        {
            disposableHud.Dispose();
        }

        if (_seamReview is IDisposable disposableReview)
        {
            disposableReview.Dispose();
        }
    }
}
