using Ta.Core.Imaging;

namespace Ta.Core.Imaging;

/// <summary>
/// 图像编码器。
///
/// 由平台层（WIC）实现。抽成接口是为了让「截图 → 保存」这条链路的其余部分
/// 不阻塞在编码实现上 —— 消费方只依赖此接口即可开发与测试。
///
/// 对应 Mac 版 ImageExportService 与 ClipboardService：
///   · PNG 无损，写入剪贴板的类型恒为 public.png
///   · JPEG quality 0.92（导出）/ 0.88（送视觉模型）
/// </summary>
public interface IImageEncoder
{
    byte[] EncodePng(RgbaBitmap bitmap);

    /// <summary>quality 取值 0–100。对应 Mac 的 compressionFactor。</summary>
    byte[] EncodeJpeg(RgbaBitmap bitmap, int quality);
}
