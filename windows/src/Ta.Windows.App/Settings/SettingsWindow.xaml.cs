using Ta.Windows.Core.Settings;

namespace Ta.Windows.App.Settings;

public partial class SettingsWindow : System.Windows.Window
{
    private readonly Func<AppSettings, string?, (bool Success, string Message)> saveSettings;
    private readonly Action<bool> showResultWindowChanged;
    private bool initialized;

    public SettingsWindow(
        AppSettings settings,
        string? apiKey,
        Func<AppSettings, string?, (bool Success, string Message)> saveSettings,
        Action<bool> showResultWindowChanged)
    {
        InitializeComponent();
        this.saveSettings = saveSettings;
        this.showResultWindowChanged = showResultWindowChanged;
        SmartTextShortcutTextBox.Text = settings.Shortcuts.SmartText;
        RegionShortcutTextBox.Text = settings.Shortcuts.Region;
        WindowShortcutTextBox.Text = settings.Shortcuts.Window;
        FullDesktopShortcutTextBox.Text = settings.Shortcuts.FullDesktop;
        RepeatRegionShortcutTextBox.Text = settings.Shortcuts.RepeatRegion;
        AutoSaveCheckBox.IsChecked = settings.Output.AutoSave;
        ShowResultWindowCheckBox.IsChecked = settings.Output.ShowResultWindow;
        HideApplicationDuringCaptureCheckBox.IsChecked = settings.Output.HideApplicationDuringCapture;
        TemporaryDirectoryTextBox.Text = settings.Output.TemporaryDirectory;
        VisionBaseUrlTextBox.Text = settings.Vision.BaseUrl;
        VisionApiKeyPasswordBox.Password = apiKey ?? string.Empty;
        VisionModelTextBox.Text = settings.Vision.Model;
        VisionTaskPromptTextBox.Text = settings.Vision.TaskPrompt;
        VisionTimeoutTextBox.Text = settings.Vision.TimeoutSeconds.ToString();
        VisionMaximumDimensionTextBox.Text = settings.Vision.MaximumImageDimension.ToString();
        VisionJpegQualityTextBox.Text = settings.Vision.JpegQuality.ToString();
        initialized = true;
    }

    private void SaveButton_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!TryReadInteger(VisionTimeoutTextBox.Text, 5, 300, "超时秒数", out var timeout) ||
            !TryReadInteger(VisionMaximumDimensionTextBox.Text, 512, 4096, "最长边像素", out var dimension) ||
            !TryReadInteger(VisionJpegQualityTextBox.Text, 40, 100, "JPEG 质量", out var quality))
        {
            return;
        }

        var settings = new AppSettings(
            new ShortcutSettings(
                SmartTextShortcutTextBox.Text.Trim(),
                RegionShortcutTextBox.Text.Trim(),
                WindowShortcutTextBox.Text.Trim(),
                FullDesktopShortcutTextBox.Text.Trim(),
                RepeatRegionShortcutTextBox.Text.Trim()),
            new CaptureOutputSettings(
                AutoSaveCheckBox.IsChecked == true,
                ShowResultWindowCheckBox.IsChecked == true,
                HideApplicationDuringCaptureCheckBox.IsChecked == true,
                TemporaryDirectoryTextBox.Text.Trim()),
            new VisionApiSettings(
                VisionBaseUrlTextBox.Text.Trim(),
                VisionModelTextBox.Text.Trim(),
                VisionTaskPromptTextBox.Text.Trim(),
                timeout,
                dimension,
                quality));

        try
        {
            var outcome = saveSettings(settings, VisionApiKeyPasswordBox.Password);
            StatusText.Text = outcome.Message;
            if (outcome.Success)
            {
                VisionApiKeyPasswordBox.Password = string.Empty;
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = $"设置没有保存，当前有效设置保持不变。下一步：{exception.Message}";
        }
    }

    private bool TryReadInteger(
        string text,
        int minimum,
        int maximum,
        string label,
        out int value)
    {
        if (int.TryParse(text, out value) && value >= minimum && value <= maximum)
        {
            return true;
        }

        StatusText.Text = $"{label}必须在 {minimum} 到 {maximum} 之间，当前设置没有保存。";
        return false;
    }

    private void CloseButton_OnClick(object sender, System.Windows.RoutedEventArgs e) => Close();

    private void ShowResultWindowCheckBox_OnChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!initialized)
        {
            return;
        }

        try
        {
            showResultWindowChanged(ShowResultWindowCheckBox.IsChecked == true);
            StatusText.Text = ShowResultWindowCheckBox.IsChecked == true
                ? "已即时启用截图结果窗口。"
                : "已即时关闭截图结果窗口；截图后只保存文件并复制路径。";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"结果窗口设置没有生效，原设置保持不变。下一步：{exception.Message}";
        }
    }
}
