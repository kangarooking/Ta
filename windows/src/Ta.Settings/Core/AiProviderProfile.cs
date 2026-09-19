using System.Text.Json.Serialization;

namespace Ta.Settings.Core;

/// <summary>
/// 一套 AI 模型配置。1:1 对应 macOS <c>AIProviderProfile</c>
/// （AIScreenshotApp/Models/AIProviderProfile.swift:4-58）。
///
/// 与 Mac 版的差异：
/// - <c>UUID</c> → <see cref="string"/>（小写 GUID 字符串，与 Mac 版 <c>uuidString.lowercased()</c> 一致）；
/// - <c>Date?</c> → <see cref="DateTimeOffset?"/>（UTC）；
/// - JSON 字段名保持 camelCase，因此 <c>aiProviderProfileStateV1</c> 可以直接跨平台互换。
/// </summary>
public sealed class AiProviderProfile
{
    /// <summary>配置 ID（小写 GUID）。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = NewId();

    /// <summary>配置名称。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>接口协议 rawValue（<see cref="ProviderKinds.Raw"/>）。</summary>
    [JsonPropertyName("providerKind")]
    public string ProviderKindRaw { get; set; } = ProviderKinds.Raw(ProviderKind.OpenAICompatible);

    /// <summary>服务地址。</summary>
    [JsonPropertyName("baseURL")]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>视觉模型名。</summary>
    [JsonPropertyName("visionModel")]
    public string VisionModel { get; set; } = string.Empty;

    /// <summary>文字模型名。</summary>
    [JsonPropertyName("textModel")]
    public string TextModel { get; set; } = string.Empty;

    /// <summary>视觉模型连通时间（UTC），null 表示还没测通过。</summary>
    [JsonPropertyName("visionVerifiedAt")]
    public DateTimeOffset? VisionVerifiedAt { get; set; }

    /// <summary>新建一个 ID。</summary>
    public static string NewId() => Guid.NewGuid().ToString("D").ToLowerInvariant();

    /// <summary>新建一套配置。</summary>
    public AiProviderProfile(string name, ProviderKind providerKind = ProviderKind.OpenAICompatible)
    {
        Id = NewId();
        Name = name;
        ProviderKindRaw = ProviderKinds.Raw(providerKind);
    }

    /// <summary>反序列化用。</summary>
    public AiProviderProfile()
    {
    }

