using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media.Imaging;

namespace Ta.Windows.App.Imaging;

public static class BitmapSourceConverter
{
    private const int MaximumPreviewDimension = 2200;

    public static BitmapSource Create(Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var scale = Math.Min(
            1d,
            MaximumPreviewDimension / (double)Math.Max(bitmap.Width, bitmap.Height));
        var width = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
        var height = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
        using var preview = scale < 1d
            ? new Bitmap(bitmap, new Size(width, height))
            : new Bitmap(bitmap);
        using var stream = new MemoryStream();
        preview.Save(stream, ImageFormat.Png);
        stream.Position = 0;

        var result = new BitmapImage();
        result.BeginInit();
        result.CacheOption = BitmapCacheOption.OnLoad;
        result.StreamSource = stream;
        result.EndInit();
        result.Freeze();
        return result;
    }
}
