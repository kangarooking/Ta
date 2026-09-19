using Ta.Settings.Core;
using Ta.Settings.Services;

namespace Ta.Settings.Tests;

/// <summary>
/// 验收 3：保存门禁与校验消息 —— 各种缺失字段分别对应哪条中文消息。
///
/// 文案出处：
/// - 字段校验 → <c>AIProviderProfile.validationMessage</c>（AIProviderProfile.swift:43-57）
/// - Key 门禁 → <c>persistDraft()</c>（ModelSettingsView.swift:816-819）
/// - 测试成功/失败文案 → <c>saveAndTest()</c>（ModelSettingsView.swift:790-802）
/// </summary>
public sealed class SaveGateAndValidationTests
{
    private static AiProviderProfile CompleteProfile(ProviderPreset preset = ProviderPreset.DeepSeek)
        => new("DeepSeek 日常", ProviderPresets.ProviderKind(preset))
        {
            BaseUrl = ProviderPresets.BaseUrl(preset),
            VisionModel = ProviderPresets.SuggestedVisionModel(preset) ?? string.Empty,
            TextModel = ProviderPresets.SuggestedTextModel(preset) ?? string.Empty,
        };

    /// <summary>四种「缺失字段 → 消息」的组合，逐条对应 Mac 版的四个 return。</summary>
    [Theory]
    [InlineData("name", "请给这套配置起一个名称。")]
    [InlineData("baseURL", "请先选择服务商，或填写服务地址。")]
    [InlineData("visionModel", "请填写一个支持图片输入的视觉模型。")]
    [InlineData("textModel", "请填写文字模型，截图翻译需要同时使用文字与视觉模型。")]
    public void ValidationMessage_ReportsTheMissingField(string missingField, string expected)
    {
        var profile = CompleteProfile();
        switch (missingField)
        {
            case "name":
                profile.Name = "   ";
                break;
            case "baseURL":
                profile.BaseUrl = string.Empty;
                break;
            case "visionModel":
                profile.VisionModel = string.Empty;
                break;
            case "textModel":
                profile.TextModel = string.Empty;
                break;
        }

        Assert.Equal(expected, ModelSaveGate.ValidationFailure(profile, ProviderPreset.DeepSeek));
    }

    /// <summary>全填齐时校验通过（返回 null）。</summary>
    [Fact]
    public void ValidationMessage_IsNullWhenEverythingIsFilled()
        => Assert.Null(ModelSaveGate.ValidationFailure(CompleteProfile(), ProviderPreset.DeepSeek));

    /// <summary>服务地址非法时报「远程服务必须使用 HTTPS」（ProviderEndpointValidator.swift:20）。</summary>
    [Fact]
    public void ValidationMessage_RejectsPlainHttpRemoteEndpoint()
    {
        var profile = CompleteProfile();
        profile.BaseUrl = "http://api.example.com/v1";
        Assert.Equal("远程服务必须使用 HTTPS", ModelSaveGate.ValidationFailure(profile, ProviderPreset.DeepSeek));
    }

    /// <summary>本机 http 是允许的（ProviderEndpointValidator.swift:18）。</summary>
    [Fact]
    public void ValidationMessage_AllowsPlainHttpOnLoopback()
    {
        var profile = CompleteProfile();
        profile.BaseUrl = "http://127.0.0.1:8000/v1";
        Assert.Null(ModelSaveGate.ValidationFailure(profile, ProviderPreset.DeepSeek));
    }

    /// <summary>解析不出 host 时报「Base URL 格式无效」。</summary>
    [Fact]
    public void ValidationMessage_RejectsMalformedEndpoint()
    {
        var profile = CompleteProfile();
        profile.BaseUrl = "not a url";
        Assert.Equal("Base URL 格式无效", ModelSaveGate.ValidationFailure(profile, ProviderPreset.DeepSeek));
    }

    /// <summary>
    /// Coding Plan 不要求视觉模型，所以空 visionModel 也算通过。
    /// </summary>
    [Fact]
    public void ValidationMessage_SkipsVisionModelForCodingPlan()
    {
        var profile = CompleteProfile(ProviderPreset.ZhipuCodingPlan);
        profile.VisionModel = string.Empty;
        Assert.Null(ModelSaveGate.ValidationFailure(profile, ProviderPreset.ZhipuCodingPlan));
    }

    /// <summary>Key 门禁：Key 为空且没有已存 Key → <c>请粘贴 API Key。</c></summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void ApiKeyGate_BlocksWhenNoKeyAtAll(string? apiKey, bool hasStoredKey)
        => Assert.Equal("请粘贴 API Key。", ModelSaveGate.ApiKeyFailure(apiKey, hasStoredKey));

