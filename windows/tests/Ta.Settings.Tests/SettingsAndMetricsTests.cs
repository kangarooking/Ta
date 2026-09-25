using System.Windows.Media;
using Ta.Settings.Brand;
using Ta.Settings.Core;

namespace Ta.Settings.Tests;

/// <summary>
/// 设置键名与默认值必须与参考文档 §10.6「全部 UserDefaults Key」一致。
/// 这些字符串是跨平台读写契约，写错就读不到 Mac 版落盘的数据。
/// </summary>
public sealed class SettingsKeysTests
{
    /// <summary>§10.6 里的键名逐值断言。</summary>
    [Theory]
    [InlineData("postCaptureAction")]
    [InlineData("lastPostCaptureQuickAction")]
    [InlineData("recognitionRoute")]
    [InlineData("ocrEngine")]
    [InlineData("recognitionLanguages")]
    [InlineData("mergeWrappedLines")]
    [InlineData("resultBarDuration")]
    [InlineData("multimodalTaskTemplate")]
    [InlineData("translationSourceLanguage")]
    [InlineData("translationTargetLanguage")]
    [InlineData("translationDefaultMode")]
    [InlineData("translationUsesVisionFallback")]
    [InlineData("deepSeekOCRBaseURL")]
    [InlineData("deepSeekOCRModel")]
    [InlineData("deepSeekOCRPromptMode")]
    [InlineData("ocrPackCatalogURL")]
    [InlineData("aiProviderProfileStateV1")]
    [InlineData("agentAccessEnabled")]
    [InlineData("agentAutomaticCaptureAllowed")]
    [InlineData("agentCloudPolicy")]
    [InlineData("agentPrivacyDenylist")]
    [InlineData("agentAllowCaptureTa")]
    [InlineData("providerBaseURL")]
    [InlineData("providerVisionModel")]
    [InlineData("providerTextModel")]
    [InlineData("providerKind")]
    [InlineData("translationBaseURL")]
    [InlineData("translationTextModel")]
    [InlineData("translationVisionModel")]
    public void KeyNames_MatchTheDocument(string expected) => Assert.Equal(
        expected,
        new[]
        {
            SettingsKeys.PostCaptureAction,
            SettingsKeys.LastPostCaptureQuickAction,
            SettingsKeys.RecognitionRoute,
            SettingsKeys.OcrEngine,
            SettingsKeys.RecognitionLanguages,
            SettingsKeys.MergeWrappedLines,
            SettingsKeys.ResultBarDuration,
            SettingsKeys.MultimodalTaskTemplate,
            SettingsKeys.TranslationSourceLanguage,
            SettingsKeys.TranslationTargetLanguage,
            SettingsKeys.TranslationDefaultMode,
            SettingsKeys.TranslationUsesVisionFallback,
            SettingsKeys.DeepSeekOcrBaseUrl,
            SettingsKeys.DeepSeekOcrModel,
            SettingsKeys.DeepSeekOcrPromptMode,
            SettingsKeys.OcrPackCatalogUrl,
            SettingsKeys.AiProviderProfileState,
            SettingsKeys.AgentAccessEnabled,
            SettingsKeys.AgentAutomaticCaptureAllowed,
            SettingsKeys.AgentCloudPolicy,
            SettingsKeys.AgentPrivacyDenylist,
            SettingsKeys.AgentAllowCaptureTa,
            SettingsKeys.LegacyProviderBaseUrl,
            SettingsKeys.LegacyProviderVisionModel,
            SettingsKeys.LegacyProviderTextModel,
            SettingsKeys.LegacyProviderKind,
            SettingsKeys.LegacyTranslationBaseUrl,
            SettingsKeys.LegacyTranslationTextModel,
            SettingsKeys.LegacyTranslationVisionModel,
        }.Single(k => string.Equals(k, expected, StringComparison.Ordinal)));

    /// <summary>快捷键键名前缀格式 <c>globalHotKey.&lt;rawValue&gt;</c>。</summary>
    [Fact]
    public void GlobalHotKeyKey_UsesDocumentedPrefix()
    {
        Assert.Equal("globalHotKey.intelligentCapture", SettingsKeys.GlobalHotKey("intelligentCapture"));
        Assert.Equal("globalHotKey.translationCapture", SettingsKeys.GlobalHotKey("translationCapture"));
    }

