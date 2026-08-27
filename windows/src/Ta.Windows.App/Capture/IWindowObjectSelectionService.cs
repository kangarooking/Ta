using Ta.Windows.Core.Capture;

namespace Ta.Windows.App.Capture;

public interface IWindowObjectSelectionService
{
    Task<CaptureArea?> SelectAsync(CancellationToken cancellationToken = default);
}
