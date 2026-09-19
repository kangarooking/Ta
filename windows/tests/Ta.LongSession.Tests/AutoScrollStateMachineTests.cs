using Ta.Core.Capture;
using Ta.Core.LongCapture;
using Ta.LongSession;

namespace Ta.LongSession.Tests;

/// <summary>
/// 状态机测试 —— 逐条移植 Mac 版
/// Tests\AIScreenshotAppTests\ScrollingCaptureSessionControllerTests.swift。
///
/// Mac 的静态方法挂在 ScrollingCaptureSessionController 上；Windows 侧同样
/// 的判定全部抽到纯逻辑类 <see cref="AutoScrollSessionLogic"/>，
/// 因此这里不触碰任何 Win32 / UIA。
/// </summary>
public sealed class AutoScrollStateMachineTests
{
    private static ViewportMotionMeasurement Moving() =>
        new(true, 24, 0.6, false);

    private static ViewportMotionMeasurement Stationary() =>
        new(true, 0.4, 0.002, true);

    private static ViewportMotionMeasurement NoReference() =>
        new(false, 0, 0, false);

    // ── 步长规划 ──────────────────────────────────────────────────

    [Fact]
    public void RecommendedScrollDistanceScalesWithSelectionAndStaysBounded()
    {
        Assert.Equal(120, AutoScrollSessionLogic.RecommendedScrollDistance(200));
        Assert.Equal(336, AutoScrollSessionLogic.RecommendedScrollDistance(800));
        Assert.Equal(460, AutoScrollSessionLogic.RecommendedScrollDistance(1600));
        Assert.Equal(120, AutoScrollSessionLogic.RecommendedScrollDistance(0));
        Assert.Equal(460, AutoScrollSessionLogic.RecommendedScrollDistance(100000));
    }

    // ── 滚轮事件分块（Mac: testAutoScrollSplitsLargeGestureIntoPortablePixelSteps）──

    [Fact]
    public void AutoScrollSplitsLargeGestureIntoPortablePixelSteps()
    {
        Assert.Equal(new[] { -32, -32, -32, -32, -2 }, AutoScrollSessionLogic.PixelDeltas(130));
        Assert.Equal(new[] { -1 }, AutoScrollSessionLogic.PixelDeltas(1));
        Assert.Equal(-96, AutoScrollSessionLogic.PixelDeltas(96).Sum());
        Assert.True(AutoScrollSessionLogic.PixelDeltas(520).All(d => Math.Abs(d) <= 32));
        Assert.Equal(new[] { 32, 32, 1 }, AutoScrollSessionLogic.PixelDeltas(-65));
    }

    [Fact]
    public void PixelDeltasClampsEmptyRequestToOneUnit()
    {
        // max(1, |0|) —— 空请求也必须发出一个事件，避免手势被完全吞掉。
        Assert.Equal(new[] { -1 }, AutoScrollSessionLogic.PixelDeltas(0));
    }

    // ── 时序常量（Mac: testAutoScrollUsesStandardFastCaptureTiming）──

    [Fact]
    public void AutoScrollUsesStandardFastCaptureTiming()
    {
        Assert.Equal(300, AutoScrollSessionLogic.StandardAutoScrollDelayMilliseconds);
        Assert.Equal(0.30, AutoScrollSessionLogic.StandardAutoScrollDelay);
        Assert.Equal(1.20, AutoScrollSessionLogic.MaximumAutoScrollSettleTime);
        Assert.Equal(14, AutoScrollSessionLogic.ScrollEventIntervalMilliseconds);
        Assert.Equal(3, AutoScrollSessionLogic.RequiredBottomConfirmationFrames);
        Assert.Equal(32, AutoScrollSessionLogic.MaximumPixelDeltaPerEvent);
        Assert.Equal(420, AutoScrollSessionLogic.ManualCaptureLoopDelayMilliseconds);
        Assert.Equal(96, AutoScrollSessionLogic.RetryScrollDistanceFloor);
    }

    // ── 尝试结算（Mac: :273-295） ─────────────────────────────────

    [Fact]
    public void VisibleMovementContinuesEvenWhenSeamMatcherRejectsFrame()
    {
        var tracker = new AutoScrollProgressTracker(2);
        tracker.Begin();
        tracker.DidSendScroll();
        tracker.Observe(FrameDisposition.Rejected);

        Assert.Equal(
            AutoScrollAttemptResolution.Progress,
            AutoScrollSessionLogic.ResolveAutoScrollAttempt(
                tracker,
                FrameDisposition.Rejected,
                Moving(),
                null,
                null,
                now: 0,
                settleDeadline: 1));
    }

