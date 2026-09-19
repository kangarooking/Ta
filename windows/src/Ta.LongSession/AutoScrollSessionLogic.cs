using Ta.Core.Capture;
using Ta.Core.LongCapture;

namespace Ta.LongSession;

/// <summary>
/// 长截图会话的**纯决策层**：自动滚动尝试状态机 + 底部确认 + 步长规划 + HUD 定位。
///
/// 逐行对应 Mac 版 ScrollingCaptureSessionController.swift 的静态方法
/// （:269-350 的 recommendedScrollDistance / resolveAutoScrollAttempt /
/// advanceBottomConfirmation / shouldCompleteAfterNoProgress /
/// autoScrollObservation）。会话控制器本身负责 I/O，本类负责判定，
/// 因此状态机的每一个分支都可以脱离 Win32 / UIA 直接单测。
///
/// 时间单位统一用「双精度秒」，与任何时钟解耦（测试里可直接传 0、1.2、…）。
/// </summary>
public static class AutoScrollSessionLogic
{
    // ---- 常量总表（对应 Windows移植参考文档 §8.1） ----

    /// <summary>自动滚动捕获循环间隔（毫秒）。对应 Mac: :35。</summary>
    public const int StandardAutoScrollDelayMilliseconds = 300;

    /// <summary>自动滚动间隔（秒）。对应 Mac: :36。</summary>
    public const double StandardAutoScrollDelay = 0.30;

    /// <summary>手势之后等待视口稳定的最长时间（秒）。对应 Mac: :37。</summary>
    public const double MaximumAutoScrollSettleTime = 1.20;

    /// <summary>停止自动滚动所需的连续底部确认帧数。对应 Mac: :38。</summary>
    public const int RequiredBottomConfirmationFrames = 3;

    /// <summary>手动模式循环间隔（毫秒）。对应 Mac: :264。</summary>
    public const int ManualCaptureLoopDelayMilliseconds = 420;

    /// <summary>推荐步长下限（点）。对应 Mac: :269-271 的 max(120, …)。</summary>
    public const int RecommendedScrollDistanceFloor = 120;

    /// <summary>推荐步长上限（点）。对应 Mac: :269-271 的 min(460, …)。</summary>
    public const int RecommendedScrollDistanceCeiling = 460;

    /// <summary>推荐步长占选区高度的比例。对应 Mac: 0.42。</summary>
    public const double RecommendedScrollDistanceFraction = 0.42;

    /// <summary>重试步长的绝对下限（点）。对应 Mac: :206-209 的 max(96, …)。</summary>
    public const int RetryScrollDistanceFloor = 96;

    /// <summary>无进度重试次数上限。对应 Mac: :31。</summary>
    public const int MaximumAttemptsWithoutProgress = 3;

    /// <summary>滚轮事件分块的最大像素增量。对应 Mac: AccessibilityAutoScrollService:40。</summary>
    public const int MaximumPixelDeltaPerEvent = 32;

    /// <summary>滚轮事件之间的间隔（毫秒）。对应 Mac: :41。</summary>
    public const int ScrollEventIntervalMilliseconds = 14;

    /// <summary>底部信号之后不再发新手势时使用的"无限未来"时间戳（秒）。</summary>
    public const double DistantFuture = double.MaxValue / 4;

    /// <summary>开始捕获（尚未结算任何手势）时使用的"无限过去"时间戳（秒）。</summary>
    public const double DistantPast = double.MinValue / 4;

    /// <summary>
    /// 推荐滚动步长：随选区高度缩放并夹取。
    /// 对应 Mac: min(460, max(120, Int(globalRect.height * 0.42)))。
    /// </summary>
    public static int RecommendedScrollDistance(double selectionHeightPoints) =>
        (int)Math.Clamp(
            Math.Round(selectionHeightPoints * RecommendedScrollDistanceFraction),
            RecommendedScrollDistanceFloor,
            RecommendedScrollDistanceCeiling);

    /// <summary>
    /// 自动滚动尝试状态机。对应 Mac: resolveAutoScrollAttempt（:273-295）。
    ///
    /// <code>
    /// noPending:  .appended → .progress   否则 → .waiting
    /// pending:    .appended → .progress
    ///            AX 进度前进 → .progress
    ///            motion.hasReference &amp;&amp; !stationary → .progress   // "不确定"，不是"无移动"
    ///            now &lt; settleDeadline → .waiting
    ///            否则 → .noMovement
    /// </code>
    /// </summary>
    public static AutoScrollAttemptResolution ResolveAutoScrollAttempt(
        AutoScrollProgressTracker progress,
        FrameDisposition observation,
        ViewportMotionMeasurement motion,
        ScrollProgress? scrollProgressBefore = null,
        ScrollProgress? scrollProgressAfter = null,
        double now = 0,
        double settleDeadline = 0)
    {
        if (!progress.HasPendingScroll)
        {
            return observation == FrameDisposition.Appended
                ? AutoScrollAttemptResolution.Progress
                : AutoScrollAttemptResolution.Waiting;
        }

        if (observation == FrameDisposition.Appended)
        {
            return AutoScrollAttemptResolution.Progress;
        }

        if (ProgressAdvanced(scrollProgressBefore, scrollProgressAfter))
        {
            return AutoScrollAttemptResolution.Progress;
        }

        // 可见移动但接缝尚未被接受 —— 这是"不确定"，绝不能算"无移动"。
        if (motion.HasReference && !motion.IsStationary)
        {
            return AutoScrollAttemptResolution.Progress;
        }

        if (now < settleDeadline)
        {
            return AutoScrollAttemptResolution.Waiting;
        }

        return AutoScrollAttemptResolution.NoMovement;
    }