    /// <summary>浅拷贝（对应 Swift 值语义，Mac 版大量依赖 struct 复制）。</summary>
    public AiProviderProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        ProviderKindRaw = ProviderKindRaw,
        BaseUrl = BaseUrl,
        VisionModel = VisionModel,
        TextModel = TextModel,
        VisionVerifiedAt = VisionVerifiedAt,
    };

    /// <summary>去掉首尾空白的名称（AIProviderProfile.swift:31-33）。</summary>
    [JsonIgnore]
    public string TrimmedName => (Name ?? string.Empty).Trim();

    /// <summary>是否填了视觉模型（AIProviderProfile.swift:35-37）。</summary>
    [JsonIgnore]
    public bool HasVisionModel => !string.IsNullOrWhiteSpace(VisionModel);

    /// <summary>是否填了文字模型（AIProviderProfile.swift:39-41）。</summary>
    [JsonIgnore]
    public bool HasTextModel => !string.IsNullOrWhiteSpace(TextModel);

    /// <summary>接口协议枚举。</summary>
    [JsonIgnore]
    public ProviderKind ProviderKind
    {
        get => ProviderKinds.FromRaw(ProviderKindRaw);
        set => ProviderKindRaw = ProviderKinds.Raw(value);
    }

    /// <summary>是否已经测通过视觉模型（Mac 版 <c>hasVerifiedConnection</c>，ModelSettingsView.swift:598-600）。</summary>
    [JsonIgnore]
    public bool HasVerifiedConnection => VisionVerifiedAt is not null;

    /// <summary>
    /// 校验消息。1:1 对应 macOS <c>AIProviderProfile.validationMessage(requiresVisionModel:requiresTextModel:)</c>
    /// （AIProviderProfile.swift:43-57）。合法时返回 null。
    /// </summary>
    /// <param name="requiresVisionModel">是否要求视觉模型（Mac 版默认 true）。</param>
    /// <param name="requiresTextModel">是否要求文字模型（Mac 版默认 false）。</param>
    public string? ValidationMessage(bool requiresVisionModel = true, bool requiresTextModel = false)
    {
        if (TrimmedName.Length == 0)
        {
            return "请给这套配置起一个名称。";
        }

        if (string.IsNullOrWhiteSpace(BaseUrl))
        {
            return "请先选择服务商，或填写服务地址。";
        }

        var endpointError = EndpointValidator.ValidationMessage(BaseUrl);
        if (endpointError is not null)
        {
            return endpointError;
        }

        if (requiresVisionModel && !HasVisionModel)
        {
            return "请填写一个支持图片输入的视觉模型。";
        }

        if (requiresTextModel && !HasTextModel)
        {
            return "请填写文字模型，截图翻译需要同时使用文字与视觉模型。";
        }

        return null;
    }

    /// <summary>值相等（对应 Swift 的 <c>Equatable</c>，normalize 自愈写入要用）。</summary>
    public bool ValueEquals(AiProviderProfile? other)
    {
        if (other is null)
        {
            return false;
        }

        return string.Equals(Id, other.Id, StringComparison.Ordinal)
            && string.Equals(Name, other.Name, StringComparison.Ordinal)
            && string.Equals(ProviderKindRaw, other.ProviderKindRaw, StringComparison.Ordinal)
            && string.Equals(BaseUrl, other.BaseUrl, StringComparison.Ordinal)
            && string.Equals(VisionModel, other.VisionModel, StringComparison.Ordinal)
            && string.Equals(TextModel, other.TextModel, StringComparison.Ordinal)
            && Nullable.Equals(VisionVerifiedAt, other.VisionVerifiedAt);
    }
}

/// <summary>
/// Provider 配置的持久化状态。1:1 对应 macOS <c>AIProviderProfileState</c>
/// （AIProviderProfile.swift:60-75）。存到 <see cref="SettingsKeys.AiProviderProfileState"/>。
/// </summary>
public sealed class AiProviderProfileState
{
    /// <summary>当前 schema 版本。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>schema 版本。</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>全部配置。</summary>
    [JsonPropertyName("profiles")]
    public List<AiProviderProfile> Profiles { get; set; } = new();

    /// <summary>AI 识图默认配置 ID。</summary>
    [JsonPropertyName("activeProfileID")]
    public string? ActiveProfileId { get; set; }

    /// <summary>截图翻译使用的配置 ID。</summary>
    [JsonPropertyName("translationProfileID")]
    public string? TranslationProfileId { get; set; }

    /// <summary>AI 识图默认配置。</summary>
    [JsonIgnore]
    public AiProviderProfile? ActiveProfile => Find(ActiveProfileId);

    /// <summary>截图翻译使用的配置。</summary>
    [JsonIgnore]
    public AiProviderProfile? TranslationProfile => Find(TranslationProfileId);

    /// <summary>按 ID 找配置。</summary>
    public AiProviderProfile? Find(string? id)
        => id is null ? null : Profiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    /// <summary>浅拷贝。</summary>
    public AiProviderProfileState Clone()
    {
        var copy = new AiProviderProfileState
        {
            SchemaVersion = SchemaVersion,
            ActiveProfileId = ActiveProfileId,
            TranslationProfileId = TranslationProfileId,
        };
        copy.Profiles.AddRange(Profiles.Select(p => p.Clone()));
        return copy;
    }