    [Fact]
    public void StationaryAttemptIsTheOnlyConditionThatCountsAsBottomProgress()
    {
        var tracker = new AutoScrollProgressTracker(2);
        tracker.DidSendScroll();

        Assert.Equal(
            AutoScrollAttemptResolution.NoMovement,
            AutoScrollSessionLogic.ResolveAutoScrollAttempt(
                tracker,
                FrameDisposition.Duplicate,
                Stationary(),
                null,
                null,
                now: 0,
                settleDeadline: 0));
    }

    [Fact]
    public void AccessabilityProgressAtEndDoesNotStopBeforePixelConfirmation()
    {
        var tracker = new AutoScrollProgressTracker(2);
        tracker.DidSendScroll();

        Assert.Equal(
            AutoScrollAttemptResolution.Progress,
            AutoScrollSessionLogic.ResolveAutoScrollAttempt(
                tracker,
                FrameDisposition.Appended,
                Moving(),
                new ScrollProgress(0.94),
                new ScrollProgress(1.0),
                now: 0,
                settleDeadline: 0));
    }

    [Fact]
    public void TrackedMovementCannotBeMistakenForNoMovement()
    {
        var tracker = new AutoScrollProgressTracker(2);
        tracker.DidSendScroll();

        Assert.Equal(
            AutoScrollAttemptResolution.Progress,
            AutoScrollSessionLogic.ResolveAutoScrollAttempt(
                tracker,
                FrameDisposition.Duplicate,
                Stationary(),
                new ScrollProgress(0.20),
                new ScrollProgress(0.24),
                now: 0,
                settleDeadline: 0));
    }

    [Fact]
    public void TrackedLazyLoadingStillClosesGestureAndContinuesScrolling()
    {
        var tracker = new AutoScrollProgressTracker(2);
        tracker.DidSendScroll();

        Assert.Equal(
            AutoScrollAttemptResolution.Progress,
            AutoScrollSessionLogic.ResolveAutoScrollAttempt(
                tracker,
                FrameDisposition.Rejected,
                Moving(),
                new ScrollProgress(0.42),
                new ScrollProgress(0.42),
                now: 0,
                settleDeadline: 0));
    }

    [Fact]
    public void NoPendingAppendedFrameIsProgress()
    {
        var tracker = new AutoScrollProgressTracker(2);

        Assert.Equal(
            AutoScrollAttemptResolution.Progress,
            AutoScrollSessionLogic.ResolveAutoScrollAttempt(
                tracker,
                FrameDisposition.Appended,
                NoReference(),
                now: 0,
                settleDeadline: 0));
    }

    [Fact]
    public void NoPendingDuplicateFrameWaits()
    {
        var tracker = new AutoScrollProgressTracker(2);

        Assert.Equal(
            AutoScrollAttemptResolution.Waiting,
            AutoScrollSessionLogic.ResolveAutoScrollAttempt(
                tracker,
                FrameDisposition.Duplicate,
                NoReference(),
                now: 0,
                settleDeadline: 0));
    }

    [Fact]
    public void PendingFrameBeforeSettleDeadlineWaits()
    {
        var tracker = new AutoScrollProgressTracker(2);
        tracker.DidSendScroll();

        Assert.Equal(
            AutoScrollAttemptResolution.Waiting,
            AutoScrollSessionLogic.ResolveAutoScrollAttempt(
                tracker,
                FrameDisposition.Duplicate,
                Stationary(),
                now: 0.5,
                settleDeadline: 1.2));
    }

    // ── 底部确认（Mac: :297-313） ─────────────────────────────────

    [Fact]
    public void BottomSignalRequiresConsecutiveStableFramesAfterScrollbarReachesMaximum()
    {
        var end = new ScrollProgress(1.0);
        var moving = Moving();
        var stable = Stationary();

        Assert.Equal(
            0,
            AutoScrollSessionLogic.AdvanceBottomConfirmation(
                0, FrameDisposition.Appended, stable, end));
        Assert.Equal(
            0,
            AutoScrollSessionLogic.AdvanceBottomConfirmation(
                0, FrameDisposition.Rejected, moving, end));
        Assert.Equal(
            1,
            AutoScrollSessionLogic.AdvanceBottomConfirmation(
                0, FrameDisposition.Duplicate, stable, end));
        Assert.Equal(
            AutoScrollSessionLogic.RequiredBottomConfirmationFrames,
            AutoScrollSessionLogic.AdvanceBottomConfirmation(
                2, FrameDisposition.Duplicate, stable, end));
    }

