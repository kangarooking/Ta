using Ta.Windows.Core.Capture;
using Ta.Windows.Core.Clipboard;
using Ta.Windows.Core.Export;

namespace Ta.Windows.Core.Tests;

public sealed class CorePolicyTests
{
    [Fact]
    public void CaptureAreaRejectsNonPositiveDimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureArea(0, 0, 0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureArea(0, 0, 10, -1));
    }

    [Fact]
    public void CaptureAreaSupportsNegativeVirtualDesktopCoordinates()
    {
        var area = new CaptureArea(-1920, 0, 1920, 1080);

        Assert.Equal(0, area.Right);
        Assert.Equal(1080, area.Bottom);
    }

    [Fact]
    public void ClipboardCommitAcceptsUnchangedClipboardForLatestJob()
    {
        var jobId = Guid.NewGuid();

        Assert.True(ClipboardCommitPolicy.CanCommit(10, 10, jobId, jobId));
    }

    [Fact]
    public void ClipboardCommitRejectsUserOrNewerJobSupersession()
    {
        var requestedJob = Guid.NewGuid();

        Assert.False(ClipboardCommitPolicy.CanCommit(10, 11, requestedJob, requestedJob));
        Assert.False(ClipboardCommitPolicy.CanCommit(10, 10, requestedJob, Guid.NewGuid()));
    }

    [Fact]
    public void SafeFileNameRemovesWindowsInvalidCharacters()
    {
        var value = SafeFileName.Create(
            DateTimeOffset.Parse("2026-08-27T14:30:00+08:00"),
            CaptureKind.Region,
            "A:B/C*D?");

        Assert.Equal("Ta_20260827_143000_region_A_B_C_D_", value);
    }

    [Fact]
    public void SafeFileNameOmitsBlankSourceAndTrimsTrailingDots()
    {
        var value = SafeFileName.Create(
            DateTimeOffset.Parse("2026-08-27T14:30:00+08:00"),
            CaptureKind.FullDesktop,
            "  ...  ");

        Assert.Equal("Ta_20260827_143000_full-desktop", value);
    }
}
