using Ta.Core.Imaging;
using Ta.OCR.Engines;
using Ta.OCR.Models;
using Ta.OCR.Packs;

namespace Ta.OCR;

/// <summary>OCR 相关设置项的读取接口。</summary>
public interface IOCRSettingStore
{
    /// <summary>
    /// 设置项 <c>ocrEngine</c>。空值/未知值由实现方回退到 <see cref="OCREnginePreference.AppleVision"/>。
    /// </summary>
    OCREnginePreference Engine { get; }

    /// <summary>设置项 <c>ocrPackCatalogURL</c>（必须 https，参考文档 §9.7）。</summary>
    string? CatalogUrl { get; }
}

/// <summary>
/// 云端 OCR（DeepSeek-OCR-2）识别入口。
///
/// 对应 Mac 版 <c>DeepSeekOCRRecognitionService</c>
/// （<c>AIScreenshotApp/Recognition/DeepSeekOCRRecognitionService.swift:1-63</c>）。
///
/// ⚠️ 该服务依赖 <c>DeepSeekOCR2Client</c> 与 Keychain，属于 Ta.AI 的职责，
/// 尚未移植到 Windows。宿主应把实现注入 <see cref="ConfiguredOCRService"/>；
/// 未注入而用户又选了 <c>deepSeekOCR2</c> 时会得到明确的中文报错，
/// **不会**静默降级到本地引擎 —— 静默降级会让用户以为已经上传云端，反而更糟。
/// </summary>
public interface ICloudOCRRecognizer
{
    Task<OCRResult> RecognizeAsync(
        RgbaBitmap image,
        IReadOnlyList<string> languages,
        CancellationToken cancellationToken);
}

/// <summary>
/// OCR 分发链。
///
/// 逐字对应 Mac 版 <c>ConfiguredOCRService</c>
/// （<c>OptionalOCRPackManager.swift:682-714</c>）。
///
/// <b>三级链（顺序即契约）</b>
/// <list type="number">
///   <item><c>deepSeekOCR2</c> → 云端（<see cref="ICloudOCRRecognizer"/>）</item>
///   <item>引擎依赖增强包**且包已安装** → 走包</item>
///   <item>否则 → 内置本地引擎（<see cref="Engines.WindowsMediaOcrEngine"/>）</item>
/// </list>
///
/// ⚠️ <b>第 2 条是「条件成立才走包」，所以包未安装时自然落到第 3 条，
/// 静默回退本地引擎、不报错</b> —— 这正是 Mac 的行为（`:705</c>），
/// 也是参考文档 §9.7 要求的「未安装增强包时 App 自动回退本地引擎」。
/// </summary>
public sealed class ConfiguredOCRService
{
    private readonly IOCRSettingStore _settings;
    private readonly OptionalOCRPackManager _packs;
    private readonly IOCRTextEngine _localEngine;
    private readonly ICloudOCRRecognizer? _cloudRecognizer;

    public ConfiguredOCRService(
        IOCRSettingStore settings,
        IOCRTextEngine? localEngine = null,
        OptionalOCRPackManager? packs = null,
        ICloudOCRRecognizer? cloudRecognizer = null)
    {
        _settings = settings;
        _localEngine = localEngine ?? new Engines.WindowsMediaOcrEngine();
        _packs = packs ?? CreateDefaultPacks(settings);
        _cloudRecognizer = cloudRecognizer;
    }

    /// <summary>暴露包管理器，供设置页展示「已安装版本 / 卸载 / 更新」。</summary>
    public OptionalOCRPackManager Packs => _packs;

    /// <summary>内置本地引擎。</summary>
    public IOCRTextEngine LocalEngine => _localEngine;

    private static OptionalOCRPackManager CreateDefaultPacks(IOCRSettingStore settings)
        => new(new OCRPackManagerOptions
        {
            ApplicationSupportRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AI Screenshot"),
            ConfiguredCatalogUrl = settings.CatalogUrl,
        });

    /// <summary>对应 <c>prewarmIfNeeded()</c>（`:686-693</c>）。</summary>
    public void PrewarmIfNeeded()
    {
        var engine = _settings.Engine;

        if (engine.UsesOptionalPack() && _packs.IsInstalled(engine))
        {
            _ = _packs.PrewarmAsync(engine);
        }
    }

    /// <summary>对应 <c>recognize(image:languages:mergeWrappedLines:)</c>（`:695-713</c>）。</summary>
    public async Task<OCRResult> RecognizeAsync(
        RgbaBitmap image,
        IReadOnlyList<string>? languages = null,
        bool mergeWrappedLines = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        var engine = _settings.Engine;

        // :699-704 —— 第 1 级：云端。
        if (engine == OCREnginePreference.DeepSeekOCR2)
        {
            if (_cloudRecognizer is null)
            {
                throw new InvalidOperationException(
                    "已选择 DeepSeek-OCR-2，但宿主尚未提供云端识别实现（ICloudOCRRecognizer）。"
                    + "请由 Ta.AI 注入 DeepSeekOCR2Client 的实现。");
            }

            return await _cloudRecognizer
                .RecognizeAsync(image, languages ?? [], cancellationToken)
                .ConfigureAwait(false);
        }

        // :705-707 —— 第 2 级：增强包。包未安装时这一条不成立，自然落到本地引擎。
        if (engine.UsesOptionalPack() && _packs.IsInstalled(engine))
        {
            return await _packs.RecognizeAsync(image, engine, cancellationToken).ConfigureAwait(false);
        }

        // :708-712 —— 第 3 级：内置本地引擎。
        return await _localEngine
            .RecognizeAsync(
                image,
                new OCRRecognizeOptions(languages, mergeWrappedLines),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>引擎偏好变化时收尾（对应 Mac 里换引擎即 <c>PersistentOCRWorker.shared.stop()</c> 的隐式行为）。</summary>
    public void StopWorker() => _packs.Worker.Stop();
}