    [Fact]
    public void BottomSignalDoesNotCompleteBeforeTheTerminalViewportSettles()
    {
        var end = new ScrollProgress(1.0);
        var firstBottomFrame = NoReference();
        var stillAnimating = new ViewportMotionMeasurement(true, 12, 0.18, false);
        var settled = new ViewportMotionMeasurement(true, 0.3, 0.001, true);

        Assert.Equal(
            0,
            AutoScrollSessionLogic.AdvanceBottomConfirmation(
                0, FrameDisposition.Rejected, firstBottomFrame, end));
        Assert.Equal(
            0,
            AutoScrollSessionLogic.AdvanceBottomConfirmation(
                0, FrameDisposition.Rejected, stillAnimating, end));
        Assert.Equal(
            1,
            AutoScrollSessionLogic.AdvanceBottomConfirmation(
                0, FrameDisposition.Duplicate, settled, end));
    }

    [Fact]
    public void BottomConfirmationResetsWhenScrollbarLeavesTheBottom()
    {
        var midScroll = new ScrollProgress(0.5);

        Assert.Equal(
            0,
            AutoScrollSessionLogic.AdvanceBottomConfirmation(
                2, FrameDisposition.Duplicate, Stationary(), midScroll));
    }

    [Fact]
    public void BottomConfirmationResetsWhenProgressIsUnknown()
    {
        Assert.Equal(
            0,
            AutoScrollSessionLogic.AdvanceBottomConfirmation(
                2, FrameDisposition.Duplicate, Stationary(), null));
    }

    // ── 无进度预算耗尽后的去向（Mac: :315-325） ────────────────────

    [Fact]
    public void TrackedScrollbarCannotStopEarlyWhileItsProgressIsBelowTheBottom()
    {
        var tracker = new AutoScrollProgressTracker(2);
        tracker.DidSendScroll();
        tracker.FinishPendingWithoutProgress();
        tracker.DidSendScroll();
        tracker.FinishPendingWithoutProgress();
        Assert.True(tracker.ShouldStop);

        Assert.False(
            AutoScrollSessionLogic.ShouldCompleteAfterNoProgress(
                tracker,
                ScrollTargetMode.AccessibilityTracked,
                new ScrollProgress(0.42)));
        Assert.True(
            AutoScrollSessionLogic.ShouldCompleteAfterNoProgress(
                tracker,
                ScrollTargetMode.AccessibilityTracked,
                new ScrollProgress(1.0)));
    }

    [Fact]
    public void FallbackModeCanFinishAfterItsFullNoMovementBudget()
    {
        var tracker = new AutoScrollProgressTracker(2);
        tracker.DidSendScroll();
        tracker.FinishPendingWithoutProgress();
        tracker.DidSendScroll();
        tracker.FinishPendingWithoutProgress();

        Assert.True(
            AutoScrollSessionLogic.ShouldCompleteAfterNoProgress(
                tracker,
                ScrollTargetMode.EventFallback,
                null));
    }

    [Fact]
    public void ShouldCompleteAfterNoProgressRequiresExhaustedBudget()
    {
        var tracker = new AutoScrollProgressTracker(2);
        tracker.DidSendScroll();

        Assert.False(
            AutoScrollSessionLogic.ShouldCompleteAfterNoProgress(
                tracker,
                ScrollTargetMode.EventFallback,
                new ScrollProgress(1.0)));
    }

    // ── 观测合成（Mac: :336-350） ─────────────────────────────────

    [Fact]
    public void StationaryViewportCountsAsNoProgressEvenWhenSeamIsRejected()
    {
        Assert.Equal(
            FrameDisposition.Duplicate,
            AutoScrollSessionLogic.AutoScrollObservation(
                FrameDisposition.Rejected,
                Stationary()));
    }

    [Fact]
    public void MovedViewportNeverCountsAsBottomWhenSeamIsNotReady()
    {
        Assert.Equal(
            FrameDisposition.Rejected,
            AutoScrollSessionLogic.AutoScrollObservation(
                FrameDisposition.Duplicate,
                Moving()));
    }

    [Fact]
    public void AppendedObservationSurvivesMotionFolding()
    {
        Assert.Equal(
            FrameDisposition.Appended,
            AutoScrollSessionLogic.AutoScrollObservation(
                FrameDisposition.Appended,
                Moving()));
        Assert.Equal(
            FrameDisposition.Duplicate,
            AutoScrollSessionLogic.AutoScrollObservation(
                FrameDisposition.Duplicate,
                NoReference()));
    }