    /// <summary>Key 门禁：Key 非空，或已有存储的 Key，都放行。</summary>
    [Theory]
    [InlineData("sk-test", false)]
    [InlineData(null, true)]
    [InlineData("", true)]
    public void ApiKeyGate_PassesWhenKeyIsPresentOrStored(string? apiKey, bool hasStoredKey)
        => Assert.Null(ModelSaveGate.ApiKeyFailure(apiKey, hasStoredKey));

    /// <summary>综合门禁：字段先于 Key 判定 —— 名字缺失时报名字的消息，不会说「请粘贴 API Key。」</summary>
    [Fact]
    public void PersistFailure_PrefersFieldValidationOverKeyGate()
    {
        var profile = CompleteProfile();
        profile.Name = string.Empty;
        Assert.Equal(
            "请给这套配置起一个名称。",
            ModelSaveGate.PersistFailure(profile, ProviderPreset.DeepSeek, apiKey: null, hasStoredKey: false));
    }

    /// <summary>综合门禁：字段齐了但没 Key → <c>请粘贴 API Key。</c></summary>
    [Fact]
    public void PersistFailure_ReportsMissingKeyWhenFieldsAreValid()
        => Assert.Equal(
            "请粘贴 API Key。",
            ModelSaveGate.PersistFailure(
                CompleteProfile(),
                ProviderPreset.DeepSeek,
                apiKey: "  ",
                hasStoredKey: false));

    /// <summary>综合门禁：字段齐 + 有 Key → null（可以保存）。</summary>
    [Fact]
    public void PersistFailure_IsNullWhenFieldsAndKeyAreReady()
        => Assert.Null(ModelSaveGate.PersistFailure(
            CompleteProfile(),
            ProviderPreset.DeepSeek,
            apiKey: "sk-abc",
            hasStoredKey: false));

    /// <summary>测试成功文案：两种模型（ModelSettingsView.swift:793）。</summary>
    [Fact]
    public void SuccessMessage_UsesBothModelsWording()
        => Assert.Equal(
            "两种模型连接成功。文字：这是文字模型的返回 · 视觉：这是视觉模型的返回",
            ModelSaveGate.SuccessMessage(true, "这是文字模型的返回", "这是视觉模型的返回"));

    /// <summary>测试成功文案：响应只取前 24 个字符。</summary>
    [Fact]
    public void SuccessMessage_TruncatesResponsesTo24Characters()
    {
        var text = new string('文', 40);
        var vision = new string('图', 40);
        var message = ModelSaveGate.SuccessMessage(true, text, vision);
        Assert.Equal($"两种模型连接成功。文字：{new string('文', 24)} · 视觉：{new string('图', 24)}", message);
    }

    /// <summary>测试成功文案：Coding Plan 只说文字模型，取前 36 个字符（ModelSettingsView.swift:795）。</summary>
    [Fact]
    public void SuccessMessage_UsesCodingPlanWordingAnd36Characters()
        => Assert.Equal(
            $"Coding Plan 文字模型连接成功：{new string('字', 36)}",
            ModelSaveGate.SuccessMessage(false, new string('字', 50), string.Empty));

    /// <summary>测试失败文案（ModelSettingsView.swift:802）。</summary>
    [Fact]
    public void TestFailureMessage_PrefixesSavedButFailed()
        => Assert.Equal(
            "配置已保存，但连接测试失败：401 Unauthorized",
            ModelSaveGate.TestFailureMessage("401 Unauthorized"));

    /// <summary>
    /// 翻译可用性：文字模型 + 视觉模型 + API Key 齐备才列出来
    /// （AIProviderProfileStore.swift:130-141 的 translationEligibility）。
    /// </summary>
    [Fact]
    public void EligibleTranslationProfiles_OnlyIncludesFullyReadyOnes()
    {
        var settings = new InMemorySettingsStore();
        var secrets = new Fakes.InMemorySecretStore();
        var store = new AiProviderProfileStore(settings, secrets);

        var ready = CompleteProfile();
        var noVision = CompleteProfile();
        noVision.Id = AiProviderProfile.NewId();
        noVision.VisionModel = string.Empty;
        var noText = CompleteProfile();
        noText.Id = AiProviderProfile.NewId();
        noText.TextModel = string.Empty;
        var noKey = CompleteProfile();
        noKey.Id = AiProviderProfile.NewId();

        store.SaveProfile(ready, "sk-ready");
        store.SaveProfile(noVision, "sk-a");
        store.SaveProfile(noText, "sk-b");
        store.SaveProfile(noKey);

        var eligible = store.EligibleTranslationProfiles(store.LoadState());
        Assert.Single(eligible);
        Assert.Equal(ready.Id, eligible[0].Id);
    }

