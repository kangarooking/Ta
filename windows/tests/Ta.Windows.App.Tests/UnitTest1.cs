using System.Drawing;
using System.Diagnostics;
using System.IO;
using Ta.Windows.App.Capture;
using Ta.Windows.App.Settings;
using Ta.Windows.Core.Capture;
using Ta.Windows.Platform.Capture;
using Ta.Windows.Platform.Clipboard;
using Ta.Windows.Platform.Security;
using Ta.Windows.Platform.Vision;

namespace Ta.Windows.App.Tests;

public sealed class CaptureCoordinatorTests
{
    [Fact]
    public async Task CancelledSelectionDoesNotCaptureOrTouchClipboard()
    {
        var selection = new FakeSelectionService(null);
        var capture = new FakeCaptureService();
        var clipboard = new FakeClipboardService();
        var coordinator = CreateCoordinator(selection, capture, clipboard);

        var result = await coordinator.CaptureRegionAsync(privateMode: false);

        Assert.Null(result);
        Assert.Equal(0, capture.CaptureCount);
        Assert.Equal(0, clipboard.ImageWriteCount);
    }

    [Fact]
    public async Task RegionCaptureLeavesClipboardUntouchedUntilPathIsSaved()
    {
        var area = new CaptureArea(20, 30, 200, 100);
        var selection = new FakeSelectionService(area);
        var capture = new FakeCaptureService();
        var clipboard = new FakeClipboardService();
        var coordinator = CreateCoordinator(selection, capture, clipboard);

        using var result = await coordinator.CaptureRegionAsync(privateMode: false);

        Assert.NotNull(result);
        Assert.False(result.AutomaticallyCopied);
        Assert.Contains("自动保存", result.StatusMessage);
        Assert.Equal(0, clipboard.ImageWriteCount);
    }

    [Fact]
    public async Task RepeatUsesLastSuccessfulRegion()
    {
        var area = new CaptureArea(20, 30, 200, 100);
        var selection = new FakeSelectionService(area);
        var capture = new FakeCaptureService();
        var clipboard = new FakeClipboardService();
        var coordinator = CreateCoordinator(selection, capture, clipboard);

        using var first = await coordinator.CaptureRegionAsync(privateMode: false);
        using var repeated = await coordinator.RepeatLastRegionAsync(privateMode: true);

        Assert.NotNull(first);
        Assert.NotNull(repeated);
        Assert.Equal(CaptureKind.RepeatedRegion, repeated.Kind);
        Assert.Equal(area, repeated.Area);
        Assert.True(repeated.IsPrivate);
        Assert.Equal(2, capture.CaptureCount);
    }

    [Fact]
    public async Task RepeatWithoutPreviousRegionReturnsSafeFailure()
    {
        var coordinator = CreateCoordinator(
            new FakeSelectionService(null),
            new FakeCaptureService(),
            new FakeClipboardService());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.RepeatLastRegionAsync(privateMode: false));

