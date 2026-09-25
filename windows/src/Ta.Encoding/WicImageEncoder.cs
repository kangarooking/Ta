using System.Windows.Media.Imaging;
using Ta.Core.Imaging;

namespace Ta.Encoding;

/// <summary>
/// 图像编码失败。
///
/// 文案逐字对应 Mac 版 <c>ImageExportError</c>
/// （Sources/AIScreenshotApp/System/ImageExportService.swift:11-22）。
/// </summary>
public sealed class ImageEncodingException : Exception
{
    public ImageEncodingException(string message)
        : base(message)
    {
    }

    public ImageEncodingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 按文件名后缀决定的编码格式。
///
/// 对应 Mac 版 ImageExportService.write 的扩展名分派
/// （Sources/AIScreenshotApp/System/ImageExportService.swift:67）：
/// <c>["jpg", "jpeg"].contains(url.pathExtension.lowercased())</c> ——
/// 命中这两个后缀（忽略大小写）走 JPEG，**其余一律 PNG**，包括没有后缀的情况。
/// </summary>
public enum ImageFileFormat
{
    /// <summary>PNG，无损。</summary>
    Png,

    /// <summary>JPEG，有损，需配质量参数。</summary>
    Jpeg,
}

/// <summary>
/// WIC 实现的 <see cref="IImageEncoder"/>（经 WPF 的官方投影调用）。
///
/// 逐字对齐 Mac 版的三个编码调用点：
///  · 导出保存      → JPEG <c>compressionFactor = 0.92</c> / PNG 无损
///                    （ImageExportService.swift:69-70）
///  · 送视觉模型    → JPEG <c>0.88</c>（MultimodalRecognitionService.swift:131）
///  · 长截图分段    → 仅 PNG（ImageExportService.swift:35）
///
/// ## 为什么走 WPF 投影而不是手写 WIC COM 声明
///
/// 原实现手写了整套 WIC COM vtable（WicInterop.cs），其中
/// <c>IWicImagingFactory</c> 多声明了一个不存在的方法，使其后所有槽位错位 +1，
/// 「编码器」实际是格式转换器 —— 产出的 PNG 结构合法但**像素全黑**。
/// 这类缺陷编译器不报、只能靠往返测试抓，且每加一个接口都要重新考古 vtable。
///
/// <see cref="BitmapEncoder"/>（WPF）内部就是官方维护的 WIC 封装，
/// vtable 由 SDK 生成器保证，没有手写声明的风险，所以改走这条路：
///  · <see cref="PngBitmapEncoder"/> —— 无损，且 PNG 恒为**未预乘** RGBA；
///  · <see cref="JpegBitmapEncoder"/> —— <see cref="JpegBitmapEncoder.QualityLevel"/>
///    即 Mac 的 compressionFactor × 100（0.92 → 92）。
///
/// 关于像素格式的取舍（这是移植里最容易出错、也最不容易被发现的一点）：
/// <see cref="RgbaBitmap.Pixels"/> 是**未预乘** RGBA。交给 WPF 时用
/// <see cref="System.Windows.Media.PixelFormats.Bgra32"/>（同样未预乘），
/// 显式不做预乘；半透明像素不被 <c>RGB *= A/255</c> 压暗 ——
/// 往返测试里有专门的半透明用例锁住这一点。
/// </summary>
public sealed class WicImageEncoder : IImageEncoder
{
    /// <summary>导出保存用的 JPEG 质量。对应 Mac <c>compressionFactor = 0.92</c>。</summary>
    public const int ExportJpegQuality = 92;

    /// <summary>送视觉模型的 JPEG 质量。对应 Mac <c>compressionFactor = 0.88</c>。</summary>
    public const int VisionModelJpegQuality = 88;

    /// <summary>PNG 恒为无损（Mac 用 <c>properties: [:]</c>，即不设任何有损选项）。</summary>
    public byte[] EncodePng(RgbaBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        return Encode(bitmap, png: true, quality: 0);
    }

    /// <summary>
    /// quality 取值 0–100，对应 Mac 的 compressionFactor（0.0–1.0）。
    /// WPF 的 <c>QualityLevel</c> 也是 0–100，直接透传。
    /// </summary>
    public byte[] EncodeJpeg(RgbaBitmap bitmap, int quality)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentOutOfRangeException.ThrowIfNegative(quality);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quality, 100);

