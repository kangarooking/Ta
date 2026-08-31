using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Ta.Windows.Core.Export;

namespace Ta.Windows.Platform.Export;

public interface IWindowsImageExportService
{
    void Save(Bitmap image, string path, ImageExportFormat format, int jpegQuality = 92);
}

public sealed class WindowsImageExportService : IWindowsImageExportService
{
    public void Save(
        Bitmap image,
        string path,
        ImageExportFormat format,
        int jpegQuality = 92)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("保存目录不存在，当前截图仍然安全保留。");
        }

        switch (format)
        {
            case ImageExportFormat.Png:
                image.Save(path, ImageFormat.Png);
                break;
            case ImageExportFormat.Jpeg:
                SaveJpeg(image, path, jpegQuality);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), "当前版本仅支持 PNG 和 JPEG 导出。");
        }
    }

    private static void SaveJpeg(Bitmap image, string path, int quality)
    {
        if (quality is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(quality), "JPEG 质量必须在 1 到 100 之间。");
        }

        var codec = ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(candidate => candidate.FormatID == ImageFormat.Jpeg.Guid)
            ?? throw new InvalidOperationException("Windows 没有可用的 JPEG 编码器，当前截图仍然安全保留。");

        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        image.Save(path, codec, parameters);
    }
}
