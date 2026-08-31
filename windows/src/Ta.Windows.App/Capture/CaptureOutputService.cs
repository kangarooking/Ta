using System.IO;
using Ta.Windows.App.Results;
using Ta.Windows.App.Settings;
using Ta.Windows.Core.Export;
using Ta.Windows.Core.Settings;
using Ta.Windows.Platform.Export;

namespace Ta.Windows.App.Capture;

public interface ICaptureOutputService
{
    string SaveToTemporaryDirectory(CaptureResult result, CaptureOutputSettings settings);

    string SaveVisionText(CaptureResult result, string text);
}

public sealed class CaptureOutputService(IWindowsImageExportService exportService) : ICaptureOutputService
{
    public string SaveToTemporaryDirectory(CaptureResult result, CaptureOutputSettings settings)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(settings);
        var directory = AppSettingsStore.ExpandTemporaryDirectory(settings.TemporaryDirectory);
        Directory.CreateDirectory(directory);
        var baseName = SafeFileName.Create(result.CapturedAt, result.Kind);
        var uniqueSuffix = result.JobId.ToString("N")[..8];
        var path = Path.Combine(directory, $"{baseName}_{uniqueSuffix}.png");
        exportService.Save(result.Image, path, ImageExportFormat.Png);
        return Path.GetFullPath(path);
    }

    public string SaveVisionText(CaptureResult result, string text)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (string.IsNullOrWhiteSpace(result.SavedFilePath))
        {
            throw new InvalidOperationException("截图尚未自动保存，无法确定 AI 识图文本路径。");
        }

        var textPath = Path.ChangeExtension(result.SavedFilePath, ".vision.txt");
        File.WriteAllText(textPath, text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return Path.GetFullPath(textPath);
    }
}
