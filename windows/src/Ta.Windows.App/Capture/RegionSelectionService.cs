using Ta.Windows.Core.Capture;
using Ta.Windows.Platform.Capture;
using Ta.Windows.Platform.Display;

namespace Ta.Windows.App.Capture;

public sealed class RegionSelectionService : IRegionSelectionService
{
    private readonly IVirtualDesktopService virtualDesktopService;
    private readonly ICursorPositionService cursorPositionService;
    private readonly IPhysicalWindowService physicalWindowService;

    public RegionSelectionService(
        IVirtualDesktopService virtualDesktopService,
        ICursorPositionService cursorPositionService,
        IPhysicalWindowService physicalWindowService)
    {
        this.virtualDesktopService = virtualDesktopService;
        this.cursorPositionService = cursorPositionService;
        this.physicalWindowService = physicalWindowService;
    }

    public Task<CaptureArea?> SelectAsync(CancellationToken cancellationToken = default)
    {
        var window = new RegionSelectionWindow(
            virtualDesktopService.GetBounds(),
            cursorPositionService,
            physicalWindowService);
        return window.SelectAsync(cancellationToken);
    }
}
