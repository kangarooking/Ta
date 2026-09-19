using System.IO;
using System.Text.Json;
using Ta.Settings.Services;

namespace Ta.Settings.Core;

/// <summary>
/// 设置存储的内存实现。UI 开发与单元测试用（不落盘）。
/// </summary>
public sealed class InMemorySettingsStore : ISettingsStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <summary>全部键值快照（测试断言用）。</summary>
    public IReadOnlyDictionary<string, string> Snapshot => _values;

    /// <inheritdoc />
    public string? GetString(string key) => _values.TryGetValue(key, out var value) ? value : null;

    /// <inheritdoc />
    public void SetString(string key, string value) => _values[key] = value;

    /// <inheritdoc />
    public bool GetBool(string key, bool fallback)
        => _values.TryGetValue(key, out var raw) && bool.TryParse(raw, out var parsed) ? parsed : fallback;

    /// <inheritdoc />
    public void SetBool(string key, bool value) => _values[key] = value ? "true" : "false";

    /// <inheritdoc />
    public double GetDouble(string key, double fallback)
        => _values.TryGetValue(key, out var raw)
           && double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    /// <inheritdoc />
    public void SetDouble(string key, double value)
        => _values[key] = value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public void Remove(string key) => _values.Remove(key);
}

/// <summary>
/// 设置存储的 JSON 文件实现，默认落在
/// <c>%LOCALAPPDATA%\Ta\settings.json</c>（对应 macOS 的 UserDefaults 域文件）。
/// </summary>
public sealed class JsonFileSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly string _path;
    private Dictionary<string, string> _values;

    /// <summary>默认存储路径。</summary>
    public static string DefaultPath
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "Ta", "settings.json");
        }
    }

    /// <summary>用指定文件构造。</summary>
    public JsonFileSettingsStore(string? path = null)
    {
        _path = path ?? DefaultPath;
        _values = Load();
    }

    /// <summary>实际使用的文件路径。</summary>
    /// <remarks>不能叫 <c>Path</c> —— 会和 <see cref="System.IO.Path"/> 静态类抢名字。</remarks>
    public string FilePath => _path;

    /// <inheritdoc />
    public string? GetString(string key)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out var value) ? value : null;
        }
    }

    /// <inheritdoc />
    public void SetString(string key, string value)
    {
        lock (_gate)
        {
            _values[key] = value;
            Save();
        }
    }

    /// <inheritdoc />
    public bool GetBool(string key, bool fallback)
        => GetString(key) is { } raw && bool.TryParse(raw, out var parsed) ? parsed : fallback;

    /// <inheritdoc />
    public void SetBool(string key, bool value) => SetString(key, value ? "true" : "false");

    /// <inheritdoc />
    public double GetDouble(string key, double fallback)
        => GetString(key) is { } raw
           && double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    /// <inheritdoc />
    public void SetDouble(string key, double value)
        => SetString(key, value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

    /// <inheritdoc />
    public void Remove(string key)
    {
        lock (_gate)
        {
            _values.Remove(key);
            Save();
        }
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (!System.IO.File.Exists(_path))
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            var json = System.IO.File.ReadAllText(_path);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, SerializerOptions)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception)
        {
            // 配置文件损坏不应让设置界面打不开，退化成空配置
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void Save()
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            System.IO.File.WriteAllText(_path, JsonSerializer.Serialize(_values, SerializerOptions));
        }
        catch (Exception)
        {
            // 只读盘/权限不足时静默失败（与 UserDefaults 写不进去时的行为一致）
        }
    }
}

/// <summary>
/// API 模型配置仓库。
///
/// 对应 macOS <c>AIProviderProfileStore</c>（AIScreenshotApp/System/AIProviderProfileStore.swift:20-…），
/// 逐条复现了它的四个关键行为：
/// 1. <see cref="LoadState"/> 在 normalize 结果与已存状态不同时**回写存储**（自愈）；
/// 2. 没有任何已存状态时走 <see cref="MigrateLegacyConfiguration"/> 迁移遗留键，
///    并且**迁移后不删除**遗留键（参考文档 §10.6 的 ⚠️ 注释）；
/// 3. <see cref="EligibleTranslationProfiles"/> 只放行「校验通过 + 已存 API Key」的配置；
/// 4. 配置名称与状态字段 camelCase 序列化，保证 <c>aiProviderProfileStateV1</c> 跨平台可读。
/// </summary>
public sealed class AiProviderProfileStore : IProviderStore
{
    /// <summary>状态的存储键。</summary>
    public const string StateDefaultsKey = SettingsKeys.AiProviderProfileState;

    /// <summary>迁移出来的配置名（AIProviderProfileStore.swift:166）。</summary>
    public const string LegacyProfileName = "原有 AI 模型";

    /// <summary>API Key 的账号前缀。</summary>
    public const string ProviderProfileAccountPrefix = "ta.providerProfile.";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ISettingsStore _settings;
    private readonly ISecretStore _secrets;

