using System.Drawing;
using System.IO;
using Microsoft.Win32;
using Ta.Windows.App.Imaging;
using Ta.Windows.Core.Export;
using Ta.Windows.Core.Vision;
using Ta.Windows.Platform.Clipboard;
using Ta.Windows.Platform.Export;

namespace Ta.Windows.App.Results;

public partial class ResultWindow : System.Windows.Window
{
    private readonly CaptureResult result;
    private readonly IWindowsClipboardService clipboardService;
    private readonly IWindowsImageExportService exportService;
    private readonly Action<Bitmap> pinImage;
    private readonly Func<CaptureResult, Task<VisionAnalysisResult>> recognizeText;
    private VisionAnalysisResult? visionResult;

    public ResultWindow(
        CaptureResult result,
        IWindowsClipboardService clipboardService,
        IWindowsImageExportService exportService,
        Action<Bitmap> pinImage,
        Func<CaptureResult, Task<VisionAnalysisResult>> recognizeText,
        VisionAnalysisResult? visionResult = null)
    {
        InitializeComponent();
        this.result = result;
        this.clipboardService = clipboardService;
        this.exportService = exportService;
        this.pinImage = pinImage;
        this.recognizeText = recognizeText;
        this.visionResult = visionResult;
        PreviewImage.Source = BitmapSourceConverter.Create(result.Image);
        StatusText.Text = BuildStatus(result.StatusMessage);
        if (visionResult?.HasText == true)
        {
            VisionTextBox.Text = visionResult.Text;
            VisionResultBorder.Visibility = System.Windows.Visibility.Visible;
            CopyVisionTextButton.Visibility = System.Windows.Visibility.Visible;
        }

        if (!string.IsNullOrWhiteSpace(result.SavedFilePath))
        {
            CopyPathButton.Visibility = System.Windows.Visibility.Visible;
        }
    }

    private string BuildStatus(string message) =>
        result.IsPrivate
            ? $"私密模式 · {message} 本次结果不会进入历史或云端。"
            : message;

    private void CopyButton_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            clipboardService.SetImageExplicit(result.Image);
            StatusText.Text = BuildStatus("图片已复制到剪贴板。");
        }
        catch (Exception exception)
        {
            StatusText.Text = $"复制没有完成，截图仍然安全保留。下一步：{exception.Message}";
        }
    }

    private void SaveButton_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = SafeFileName.Create(result.CapturedAt, result.Kind),
            DefaultExt = ".png",
            AddExtension = true,
            Filter = "PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg;*.jpeg)|*.jpg;*.jpeg",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var extension = Path.GetExtension(dialog.FileName);
            var format = extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                         extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                ? ImageExportFormat.Jpeg
                : ImageExportFormat.Png;
            exportService.Save(result.Image, dialog.FileName, format);
            StatusText.Text = BuildStatus($"图片已保存到 {dialog.FileName}");
        }
        catch (Exception exception)
        {
            StatusText.Text = $"保存没有完成，截图仍然安全保留。下一步：{exception.Message}";
        }
    }

    private void CopyVisionTextButton_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (visionResult?.HasText != true)
        {
            return;
        }

        try
        {
            clipboardService.SetTextExplicit(visionResult.Text);
            StatusText.Text = BuildStatus("AI 识图结果已复制到剪贴板。");
        }
        catch (Exception exception)
        {
            StatusText.Text = $"复制识图结果没有完成，结果仍然安全保留。下一步：{exception.Message}";
        }
    }

    private void CopyPathButton_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(result.SavedFilePath))
        {
            return;
        }

        try
        {
            clipboardService.SetTextExplicit(result.SavedFilePath);
            StatusText.Text = BuildStatus("完整文件路径已复制到剪贴板。");
        }
        catch (Exception exception)
        {
            StatusText.Text = $"复制路径没有完成，文件仍然安全保留。下一步：{exception.Message}";
        }
    }

    private async void RecognizeTextButton_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        RecognizeTextButton.IsEnabled = false;
        StatusText.Text = BuildStatus("正在把当前选区的压缩副本发送到设置中的视觉模型，请稍候。");
        try
        {
            visionResult = await recognizeText(result);
            VisionTextBox.Text = visionResult.Text;
            VisionResultBorder.Visibility = System.Windows.Visibility.Visible;
            CopyVisionTextButton.Visibility = System.Windows.Visibility.Visible;
            clipboardService.SetTextExplicit(visionResult.Text);
            StatusText.Text = BuildStatus($"AI 识别完成并已复制文字。模型：{visionResult.Model}。");
        }
        catch (Exception exception)
        {
            StatusText.Text = $"AI 识别没有完成，原截图和文件路径仍然安全保留。下一步：{exception.Message}";
        }
        finally
        {
            RecognizeTextButton.IsEnabled = true;
        }
    }

    private void PinButton_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        Bitmap? clone = null;
        try
        {
            var pixelCount = checked((long)result.Image.Width * result.Image.Height);
            if (pixelCount > 25_000_000)
            {
                throw new InvalidOperationException("图片超过 2500 万像素，为避免内存不足，请先保存或裁剪后再贴图。");
            }

            clone = new Bitmap(result.Image);
            pinImage(clone);
            clone = null;
            StatusText.Text = BuildStatus("图片已钉在桌面最上层。");
        }
        catch (Exception exception)
        {
            StatusText.Text = $"贴图没有完成，原截图仍然安全保留。下一步：{exception.Message}";
        }
        finally
        {
            clone?.Dispose();
        }
    }

    private void Window_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape)
        {
            return;
        }

        e.Handled = true;
        Close();
    }

    private void CloseButton_OnClick(object sender, System.Windows.RoutedEventArgs e) => Close();
}