    /// <summary>
    /// 底部确认计数。对应 Mac: advanceBottomConfirmation（:297-313）。
    ///
    /// 滚动条可能先于滚动动画到达最大值，只有**连续稳定**的底部帧才确认完成。
    /// </summary>
    public static int AdvanceBottomConfirmation(
        int currentCount,
        FrameDisposition observation,
        ViewportMotionMeasurement? motion,
        ScrollProgress? scrollProgressAfter,
        int requiredCount = RequiredBottomConfirmationFrames)
    {
        if (scrollProgressAfter?.IsAtEnd != true || requiredCount <= 0)
        {
            return 0;
        }

        if (observation == FrameDisposition.Appended)
        {
            return 0;
        }

        if (motion?.HasReference != true || motion?.IsStationary != true)
        {
            return 0;
        }

        return Math.Min(requiredCount, currentCount + 1);
    }

    /// <summary>
    /// 无进度预算耗尽后，究竟该收尾还是该换更小步长重试。
    /// 对应 Mac: shouldCompleteAfterNoProgress（:315-325）。
    /// </summary>
    public static bool ShouldCompleteAfterNoProgress(
        AutoScrollProgressTracker progress,
        ScrollTargetMode targetMode,
        ScrollProgress? currentScrollProgress)
    {
        if (!progress.ShouldStop)
        {
            return false;
        }

        if (currentScrollProgress is { } current)
        {
            return current.IsAtEnd;
        }

        return targetMode == ScrollTargetMode.EventFallback;
    }

    /// <summary>
    /// 把拼接结果与视口运动合成一个观测值。对应 Mac: autoScrollObservation（:336-350）。
    /// </summary>
    public static FrameDisposition AutoScrollObservation(
        FrameDisposition stitchDisposition,
        ViewportMotionMeasurement motion)
    {
        if (stitchDisposition == FrameDisposition.Appended)
        {
            return stitchDisposition;
        }

        if (!motion.HasReference)
        {
            return stitchDisposition;
        }

        if (motion.IsStationary)
        {
            return FrameDisposition.Duplicate;
        }

        // 视口明显动了，但接缝匹配还没接受稳定帧 —— 不确定，不是到底。
        return FrameDisposition.Rejected;
    }

    /// <summary>无障碍进度是否前进。对应 Mac: progressAdvanced（:187-194）。</summary>
    public static bool ProgressAdvanced(ScrollProgress? before, ScrollProgress? after)
    {
        if (before is null || after is null)
        {
            return false;
        }

        return after.NormalizedValue > before.NormalizedValue + ScrollProgress.ProgressAdvancedTolerance;
    }

    /// <summary>
    /// 大手势分块：把请求的像素增量拆成 ≤ 32px 的事件序列。
    /// 对应 Mac: pixelDeltas(for:)（:196-206），测试锁定
    /// <c>pixelDeltas(130) == [-32,-32,-32,-32,-2]</c>、<c>pixelDeltas(-65) == [32,32,1]</c>。
    ///
    /// 负值 = 向下（内容上移），与 Mac 的 wheel1 符号一致。
    /// </summary>
    public static IReadOnlyList<int> PixelDeltas(int requestedPixels)
    {
        var remaining = Math.Max(1, Math.Abs(requestedPixels));
        var deltas = new List<int>();
        var sign = requestedPixels >= 0 ? -1 : 1;

        while (remaining > 0)
        {
            var step = Math.Min(MaximumPixelDeltaPerEvent, remaining);
            deltas.Add(sign * step);
            remaining -= step;
        }

        return deltas;
    }

    // ---- HUD 定位（纯数学，对应 Mac: showHUD(near:) :352-390） ----

    /// <summary>HUD 面板尺寸。对应 Mac: CGSize(width: 520, height: 124)。</summary>
    public static readonly (int Width, int Height) HudPanelSize = (520, 124);

    /// <summary>HUD 与可见区域 / 选区之间的边距（点）。对应 Mac: 12 / 8。</summary>
    public const double HudScreenMargin = 12;

    /// <summary>HUD 垂直方向的额外安全边距（点）。对应 Mac: visible.minY + 8。</summary>
    public const double HudVerticalSafetyMargin = 8;

    /// <summary>
    /// HUD 位置：选区水平居中、夹到可见区域 ±12；
    /// 垂直优先放选区下方 12pt，放不下则放上方，再放不下则贴可见区底部。
    /// 对应 Mac: :366-373。
    /// </summary>
    public static (double X, double Y) ComputeHudPosition(RectD selection, RectD visibleFrame)
    {
        var (panelWidth, panelHeight) = HudPanelSize;

        var x = Math.Clamp(
            selection.MinX + selection.Width / 2 - panelWidth / 2.0,
            visibleFrame.MinX + HudScreenMargin,
            visibleFrame.MaxX - panelWidth - HudScreenMargin);

        var below = selection.MinY - panelHeight - HudScreenMargin;
        var y = below >= visibleFrame.MinY + HudVerticalSafetyMargin
            ? below
            : Math.Min(
                visibleFrame.MaxY - panelHeight - HudVerticalSafetyMargin,
                selection.MaxY + HudScreenMargin);

        return (x, y);
    }
}