    /// <summary>构造。</summary>
    public AiProviderProfileStore(ISettingsStore settings, ISecretStore secrets)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
    }

    /// <summary>某套配置的密钥账号名（对应 <c>keychainAccount(for:)</c>）。</summary>
    public static string SecretAccount(string profileId) => ProviderProfileAccountPrefix + profileId.ToLowerInvariant();

    /// <inheritdoc />
    public AiProviderProfileState LoadState()
    {
        var raw = _settings.GetString(StateDefaultsKey);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            AiProviderProfileState? state;
            try
            {
                state = JsonSerializer.Deserialize<AiProviderProfileState>(raw, SerializerOptions);
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("已保存的 AI 模型配置无法读取。");
            }

            if (state is null)
            {
                throw new InvalidOperationException("已保存的 AI 模型配置无法读取。");
            }

            state.Profiles ??= new List<AiProviderProfile>();
            var normalized = Normalize(state);
            if (!normalized.ValueEquals(state))
            {
                SaveState(normalized);
            }

            return normalized;
        }

        var migrated = MigrateLegacyConfiguration();
        SaveState(migrated);
        return migrated;
    }

    /// <inheritdoc />
    public void SaveState(AiProviderProfileState state)
    {
        var normalized = Normalize(state);
        _settings.SetString(
            StateDefaultsKey,
            JsonSerializer.Serialize(normalized, SerializerOptions));
    }

    /// <inheritdoc />
    public AiProviderProfileState SaveProfile(AiProviderProfile profile, string? apiKey = null)
    {
        var state = LoadState();
        var index = state.Profiles.FindIndex(p => string.Equals(p.Id, profile.Id, StringComparison.Ordinal));
        if (index >= 0)
        {
            state.Profiles[index] = profile;
        }
        else
        {
            state.Profiles.Add(profile);
        }

        if (!string.IsNullOrEmpty(apiKey))
        {
            _secrets.Save(apiKey, SecretAccount(profile.Id));
        }

        SaveState(state);
        return state;
    }

    /// <inheritdoc />
    public AiProviderProfileState DeleteProfile(string profileId)
    {
        var state = LoadState();
        state.Profiles.RemoveAll(p => string.Equals(p.Id, profileId, StringComparison.Ordinal));
        _secrets.Delete(SecretAccount(profileId));
        if (string.Equals(state.ActiveProfileId, profileId, StringComparison.Ordinal))
        {
            state.ActiveProfileId = null;
        }

        if (string.Equals(state.TranslationProfileId, profileId, StringComparison.Ordinal))
        {
            state.TranslationProfileId = null;
        }

        SaveState(state);
        return state;
    }

    /// <inheritdoc />
    public AiProviderProfileState SetActiveProfile(string? profileId)
    {
        var state = LoadState();
        state.ActiveProfileId = profileId;
        SaveState(state);
        return state;
    }

    /// <inheritdoc />
    public AiProviderProfileState SetTranslationProfile(string? profileId)
    {
        var state = LoadState();
        state.TranslationProfileId = profileId;
        SaveState(state);
        return state;
    }

    /// <inheritdoc />
    public bool HasApiKey(string profileId) => _secrets.Contains(SecretAccount(profileId));

    /// <inheritdoc />
    public string? ApiKey(string profileId) => _secrets.Read(SecretAccount(profileId));

    /// <inheritdoc />
    public void RemoveApiKey(string profileId) => _secrets.Delete(SecretAccount(profileId));

    /// <inheritdoc />
    public string? TranslationEligibility(AiProviderProfile profile)
    {
        var message = profile.ValidationMessage(requiresTextModel: true);
        if (message is not null)
        {
            return message;
        }

        if (!HasApiKey(profile.Id))
        {
            return "请先为这套配置保存 API Key。";
        }

        return null;
    }

    /// <inheritdoc />
    public IReadOnlyList<AiProviderProfile> EligibleTranslationProfiles(AiProviderProfileState state)
        => state.Profiles.Where(p => TranslationEligibility(p) is null).ToList();

    /// <summary>
    /// 自愈归一化（AIProviderProfileStore.swift:113-122）：
    /// activeProfileID 指向不存在的配置时，回落到第一套「校验通过」的配置；
    /// translationProfileID 指向不存在的配置时置空。
    /// </summary>
    public AiProviderProfileState Normalize(AiProviderProfileState state)
    {
        var result = state.Clone();
        result.SchemaVersion = AiProviderProfileState.CurrentSchemaVersion;
        if (result.Find(result.ActiveProfileId) is null)
        {
            result.ActiveProfileId = result.Profiles.FirstOrDefault(p => p.ValidationMessage() is null)?.Id;
        }

        if (result.TranslationProfileId is not null && result.Find(result.TranslationProfileId) is null)
        {
            result.TranslationProfileId = null;
        }

        return result;
    }

    /// <summary>
    /// 从遗留键迁移出第一套配置（AIProviderProfileStore.swift:124-…）。
    /// ⚠️ 遗留键在迁移后**从不删除**（参考文档 §10.6）。
    /// </summary>
    public AiProviderProfileState MigrateLegacyConfiguration()
    {
        var state = new AiProviderProfileState();

        var legacyBaseUrl = _settings.GetString(SettingsKeys.LegacyProviderBaseUrl) ?? string.Empty;
        var legacyVisionModel = _settings.GetString(SettingsKeys.LegacyProviderVisionModel) ?? string.Empty;
        var legacyTextModel = _settings.GetString(SettingsKeys.LegacyProviderTextModel) ?? string.Empty;
        var legacyProvider = ProviderKinds.FromRaw(_settings.GetString(SettingsKeys.LegacyProviderKind));
        var legacyAiKey = _secrets.Read(LegacyProviderAccount);

        if (legacyBaseUrl.Length > 0 || legacyVisionModel.Length > 0 || legacyAiKey is not null)
        {
            var profile = new AiProviderProfile(LegacyProfileName, legacyProvider)
            {
                BaseUrl = legacyBaseUrl,
                VisionModel = legacyVisionModel,
                TextModel = legacyTextModel,
            };
            state.Profiles.Add(profile);
            state.ActiveProfileId = profile.Id;
            if (!string.IsNullOrEmpty(legacyAiKey))
            {
                _secrets.Save(legacyAiKey, SecretAccount(profile.Id));
            }
        }

        return state;
    }

    /// <summary>Mac 版旧的单一 Provider 密钥账号名（迁移读用）。</summary>
    public const string LegacyProviderAccount = "ta.multimodalProvider";
}
