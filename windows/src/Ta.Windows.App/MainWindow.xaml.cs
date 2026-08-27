using System.ComponentModel;
using Ta.Windows.App.Capture;
using Ta.Windows.Core.Settings;
using Ta.Windows.Platform.Diagnostics;

namespace Ta.Windows.App;

public partial class MainWindow : System.Windows.Window
{
    private readonly Func<CaptureAction, bool, Task> executeCapture;
    private readonly Action restoreLatestResult;
    private readonly Action openSettings;
    private readonly Action<bool> privateModeChanged;
    private bool allowClose;

    public MainWindow(
        Func<CaptureAction, bool, Task> executeCapture,
        Action restoreLatestResult,
        Action openSettings,
        Action<bool> privateModeChanged)
    {
        InitializeComponent();
        this.executeCapture = executeCapture ?? throw new ArgumentNullException(nameof(executeCapture));
        this.restoreLatestResult = restoreLatestResult ?? throw new ArgumentNullException(nameof(restoreLatestResult));
        this.openSettings = openSettings ?? throw new ArgumentNullException(nameof(openSettings));
        this.privateModeChanged = privateModeChanged ?? throw new ArgumentNullException(nameof(privateModeChanged));
    }

    public void SetStatus(string message) => StatusText.Text = message;

    public void CloseForExit()
    {
        allowClose = true;
        Close();
    }

    public void ApplyShortcutLabels(ShortcutSettings shortcuts)
    {
        SmartTextButton.Content = $"智能识图     {shortcuts.SmartText}";
        RegionButton.Content = $"区域截图     {shortcuts.Region}";
        WindowButton.Content = $"活动窗口截图     {shortcuts.Window}";
        FullDesktopButton.Content = $"全屏截图     {shortcuts.FullDesktop}";
        RepeatButton.Content = $"重复区域     {shortcuts.RepeatRegion}";
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!allowClose)
        {
            e.Cancel = true;
            Hide();
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                ProcessMemoryService.TrimIdleWorkingSet);
            return;
        }

        base.OnClosing(e);
    }

    private bool IsPrivateMode => PrivateModeCheckBox.IsChecked == true;

    private async Task ExecuteAsync(CaptureAction action)
    {
        try
        {
            SetStatus("正在准备截图，请按 Esc 取消区域选择。");
            await executeCapture(action, IsPrivateMode);
        }
        catch (Exception exception)
        {
            SetStatus($"截图没有完成。当前剪贴板和已有结果仍然安全。下一步：{exception.Message}");
        }
    }

    private async void SmartTextButton_OnClick(object sender, System.Windows.RoutedEventArgs e) =>
        await ExecuteAsync(CaptureAction.SmartText);

    private async void RegionButton_OnClick(object sender, System.Windows.RoutedEventArgs e) =>
        await ExecuteAsync(CaptureAction.Region);

    private async void FullDesktopButton_OnClick(object sender, System.Windows.RoutedEventArgs e) =>
        await ExecuteAsync(CaptureAction.FullDesktop);

    private async void WindowButton_OnClick(object sender, System.Windows.RoutedEventArgs e) =>
        await ExecuteAsync(CaptureAction.Window);

    private async void RepeatButton_OnClick(object sender, System.Windows.RoutedEventArgs e) =>
        await ExecuteAsync(CaptureAction.RepeatRegion);

    private void RestoreResultButton_OnClick(object sender, System.Windows.RoutedEventArgs e) =>
        restoreLatestResult();

    private void SettingsButton_OnClick(object sender, System.Windows.RoutedEventArgs e) =>
        openSettings();

    private void PrivateModeCheckBox_OnChanged(object sender, System.Windows.RoutedEventArgs e) =>
        privateModeChanged(IsPrivateMode);
}