        return Encode(bitmap, png: false, quality);
    }

    /// <summary>
    /// 按文件后缀选择格式后编码。这是导出保存路径（含保存对话框选出的任意文件名）的入口。
    /// </summary>
    public byte[] EncodeForFile(string fileName, RgbaBitmap bitmap)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(bitmap);

        return ResolveFileFormat(fileName) == ImageFileFormat.Jpeg
            ? EncodeJpeg(bitmap, ExportJpegQuality)
            : EncodePng(bitmap);
    }

    /// <summary>
    /// 按文件后缀决定格式。
    ///
    /// 对应 Mac <c>["jpg", "jpeg"].contains(url.pathExtension.lowercased())</c>
    /// （ImageExportService.swift:67）。注意「其余 → PNG」是 Mac 的实际行为，
    /// 不是保守的「未知就报错」—— 移植必须复现，否则用户存成 <c>.bmp</c> 会被拒。
    /// </summary>
    public static ImageFileFormat ResolveFileFormat(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var extension = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return extension is "jpg" or "jpeg" ? ImageFileFormat.Jpeg : ImageFileFormat.Png;
    }

    /// <summary>
    /// 编码并原子写入文件。
    ///
    /// 对应 Mac <c>data.write(to: url, options: .atomic)</c>（ImageExportService.swift:73）。
    /// Windows 上 <see cref="File.WriteAllBytes(string, byte[])"/> **不是**原子的 ——
    /// 中途断电/崩溃会留下截断的半张图（见参考文档 §14 风险 #36）。
    /// 因此这里写同目录临时文件再 <see cref="File.Move(string, string, bool)"/> 替换，
    /// 后者内部就是 <c>MoveFileEx(MOVEFILE_REPLACE_EXISTING)</c>。
    /// </summary>
    public void Save(RgbaBitmap bitmap, string path)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ImageEncodingException($"无法保存图片：目录无效（{fullPath}）。");
        }

        Directory.CreateDirectory(directory);

        var data = EncodeForFile(Path.GetFileName(fullPath), bitmap);
        var temporaryPath = fullPath + ".ta-tmp-" + Guid.NewGuid().ToString("n");

        try
        {
            File.WriteAllBytes(temporaryPath, data);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            TryDelete(temporaryPath);
            throw new ImageEncodingException($"无法保存图片：{ex.Message}", ex);
        }
    }

    /// <summary>集中做字节编码。png=false 时按 JPEG quality 编码。</summary>
    private static byte[] Encode(RgbaBitmap bitmap, bool png, int quality)
    {
        try
        {
            // 来源像素：未预乘 RGBA → 未预乘 BGRA（纯通道重排，亮度不变）。
            // WPF 的 Bgra32 内存序是 B,G,R,A，与 WIC 的 32bppBGRA 一致。
            var bgra = SwapRedBlue(bitmap);

            // BitmapSource.Create 按给定 stride 复制整块像素。
            // RgbaBitmap.Stride = Width×4（无行填充），天然满足 Bgra32 的 4 字节对齐。
            var source = BitmapSource.Create(
                bitmap.Width,
                bitmap.Height,
                dpiX: 96,
                dpiY: 96,
                System.Windows.Media.PixelFormats.Bgra32,
                palette: null,
                bgra,
                bitmap.Stride);
            source.Freeze();

            // WPF 的 QualityLevel 合法区间是 1–100；Mac/WIC 允许 0.0，
            // 语义上映射为「最低可用质量」，这里夹到 1。
            BitmapEncoder encoder = png
                ? new PngBitmapEncoder()
                : new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
            encoder.Frames.Add(BitmapFrame.Create(source));

            using var output = new MemoryStream();
            encoder.Save(output);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is not ImageEncodingException)
        {
            throw new ImageEncodingException(
                $"无法编码图片：{(png ? "PNG" : "JPEG")} 编码失败（{ex.Message}）。", ex);
        }
    }

    /// <summary>RGBA → BGRA 的托管侧通道交换（只换 R 和 B，A 与 G 不动）。</summary>
    private static byte[] SwapRedBlue(RgbaBitmap bitmap)
    {
        var source = bitmap.Pixels;
        var result = new byte[source.Length];
        for (var i = 0; i < source.Length; i += 4)
        {
            result[i] = source[i + 2];
            result[i + 1] = source[i + 1];
            result[i + 2] = source[i];
            result[i + 3] = source[i + 3];
        }

        return result;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 临时文件清理失败不该盖掉原始异常。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }
}
