using Ta.AgentBridge.Protocol;

namespace Ta.AgentBridge;

/// <summary>
/// Agent 偏好的可注入存储。对应 Mac 版 UserDefaults 在本策略中的用法。
/// Windows 侧换成可注入存储（键名与 Mac 的 UserDefaults key 逐字一致），
/// 便于单测，并让真实设置存储（Ta.Settings）日后接入而无需改动策略逻辑。
/// </summary>
public interface IAgentPreferenceStore
{
    /// <summary>对应 Mac: defaults.object(forKey:) != nil —— 区分「未设置」与「显式 false」。</summary>
    bool HasKey(string key);

    bool GetBool(string key, bool fallback = false);

    string? GetString(string key);
}

/// <summary>基于环境变量的默认存储（生产桩）。真实设置存储由 Ta.Settings 接入。</summary>
public sealed class EnvironmentPreferenceStore : IAgentPreferenceStore
{
    public bool HasKey(string key) => Environment.GetEnvironmentVariable(KeyToEnv(key)) is not null;

    public bool GetBool(string key, bool fallback = false) =>
        bool.TryParse(Environment.GetEnvironmentVariable(KeyToEnv(key)), out var value) ? value : fallback;

    public string? GetString(string key) => Environment.GetEnvironmentVariable(KeyToEnv(key));

    private static string KeyToEnv(string key) => "TA_AGENT_" + key.ToUpperInvariant();
}

/// <summary>内存存储，供单测注入。</summary>
public sealed class InMemoryPreferenceStore : IAgentPreferenceStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public void SetBool(string key, bool value) => _values[key] = value ? "1" : "0";

    public void SetString(string key, string value) => _values[key] = value;

    public void Clear(string key) => _values.Remove(key);

    public bool HasKey(string key) => _values.ContainsKey(key);

    public bool GetBool(string key, bool fallback = false) =>
        _values.TryGetValue(key, out var raw) && bool.TryParse(raw, out var value) ? value : fallback;

    public string? GetString(string key) => _values.TryGetValue(key, out var value) ? value : null;
}

/// <summary>存储键名。对应 Mac 版 TaAgentPrivacyPolicy.swift:4-10。⚠️ 键名逐字一致。</summary>
public static class TaAgentPreferenceKey
{
    public const string AccessEnabled = "agentAccessEnabled";
    public const string AutomaticCaptureAllowed = "agentAutomaticCaptureAllowed";
    public const string CloudPolicy = "agentCloudPolicy";
    public const string PrivacyDenylist = "agentPrivacyDenylist";
    public const string AllowCaptureTa = "agentAllowCaptureTa";
}

/// <summary>
/// 隐私策略。对应 Mac 版 TaAgentPrivacyPolicy.swift:12-117，规则与中文文案逐字复现。
///
/// ⚠️ 四条拦截规则与提示文案必须与 Mac 完全一致 —— 跨端行为契约。
/// 其中 captureError 在 backend.capture() **之前**求值，故被拦截时不产生任何像素。
/// 用 record 而非 class，以便测试用 with 表达式派生变体（对应 Mac 的 Equatable struct）。
/// </summary>
public sealed record TaAgentPrivacyPolicy
{
    /// <summary>Ta 自身 bundle id。对应 Mac: PersistentConfigurationIdentity.bundleIdentifier。⚠️ 保持稳定字面量。</summary>
    public const string TaBundleIdentifier = "com.kangarooking.AIScreenshot";

    public bool IsEnabled { get; init; }
    public bool AutomaticCaptureAllowed { get; init; }
    public AgentCloudPolicy CloudPolicy { get; init; }
    public IReadOnlySet<string> BlockedBundleIdentifiers { get; init; }
    public bool AllowCaptureTa { get; init; }

