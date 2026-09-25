using Ta.Shell.Contracts;
using Ta.Shell.Models;
using Ta.Core.Capture;
using Ta.Core.Imaging;
using Ta.Shell.UI;

namespace Ta.Shell.Orchestration;

/// <summary>
/// 主捕获链路。对应 Mac 版 <c>CaptureCoordinator</c>（CaptureCoordinator.swift, 973 行）。
///
/// # 关键设计：先冻结整屏，再框选（移植参考文档 §3.1）
///
/// Mac 版 CaptureCoordinator.swift:162-164 的源码注释说明了这个设计：
/// <code>
/// "The overlay is shown only after the full display frame has been captured,
///  so the user always selects from the shortcut-time image rather than a live page."
/// </code>
///
/// 移植必须保留这条链路：
/// <code>
/// 快捷键按下
///   → 立即变十字光标          ← 在覆盖层出现之前
///   → 捕获整屏（冻结）        ← CaptureDisplay，全屏
///   → 显示覆盖层（背景 = 冻结帧）
///   → 用户框选
///   → 从冻结帧裁剪            ← CropFrozen，不再二次实时捕获
///   → 动作分派
/// </code>
///
/// 两个行为契约：
///   1. 覆盖层无闪烁（背景是位图而非实时画面）；
///   2. 选区内容与快捷键按下瞬间一致。
///
/// # 为什么这个类可以单测
///
/// 依赖全部是接口：<see cref="Ta.Core.Capture.IScreenCapture"/>（捕获）、
/// <see cref="ISelectionOverlay"/>（覆盖层）、<see cref="CaptureActionRouter"/>（动作分派）、
/// <see cref="IPointerCursor"/>（光标）、<see cref="IAppWindowVisibility"/>（自身窗口隐藏）。
/// 测试注入内存假实现即可跑完整条链路，不需要真截图、真窗口。
/// </summary>
public sealed class CaptureCoordinator
{
    private readonly IScreenCapture _capture;
    private readonly ISelectionOverlay _overlay;
    private readonly CaptureActionRouter _router;
    private readonly IClipboardService _clipboard;
    private readonly ILongCaptureSession? _longSession;
    private readonly IResultBarSink? _resultBar;
    private readonly ISettingsStore? _settings;
    private readonly IPointerCursor? _cursor;
    private readonly IAppWindowVisibility? _appWindows;
    private readonly Func<ISelectionOverlay>? _overlayFactory;
    // 非空：构造函数里已用 `captureOnBackground ?? 默认实现` 保证一定赋值。
    private readonly Func<int, double, Task<RgbaBitmap>> _captureOnBackground;

    private CancellationTokenSource? _activeJob;
    private int _jobCounter;

    public CaptureCoordinator(
        IScreenCapture capture,
        ISelectionOverlay overlay,
        CaptureActionRouter router,
        IClipboardService clipboard,
        ILongCaptureSession? longSession = null,
        IResultBarSink? resultBar = null,
        ISettingsStore? settings = null,
        IPointerCursor? cursor = null,
        IAppWindowVisibility? appWindows = null,
        Func<ISelectionOverlay>? overlayFactory = null,
        Func<int, double, Task<RgbaBitmap>>? captureOnBackground = null)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _longSession = longSession;
        _resultBar = resultBar;
        _settings = settings;
        _cursor = cursor;
        _appWindows = appWindows;
        _overlayFactory = overlayFactory;