    /// <summary>翻译不可用时给出可读原因（AIProviderProfileStore.swift:137）。</summary>
    [Fact]
    public void TranslationEligibility_ExplainsMissingKey()
    {
        var store = new AiProviderProfileStore(new InMemorySettingsStore(), new Fakes.InMemorySecretStore());
        var profile = CompleteProfile();
        Assert.Equal("请先为这套配置保存 API Key。", store.TranslationEligibility(profile));
    }

    /// <summary>
    /// loadState 在 normalize 结果与已存状态不同时会回写存储（自愈，参考文档 §10.6 ⚠️）。
    /// </summary>
    [Fact]
    public void LoadState_SelfHealsDanglingActiveProfileId()
    {
        var settings = new InMemorySettingsStore();
        var secrets = new Fakes.InMemorySecretStore();
        var store = new AiProviderProfileStore(settings, secrets);
        var profile = CompleteProfile();
        store.SaveProfile(profile, "sk-ready");

        // 手工写一个指向不存在 ID 的脏状态
        settings.SetString(
            AiProviderProfileStore.StateDefaultsKey,
            $$"""
              {"schemaVersion":1,"profiles":[{"id":"{{profile.Id}}","name":"DeepSeek 日常","providerKind":"openAICompatible","baseURL":"https://api.deepseek.com","visionModel":"deepseek-v4-flash-vision-exp","textModel":"deepseek-v4-flash","visionVerifiedAt":null}],"activeProfileID":"00000000-0000-0000-0000-000000000000","translationProfileID":null}
              """);

        var healed = store.LoadState();
        Assert.Equal(profile.Id, healed.ActiveProfileId);

        // 自愈必须落盘，不能只在内存里改
        var reread = new AiProviderProfileStore(settings, secrets).LoadState();
        Assert.Equal(profile.Id, reread.ActiveProfileId);
    }

    /// <summary>
    /// 没有已存状态时走遗留键迁移，并且**不删除**遗留键（AIProviderProfileStore.swift:124-，§10.6 ⚠️）。
    /// </summary>
    [Fact]
    public void LoadState_MigratesLegacyKeysAndKeepsThem()
    {
        var settings = new InMemorySettingsStore();
        var secrets = new Fakes.InMemorySecretStore();
        settings.SetString(SettingsKeys.LegacyProviderBaseUrl, "https://api.deepseek.com");
        settings.SetString(SettingsKeys.LegacyProviderVisionModel, "deepseek-v4-flash-vision-exp");
        settings.SetString(SettingsKeys.LegacyProviderTextModel, "deepseek-v4-flash");
        secrets.Save("sk-legacy", AiProviderProfileStore.LegacyProviderAccount);

        var store = new AiProviderProfileStore(settings, secrets);
        var state = store.LoadState();

        Assert.Single(state.Profiles);
        Assert.Equal("原有 AI 模型", state.Profiles[0].TrimmedName);
        Assert.True(store.HasApiKey(state.Profiles[0].Id));

        // 遗留键仍在
        Assert.Equal("https://api.deepseek.com", settings.GetString(SettingsKeys.LegacyProviderBaseUrl));
        Assert.NotNull(secrets.Read(AiProviderProfileStore.LegacyProviderAccount));
    }

    /// <summary>向导完成度与推荐落点（ModelSettingsView.swift:1046-1067）。</summary>
    [Fact]
    public void SetupProgress_DrivesRecommendedStep()
    {
        var empty = new ModelSetupProgress();
        Assert.False(empty.IsComplete);
        Assert.Equal(0, empty.CompletedSteps);
        Assert.Equal("先选择服务商", empty.NextStep);
        Assert.Equal(ModelSetupStep.Provider, ModelSetupSteps.Recommended(empty));

        var noKey = empty with { HasEndpoint = true, HasVisionModel = true, HasTextModel = true };
        Assert.Equal("接下来填写 API Key", noKey.NextStep);
        Assert.Equal(ModelSetupStep.Credentials, ModelSetupSteps.Recommended(noKey));

        var noVision = empty with { HasEndpoint = true, HasApiKey = true, HasTextModel = true };
        Assert.Equal("填写视觉模型", noVision.NextStep);

        var noText = empty with { HasEndpoint = true, HasApiKey = true, HasVisionModel = true };
        Assert.Equal("最后填写文字模型", noText.NextStep);

        var complete = empty with { HasEndpoint = true, HasApiKey = true, HasVisionModel = true, HasTextModel = true };
        Assert.True(complete.IsComplete);
        Assert.Equal(4, complete.CompletedSteps);
        Assert.Equal("可以保存并测试连接", complete.NextStep);
        Assert.Equal(ModelSetupStep.Complete, ModelSetupSteps.Recommended(complete));
    }
}
