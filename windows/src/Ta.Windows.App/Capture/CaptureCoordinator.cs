using Ta.Windows.App.Results;
using Ta.Windows.Core.Capture;
using Ta.Windows.Platform.Capture;
using Ta.Windows.Platform.Clipboard;

namespace Ta.Windows.App.Capture;

public sealed class CaptureCoordinator
{
    private readonly IRegionSelectionService regionSelectionService;
    private readonly IScreenCaptureService screenCaptureService;
    private readonly IWindowsClipboardService clipboardService;
    private readonly IVirtualDesktopService virtualDesktopService;
    private readonly IWindowObjectSelectionService windowObjectSelectionService;
    private readonly Func<DateTimeOffset> clock;
    private readonly object stateLock = new();
    private Guid latestJobId;
    private CaptureArea? lastRegion;

    public CaptureCoordinator(
        IRegionSelectionService regionSelectionService,
        IScreenCaptureService screenCaptureService,
        IWindowsClipboardService clipboardService,
        IVirtualDesktopService virtualDesktopService,
        IWindowObjectSelectionService windowObjectSelectionService,
        Func<DateTimeOffset>? clock = null)
    {
        this.regionSelectionService = regionSelectionService
            ?? throw new ArgumentNullException(nameof(regionSelectionService));
        this.screenCaptureService = screenCaptureService
            ?? throw new ArgumentNullException(nameof(screenCaptureService));
        this.clipboardService = clipboardService
            ?? throw new ArgumentNullException(nameof(clipboardService));
        this.virtualDesktopService = virtualDesktopService
            ?? throw new ArgumentNullException(nameof(virtualDesktopService));
        this.windowObjectSelectionService = windowObjectSelectionService
            ?? throw new ArgumentNullException(nameof(windowObjectSelectionService));
        this.clock = clock ?? (() => DateTimeOffset.Now);
    }

    public async Task<CaptureResult?> CaptureRegionAsync(
        bool privateMode,
        CancellationToken cancellationToken = default)
    {
        var job = BeginJob();
        var sequence = clipboardService.GetSequenceNumber();
        var area = await regionSelectionService.SelectAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (area is null)
        {
            return null;
        }

        var result = CompleteCapture(
            job,
            sequence,
            area.Value,
            CaptureKind.Region,
            privateMode);
        lock (stateLock)
        {
            lastRegion = area;
        }

        return result;
    }

    public async Task<CaptureResult?> CaptureSmartTextSourceAsync(
        bool privateMode,
        CancellationToken cancellationToken = default)
    {
        var job = BeginJob();
        var sequence = clipboardService.GetSequenceNumber();
        var area = await regionSelectionService.SelectAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (area is null)
        {
            return null;
        }

        var result = CompleteCapture(
            job,
            sequence,
            area.Value,
            CaptureKind.SmartText,
            privateMode);
        lock (stateLock)
        {
            lastRegion = area;
        }

        return result;
    }

    public Task<CaptureResult> CaptureFullDesktopAsync(
        bool privateMode,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var job = BeginJob();
        var sequence = clipboardService.GetSequenceNumber();
        var result = CompleteCapture(
            job,
            sequence,
            virtualDesktopService.GetBounds(),
            CaptureKind.FullDesktop,
            privateMode);
        return Task.FromResult(result);
    }

    public async Task<CaptureResult?> CaptureWindowObjectAsync(
        bool privateMode,
        CancellationToken cancellationToken = default)
    {
        var job = BeginJob();
        var sequence = clipboardService.GetSequenceNumber();
        var area = await windowObjectSelectionService.SelectAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (area is null)
        {
            return null;
        }

        return CompleteCapture(
            job,
            sequence,
            area.Value,
            CaptureKind.Window,
            privateMode);
    }

    public Task<CaptureResult> RepeatLastRegionAsync(
        bool privateMode,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CaptureArea area;
        lock (stateLock)
        {
            area = lastRegion
                ?? throw new InvalidOperationException("还没有可重复的上一次区域，请先完成一次区域截图。");
        }

        if (!virtualDesktopService.GetBounds().Contains(area))
        {
            throw new InvalidOperationException("显示器布局已经变化，上一次区域不再有效，请重新选择区域。");
        }

        var job = BeginJob();
        var sequence = clipboardService.GetSequenceNumber();
        var result = CompleteCapture(
            job,
            sequence,
            area,
            CaptureKind.RepeatedRegion,
            privateMode);
        return Task.FromResult(result);
    }

    public bool TryCommitText(
        CaptureResult result,
        string text,
        out string? failureMessage)
    {
        ArgumentNullException.ThrowIfNull(result);
        Guid currentLatestJob;
        lock (stateLock)
        {
            currentLatestJob = latestJobId;
        }

        return clipboardService.TrySetText(
            text,
            result.ClipboardSequenceAtStart,
            result.JobId,
            currentLatestJob,
            out failureMessage);
    }

    private Guid BeginJob()
    {
        var jobId = Guid.NewGuid();
        lock (stateLock)
        {
            latestJobId = jobId;
        }

        return jobId;
    }

    private CaptureResult CompleteCapture(
        Guid jobId,
        uint expectedClipboardSequence,
        CaptureArea area,
        CaptureKind kind,
        bool privateMode)
    {
        var image = screenCaptureService.Capture(area);
        try
        {
            return new CaptureResult
            {
                JobId = jobId,
                Kind = kind,
                Area = area,
                Image = image,
                CapturedAt = clock(),
                IsPrivate = privateMode,
                AutomaticallyCopied = false,
                StatusMessage = "截图已完成，正在准备自动保存。",
                ClipboardSequenceAtStart = expectedClipboardSequence,
            };
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }
}
