using Ta.Core.LongCapture;
using Ta.LongSession;

namespace Ta.LongSession.Tests;

/// <summary>
/// AutoScrollProgressTracker 测试 —— 逐条移植 Mac 版
/// Tests\AIScreenshotCoreTests\AutoScrollProgressTrackerTests.swift。
///
/// 注意一处刻意差异：Mac 的默认值是 2，而会话控制器实际总是传 3
/// （ScrollingCaptureSessionController.swift:31）。Windows 侧把会话层常量
/// 作为默认值（参考文档 §8.1 要求 3），因此默认上限断言为 3。
/// </summary>
public sealed class AutoScrollProgressTrackerTests
{
    [Fact]
    public void DuplicateCaptureTicksDoNotConsumeScrollAttemptBudget()
    {
        var tracker = new AutoScrollProgressTracker(6);
        tracker.Begin();
        tracker.DidSendScroll();

        for (var i = 0; i < 20; i++)
        {
            tracker.Observe(FrameDisposition.Duplicate);
        }

        Assert.Equal(0, tracker.AttemptsWithoutProgress);
        Assert.False(tracker.ShouldStop);
    }

    [Fact]
    public void ExplicitNoProgressResolutionCountsOneAttempt()
    {
        var tracker = new AutoScrollProgressTracker(6);
        tracker.Begin();
        tracker.DidSendScroll();
        tracker.Observe(FrameDisposition.Duplicate);
        tracker.FinishPendingWithoutProgress();

        Assert.Equal(1, tracker.AttemptsWithoutProgress);
        Assert.False(tracker.ShouldStop);
    }

    [Fact]
    public void AppendedFrameResetsNoProgressAttempts()
    {
        var tracker = new AutoScrollProgressTracker(6);
        tracker.Begin();
        for (var i = 0; i < 4; i++)
        {
            tracker.DidSendScroll();
            tracker.Observe(FrameDisposition.Duplicate);
            tracker.FinishPendingWithoutProgress();
        }

        Assert.Equal(4, tracker.AttemptsWithoutProgress);

        tracker.DidSendScroll();
        tracker.Observe(FrameDisposition.Appended);

        Assert.Equal(0, tracker.AttemptsWithoutProgress);
        Assert.False(tracker.ShouldStop);
    }

    [Fact]
    public void StopsAfterTwoActualScrollAttemptsWithoutProgress()
    {
        var tracker = new AutoScrollProgressTracker(2);
        tracker.Begin();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            tracker.DidSendScroll();
            tracker.Observe(FrameDisposition.Duplicate);
            tracker.FinishPendingWithoutProgress();
            Assert.Equal(attempt == 1, tracker.ShouldStop);
        }

        Assert.Equal(2, tracker.AttemptsWithoutProgress);
        Assert.True(tracker.ShouldStop);
    }

    [Fact]
    public void DefaultNoProgressLimitMatchesSessionConstant()
    {
        // Mac 版默认 2，但会话层恒定传 3；Windows 直接把会话常量作为默认值。
        Assert.Equal(3, new AutoScrollProgressTracker().MaximumAttemptsWithoutProgress);
        Assert.Equal(3, AutoScrollSessionLogic.MaximumAttemptsWithoutProgress);
    }

    [Fact]
    public void RejectedFrameRequestsSettlingTimeWithoutConsumingRetry()
    {
        var tracker = new AutoScrollProgressTracker(2);
        tracker.Begin();
        tracker.DidSendScroll();

        tracker.Observe(FrameDisposition.Rejected);

        Assert.True(tracker.NeedsMoreSettlingTime);
        Assert.Equal(0, tracker.AttemptsWithoutProgress);
    }

    [Fact]
    public void AppendedFrameClearsSettlingWait()
    {
        var tracker = new AutoScrollProgressTracker(2);
        tracker.Begin();
        tracker.DidSendScroll();
        tracker.Observe(FrameDisposition.Rejected);

        tracker.Observe(FrameDisposition.Appended);

        Assert.False(tracker.NeedsMoreSettlingTime);
        Assert.False(tracker.HasPendingScroll);
        Assert.Equal(0, tracker.AttemptsWithoutProgress);
    }

    [Fact]
    public void SecondScrollCannotOpenWhilePreviousAttemptIsPending()
    {
        var tracker = new AutoScrollProgressTracker(2);
        tracker.DidSendScroll();
        tracker.DidSendScroll();

        tracker.FinishPendingWithoutProgress();

        Assert.Equal(1, tracker.AttemptsWithoutProgress);
    }

    [Fact]
    public void RolledBackSeamDoesNotConsumeBottomBudget()
    {
        var tracker = new AutoScrollProgressTracker(2);
        tracker.DidSendScroll();
        tracker.Observe(FrameDisposition.Rejected);

        tracker.CancelPendingAttempt();

        Assert.False(tracker.HasPendingScroll);
        Assert.False(tracker.NeedsMoreSettlingTime);
        Assert.Equal(0, tracker.AttemptsWithoutProgress);
        Assert.False(tracker.ShouldStop);
    }

    [Fact]
    public void ConfirmedViewportMovementClosesPendingGestureWithoutCountingFailure()
    {
        var tracker = new AutoScrollProgressTracker(2);
        tracker.DidSendScroll();
        tracker.Observe(FrameDisposition.Rejected);

        tracker.FinishPendingWithProgress();

        Assert.False(tracker.HasPendingScroll);
        Assert.False(tracker.NeedsMoreSettlingTime);
        Assert.Equal(0, tracker.AttemptsWithoutProgress);
        Assert.False(tracker.ShouldStop);
    }

    [Fact]
    public void BeginResetsEverything()
    {
        var tracker = new AutoScrollProgressTracker(2);
        tracker.DidSendScroll();
        tracker.Observe(FrameDisposition.Rejected);
        tracker.FinishPendingWithoutProgress();

        tracker.Begin();

        Assert.False(tracker.HasPendingScroll);
        Assert.False(tracker.NeedsMoreSettlingTime);
        Assert.Equal(0, tracker.AttemptsWithoutProgress);
    }

    [Fact]
    public void MaximumAttemptsIsAtLeastOne()
    {
        Assert.Equal(1, new AutoScrollProgressTracker(0).MaximumAttemptsWithoutProgress);
        Assert.Equal(1, new AutoScrollProgressTracker(-5).MaximumAttemptsWithoutProgress);
    }
}
