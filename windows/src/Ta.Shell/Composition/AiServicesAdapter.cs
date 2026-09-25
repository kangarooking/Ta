using Ta.Core.Imaging;
using Ta.Settings.Core;
using Ta.Shell.Contracts;
using Ta.Shell.Models;
using Ta.Shell.Orchestration;
using Ta.AI;

namespace Ta.Shell.Composition;

/// <summary>
/// AI Provider 档案仓库接缝：从共享设置文件 + DPAPI 密钥存储读取「当前生效的一套模型」。
///
/// 优先 <c>aiProviderProfileStateV1.translationProfileId</c>（翻译用） /
/// <c>activeProfileId</c>（视觉用），与设置窗的语义一致；
/// 档案缺失或没配 Key 时各服务如实报「未配置」。
/// </summary>
public sealed class ProviderCatalog
{
    private readonly JsonAppSettingsStore _settings;
    private readonly Ta.Settings.Services.ISecretStore _secrets;
    private readonly AiProviderProfileStore _profiles;

    public ProviderCatalog(JsonAppSettingsStore settings, Ta.Settings.Services.ISecretStore secrets)
    {
        _settings = settings;
        _secrets = secrets;
        _profiles = new AiProviderProfileStore(new ForwardedSettingsStore(settings), secrets);
    }

    /// <summary>内部把 Shell 存储当 Ta.Settings 存储：两者都是「字符串 → 字符串」的 JSON 文件，键名共用。</summary>
    private AiProviderProfileStore Profiles => _profiles;

    /// <summary>视觉（多模态）生效档案。对应 Mac 版 MultimodalRecognitionService 的 activeProfile。</summary>
    public ActiveProfile? ActiveVision()
    {
        var state = Profiles.LoadState();
        var profile = state.ActiveProfile;
        return Wrap(profile, requireVision: true, requireText: false);
    }

    /// <summary>翻译生效档案。对应 Mac 版 ScreenshotTranslationService 的 translationProfile。</summary>
    public ActiveProfile? ActiveTranslation()
    {
        var state = Profiles.LoadState();
        var profile = state.TranslationProfile ?? state.ActiveProfile;
        return Wrap(profile, requireVision: true, requireText: true);
    }

    private ActiveProfile? Wrap(AiProviderProfile? profile, bool requireVision, bool requireText)
    {
        if (profile is null)
        {
            return null;
        }

        if (requireVision && !profile.HasVisionModel)
        {
            return null;
        }

        if (requireText && !profile.HasTextModel)
        {
            return null;
        }

        var apiKey = Profiles.ApiKey(profile.Id);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        return new ActiveProfile(
            profile.Name,
            profile.BaseUrl,
            profile.VisionModel,
            profile.TextModel,
            profile.ProviderKind,
            apiKey!);
    }

    /// <summary>生效档案快照。</summary>
    public sealed record ActiveProfile(
        string Name,
        string BaseUrl,
        string VisionModel,
        string TextModel,
        ProviderKind Kind,
        string ApiKey);
}

/// <summary>
/// <see cref="IMultimodalService"/> 的真实实现：OpenAI 兼容视觉客户端 + Provider 档案。
/// 对应 Mac 版 <c>MultimodalRecognitionService</c>。
/// </summary>
public sealed class ProviderMultimodalService : IMultimodalService
{
    private readonly ProviderCatalog _catalog;
    private readonly JsonAppSettingsStore _settings;
    private readonly Ta.AI.OpenAICompatibleVisionClient _client = new();

    public ProviderMultimodalService(ProviderCatalog catalog, JsonAppSettingsStore settings)
    {
        _catalog = catalog;
        _settings = settings;
    }

    /// <inheritdoc />
    public bool IsConfigured => _catalog.ActiveVision() is not null;

    /// <inheritdoc />
    public string ActiveModelName => _catalog.ActiveVision() is { } profile
        ? $"{profile.Name} · {profile.VisionModel}"
        : "未配置";

