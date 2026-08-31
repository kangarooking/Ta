using System.Drawing;
using Ta.Windows.Core.Capture;

namespace Ta.Windows.App.Results;

public sealed class CaptureResult : IDisposable
{
    private bool disposed;

    public required Guid JobId { get; init; }

    public required CaptureKind Kind { get; init; }

    public required CaptureArea Area { get; init; }

    public required Bitmap Image { get; init; }

    public required DateTimeOffset CapturedAt { get; init; }

    public required bool IsPrivate { get; init; }

    public required bool AutomaticallyCopied { get; set; }

    public required string StatusMessage { get; set; }

    public required uint ClipboardSequenceAtStart { get; init; }

    public string? SavedFilePath { get; set; }

    public string? VisionTextFilePath { get; set; }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        Image.Dispose();
        disposed = true;
    }
}
