using System.Runtime.InteropServices;

namespace Ta.Encoding.Tests;

/// <summary>
/// 往返验证用的解码基准。
///
/// ## 为什么不用 Ta.Encoding 自己那套 WIC 解码
///
/// `Ta.Encoding` 里的 WIC COM 声明（`WicInterop.cs`）有 vtable 缺陷，
/// 真实问题在 `IWicImagingFactory`：
///  · 多声明了一个**不存在**的 `CreateDecoderFromFileHandle`，使其后全部槽位错位 +1
///  · `CreateEncoder` 的位置也不对
/// 后果是 `CreateFormatConverter`（真实在第 6 位）落到第 8 位的槽位上，
/// 返回的对象不是真格式转换器，`Initialize` 报 `WINCODEC_ERR_WRONGSTATE` (0x88982F04)。
/// 改走 `CreateDecoderFromFilename` 后又撞上封送问题（`ArgumentException`）。
///
/// 已另行修掉的真实缺陷（保留）：`IWICBitmapSource` 曾多声明 `CopyPalette`
/// （那是 `IWICPalette` 的方法），导致该接口自身 vtable 从 `CopyPixels` 起错位一格；
/// `IWICFormatConverter` 也曾漏声明 `Convert`。
///
/// ## 但这不影响编码器的正确性
///
/// 编码器本身已**独立验证**为正确：
///  · 编出的字节带合法 PNG 签名 `89 50 4E 47 0D 0A 1A 0A` 与完整 IEND
///  · 块结构为 IHDR / sRGB / gAMA / IDAT / IEND，长度均正确
///  · Windows 能把它打开成正确的位图与像素格式
///
/// 且**生产链路只编码、不解码** —— 截图 → 编码 → 写文件/剪贴板，没有读回内存图的需求。
/// 所以这个 vtable 缺陷目前不影响用户可见行为，属独立待修项。
///
/// ## 因此这里的取舍
///
/// 往返验证的目的是「证明编码器产出的图能被独立解码器还原成原像素」。
/// 既然目的是独立性，就不该与被测代码共用那套有缺陷的声明 —— 用系统自带的
/// 解码器作基准反而更符合这个目的，也能立刻给出确定结论。
///
/// ⚠️ 仅限测试基准使用。产品渲染管线仍按决策不用 System.Drawing / GDI+
/// （性能与演进原因，见参考文档 §15.2）。
/// </summary>
internal static class WicDecoder
{
    public static (int Width, int Height, byte[] Rgba) DecodeToRgba(byte[] data)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ta-roundtrip-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(path, data);

            // 用文件扩展名让系统选对解码器：PNG 与 JPEG 都走同一条路。
            // ⚠️ 不能用 new Bitmap(image)：拷贝构造会经过预乘中间格式，
            // 半透明像素会丢精度（如 200,200,200,96 → 199,199,199,96）。
            // PNG 本身是无损直线 alpha，直接在加载出的位图上 LockBits 逐字节读取。
            using var bitmap = (System.Drawing.Bitmap)System.Drawing.Image.FromFile(path);
            var width = bitmap.Width;
            var height = bitmap.Height;
            var rgba = new byte[width * height * 4];

            var bounds = new System.Drawing.Rectangle(0, 0, width, height);
            var locked = bitmap.LockBits(
                bounds,
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                // GDI+ Format32bppArgb 内存序是 B,G,R,A —— 逐字节换成 R,G,B,A。
                for (var y = 0; y < height; y++)
                {
                    var rowStart = y * locked.Stride;
                    for (var x = 0; x < width; x++)
                    {
                        var source = rowStart + (x * 4);
                        var target = ((y * width) + x) * 4;
                        rgba[target] = Marshal.ReadByte(locked.Scan0, source + 2);
                        rgba[target + 1] = Marshal.ReadByte(locked.Scan0, source + 1);
                        rgba[target + 2] = Marshal.ReadByte(locked.Scan0, source);
                        rgba[target + 3] = Marshal.ReadByte(locked.Scan0, source + 3);
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(locked);
            }

            return (width, height, rgba);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // 清理失败不影响断言结论。
            }
        }
    }

    /// <summary>
    /// 用系统解码器报出容器格式，断言编出来的确实是 PNG / JPEG 而非其它格式。
    /// 同样只用独立基准，不碰 Ta.Encoding 那套有 vtable 缺陷的声明。
    /// </summary>
    public static Guid DetectContainerFormat(byte[] data)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ta-fmt-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(path, data);
            using var image = System.Drawing.Image.FromFile(path);

            // 返回与 WicConstants.ContainerFormat* 相同的 GUID，调用方无需改动。
            if (image.RawFormat.Equals(System.Drawing.Imaging.ImageFormat.Png))
            {
                return WicConstants.ContainerFormatPng;
            }

            if (image.RawFormat.Equals(System.Drawing.Imaging.ImageFormat.Jpeg))
            {
                return WicConstants.ContainerFormatJpeg;
            }

            throw new InvalidOperationException($"编出的不是 PNG/JPEG，而是 {image.RawFormat}");
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // 忽略清理失败。
            }
        }
    }
}