        // 冻结整屏是重操作（WGC 要起停帧池，1–2 帧延迟）。必须真 await：
        // 曾经的实现是 Task.Run(...).GetResult() 在 STA 主线程上阻塞等待 ——
        // WGC 的帧回调/DWM 合成依赖消息泵，主线程阻塞会直接 AccessViolation
        // 杀死进程（「点击开始截图」第二层根因）。测试可注入假实现去掉抖动。
        _captureOnBackground = captureOnBackground
            ?? ((displayId, pixelScale) => Task.Run(() => _capture.CaptureDisplay(displayId, pixelScale)));
    }

    /// <summary>最近一次任务的 jobId；0 表示还没有。用于竞态判定的 jobIsLatest。</summary>
    public int LatestJobId { get; private set; }

    /// <summary>
    /// 开始一次截图。对应 Mac 版 <c>CaptureCoordinator.start(mode:completion:)</c>
    /// （CaptureCoordinator.swift:141-215）。
    /// </summary>
    public async Task<CaptureOutcome> StartAsync(
        CaptureMode mode,
        CancellationToken cancellationToken = default)
    {
        // 1. 取消上一个进行中的任务（:142）。
        //    Mac 版用 processingTask?.cancel()；这里用 CTS 保证新任务开始时旧任务一定停下。
        CancelActiveJob();

        // 1.5 收起结果窗（含置顶等待窗），并留一拍给 DWM 完成合成 ——
        // 不收起的话置顶窗会遮挡冻结帧：用户框选区域被窗口盖住 → 截到窗口自己的
        // 空白纸底 → OCR「选区内明明有汉字却识别不出」（dump 图实锤）。
        PrepareOverlayCapture();

        // 2. 权限门槛（:144 / :951-972）。失败则显示失败结果条并中止。
        if (!EnsurePermission())
        {
            return CaptureOutcome.Failed("需要屏幕录制权限");
        }

        // 3. .long 分流到长截图会话（:148-151），不经动作分派。
        if (mode == CaptureMode.Long)
        {
            return await StartLongCaptureAsync(cancellationToken);
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeJob = linked;
        var jobId = ++_jobCounter;
        LatestJobId = jobId;
        var token = linked.Token;

        try
        {
            var settings = _settings is null ? new CaptureSettings() : CaptureSettings.Load(_settings);
            var plan = ModeActionMapper.PlanFor(mode, settings);

            // 4. prepareStartContext()：鼠标所在屏 + 显示 ID + 源应用进程 + 吸附目标（:157）。
            //    `is not { }` 一次完成 null 检查与不可空窄化。
            var overlay = ResolveOverlay();
            if (ResolveOverlay().PrepareStartContext() is not { } context)
            {
                Show(ResultBarKind.Failure, "找不到显示器", "请检查显示设置后重试", autoHide: false);
                return CaptureOutcome.Failed("找不到显示器");
            }

            // 记录处理开始前的剪贴板序号（:155）。
            // 由 router 在提交时读取「当前」序号并与它比对 —— 这就是竞态保护的凭据。
            var ticket = CaptureTicket();

            // 5. 立即设十字光标 —— 覆盖层出现之前（:165）。
            _cursor?.ShowCrosshair();

            // 6. 冻结整屏（:169-173）。
            RgbaBitmap frozenDisplay;
            var frozenDisplayInfo = context.Display;
            try
            {
                frozenDisplay = await _captureOnBackground(
                    frozenDisplayInfo.Id, frozenDisplayInfo.PixelScale);
            }
            catch (Exception error)
            {
                Program.Log($"冻结整屏失败: {error.GetType().Name}: {error.Message}");
                _cursor?.RestoreArrow();
                Show(ResultBarKind.Failure, "截图准备失败", error.Message, autoHide: false);
                return CaptureOutcome.Failed("截图准备失败");
            }

            token.ThrowIfCancellationRequested();

            // 任务已被更新任务取代（:175 的 guard latestJobID == jobID）。
            if (LatestJobId != jobId)
            {
                _cursor?.RestoreArrow();
                return CaptureOutcome.Cancelled();
            }

            // 7. 显示覆盖层（:177-181）。
            //    显示操作栏的条件是「没有预设动作」—— showsActionToolbar: configuredAction == nil。
            //    这里同时把 Ta 自己的窗口藏起来（任务书 E 项）。
            _appWindows?.HideForCapture();

            SelectionResult selectionResult;
            try
            {
                selectionResult = await ResolveOverlay().BeginAsync(
                    new SelectionRequest(
                        context,
                        frozenDisplay,
                        showsActionToolbar: plan.ShowsActionToolbar),
                    token);
            }
            finally
            {
                _appWindows?.RestoreAfterCapture();
                _cursor?.RestoreArrow();
            }

            token.ThrowIfCancellationRequested();

            if (selectionResult.WasCancelled)
            {
                // 取消不产生文件、也不修改剪贴板 —— 产品承诺。
                return CaptureOutcome.Cancelled();
            }

            // 8. 完成后从**冻结帧**裁剪，不再二次实时捕获（§3.1 的核心）。
            var selection = new CaptureSelection
            {
                GlobalRect = selectionResult.Selection,
                ScreenFrame = context.Display.Frame,
                BackingScaleFactor = context.Display.PixelScale,
                FrozenDisplayImageWidth = frozenDisplay.Width,
                FrozenDisplayImageHeight = frozenDisplay.Height,
            };

            RgbaBitmap cropped;
            try
            {
                cropped = _capture.CropFrozen(frozenDisplay, selection);
            }
            catch (Exception error)
            {
                Show(ResultBarKind.Failure, "截图失败", error.Message, autoHide: false);
                return CaptureOutcome.Failed("截图失败");
            }

            // 9. action = 工具栏选择 ?? 模式配置 ?? .copyImage，并持久化 lastPostCaptureQuickAction（:188-189）。
            var action = ModeActionMapper.ResolveAction(
                selectionResult.SelectedAction,
                plan.Action);

            _settings?.Write(SettingKeys.LastPostCaptureQuickAction, action.RawValue());

            // edit 动作下覆盖层必须存活到编辑器关闭之后（§5.6）。
            var deferDismissal = action == CaptureQuickAction.Edit;
            if (!deferDismissal)
            {
                ResolveOverlay().Dismiss();
            }

            var outcome = await _router.ExecuteAsync(
                new CaptureActionRouter.ActionRequest(action, cropped, selection, ticket, LatestJobId == jobId, jobId),
                token);

            if (deferDismissal)
            {
                ResolveOverlay().Dismiss();
            }

            return outcome;
        }
        catch (OperationCanceledException)
        {
            _resultBar?.Hide();
            return CaptureOutcome.Cancelled();
        }
        catch (Exception error)
        {
            Show(ResultBarKind.Failure, "截图失败", error.Message, autoHide: false);
            return CaptureOutcome.Failed("截图失败");
        }
        finally
        {
            ReleaseActiveJob(linked);
        }
    }

    /// <summary>
    /// 固定测试区域截图。对应 Mac 版 <c>startFixedTestRegion</c>
    /// （CaptureCoordinator.swift:217-259），由 <c>--capture-fixed</c> 触发。
    ///
    /// 这条路径<b>不显示覆盖层</b> —— 直接取主屏中部的一块固定区域跑完整条处理链，
    /// 用于自动化冒烟。
    /// </summary>
    public async Task<CaptureOutcome> StartFixedTestRegionAsync(
        CaptureMode mode,
        CancellationToken cancellationToken = default)
    {
        CancelActiveJob();

        if (!EnsurePermission())
        {
            return CaptureOutcome.Failed("需要屏幕录制权限");
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeJob = linked;
        var jobId = ++_jobCounter;
        LatestJobId = jobId;

        try
        {
            // `is not { } display` 的写法同时完成 null 检查与不可空窄化 ——
            // 比 `if (x is null) return;` 更让编译器满意，也不用引入额外变量。
            if (PrimaryDisplay() is not { } display)
            {
                return CaptureOutcome.Failed("找不到显示器");
            }

            var settings = _settings is null ? new CaptureSettings() : CaptureSettings.Load(_settings);
            var action = ModeActionMapper.ConfiguredActionFor(mode, settings)
                         ?? CaptureQuickAction.LocalOcr;

            // 固定区域：主屏中部，与 Mac 版 :236-246 同比例（12% 左、宽 58%、高 28%）。
            var frame = display.Frame;
            var globalRect = new RectD(
                frame.X + (frame.Width * 0.12),
                frame.Y + (frame.Height * 0.34),
                Math.Min(frame.Width * 0.58, 680),
                Math.Min(frame.Height * 0.28, 210));

            var selection = new CaptureSelection
            {
                GlobalRect = globalRect,
                ScreenFrame = frame,
                BackingScaleFactor = display.PixelScale,
            };

            RgbaBitmap frozen;
            var frozenFrom = display;
            try
            {
                frozen = await _captureOnBackground(frozenFrom.Id, frozenFrom.PixelScale);
            }
            catch (Exception error)
            {
                Show(ResultBarKind.Failure, "截图准备失败", error.Message, autoHide: false);
                return CaptureOutcome.Failed("截图准备失败");
            }

            RgbaBitmap cropped;
            try
            {
                cropped = _capture.CropFrozen(frozen, selection);
            }
            catch (Exception error)
            {
                Show(ResultBarKind.Failure, "截图失败", error.Message, autoHide: false);
                return CaptureOutcome.Failed("截图失败");
            }

            return await _router.ExecuteAsync(
                new CaptureActionRouter.ActionRequest(
                    action, cropped, selection, CaptureTicket(), LatestJobId == jobId, jobId),
                linked.Token);
        }
        catch (OperationCanceledException)
        {
            _resultBar?.Hide();
            return CaptureOutcome.Cancelled();
        }
        catch (Exception error)
        {
            Show(ResultBarKind.Failure, "截图失败", error.Message, autoHide: false);
            return CaptureOutcome.Failed("截图失败");
        }
        finally
        {
            ReleaseActiveJob(linked);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 长截图。对应 Mac 版 startLongCapture（:820-907）
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<CaptureOutcome> StartLongCaptureAsync(CancellationToken cancellationToken)
    {
        if (_longSession is null)
        {
            Show(ResultBarKind.Failure, "长截图不可用",
                "长截图会话尚未接入（子系统仍在开发中）", autoHide: false);
            return CaptureOutcome.Failed("长截图不可用");
        }

        CancelActiveJob();

        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeJob = linked;
        var jobId = ++_jobCounter;
        LatestJobId = jobId;

        try
        {
            _cursor?.ShowCrosshair();
            _appWindows?.HideForCapture();

            SelectionResult selectionResult;
            try
            {
                selectionResult = await ResolveOverlay().BeginAsync(
                    new SelectionRequest(
                        ResolveOverlay().PrepareStartContext()
                        ?? throw new InvalidOperationException("找不到显示器"),
                        frozenDisplayImage: null,

                        // 长截图不显示操作栏 —— 对应 Mac 版 :825-827。
                        showsActionToolbar: false),
                    linked.Token);
            }
            finally
            {
                _appWindows?.RestoreAfterCapture();
                _cursor?.RestoreArrow();
            }

            ResolveOverlay().Dismiss();

            if (selectionResult.WasCancelled)
            {
                return CaptureOutcome.Cancelled();
            }

            var display = _capture.Displays.FirstOrDefault(d => d.IsPrimary)
                          ?? _capture.Displays.FirstOrDefault();

            var selection = new CaptureSelection
            {
                GlobalRect = selectionResult.Selection,
                ScreenFrame = display?.Frame ?? new RectD(0, 0, 1920, 1080),
                BackingScaleFactor = display?.PixelScale ?? 1,
            };

            Show(ResultBarKind.Processing, "长截图已开始",
                "以选区顶部为起点，拓会自动锁定滚动区域并持续采集到底部", autoHide: true);

            var ticket = CaptureTicket();
            var sessionOutcome = await _longSession.RunAsync(selection, linked.Token);

            switch (sessionOutcome)
            {
                case var cancelled when cancelled.WasCancelled:
                    Show(ResultBarKind.Warning, "已取消长截图", "采集帧已从内存清除", autoHide: true);
                    return CaptureOutcome.Cancelled();

                case var failed when failed.FailureMessage is not null:
                    Show(ResultBarKind.Failure, "长截图失败", failed.FailureMessage, autoHide: false);
                    return CaptureOutcome.Failed("长截图失败");
            }

            if (sessionOutcome.Images.Count == 0)
            {
                return CaptureOutcome.Failed("长截图没有生成图片");
            }

            return await FinishLongCaptureAsync(sessionOutcome, ticket, LatestJobId == jobId, jobId, linked.Token);
        }
        catch (OperationCanceledException)
        {
            _resultBar?.Hide();
            return CaptureOutcome.Cancelled();
        }
        catch (Exception error)
        {
            Program.Log($"截图主链异常: {error.Message}");
            Show(ResultBarKind.Failure, "长截图失败", error.Message, autoHide: false);
            return CaptureOutcome.Failed("长截图失败");
        }
        finally
        {
            ReleaseActiveJob(linked);
        }
    }

    /// <summary>
    /// 长截图收尾：复制第 1 段到剪贴板 + 汇报统计。
    /// 对应 Mac 版 :858-903。
    ///
    /// 与 Mac 版的差异：Mac 版在这里还调用 <c>ImageExportService.save(images)</c>
    /// 把分段落盘。落盘需要 <see cref="IScreenshotExporter"/>，
    /// 而本任务的边界是「编排」—— 导出器仍由其他模块提供，
    /// 因此当前只做复制与汇报，保存留待导出器接入（见报告的「仍是桩」一节）。
    /// </summary>
    private async Task<CaptureOutcome> FinishLongCaptureAsync(
        Contracts.LongCaptureOutcome session,
        ClipboardCommitPolicy.ClipboardCommitTicket ticket,
        bool jobIsLatest,
        int jobId,
        CancellationToken cancellationToken)
    {
        var first = session.Images[0];

        // 复制第一段。走 router 的 CopyImage 分支 —— 它自带竞态保护与文案。
        var copyOutcome = await _router.ExecuteAsync(
            new CaptureActionRouter.ActionRequest(
                CaptureQuickAction.CopyImage, first, default, ticket, jobIsLatest, jobId),
            cancellationToken);

        var totalHeight = session.Images.Sum(i => i.Height);
        var detail = $"{first.Width} × {totalHeight} · {session.AcceptedFrames} 个有效画面";

        if (session.SkippedFrames > 0)
        {
            detail += $" · 跳过 {session.SkippedFrames} 帧";
        }

        if (session.ReviewedSeams > 0)
        {
            detail += $" · {session.ReviewedSeams} 个低置信度接缝";
        }

        if (session.Images.Count > 1)
        {
            detail += $" · 已分为 {session.Images.Count} 段";
        }

        // 对应 Mac 版 :884-886 —— 复制成功与否都要如实写进 detail。
        detail += copyOutcome.Kind == CaptureOutcomeKind.Completed
            ? (session.Images.Count > 1 ? " · 已复制第 1 段" : " · 已复制")
            : " · 未覆盖已变化的剪贴板";

        Show(ResultBarKind.Success, "长截图已生成", detail, autoHide: true);
        return CaptureOutcome.Completed("长截图已生成");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UI 冒烟夹具。对应 Mac 版 :36-118
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>操作栏冒烟。对应 openCaptureToolbarSmokeFixture（:88-103）。</summary>
    public async Task<CaptureOutcome> OpenCaptureToolbarSmokeAsync(CancellationToken cancellationToken)
    {
        var display = _capture.Displays.FirstOrDefault(d => d.IsPrimary)
                      ?? _capture.Displays.FirstOrDefault();

        if (display is null)
        {
            return CaptureOutcome.Failed("找不到显示器");
        }

        var frame = display.Frame;
        var preset = new RectD(
            frame.X + (frame.Width * 0.18),
            frame.Y + (frame.Height * 0.34),
            frame.Width * 0.64,
            frame.Height * 0.42);

        _appWindows?.HideForCapture();
        try
        {
            var result = await ResolveOverlay().BeginAsync(
                new SelectionRequest(
                    ResolveOverlay().PrepareStartContext()
                    ?? new OverlayStartContext(display),
                    frozenDisplayImage: null,
                    showsActionToolbar: true,
                    presetSelection: preset),
                cancellationToken);

            ResolveOverlay().Dismiss();
            return result.WasCancelled ? CaptureOutcome.Cancelled() : CaptureOutcome.Completed("操作栏冒烟完成");
        }
        finally
        {
            _appWindows?.RestoreAfterCapture();
        }
    }

    /// <summary>结果条冒烟。对应 openResultBarSmokeFixture（:105-117）。</summary>
    public async Task<CaptureOutcome> OpenResultBarSmokeAsync(
        ResultBarKind kind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var (title, detail) = kind switch
        {
            ResultBarKind.Processing => ("正在识别图片…", "本地处理，不会上传"),
            ResultBarKind.Success => ("已复制图片", "910 × 358"),
            ResultBarKind.Warning => ("已复制，部分文字可能有误", "请检查识别结果"),
            _ => ("复制失败", "剪贴板被其他应用占用"),
        };

        // Mac 版用 autoHide: false + dismissalOverrideSeconds: 60 —— 冒烟要能看清。
        Show(kind, title, detail, autoHide: false, overrideSeconds: 60);

        await Task.CompletedTask;
        return CaptureOutcome.Completed("结果条冒烟完成");
    }

    /// <summary>独立标注编辑器冒烟。对应 openEditorSmokeFixture（:36-39）。</summary>
    public async Task<CaptureOutcome> OpenEditorSmokeAsync(CancellationToken cancellationToken)
    {
        var bitmap = MakeSmokeFixtureImage();
        try
        {
            var outcome = await _router.ExecuteAsync(
                new CaptureActionRouter.ActionRequest(
                    CaptureQuickAction.Edit, bitmap, default,
                    CaptureTicket(), true, ++_jobCounter),
                cancellationToken);

            return outcome;
        }
        finally
        {
            // 假实现不需要释放；真实位图由 GC 回收（RgbaBitmap.Dispose 是空实现）。
        }
    }

    /// <summary>原位标注编辑器冒烟。对应 openInlineEditorSmokeFixture（:41-86）。</summary>
    public async Task<CaptureOutcome> OpenInlineEditorSmokeAsync(CancellationToken cancellationToken)
    {
        var bitmap = MakeSmokeFixtureImage();
        return await _router.ExecuteAsync(
            new CaptureActionRouter.ActionRequest(
                CaptureQuickAction.Edit, bitmap, default,
                CaptureTicket(), true, ++_jobCounter),
            cancellationToken);
    }

    /// <summary>
    /// 冒烟用的合成图：纯色 + 一行说明文字。
    /// 对应 Mac 版 makeSmokeFixtureImage（:119-139）—— Mac 版画的是 1000×620 的
    /// 标注示例图，Windows 侧尺寸保持一致但用纯色填充（标注子系统不归本任务范围）。
    /// </summary>
    private static RgbaBitmap MakeSmokeFixtureImage()
    {
        var bitmap = new RgbaBitmap(1000, 620);
        bitmap.Fill(250, 246, 238);
        return bitmap;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 内部
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 权限门槛。对应 Mac 版 ensureScreenCapturePermission（:951-972）。
    ///
    /// Windows 没有等价于 <c>CGPreflightScreenCaptureAccess</c> 的静态检查，
    /// 因此 <see cref="Ta.Core.Capture.IScreenCapture.HasPermission"/> 由平台层决定语义
    /// （参考文档 §13）。未授权时显示失败结果条并中止。
    /// </summary>
    private bool EnsurePermission()
    {
        if (_capture.HasPermission)
        {
            return true;
        }

        if (!_capture.RequestPermission())
        {
            Show(ResultBarKind.Failure, "需要屏幕录制权限",
                "请在系统设置中允许「拓」后重试", autoHide: false);
            return false;
        }

        return true;
    }

    /// <summary>
    /// 取当前覆盖层实例。
    ///
    /// 真覆盖层（<c>Ta.Platform.Overlay.SelectionOverlay</c>）是<b>一次性</b>的：
    /// 它在一个独立线程上创建窗口、跑消息循环，完成后窗口销毁。
    /// 因此每次会话都要一个新实例 —— 这正是需要 <c>_overlayFactory</c> 的原因。
    /// 注入的单一 <see cref="_overlay"/> 只用于「脚本化假覆盖层」的测试场景。
    /// </summary>
    private ISelectionOverlay ResolveOverlay() => _overlayFactory?.Invoke() ?? _overlay;

    /// <summary>
    /// 取当前剪贴板序号，构造竞态凭据。
    ///
    /// 对应 Mac 版 :155 的 <c>let initialChangeCount = clipboardService.changeCount</c>
    /// —— 在覆盖层显示<b>之前</b>取样。这样处理期间用户复制了别的内容，
    /// 提交时序号对不上，就能被 <see cref="ClipboardCommitPolicy"/> 拦下。
    /// </summary>
    private ClipboardCommitPolicy.ClipboardCommitTicket CaptureTicket() =>
        new(_clipboard.ChangeCount);

    /// <summary>
    /// 主显示器；没有主屏标记时退回第一块。
    /// 对应 Mac 版 <c>NSScreen.main ?? NSScreen.screens.first</c> 的兜底链。
    /// </summary>
    private DisplayInfo? PrimaryDisplay() =>
        _capture.Displays.FirstOrDefault(d => d.IsPrimary)
        ?? _capture.Displays.FirstOrDefault();

    /// <summary>
    /// 覆盖层截图前的准备：收起结果窗，并留一拍让 DWM 完成合成。
    /// 结果窗是置顶窗（用户要求切屏不消失），不收起的话会**遮挡冻结帧**：
    /// 用户框选的区域被窗口盖住 → 截到的是窗口自己的空白 → OCR「选区内明明有汉字却识别不出」。
    /// 120ms 是 DWM 窗口消失的合成延迟余量（与 WGC 收尾同一量级）。
    /// </summary>
    private void PrepareOverlayCapture()
    {
        _router.PrepareForCapture();
        Thread.Sleep(120);
    }

    private void CancelActiveJob()
    {
        var active = _activeJob;
        _activeJob = null;

        if (active is null)
        {
            return;
        }

        try
        {
            active.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经在 finally 里释放过了。
        }
    }

    private void ReleaseActiveJob(CancellationTokenSource source)
    {
        if (ReferenceEquals(_activeJob, source))
        {
            _activeJob = null;
        }

        source.Dispose();
    }

    private void Show(
        ResultBarKind kind,
        string title,
        string? detail,
        bool autoHide,
        double? overrideSeconds = null)
    {
        _resultBar?.Show(
            new ResultBarState(kind, title, detail),
            autoHide
                ? ResultBarDisplayOptions.Auto(overrideSeconds)
                : ResultBarDisplayOptions.Sticky(overrideSeconds));
    }
}