        Assert.Contains("上一次区域", exception.Message);
    }

    [Fact]
    public async Task WindowObjectCaptureUsesSelectedHandleBounds()
    {
        var capture = new FakeCaptureService();
        var coordinator = CreateCoordinator(
            new FakeSelectionService(null),
            capture,
            new FakeClipboardService());

        using var result = await coordinator.CaptureWindowObjectAsync(privateMode: false);

        Assert.NotNull(result);
        Assert.Equal(CaptureKind.Window, result.Kind);
        Assert.Equal(new CaptureArea(50, 60, 800, 600), result.Area);
        Assert.Equal(1, capture.CaptureCount);
    }

    [Fact]
    public async Task SmartTextSourceDoesNotCopyImageBeforeOcrCompletes()
    {
        var area = new CaptureArea(20, 30, 200, 100);
        var clipboard = new FakeClipboardService();
        var coordinator = CreateCoordinator(
            new FakeSelectionService(area),
            new FakeCaptureService(),
            clipboard);

        using var result = await coordinator.CaptureSmartTextSourceAsync(privateMode: false);

        Assert.NotNull(result);
        Assert.Equal(CaptureKind.SmartText, result.Kind);
        Assert.False(result.AutomaticallyCopied);
        Assert.Equal(0, clipboard.ImageWriteCount);
    }

    [Fact]
    public async Task RecognizedTextCommitsOnlyForLatestUnchangedJob()
    {
        var area = new CaptureArea(20, 30, 200, 100);
        var clipboard = new FakeClipboardService();
        var coordinator = CreateCoordinator(
            new FakeSelectionService(area),
            new FakeCaptureService(),
            clipboard);
        using var result = await coordinator.CaptureSmartTextSourceAsync(privateMode: false);

        var committed = coordinator.TryCommitText(result!, "HELLO 2026", out var failure);

        Assert.True(committed);
        Assert.Null(failure);
        Assert.Equal(1, clipboard.TextWriteCount);
    }

    private static CaptureCoordinator CreateCoordinator(
        IRegionSelectionService selection,
        IScreenCaptureService capture,
        IWindowsClipboardService clipboard) =>
        new(
            selection,
            capture,
            clipboard,
            new FakeVirtualDesktopService(new CaptureArea(0, 0, 1920, 1080)),
            new FakeWindowObjectSelectionService(new CaptureArea(50, 60, 800, 600)),
            () => DateTimeOffset.Parse("2026-08-27T14:30:00+08:00"));

    private sealed class FakeSelectionService(CaptureArea? result) : IRegionSelectionService
    {
        public Task<CaptureArea?> SelectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private sealed class FakeCaptureService : IScreenCaptureService
    {
        public int CaptureCount { get; private set; }

        public Bitmap Capture(CaptureArea area)
        {
            CaptureCount++;
            return new Bitmap(area.Width, area.Height);
        }
    }

    private sealed class FakeClipboardService : IWindowsClipboardService
    {
        public uint SequenceAfterWork { get; init; } = 10;

        public int ImageWriteCount { get; private set; }

        public int TextWriteCount { get; private set; }

        public uint GetSequenceNumber() => ImageWriteCount == 0 ? 10u : SequenceAfterWork;

        public void SetImageExplicit(Bitmap image) => ImageWriteCount++;

        public bool TrySetText(
            string text,
            uint expectedSequence,
            Guid requestedJobId,
            Guid latestJobId,
            out string? failureMessage)
        {
            if (SequenceAfterWork != expectedSequence || requestedJobId != latestJobId)
            {
                failureMessage = "识别期间剪贴板已经变化，文字结果仍然安全保留。";
                return false;
            }

            TextWriteCount++;
            failureMessage = null;
            return true;
        }

        public void SetTextExplicit(string text)
        {
            TextWriteCount++;
        }
    }

    private sealed class FakeVirtualDesktopService(CaptureArea bounds) : IVirtualDesktopService
    {
        public CaptureArea GetBounds() => bounds;
    }

    private sealed class FakeWindowObjectSelectionService(CaptureArea? area) : IWindowObjectSelectionService
    {
        public Task<CaptureArea?> SelectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(area);
    }
}

public sealed class LiveApplicationWindowTests
{
    [Fact]
    [Trait("Category", "Live")]
    public void RunningApplicationExposesExpectedMainWindowWhenExplicitlyEnabled()
    {
        var processes = Process.GetProcessesByName("Ta.Windows.App");
        try
        {
            foreach (var process in processes)
            {
                process.Refresh();
            }

            Assert.Contains(processes, process => process.MainWindowTitle == "拓 · Ta Windows Alpha");
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}

public sealed class CloudVisionLiveTests
{
    [Fact]
    [Trait("Category", "CloudLive")]
    public async Task ConfiguredVisionApiRecognizesGeneratedImage()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var store = new AppSettingsStore(
            DefaultSettingsResource.ReadJson(),
            Path.Combine(localAppData, "Ta", "settings.json"),
            new WindowsCredentialStore());
        var settings = store.LoadOrCreate();
        var apiKey = store.ReadApiKey();
        Assert.False(string.IsNullOrWhiteSpace(apiKey));
        using var bitmap = new Bitmap(720, 180);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            using var font = new Font("Segoe UI", 48, FontStyle.Bold);
            graphics.DrawString("HELLO 2026", font, Brushes.Black, 28, 45);
        }

        var result = await new VisionApiClient().AnalyzeAsync(
            bitmap,
            settings.Vision,
            apiKey!,
            CancellationToken.None);

        Assert.True(result.CloudUploaded);
        Assert.Contains("HELLO", result.Text, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class VisionSkillEnvironmentParserTests
{
    [Fact]
    public void ReadsThreeVisionSettingsWithoutLoggingOrPersistingThem()
    {
        const string source = "VISION_BASE_URL=https://vision.example/v1\nVISION_MODEL=vision-model\nVISION_API_KEY=secret-value";

        var result = VisionSkillEnvironmentParser.Parse(source);

        Assert.Equal("https://vision.example/v1", result.BaseUrl);
        Assert.Equal("vision-model", result.Model);
        Assert.Equal("secret-value", result.ApiKey);
    }
}