    /// <summary>§10.6 的默认值逐条断言。</summary>
    [Fact]
    public void Defaults_MatchTheDocument()
    {
        Assert.Equal("zh-Hans,en-US", SettingsDefaults.RecognitionLanguages);
        Assert.Equal(3.0, SettingsDefaults.ResultBarDuration);
        Assert.Equal(1.5, SettingsDefaults.ResultBarDurationMin);
        Assert.Equal(8.0, SettingsDefaults.ResultBarDurationMax);
        Assert.Equal(0.5, SettingsDefaults.ResultBarDurationStep);
        Assert.False(SettingsDefaults.MergeWrappedLines);
        Assert.True(SettingsDefaults.TranslationUsesVisionFallback);
        Assert.True(SettingsDefaults.AgentAccessEnabled);
        Assert.True(SettingsDefaults.AgentAutomaticCaptureAllowed);
        Assert.True(SettingsDefaults.AgentAllowCaptureTa);
    }

    /// <summary>DeepSeek-OCR-2 的服务地址 placeholder 与模型默认值（DeepSeekOCR2Client.swift:44-45）。</summary>
    [Fact]
    public void DeepSeekOcrDefaults_MatchSource()
    {
        Assert.Equal("http://127.0.0.1:8000/v1", Ta.Settings.UI.Tabs.RecognitionPage.RecommendedLocalBaseUrl);
        Assert.Equal("deepseek-ai/DeepSeek-OCR-2", Ta.Settings.UI.Tabs.RecognitionPage.LatestOfficialModel);
    }

    /// <summary>翻译页语言预设（TranslationSettingsView.swift:156-157）。</summary>
    [Fact]
    public void LanguagePresets_MatchSource()
    {
        Assert.Equal(
            new[] { "自动检测", "英文", "简体中文", "日文", "韩文" },
            Ta.Settings.UI.Tabs.TranslationPage.SourcePresets);
        Assert.Equal(
            new[] { "简体中文", "英文", "繁体中文", "日文", "韩文", "西班牙文" },
            Ta.Settings.UI.Tabs.TranslationPage.TargetPresets);
    }
}

/// <summary>
/// 窗口与向导度量（参考文档 §9.9、§9.10、§12.4）。
/// </summary>
public sealed class WindowMetricsTests
{
    /// <summary>
    /// 设置窗口 780×680。Mac 原版 600pt，但 WPF 版度量（tab 栏、ModelsPage 向导）更高，
    /// 600 会把 AI 模型页的「下一步」按钮裁出窗口外 —— 高度上调为 680（见 SettingsWindow 注释）。
    /// </summary>
    [Fact]
    public void SettingsWindow_Is780By680()
    {
        Assert.Equal(780, Ta.Settings.UI.SettingsWindow.WindowWidth);
        Assert.Equal(680, Ta.Settings.UI.SettingsWindow.WindowHeight);
        Assert.Equal(18, Ta.Settings.UI.SettingsWindow.ContentPadding);
    }

    /// <summary>设置窗口 7 个标签页，标题与顺序都对（SettingsView.swift:85-119）。</summary>
    [Fact]
    public void SettingsWindow_HasSevenTabsInDocumentOrder()
    {
        var tabs = Ta.Settings.UI.SettingsTabs.All;
        Assert.Equal(7, tabs.Count);
        Assert.Equal(
            new[] { "权限", "常规", "快捷键", "识别", "翻译", "AI 模型", "Agent" },
            tabs.Select(Ta.Settings.UI.SettingsTabs.Title));
    }

    /// <summary>欢迎窗口 1000×724，最小 900×660（对齐 Mac 版 WelcomeView 画布比例）。</summary>
    [Fact]
    public void WelcomeWindow_Is1000By724With900By660Minimum()
    {
        Assert.Equal(1000, Ta.Settings.UI.WelcomeViewMetrics.DefaultWindowSize.Width);
        Assert.Equal(724, Ta.Settings.UI.WelcomeViewMetrics.DefaultWindowSize.Height);
        Assert.Equal(900, Ta.Settings.UI.WelcomeViewMetrics.MinimumWindowSize.Width);
        Assert.Equal(660, Ta.Settings.UI.WelcomeViewMetrics.MinimumWindowSize.Height);
    }

    /// <summary>欢迎页布局度量（WelcomeView.swift:9-16）。</summary>
    [Fact]
    public void WelcomeMetrics_MatchSource()
    {
        Assert.Equal(32, Ta.Settings.UI.WelcomeViewMetrics.ContentPadding);
        Assert.Equal(24, Ta.Settings.UI.WelcomeViewMetrics.SectionSpacing);
        Assert.Equal(14, Ta.Settings.UI.WelcomeViewMetrics.GridSpacing);
        Assert.Equal(3, Ta.Settings.UI.WelcomeViewMetrics.QuickActionColumnCount);
        Assert.Equal(6, Ta.Settings.UI.WelcomeViewMetrics.QuickActionCount);
        Assert.Equal(104, Ta.Settings.UI.WelcomeViewMetrics.QuickActionMinimumHeight);
        Assert.Equal(20, Ta.Settings.UI.WelcomeViewMetrics.PrimaryCornerRadius);
        Assert.Equal(15, Ta.Settings.UI.WelcomeViewMetrics.QuickActionCornerRadius);
    }

