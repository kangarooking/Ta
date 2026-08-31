using Ta.Windows.Core.Capture;
using Ta.Windows.Platform.Capture;
using Ta.Windows.Platform.Display;

namespace Ta.Windows.App.Capture;

public sealed class WindowObjectSelectionService(
    IVirtualDesktopService virtualDesktopService,
    ICursorPositionService cursorPositionService,
    IPhysicalWindowService physicalWindowService,
    IWindowTargetResolver targetResolver) : IWindowObjectSelectionService
{
    public Task<CaptureArea?> SelectAsync(CancellationToken cancellationToken = default)
    {
        var window = new WindowObjectSelectionWindow(
            virtualDesktopService.GetBounds(),
            cursorPositionService,
            physicalWindowService,
            targetResolver);
        return window.SelectAsync(cancellationToken);
    }
}