    /// <inheritdoc />
    public async Task<string> RecognizeAsync(
        RgbaBitmap image,
        Contracts.MultimodalTask? task = null,
        CancellationToken cancellationToken = default)
    {
        var templateRaw = task switch
        {
            Contracts.MultimodalTask.ExtractText => "extractText",
            Contracts.MultimodalTask.ExplainCode => "explainCode",
            Contracts.MultimodalTask.TableMarkdown => "tableMarkdown",
            Contracts.MultimodalTask.FormulaLaTeX => "formulaLaTeX",
            _ => null,
        };

        return await RecognizeByTemplateRaw(image, templateRaw, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 按模板 rawValue 识图（null = 用设置里的 <c>multimodalTaskTemplate</c>）。
    /// Agent 桥的 <c>analyzeImage</c> 直接传任务模板字符串，走这里。
    /// </summary>
    public async Task<string> RecognizeByTemplateRaw(
        RgbaBitmap image,
        string? templateRaw,
        CancellationToken cancellationToken = default)
    {
        var profile = _catalog.ActiveVision()
            ?? throw new InvalidOperationException("尚未配置 AI 模型：请到「设置 → AI 模型」完成配置。");

        var template = ParseTemplateRaw(templateRaw);
        var png = Ta.Encoding.GdiImageEncoder.Instance.EncodePng(image);

        return await _client
            .RecognizeAsync(
                profile.BaseUrl,
                profile.VisionModel,
                profile.ApiKey,
                png,
                template.Prompt(),
                mimeType: "image/png",
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private Ta.AI.MultimodalTaskTemplate ParseTemplateRaw(string? raw)
    {
        var normalized = string.IsNullOrWhiteSpace(raw)
            ? _settings.ReadString(SettingKeys.MultimodalTaskTemplate, "general")
            : raw.Trim();
        return normalized switch
        {
            "extractText" => Ta.AI.MultimodalTaskTemplate.ExtractText,
            "translateChinese" => Ta.AI.MultimodalTaskTemplate.TranslateChinese,
            "translateEnglish" => Ta.AI.MultimodalTaskTemplate.TranslateEnglish,
            "explainCode" => Ta.AI.MultimodalTaskTemplate.ExplainCode,
            "tableMarkdown" => Ta.AI.MultimodalTaskTemplate.TableMarkdown,
            "tableCSV" => Ta.AI.MultimodalTaskTemplate.TableCSV,
            "formulaLaTeX" => Ta.AI.MultimodalTaskTemplate.FormulaLaTeX,
            _ => Ta.AI.MultimodalTaskTemplate.General,
        };
    }
}

/// <summary>
/// <see cref="ITranslationService"/> 的真实实现：文字模型翻译 + 视觉回退。
/// 对应 Mac 版 <c>ScreenshotTranslationService</c> 的文字/图片两条路径
/// （渲染成图的部分由编排层的翻译模式决定，此处接口与 Mac 对齐）。
/// </summary>
public sealed class ProviderTranslationService : ITranslationService
{
    private readonly ProviderCatalog _catalog;
    private readonly JsonAppSettingsStore _settings;
    private readonly Ta.AI.TranslationProviderClient _client = new();

    public ProviderTranslationService(ProviderCatalog catalog, JsonAppSettingsStore settings)
    {
        _catalog = catalog;
        _settings = settings;
    }

    /// <inheritdoc />
    public bool UsesVisionFallback =>
        _settings.ReadBool(SettingKeys.TranslationUsesVisionFallback, true);

    /// <inheritdoc />
    public string TargetLanguage =>
        _settings.ReadString(SettingKeys.TranslationTargetLanguage, "简体中文");

    /// <inheritdoc />
    public string SelectedTextModelName => _catalog.ActiveTranslation() is { } profile
        ? $"{profile.Name} · {profile.TextModel}"
        : "未配置";

    /// <inheritdoc />
    public void ValidateConfiguration()
    {
        var profile = _catalog.ActiveTranslation();
        if (profile is null)
        {
            throw new InvalidOperationException(
                "请先到「设置 → AI 模型」配置一套带文字模型和视觉模型的服务，然后在翻译设置中选择它。");
        }
    }

    /// <inheritdoc />
    public async Task<string> TranslateTextAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        var profile = RequireProfile();
        var source = _settings.ReadString(SettingKeys.TranslationSourceLanguage, "自动检测");

        return await _client
            .TranslateTextAsync(
                profile.BaseUrl,
                profile.TextModel,
                profile.ApiKey,
                text,
                source,
                TargetLanguage,
                MapKind(profile.Kind),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string> TranslateImageAsync(
        RgbaBitmap image,
        CancellationToken cancellationToken = default)
    {
        var profile = RequireProfile();
        var source = _settings.ReadString(SettingKeys.TranslationSourceLanguage, "自动检测");
        var png = Ta.Encoding.GdiImageEncoder.Instance.EncodePng(image);

        return await _client
            .TranslateImageAsync(
                profile.BaseUrl,
                profile.VisionModel,
                profile.ApiKey,
                png,
                source,
                TargetLanguage,
                MapKind(profile.Kind),
                mimeType: "image/png",
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private ProviderCatalog.ActiveProfile RequireProfile() =>
        _catalog.ActiveTranslation()
        ?? throw new InvalidOperationException(
            "请先到「设置 → AI 模型」配置一套带文字模型和视觉模型的服务，然后在翻译设置中选择它。");

    private static Ta.AI.VisionProviderKind MapKind(ProviderKind kind) => kind switch
    {
        ProviderKind.AzureOpenAI => Ta.AI.VisionProviderKind.AzureOpenAI,
        ProviderKind.Anthropic => Ta.AI.VisionProviderKind.Anthropic,
        ProviderKind.GoogleGemini => Ta.AI.VisionProviderKind.GoogleGemini,
        _ => Ta.AI.VisionProviderKind.OpenAICompatible,
    };
}

/// <summary>
/// 把 Shell 的 <see cref="Ta.Shell.Contracts.ISettingsStore"/>（富类型）
/// 转发为 Ta.Settings 的 <see cref="Ta.Settings.Services.ISettingsStore"/>。
/// 每次读都直达共享 JSON 存储 —— 保证设置窗改完 Provider 档案后，截图侧立刻看到。
/// </summary>
public sealed class ForwardedSettingsStore : Ta.Settings.Services.ISettingsStore
{
    private readonly Ta.Shell.Contracts.ISettingsStore _inner;

    public ForwardedSettingsStore(Ta.Shell.Contracts.ISettingsStore inner)
    {
        _inner = inner;
    }

    /// <inheritdoc />
    public string? GetString(string key) => _inner.Read(key);

    /// <inheritdoc />
    public void SetString(string key, string value) => _inner.Write(key, value);

    /// <inheritdoc />
    public bool GetBool(string key, bool fallback) => _inner.ReadBool(key, fallback);

    /// <inheritdoc />
    public void SetBool(string key, bool value) => _inner.Write(key, value ? "true" : "false");

    /// <inheritdoc />
    public double GetDouble(string key, double fallback) => _inner.ReadDouble(key, fallback);

    /// <inheritdoc />
    public void SetDouble(string key, double value) =>
        _inner.Write(key, value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

    /// <inheritdoc />
    public void Remove(string key) => _inner.Delete(key);
}