    /// <summary>
    /// AI 模型向导的左栏 154、圆点 24、连接线 52（参考文档 §9.9）。
    /// 断言的是 <c>ModelsPage</c> 自己声明的常量，也就是布局真正用到的数字。
    /// </summary>
    [Fact]
    public void WizardRailMetrics_MatchDocument()
    {
        Assert.Equal(154, Ta.Settings.UI.Tabs.ModelsPage.RailWidth);
        Assert.Equal(24, Ta.Settings.UI.Tabs.ModelsPage.StepDotSize);
        Assert.Equal(52, Ta.Settings.UI.Tabs.ModelsPage.StepConnectorHeight);
    }

    /// <summary>6 个全局快捷键的默认键（参考文档 §4.2）。</summary>
    [Fact]
    public void DefaultHotKeys_MatchDocument()
    {
        Assert.Equal(
            new[]
            {
                ("intelligentCapture", "1"),
                ("interactiveCapture", "2"),
                ("imageCapture", "3"),
                ("pinCapture", "4"),
                ("longCapture", "5"),
                ("translationCapture", "6"),
            },
            GlobalHotKeyActions.All.Select(a => (GlobalHotKeyActions.Raw(a), GlobalHotKeyActions.DefaultShortcut(a).KeyLabel)));
    }

    /// <summary>默认修饰键统一是 Win+Alt+Shift（对应 Mac 的 cmd|option|shift，无 control）。</summary>
    [Fact]
    public void DefaultHotKeys_UseThreeModifiersWithoutControl()
    {
        const HotKeyModifiers expected = HotKeyModifiers.Win | HotKeyModifiers.Alt | HotKeyModifiers.Shift;
        foreach (var action in GlobalHotKeyActions.All)
        {
            Assert.Equal(expected, GlobalHotKeyActions.DefaultShortcut(action).Modifiers);
        }

        // 与 Mac 版 displayText 同构的符号串（顺序 ⌃⇧⌥⌘，Win→⌘）：⇧⌥⌘N
        Assert.Equal("⇧⌥⌘1", GlobalHotKeyActions.DefaultShortcut(GlobalHotKeyAction.IntelligentCapture).MacParitySymbolicText);
    }

    /// <summary>每个标签页都必须有可渲染的图标几何（防止图标几何写错后静默变空白）。</summary>
    [Fact]
    public void EveryTabIcon_HasGeometry()
    {
        foreach (var tab in Ta.Settings.UI.SettingsTabs.All)
        {
            Assert.NotEmpty(TaIcons.Get(Ta.Settings.UI.SettingsTabs.Icon(tab)));
        }
    }

    /// <summary>枚举里声明的每个图标都必须能画出东西。</summary>
    [Fact]
    public void EveryDeclaredIcon_HasNonEmptyGeometry()
    {
        var kinds = Enum.GetValues<TaIconKind>();
        Assert.True(kinds.Length >= 35, $"图标数量偏少：{kinds.Length}");
        foreach (var kind in kinds)
        {
            var shapes = TaIcons.Get(kind);
            Assert.NotEmpty(shapes);
            Assert.All(shapes, s => Assert.False(s.Geometry.IsEmpty()));
            Assert.False(TaIcons.Merged(kind).IsEmpty());
        }
    }

    /// <summary>虚线图标必须标了虚线，否则会画成实线。</summary>
    [Fact]
    public void DashedIcons_AreFlagged()
    {
        Assert.True(TaIcons.IsDashed(TaIconKind.DashedCircle));
        Assert.True(TaIcons.IsDashed(TaIconKind.DashedRect));
        Assert.False(TaIcons.IsDashed(TaIconKind.Gear));
    }

    /// <summary>SwiftUI 磅值到 DIP 的换算（1pt = 4/3 DIP）。</summary>
    [Fact]
    public void Typography_ConvertsPointsToDips()
    {
        Assert.Equal(20 * 4.0 / 3.0, TaTypography.Title3);
        Assert.Equal(13 * 4.0 / 3.0, TaTypography.Headline);
        Assert.Equal(10 * 4.0 / 3.0, TaTypography.Caption2);
    }
}
