using Ta.Core.LongCapture;

namespace Ta.LongSession;

/// <summary>
/// 自动滚动进度记账器 —— 按**手势**（而非按 tick）记账。
///
/// 逐行对应 Mac 版 AIScreenshotCore/LongCapture/AutoScrollProgressTracker.swift。
///
/// 核心语义：慢动画可能产出大量重复帧，但这些帧都属于**同一次手势尝试**，
/// 不能重复消耗「无进度」预算。只有「稳定期截止 + 视口仍未变化」这种
/// 显式结论才能让预算 +1。
///
/// 本类是纯逻辑，不触碰 Win32 / UIA，可完整单测。
/// </summary>
public sealed class AutoScrollProgressTracker
{
    /// <summary>无进度重试上限。会话层传入 3（对应 Mac: ScrollingCaptureSessionController:31）。</summary>
    public int MaximumAttemptsWithoutProgress { get; }

    /// <summary>已累计的无进度尝试次数。</summary>
    public int AttemptsWithoutProgress { get; private set; }

    /// <summary>是否有一个已发出、尚未结算的手势。</summary>
    public bool HasPendingScroll { get; private set; }

    /// <summary>该手势期间是否出现过 rejected 帧（需要更多稳定时间）。</summary>
    private bool _pendingScrollSawInconclusiveFrame;

    public AutoScrollProgressTracker(int maximumAttemptsWithoutProgress = 3)
    {
        MaximumAttemptsWithoutProgress = Math.Max(1, maximumAttemptsWithoutProgress);
    }

    /// <summary>预算耗尽，应停止自动滚动。</summary>
    public bool ShouldStop => AttemptsWithoutProgress >= MaximumAttemptsWithoutProgress;

    /// <summary>
    /// 被拒绝的匹配可能只是页面仍在动画，而不是滚动失败。
    /// 给这次尝试一个有界稳定窗口，再决定是否消耗下一次重试。
    /// </summary>
    public bool NeedsMoreSettlingTime => HasPendingScroll && _pendingScrollSawInconclusiveFrame;

    /// <summary>重置（对应 Mac: begin()）。恢复初始状态。</summary>
    public void Begin()
    {
        AttemptsWithoutProgress = 0;
        HasPendingScroll = false;
        _pendingScrollSawInconclusiveFrame = false;
    }

    /// <summary>
    /// 开启**且仅开启一次**尝试。上一次尝试未结算为 progress/noMovement 之前，
    /// 绝不发送第二次手势。
    /// </summary>
    public void DidSendScroll()
    {
        if (HasPendingScroll)
        {
            return;
        }

        HasPendingScroll = true;
        _pendingScrollSawInconclusiveFrame = false;
    }

    /// <summary>观察一次拼接结果。</summary>
    public void Observe(FrameDisposition disposition)
    {
        if (!HasPendingScroll)
        {
            return;
        }

        switch (disposition)
        {
            case FrameDisposition.Appended:
                AttemptsWithoutProgress = 0;
                HasPendingScroll = false;
                _pendingScrollSawInconclusiveFrame = false;
                break;
            case FrameDisposition.Rejected:
                _pendingScrollSawInconclusiveFrame = true;
                break;
        }
    }

    /// <summary>
    /// 仅在稳定期截止、且已接受的视口仍未变化时调用。
    /// 可见移动之后的匹配失败**不是**到底，绝不能消耗该预算。
    /// </summary>
    public void FinishPendingWithoutProgress()
    {
        if (!HasPendingScroll)
        {
            return;
        }

        AttemptsWithoutProgress++;
        HasPendingScroll = false;
        _pendingScrollSawInconclusiveFrame = false;
    }

    /// <summary>
    /// 当移动被接缝匹配之外的证据独立确认时（例如 AX 滚动条进度或视口像素），
    /// 关闭手势。拼接质量绝不能绑架滚动驱动。
    /// </summary>
    public void FinishPendingWithProgress()
    {
        if (!HasPendingScroll)
        {
            return;
        }

        AttemptsWithoutProgress = 0;
        HasPendingScroll = false;
        _pendingScrollSawInconclusiveFrame = false;
    }

    /// <summary>
    /// 清除一次为接缝恢复而故意回滚的手势。
    /// 被恢复的手势不是到底的证据，不消耗无进度预算。
    /// </summary>
    public void CancelPendingAttempt()
    {
        HasPendingScroll = false;
        _pendingScrollSawInconclusiveFrame = false;
    }
}
