using Ta.Core.Capture;

namespace Ta.LongSession;

/// <summary>时钟抽象（双精度秒）。测试用手动时钟推进，不产生真实等待。</summary>
public interface IClock
{
    double Now();
}

/// <summary>Stopwatch 时钟。</summary>
public sealed class StopwatchClock : IClock
{
    private readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();

    public double Now() => _stopwatch.Elapsed.TotalSeconds;
}

/// <summary>延迟抽象。测试用瞬时完成替身。</summary>
public interface IDelay
{
    Task Delay(int milliseconds, CancellationToken cancellationToken);
}

/// <summary>真实 Task.Delay。</summary>
public sealed class TaskDelay : IDelay
{
    public Task Delay(int milliseconds, CancellationToken cancellationToken) =>
        Task.Delay(milliseconds, cancellationToken);
}

/// <summary>自动滚动驱动抽象。对应 Mac 版 AccessibilityAutoScrollService 的行为面。</summary>
public interface IAutoScrollDriver
{
    ScrollTarget? ResolveTarget(CaptureSelection selection, int? sourceProcessId);

    ScrollProgress? ReadProgress(ScrollTarget target);

    Task<bool> PostDownwardScrollAsync(
        CaptureSelection selection,
        ScrollTarget? target,
        int pixels,
        ScrollStrategy strategy,
        CancellationToken cancellationToken);
}

/// <summary>基于 UI Automation 的默认驱动。</summary>
public sealed class UiaAutoScrollDriver : IAutoScrollDriver
{
    private readonly AccessibilityAutoScrollService _service = new();

    public ScrollTarget? ResolveTarget(CaptureSelection selection, int? sourceProcessId) =>
        _service.ResolveTarget(selection, sourceProcessId);

    public ScrollProgress? ReadProgress(ScrollTarget target) => _service.Progress(target);

    public Task<bool> PostDownwardScrollAsync(
        CaptureSelection selection,
        ScrollTarget? target,
        int pixels,
        ScrollStrategy strategy,
        CancellationToken cancellationToken) =>
        _service.PostDownwardScrollAsync(selection, target, pixels, strategy, cancellationToken);
}