    // ── 进度归一化 / 前进判定（Mac: :187-194） ────────────────────

    [Fact]
    public void ScrollProgressNormalizesNonUnitRanges()
    {
        var progress = new ScrollProgress(35, 10, 60);
        Assert.Equal(0.5, progress.NormalizedValue, 6);
        Assert.False(progress.IsAtEnd);
        Assert.True(new ScrollProgress(60, 10, 60).IsAtEnd);
    }

    [Fact]
    public void ScrollProgressHandlesDegenerateRanges()
    {
        Assert.Equal(0, new ScrollProgress(5, 5, 5).NormalizedValue);
        Assert.False(new ScrollProgress(5, 5, 5).IsAtEnd);
        Assert.Equal(0, new ScrollProgress(-10, 0, 1).NormalizedValue);   // 夹到 0
        Assert.Equal(1, new ScrollProgress(10, 0, 1).NormalizedValue);    // 夹到 1
        Assert.True(new ScrollProgress(1, 0, 1).IsAtEnd);
    }

    [Fact]
    public void ProgressAdvancedUsesTolerance()
    {
        Assert.True(AutoScrollSessionLogic.ProgressAdvanced(
            new ScrollProgress(0.20), new ScrollProgress(0.24)));
        Assert.False(AutoScrollSessionLogic.ProgressAdvanced(
            new ScrollProgress(0.42), new ScrollProgress(0.42)));
        Assert.False(AutoScrollSessionLogic.ProgressAdvanced(
            new ScrollProgress(0.42), new ScrollProgress(0.42004)));   // 小于容差
        Assert.True(AutoScrollSessionLogic.ProgressAdvanced(
            new ScrollProgress(0.42), new ScrollProgress(0.42006)));   // 超过容差
        Assert.False(AutoScrollSessionLogic.ProgressAdvanced(null, new ScrollProgress(1)));
        Assert.False(AutoScrollSessionLogic.ProgressAdvanced(new ScrollProgress(1), null));
        Assert.False(AutoScrollSessionLogic.ProgressAdvanced(null, null));
    }

    [Fact]
    public void ScrollProgressFromUiaPercentMapsToNormalized()
    {
        // UIA ScrollPattern.VerticalScrollPercent 是 0..100。
        var atEnd = new ScrollProgress(100, 0, 100);
        Assert.True(atEnd.IsAtEnd);
        Assert.False(new ScrollProgress(50, 0, 100).IsAtEnd);
        Assert.True(AutoScrollSessionLogic.ProgressAdvanced(
            new ScrollProgress(20, 0, 100),
            new ScrollProgress(25, 0, 100)));
    }

    // ── HUD 定位（Mac: :366-373） ─────────────────────────────────

    [Fact]
    public void HudPositionPrefersBelowSelectionAndClampsHorizontally()
    {
        // 选区 x=100..700, y=100..500（高 400）→ recommended=168（不用于定位，仅布局）
        var selection = new RectD(100, 100, 600, 400);
        var visible = new RectD(0, 0, 1920, 1080);

        var (x, y) = AutoScrollSessionLogic.ComputeHudPosition(selection, visible);

        // 水平：选区中心 400 - 260 = 140
        Assert.Equal(140, x, 6);
        // 垂直：下方 = 100 - 124 - 12 = -36 < 8 → 走上方 = min(1080-124-8, 500+12) = 512
        Assert.Equal(512, y, 6);
    }

    [Fact]
    public void HudPositionGoesAboveWhenBelowIsBlocked()
    {
        var selection = new RectD(100, 1000, 600, 400);
        var visible = new RectD(0, 0, 1920, 1080);

        var (_, y) = AutoScrollSessionLogic.ComputeHudPosition(selection, visible);

        // 下方 = 1000 - 124 - 12 = 864 ≥ 8 → 放在选区上方
        Assert.Equal(864, y, 6);
    }

    [Fact]
    public void HudPositionClampsToVisibleFrame()
    {
        // 选区贴近右缘：水平中心 2100 → 夹到 visible.maxX - 520 - 12 = 1388。
        var selection = new RectD(1800, 100, 600, 400);
        var visible = new RectD(0, 0, 1920, 1080);

        var (x, y) = AutoScrollSessionLogic.ComputeHudPosition(selection, visible);

        Assert.Equal(1388, x, 6);
        Assert.Equal(512, y, 6);
    }
}
