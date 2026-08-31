using System.Windows.Input;
using System.Windows.Threading;
using Ta.Windows.Core.Capture;
using Ta.Windows.Platform.Display;

namespace Ta.Windows.App.Capture;

public partial class RegionSelectionWindow : System.Windows.Window
{
    private readonly CaptureArea desktopBounds;
    private readonly ICursorPositionService cursorPositionService;
    private readonly IPhysicalWindowService physicalWindowService;
    private readonly TaskCompletionSource<CaptureArea?> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PhysicalScreenPoint dragStart;
    private bool dragging;
    private CaptureArea? selectedArea;
    private CancellationTokenRegistration cancellationRegistration;

    public RegionSelectionWindow(
        CaptureArea desktopBounds,
        ICursorPositionService cursorPositionService,
        IPhysicalWindowService physicalWindowService)
    {
        InitializeComponent();
        this.desktopBounds = desktopBounds;
        this.cursorPositionService = cursorPositionService;
        this.physicalWindowService = physicalWindowService;
    }

    public Task<CaptureArea?> SelectAsync(CancellationToken cancellationToken)
    {
        cancellationRegistration = cancellationToken.Register(() =>
            Dispatcher.BeginInvoke(DispatcherPriority.Send, Close));
        Show();
        Activate();
        Focus();
        return completion.Task;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        physicalWindowService.CoverArea(this, desktopBounds);
    }

    protected override void OnClosed(EventArgs e)
    {
        cancellationRegistration.Dispose();
        DesktopCompositionService.TryFlush();
        completion.TrySetResult(selectedArea);
        base.OnClosed(e);
    }

    private void Window_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        dragStart = cursorPositionService.GetPosition();
        dragging = true;
        selectedArea = null;
        Mouse.Capture(this);
        UpdateSelection(dragStart);
        e.Handled = true;
    }

    private void Window_OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!dragging)
        {
            return;
        }

        UpdateSelection(cursorPositionService.GetPosition());
        e.Handled = true;
    }

    private void Window_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!dragging)
        {
            return;
        }

        var current = cursorPositionService.GetPosition();
        dragging = false;
        Mouse.Capture(null);
        var area = NormalizeArea(dragStart, current);
        if (area.Width < 2 || area.Height < 2)
        {
            SelectionBorder.Visibility = System.Windows.Visibility.Collapsed;
            InstructionText.Text = "选区太小，请重新拖动 · Esc 取消";
            return;
        }

        selectedArea = area;
        Hide();
        Close();
        e.Handled = true;
    }

    private void Window_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        selectedArea = null;
        Close();
        e.Handled = true;
    }

    private void UpdateSelection(PhysicalScreenPoint current)
    {
        var start = PointFromScreen(new System.Windows.Point(dragStart.X, dragStart.Y));
        var end = PointFromScreen(new System.Windows.Point(current.X, current.Y));
        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);

        System.Windows.Controls.Canvas.SetLeft(SelectionBorder, left);
        System.Windows.Controls.Canvas.SetTop(SelectionBorder, top);
        SelectionBorder.Width = Math.Abs(end.X - start.X);
        SelectionBorder.Height = Math.Abs(end.Y - start.Y);
        SelectionBorder.Visibility = System.Windows.Visibility.Visible;

        var physical = NormalizeArea(dragStart, current);
        InstructionText.Text = $"{physical.Width} × {physical.Height} 像素 · 松开完成 · Esc 取消";
    }

    private static CaptureArea NormalizeArea(PhysicalScreenPoint start, PhysicalScreenPoint end)
    {
        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        var width = Math.Max(1, Math.Abs(end.X - start.X));
        var height = Math.Max(1, Math.Abs(end.Y - start.Y));
        return new CaptureArea(left, top, width, height);
    }
}