    /// <summary>值相等。</summary>
    public bool ValueEquals(AiProviderProfileState? other)
    {
        if (other is null)
        {
            return false;
        }

        if (SchemaVersion != other.SchemaVersion
            || !string.Equals(ActiveProfileId, other.ActiveProfileId, StringComparison.Ordinal)
            || !string.Equals(TranslationProfileId, other.TranslationProfileId, StringComparison.Ordinal)
            || Profiles.Count != other.Profiles.Count)
        {
            return false;
        }

        for (var i = 0; i < Profiles.Count; i++)
        {
            if (!Profiles[i].ValueEquals(other.Profiles[i]))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// AI 模型 3 步引导向导的步骤。对应 macOS <c>ModelSetupStep</c>（ModelSettingsView.swift:1026-1044）。
/// </summary>
public enum ModelSetupStep
{
    /// <summary>选择服务商。</summary>
    Provider = 0,

    /// <summary>填写 API Key。</summary>
    Credentials = 1,

    /// <summary>测试并保存。</summary>
    Complete = 2,
}

/// <summary><see cref="ModelSetupStep"/> 的标题。</summary>
public static class ModelSetupSteps
{
    /// <summary>全部步骤，顺序同 Mac 版 <c>CaseIterable</c>（provider → credentials → complete）。</summary>
    public static IReadOnlyList<ModelSetupStep> All { get; } = new[]
    {
        ModelSetupStep.Provider,
        ModelSetupStep.Credentials,
        ModelSetupStep.Complete,
    };

    /// <summary>步骤标题（ModelSettingsView.swift:1031-1036）。</summary>
    public static string Title(ModelSetupStep step) => step switch
    {
        ModelSetupStep.Provider => "选择服务商",
        ModelSetupStep.Credentials => "填写 API Key",
        ModelSetupStep.Complete => "测试并保存",
        _ => string.Empty,
    };

    /// <summary>
    /// 按完整度推荐落点（ModelSettingsView.swift:1039-1043）。
    /// </summary>
    public static ModelSetupStep Recommended(ModelSetupProgress progress)
    {
        if (!progress.HasEndpoint)
        {
            return ModelSetupStep.Provider;
        }

        return progress.IsComplete ? ModelSetupStep.Complete : ModelSetupStep.Credentials;
    }
}

/// <summary>
/// 向导完成度。对应 macOS <c>ModelSetupProgress</c>（ModelSettingsView.swift:1046-1067）。
/// 用 record 是为了能用 <c>with</c> 表达式派生各种「缺一项」的组合，测试里大量这么写。
/// </summary>
public sealed record ModelSetupProgress
{
    /// <summary>填了服务地址。</summary>
    public bool HasEndpoint { get; init; }

    /// <summary>有 API Key（本次输入或已存储）。</summary>
    public bool HasApiKey { get; init; }

    /// <summary>填了视觉模型（不支持视觉的服务商恒为 true）。</summary>
    public bool HasVisionModel { get; init; }

    /// <summary>填了文字模型。</summary>
    public bool HasTextModel { get; init; }

    /// <summary>已完成的项数。</summary>
    public int CompletedSteps => new[] { HasEndpoint, HasApiKey, HasVisionModel, HasTextModel }.Count(x => x);

    /// <summary>是否四项齐全（「仅保存 / 测试并保存」按钮的启用条件）。</summary>
    public bool IsComplete => HasEndpoint && HasApiKey && HasVisionModel && HasTextModel;

    /// <summary>下一步提示文案（ModelSettingsView.swift:1060-1066）。</summary>
    public string NextStep
    {
        get
        {
            if (!HasEndpoint)
            {
                return "先选择服务商";
            }

            if (!HasApiKey)
            {
                return "接下来填写 API Key";
            }

            if (!HasVisionModel)
            {
                return "填写视觉模型";
            }

            if (!HasTextModel)
            {
                return "最后填写文字模型";
            }

            return "可以保存并测试连接";
        }
    }
}
