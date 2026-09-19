using Ta.AgentBridge.Protocol;

namespace Ta.AgentBridge.Tests;

/// <summary>
/// 隐私策略测试。逐条对应 Mac 版 TaAgentPrivacyPolicy 的四条拦截规则与中文文案
/// （参考文档 §11.4）。文案逐字比对 —— 跨端行为契约。
/// </summary>
public class TaAgentPrivacyPolicyTests
{
    private static TaAgentPrivacyPolicy Allowed() => new(
        isEnabled: true,
        automaticCaptureAllowed: true,
        cloudPolicy: AgentCloudPolicy.Allow,
        blockedBundleIdentifiers: Array.Empty<string>(),
        allowCaptureTa: true);

    // ── 规则 1：Agent 调用已关闭 ────────────────────────────────────

    [Fact]
    public void 规则一_访问关闭时拦截()
    {
        var policy = Allowed() with { IsEnabled = false };

        var error = policy.CaptureError(targetBundleIdentifier: null, visibleBundleIdentifiers: Array.Empty<string>());

        Assert.NotNull(error);
        Assert.Equal(AgentErrorCode.TargetBlockedByPrivacyPolicy, error!.Code);
        Assert.Equal("拓的 Agent 调用已关闭。", error.Message);
        Assert.Equal("打开拓 → 设置 → Agent 与自动化后启用。", error.Hint);
        Assert.False(error.Retryable);
    }

    // ── 规则 2：自动截图已关闭 ──────────────────────────────────────

    [Fact]
    public void 规则二_自动截图关闭时拦截()
    {
        var policy = Allowed() with { AutomaticCaptureAllowed = false };

        var error = policy.CaptureError(targetBundleIdentifier: null, visibleBundleIdentifiers: Array.Empty<string>());

        Assert.NotNull(error);
        Assert.Equal("Agent 自动截图已关闭。", error!.Message);
        Assert.Equal("可在拓的 Agent 与自动化设置中启用。", error.Hint);
    }

    // ── 规则 3：目标被黑名单/可见窗口/自截阻止 ──────────────────────

    [Fact]
    public void 规则三_目标bundle在黑名单时拦截()
    {
        var policy = Allowed() with { BlockedBundleIdentifiers = new[] { "com.apple.safari" }.ToHashSet(StringComparer.Ordinal) };

        var error = policy.CaptureError("com.apple.Safari", Array.Empty<string>());

        Assert.NotNull(error);
        Assert.Equal("目标 App 已被 Agent 截图隐私策略阻止。", error!.Message);
        Assert.Equal("如确有需要，请在拓的隐私 App 黑名单中调整。", error.Hint);
    }

    [Fact]
    public void 规则三_可见窗口与黑名单有交集时拦截()
    {
        var policy = Allowed() with { BlockedBundleIdentifiers = new[] { "com.apple.safari" }.ToHashSet(StringComparer.Ordinal) };

        // 目标无 bundle（整屏），但可见窗口含被拉黑应用。
        var error = policy.CaptureError(null, new[] { "com.other.app", "com.apple.Safari" });

        Assert.NotNull(error);
        Assert.Equal("目标 App 已被 Agent 截图隐私策略阻止。", error!.Message);
    }

    [Fact]
    public void 规则三_禁止捕获Ta时拦截()
    {
        var policy = Allowed() with { AllowCaptureTa = false };

        var error = policy.CaptureError(TaAgentPrivacyPolicy.TaBundleIdentifier, Array.Empty<string>());

        Assert.NotNull(error);
        Assert.Equal("目标 App 已被 Agent 截图隐私策略阻止。", error!.Message);
    }

    [Fact]
    public void 规则三_允许时放行()
    {
        var policy = Allowed() with { BlockedBundleIdentifiers = new[] { "com.apple.safari" }.ToHashSet(StringComparer.Ordinal) };

        var error = policy.CaptureError("com.other.app", new[] { "com.other.app" });

        Assert.Null(error);
    }

    // ── 规则 4：云端上传禁止 ────────────────────────────────────────

    [Fact]
    public void 规则四_云端deny时拦截()
    {
        var policy = Allowed();

        var error = policy.CloudError(AgentCloudPolicy.Deny);

        Assert.NotNull(error);
        Assert.Equal(AgentErrorCode.CloudUploadNotAllowed, error!.Code);
        Assert.Equal("当前请求禁止把图片或文字发送到云端模型。", error.Message);
        Assert.Equal("改用本地能力，或在明确获得用户许可后设置 cloud=allow。", error.Hint);
        Assert.False(error.Retryable);
    }

    [Fact]
    public void 规则四_请求覆盖策略()
    {
        var policy = Allowed() with { CloudPolicy = AgentCloudPolicy.Deny };

        // 请求 allow 覆盖默认 deny。
        Assert.Null(policy.CloudError(AgentCloudPolicy.Allow));
        // 未指定则用默认 deny。
        Assert.NotNull(policy.CloudError(null));
    }

    // ── allowsWindowMetadata ────────────────────────────────────────

    [Fact]
    public void 窗口元数据_黑名单与自截被省略()
    {
        var policy = Allowed() with
        {
            BlockedBundleIdentifiers = new[] { "com.apple.safari" }.ToHashSet(StringComparer.Ordinal),
            AllowCaptureTa = false,
        };

        Assert.False(policy.AllowsWindowMetadata("com.apple.Safari"));
        Assert.False(policy.AllowsWindowMetadata(TaAgentPrivacyPolicy.TaBundleIdentifier));
        Assert.True(policy.AllowsWindowMetadata("com.other.app"));
        Assert.True(policy.AllowsWindowMetadata(null));
    }

    // ── 从存储加载 + 黑名单解析 ─────────────────────────────────────

    [Fact]
    public void 加载_默认值与键名()
    {
        var store = new InMemoryPreferenceStore();

        var policy = TaAgentPrivacyPolicy.Load(store);

        // 未设置时默认：访问开启、自动截图允许、cloud=auto、允许捕获 Ta。
        Assert.True(policy.IsEnabled);
        Assert.True(policy.AutomaticCaptureAllowed);
        Assert.Equal(AgentCloudPolicy.Auto, policy.CloudPolicy);
        Assert.True(policy.AllowCaptureTa);
        Assert.Empty(policy.BlockedBundleIdentifiers);
    }

    [Fact]
    public void 加载_黑名单按逗号分号换行分隔并trim()
    {
        var store = new InMemoryPreferenceStore();
        store.SetString(TaAgentPreferenceKey.PrivacyDenylist, "com.apple.Safari, com.other.app ;com.third.app\n  ");

        var policy = TaAgentPrivacyPolicy.Load(store);

        // 存储统一小写。
        Assert.Contains("com.apple.safari", policy.BlockedBundleIdentifiers);
        Assert.Contains("com.other.app", policy.BlockedBundleIdentifiers);
        Assert.Contains("com.third.app", policy.BlockedBundleIdentifiers);
        Assert.Equal(3, policy.BlockedBundleIdentifiers.Count);
    }

    [Fact]
    public void 加载_显式关闭可被读取()
    {
        var store = new InMemoryPreferenceStore();
        store.SetBool(TaAgentPreferenceKey.AccessEnabled, false);

        var policy = TaAgentPrivacyPolicy.Load(store);

        Assert.False(policy.IsEnabled);
    }
}
