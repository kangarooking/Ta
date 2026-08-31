using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Ta.Windows.Core.Capture;
using Ta.Windows.Platform.Capture;
using Ta.Windows.Platform.Display;

namespace Ta.Windows.App.Capture;

public partial class WindowObjectSelectionWindow : System.Windows.Window
{
    private readonly CaptureArea desktopBounds;
    private readonly ICursorPositionService cursorPositionService;
    private readonly IPhysicalWindowService physicalWindowService;
    private readonly IWindowTargetResolver targetResolver;
    private readonly TaskCompletionSource<CaptureArea?> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenRegistration cancellationRegistration;
    private nint overlayHandle;
    private WindowTarget? currentTarget;
    private WindowTargetMode mode = WindowTargetMode.Window;

    public WindowObjectSelectionWindow(
        CaptureArea desktopBounds,
        ICursorPositionService cursorPositionService,
        IPhysicalWindowService physicalWindowService,
        IWindowTargetResolver targetResolver)
    {
        InitializeComponent();
        this.desktopBounds = desktopBounds;
        this.cursorPositionService = cursorPositionService;
        this.physicalWindowService = physicalWindowService;
        this.targetResolver = targetResolver;
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
        overlayHandle = new WindowInteropHelper(this).Handle;
        physicalWindowService.CoverArea(this, desktopBounds);
        UpdateTarget();
    }

    protected override void OnClosed(EventArgs e)
    {
        cancellationRegistration.Dispose();
        DesktopCompositionService.TryFlush();
        completion.TrySetResult(currentTarget?.Area);
        base.OnClosed(e);
    }

    private void Window_OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        UpdateTarget();
        e.Handled = true;
    }

    private void Window_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        UpdateTarget();
        if (currentTarget is null)
        {
            InstructionText.Text = "当前位置没有可捕获的窗口或对象，请移动鼠标后重试 · Esc 取消";
            return;
        }

        Hide();
        Close();
        e.Handled = true;
    }

    private void Window_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            currentTarget = null;
            Close();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Tab)
        {
            mode = mode == WindowTargetMode.Object
                ? WindowTargetMode.Window
                : WindowTargetMode.Object;
            UpdateTarget();
            e.Handled = true;
        }
    }

    private void UpdateTarget()
    {
        if (overlayHandle == nint.Zero)
        {
            return;
        }

        var point = cursorPositionService.GetPosition();
        currentTarget = targetResolver.Resolve(point, overlayHandle, mode);
        if (currentTarget is null)
        {
            TargetBorder.Visibility = System.Windows.Visibility.Collapsed;
            return;
        }

        var topLeft = PointFromScreen(new System.Windows.Point(
            currentTarget.Area.X,
            currentTarget.Area.Y));
        var bottomRight = PointFromScreen(new System.Windows.Point(
            currentTarget.Area.Right,
            currentTarget.Area.Bottom));
        System.Windows.Controls.Canvas.SetLeft(TargetBorder, topLeft.X);
        System.Windows.Controls.Canvas.SetTop(TargetBorder, topLeft.Y);
        TargetBorder.Width = Math.Max(1, bottomRight.X - topLeft.X);
        TargetBorder.Height = Math.Max(1, bottomRight.Y - topLeft.Y);
        TargetBorder.Visibility = System.Windows.Visibility.Visible;

        var modeLabel = currentTarget.Mode == WindowTargetMode.Object ? "对象" : "窗口";
        InstructionText.Text = $"{modeLabel}：{currentTarget.Label} · {currentTarget.Area.Width}×{currentTarget.Area.Height} · 单击捕获 · Tab 切换 · Esc 取消";
    }
}
