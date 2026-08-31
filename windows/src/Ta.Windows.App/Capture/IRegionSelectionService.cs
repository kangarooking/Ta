using Ta.Windows.Core.Capture;

namespace Ta.Windows.App.Capture;

public interface IRegionSelectionService
{
    Task<CaptureArea?> SelectAsync(CancellationToken cancellationToken = default);
}
