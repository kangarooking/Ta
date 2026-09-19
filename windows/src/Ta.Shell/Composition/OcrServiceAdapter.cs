using Ta.Core.Imaging;
using Ta.OCR;
using Ta.OCR.Models;
using Ta.Shell.Contracts;
using Ta.Shell.Models;
using Ta.Shell.Orchestration;
using ShellOcrResult = Ta.Shell.Contracts.OcrResult;

namespace Ta.Shell.Composition;

/// <summary>
/// <see cref="IOCRSettingStore"/> 的设置文件实现：读 <c>ocrEngine</c> 与 <c>ocrPackCatalogURL</c>。
/// </summary>
public sealed class SettingsOcrSettingStore : IOCRSettingStore
{
    private readonly JsonAppSettingsStore _settings;

    public SettingsOcrSettingStore(JsonAppSettingsStore settings)
    {
        _settings = settings;
    }

    /// <inheritdoc />
    public OCREnginePreference Engine =>
        OCREnginePreferenceExtensions.ParseEngine(_settings.ReadString(SettingKeys.OcrEngine, string.Empty));

    /// <inheritdoc />
    public string? CatalogUrl => _settings.Read("ocrPackCatalogURL");
}

/// <summary>
/// DeepSeek-OCR-2 云端识别。对应 Mac 版 <c>DeepSeekOCRRecognitionService</c>
/// （AIScreenshotApp/Recognition/DeepSeekOCRRecognitionService.swift:17-45）：
/// 设置里的 baseURL / model / promptMode → <see cref="Ta.AI.DeepSeekOCR2Client"/>。
///
/// 服务地址用本地部署（如 <c>http://127.0.0.1:8000/v1</c>）时无需 API Key；
/// API Key 若存在于与视觉 Profile 相同的密钥存储里，这里不做关联 ——
/// DeepSeek-OCR-2 是独立服务，与 AI Provider 档案无关（与 Mac 一致）。
/// </summary>
public sealed class DeepSeekCloudOcrRecognizer : ICloudOCRRecognizer
{
    private readonly JsonAppSettingsStore _settings;
    private readonly Ta.AI.DeepSeekOCR2Client _client = new();

    public DeepSeekCloudOcrRecognizer(JsonAppSettingsStore settings)
    {
        _settings = settings;
    }

    /// <inheritdoc />
    public async Task<OCRResult> RecognizeAsync(
        RgbaBitmap image,
        IReadOnlyList<string> languages,
        CancellationToken cancellationToken)
    {
        var baseUrl = _settings.Read(Ta.Settings.Core.SettingsKeys.DeepSeekOcrBaseUrl) ?? string.Empty;
        var model = _settings.ReadString(Ta.Settings.Core.SettingsKeys.DeepSeekOcrModel,
            Ta.AI.DeepSeekOCR2Client.LatestOfficialModel);
        var mode = Ta.AI.DeepSeekOCRPromptModeExtensions.ParseRawValue(
            _settings.Read(Ta.Settings.Core.SettingsKeys.DeepSeekOcrPromptMode))
            ?? Ta.AI.DeepSeekOCRPromptMode.PlainText;

        // Mac 版同样忽略 languages（DeepSeek-OCR-2 不接受语言参数），仅用于日志语义。
        _ = languages;

        var png = GdiEncode(image);
        var text = await _client
            .RecognizeAsync(baseUrl, model, apiKey: string.Empty, imageData: png,
                mimeType: "image/png", mode: mode, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new OCRResult(
            text,
            CaptureContentType.PlainText,
            confidence: 1,
            languages: languages,
            engine: OCREnginePreference.DeepSeekOCR2);
    }

    private static byte[] GdiEncode(RgbaBitmap image) =>
        Ta.Encoding.GdiImageEncoder.Instance.EncodePng(image);
}

/// <summary>
/// <see cref="IOcrService"/>（Shell 应用层）→ <see cref="Ta.OCR.ConfiguredOCRService"/> 适配。
///
/// 对应 Mac 版在 AppModel 里直接使用 ConfiguredOCRService。
/// <see cref="Prewarm"/> 对应 <c>prewarmIfNeeded()</c>。
/// </summary>
public sealed class ConfiguredOcrServiceAdapter : IOcrService
{
    private readonly Ta.OCR.ConfiguredOCRService _inner;
    private readonly SettingsOcrSettingStore _settings;

    public ConfiguredOcrServiceAdapter(Ta.OCR.ConfiguredOCRService inner, SettingsOcrSettingStore settings)
    {
        _inner = inner;
        _settings = settings;
    }

    /// <summary>暴露包管理器给设置页（当前独立进程形态下暂不消费，保留接缝）。</summary>
    public Ta.OCR.Packs.OptionalOCRPackManager Packs => _inner.Packs;

    /// <inheritdoc />
    public string EngineDisplayName
    {
        get
        {
            try
            {
                return _settings.Engine.DisplayName();
            }
            catch (Exception)
            {
                return "Windows 媒体 OCR（内置）";
            }
        }
    }

    /// <inheritdoc />
    public async Task<ShellOcrResult> RecognizeAsync(
        RgbaBitmap image,
        OcrRequestOptions options,
        CancellationToken cancellationToken = default)
    {
        var result = await _inner
            .RecognizeAsync(image, options.Languages, options.MergeWrappedLines, cancellationToken)
            .ConfigureAwait(false);

        return new ShellOcrResult(
            result.Text,
            MapContentType(result.ContentType),
            result.Confidence,
            result.Engine.DisplayName());
    }

    /// <inheritdoc />
    public void Prewarm() => _inner.PrewarmIfNeeded();

    private static OcrContentType MapContentType(CaptureContentType contentType) => contentType switch
    {
        CaptureContentType.Code => OcrContentType.Code,
        CaptureContentType.Table => OcrContentType.Table,
        CaptureContentType.QrCode => OcrContentType.QrCode,
        CaptureContentType.Formula => OcrContentType.Formula,
        CaptureContentType.Image => OcrContentType.Image,
        _ => OcrContentType.PlainText,
    };
}