    public TaAgentPrivacyPolicy(
        bool isEnabled,
        bool automaticCaptureAllowed,
        AgentCloudPolicy cloudPolicy,
        IEnumerable<string> blockedBundleIdentifiers,
        bool allowCaptureTa)
    {
        IsEnabled = isEnabled;
        AutomaticCaptureAllowed = automaticCaptureAllowed;
        CloudPolicy = cloudPolicy;
        // 对应 Mac: Set(blockedBundleIdentifiers.map { $0.lowercased() })
        BlockedBundleIdentifiers = blockedBundleIdentifiers
            .Select(id => id.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        AllowCaptureTa = allowCaptureTa;
    }

    /// <summary>从存储加载。对应 Mac: TaAgentPrivacyPolicy.load(defaults:)。</summary>
    public static TaAgentPrivacyPolicy Load(IAgentPreferenceStore store)
    {
        // 对应 Mac: object(forKey:) == nil ? true : bool(forKey:)
        var enabled = store.HasKey(TaAgentPreferenceKey.AccessEnabled)
            ? store.GetBool(TaAgentPreferenceKey.AccessEnabled)
            : true;
        var captureAllowed = store.HasKey(TaAgentPreferenceKey.AutomaticCaptureAllowed)
            ? store.GetBool(TaAgentPreferenceKey.AutomaticCaptureAllowed)
            : true;
        var allowTa = store.HasKey(TaAgentPreferenceKey.AllowCaptureTa)
            ? store.GetBool(TaAgentPreferenceKey.AllowCaptureTa)
            : true;
        var cloudRaw = store.GetString(TaAgentPreferenceKey.CloudPolicy) ?? AgentCloudPolicyExtensions.Raw(AgentCloudPolicy.Auto);
        var denylist = store.GetString(TaAgentPreferenceKey.PrivacyDenylist) ?? "";

        return new TaAgentPrivacyPolicy(
            enabled,
            captureAllowed,
            AgentCloudPolicyExtensions.TryParse(cloudRaw, out var policy) ? policy : AgentCloudPolicy.Auto,
            ParseDenylist(denylist),
            allowTa);
    }

    private static IEnumerable<string> ParseDenylist(string raw) =>
        raw.Split(new[] { ',', ';', '\n' }, StringSplitOptions.None)
            .Select(item => item.Trim())
            .Where(item => item.Length > 0);

    /// <summary>
    /// 截图拦截。对应 Mac: captureError(for:visibleBundleIdentifiers:)（:60-96）。
    /// 返回 null 表示放行，否则为应回传的错误负载。
    /// </summary>
    public AgentErrorPayload? CaptureError(string? targetBundleIdentifier, IEnumerable<string> visibleBundleIdentifiers)
    {
        if (!IsEnabled)
        {
            return new AgentErrorPayload(
                AgentErrorCode.TargetBlockedByPrivacyPolicy,
                "拓的 Agent 调用已关闭。",
                "打开拓 → 设置 → Agent 与自动化后启用。");
        }

        if (!AutomaticCaptureAllowed)
        {
            return new AgentErrorPayload(
                AgentErrorCode.TargetBlockedByPrivacyPolicy,
                "Agent 自动截图已关闭。",
                "可在拓的 Agent 与自动化设置中启用。");
        }

        var visible = visibleBundleIdentifiers.Select(id => id.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var targetBundle = targetBundleIdentifier?.ToLowerInvariant();
        var containsBlockedApp = visible.Overlaps(BlockedBundleIdentifiers);
        var taId = TaBundleIdentifier.ToLowerInvariant();
        var containsTa = visible.Contains(taId) || targetBundle == taId;

        if ((targetBundle is not null && BlockedBundleIdentifiers.Contains(targetBundle))
            || containsBlockedApp
            || (!AllowCaptureTa && containsTa))
        {
            return new AgentErrorPayload(
                AgentErrorCode.TargetBlockedByPrivacyPolicy,
                "目标 App 已被 Agent 截图隐私策略阻止。",
                "如确有需要，请在拓的隐私 App 黑名单中调整。");
        }

        return null;
    }

    /// <summary>是否允许返回该窗口的元数据。对应 Mac: allowsWindowMetadata(bundleIdentifier:)（:98-103）。</summary>
    public bool AllowsWindowMetadata(string? bundleIdentifier)
    {
        if (bundleIdentifier?.ToLowerInvariant() is not { } normalized)
        {
            return true;
        }

        if (BlockedBundleIdentifiers.Contains(normalized))
        {
            return false;
        }

        if (!AllowCaptureTa && normalized == TaBundleIdentifier.ToLowerInvariant())
        {
            return false;
        }

        return true;
    }

    /// <summary>云端上传拦截。对应 Mac: cloudError(requested:)（:105-116）。</summary>
    public AgentErrorPayload? CloudError(AgentCloudPolicy? requested)
    {
        var effective = requested ?? CloudPolicy;
        if (effective != AgentCloudPolicy.Deny)
        {
            return null;
        }

        return new AgentErrorPayload(
            AgentErrorCode.CloudUploadNotAllowed,
            "当前请求禁止把图片或文字发送到云端模型。",
            "改用本地能力，或在明确获得用户许可后设置 cloud=allow。");
    }
}
