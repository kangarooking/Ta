using System.Windows.Controls;
using System.Windows.Media;
using Ta.Settings.Services;

namespace Ta.Settings.UI.Tabs;

/// <summary>
/// 翻译页。对应 macOS <c>TranslationSettingsView</c>（TranslationSettingsView.swift:1-291）。
///
/// 与 Mac 版的差异只有一处文案（任务要求）：隐私说明里的「Keychain」改成 Windows 上的
/// 实际存储位置 —— API Key 走 DPAPI 加密后存到
/// <c>%LOCALAPPDATA%\Ta\secrets.json</c>，只有当前 Windows 用户能解密。
/// </summary>
public sealed class TranslationPage : TaTabPage
{
    /// <summary>源语言预设（TranslationSettingsView.swift:156）。</summary>
    public static readonly string[] SourcePresets = { "自动检测", "英文", "简体中文", "日文", "韩文" };

    /// <summary>目标语言预设（TranslationSettingsView.swift:157）。</summary>
    public static readonly string[] TargetPresets = { "简体中文", "英文", "繁体中文", "日文", "韩文", "西班牙文" };

    private readonly Border _modelHost;
    private readonly Border _status = new();
    private readonly Border _statusLine = new();
    /// <summary>构造。</summary>
    public TranslationPage(TaServices services)
        : base(services)
    {
        _modelHost = new Border { Background = Brushes.Transparent };
        Services.OpenAiModelSettingsRequested += Services_OpenAiModelSettingsRequested;
    }

    /// <inheritdoc />
    protected override FrameworkElement Build()
    {
        var source = Services.Settings.GetString(SettingsKeys.TranslationSourceLanguage)
            ?? TranslationConfiguration.DefaultSourceLanguage;
        var target = Services.Settings.GetString(SettingsKeys.TranslationTargetLanguage)
            ?? TranslationConfiguration.DefaultTargetLanguage;

        var sourceBox = new TaTextBox(null, source, 240);
        sourceBox.Input.TextChanged += (_, _) =>
            Services.Settings.SetString(SettingsKeys.TranslationSourceLanguage, sourceBox.Text);
        var sourcePresets = new TaPicker(SourcePresets, source, 150);
        sourcePresets.SelectionChanged += (_, _) =>
        {
            if (sourcePresets.SelectedItem is { } value)
            {
                sourceBox.Text = value;
                Services.Settings.SetString(SettingsKeys.TranslationSourceLanguage, value);
            }
        };

        var targetBox = new TaTextBox(null, target, 240);
        targetBox.Input.TextChanged += (_, _) =>
            Services.Settings.SetString(SettingsKeys.TranslationTargetLanguage, targetBox.Text);
        var targetPresets = new TaPicker(TargetPresets, target, 150);
        targetPresets.SelectionChanged += (_, _) =>
        {
            if (targetPresets.SelectedItem is { } value)
            {
                targetBox.Text = value;
                Services.Settings.SetString(SettingsKeys.TranslationTargetLanguage, value);
            }
        };

        var mode = TranslationModes.FromRaw(Services.Settings.GetString(SettingsKeys.TranslationDefaultMode));
        var modePicker = new TaPicker(
            TranslationModes.All.Select(TranslationModes.DisplayName).ToArray(),
            TranslationModes.DisplayName(mode),
            240);
        modePicker.SelectionChanged += (_, _) =>
        {
            var selected = TranslationModes.All.FirstOrDefault(
                m => string.Equals(TranslationModes.DisplayName(m), modePicker.SelectedItem, StringComparison.Ordinal));
            Services.Settings.SetString(SettingsKeys.TranslationDefaultMode, TranslationModes.Raw(selected));
        };

        var visionFallback = new TaToggleRow(
            "本地 OCR 置信度较低时，使用视觉模型读取截图",
            detail: null,
            isOn: Services.Settings.GetBool(
                SettingsKeys.TranslationUsesVisionFallback,
                SettingsDefaults.TranslationUsesVisionFallback));
        visionFallback.Toggled += (_, _) =>
            Services.Settings.SetBool(SettingsKeys.TranslationUsesVisionFallback, visionFallback.IsOn);

        var translationShortcut = Services.HotKeys.ShortcutFor(GlobalHotKeyAction.TranslationCapture);

        return Page(
            new TaCard(
                TaLayout.V(
                    8,
                    TaLayout.H(
                        12,
                        new Brand.TaIcon
                        {
                            Kind = Brand.TaIconKind.BookClosed,
                            Size = 19,
                            Brush = Brand.TaBrushes.Cinnabar,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                        TaLayout.V(
                            2,
                            TaPrimitives.Text("截图翻译", Brand.TaTypography.Title3, Brand.TaBrushes.Ink, FontWeights.SemiBold),
                            TaPrimitives.Text(
                                "沿用已保存的 AI 模型，只需设置语言和默认行为。",
                                Brand.TaTypography.Caption,
                                Brand.TaBrushes.MutedInk))),
                    _modelHost,
                    _status,
                    _statusLine),
                new Thickness(14),
                12,
                Brand.TaBrushes.ElevatedPaper84),
            new TaCard(
                TaLayout.V(
                    10,
                    new TaSectionHeader("翻译语言", "默认自动识别截图语言，也可以直接输入其他语言。"),
                    TaLayout.V(6, BuildLanguageRow("源语言", sourceBox, sourcePresets), BuildLanguageRow("目标语言", targetBox, targetPresets))),
                new Thickness(14),
                12,
                Brand.TaBrushes.ElevatedPaper84),
            new TaCard(
                TaLayout.V(
                    10,
                    new TaSectionHeader("默认行为", "截图工具栏点击“翻译”后直接执行，不再二次确认。"),
                    new TaFieldRow("默认翻译方式", modePicker),
                    visionFallback,
                    TaPrimitives.Text(
                        $"快捷键 {translationShortcut.DisplayText} 始终执行“翻译文字并复制”。",
                        Brand.TaTypography.Caption2,
                        Brand.TaBrushes.MutedInk)),
                new Thickness(14),
                12,
                Brand.TaBrushes.ElevatedPaper84),
            new Border
            {
                Padding = new Thickness(3, 0, 0, 0),
                Child = TaLayout.H(
                    8,
                    new Brand.TaIcon
                    {
                        Kind = Brand.TaIconKind.LockShieldFilled,
                        Size = 14,
                        Brush = Brand.TaBrushes.Success,
                        VerticalAlignment = VerticalAlignment.Top,
                    },
                    TaPrimitives.Text(
                        "API Key 用 Windows DPAPI 加密后保存在本机 %LOCALAPPDATA%\\Ta\\secrets.json，只有当前 Windows 用户能解密，覆盖升级拓后继续保留；只有主动翻译或识图时才会发送所选截图。",
                        Brand.TaTypography.Caption2,
                        Brand.TaBrushes.MutedInk,
                        wrap: true)),
            });
    }

    /// <inheritdoc />
    public override void Refresh() => RebuildModelSection();

    private static FrameworkElement BuildLanguageRow(string label, TaTextBox box, TaPicker presets)
    {
        var labelBlock = TaPrimitives.Text(label, Brand.TaTypography.Callout, Brand.TaBrushes.Ink);
        labelBlock.VerticalAlignment = VerticalAlignment.Center;
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(labelBlock, 0);
        row.Children.Add(labelBlock);
        Grid.SetColumn(box, 1);
        row.Children.Add(box);
        presets.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(presets, 2);
        row.Children.Add(presets);
        return row;
    }

    private void RebuildModelSection()
    {
        var state = Services.Providers.LoadState();
        var eligible = Services.Providers.EligibleTranslationProfiles(state);
        if (state.TranslationProfileId is null || !eligible.Any(p => string.Equals(p.Id, state.TranslationProfileId, StringComparison.Ordinal)))
        {
            state = Services.Providers.SetTranslationProfile(eligible.Count > 0 ? eligible[0].Id : null);
            eligible = Services.Providers.EligibleTranslationProfiles(state);
        }

        var selected = state.Find(state.TranslationProfileId);

        if (eligible.Count == 0)
        {
            _modelHost.Child = BuildEmptyState();
            return;
        }

        var picker = new TaPicker(
            eligible.Select(p => p.TrimmedName).ToArray(),
            selected?.TrimmedName,
            260);
        picker.SelectionChanged += (_, _) =>
        {
            var target = eligible.FirstOrDefault(
                p => string.Equals(p.TrimmedName, picker.SelectedItem, StringComparison.Ordinal));
            if (target is not null)
            {
                _ = Services.Providers.SetTranslationProfile(target.Id);
                ShowStatus("已切换翻译模型。", isError: false);
            }
        };

        var manage = TaButton.Create("管理模型", TaButtonKind.Secondary, () => Services.RequestOpenAiModelSettings());
        var test = TaButton.Create("测试", TaButtonKind.Secondary, () => _ = TestAsync(selected));

        _modelHost.Child = new Border
        {
            Margin = new Thickness(0, 6, 0, 0),
            Child = TaLayout.V(
                10,
                TaLayout.H(
                    10,
                    new Brand.TaIcon
                    {
                        Kind = Brand.TaIconKind.ShieldCheck,
                        Size = 18,
                        Brush = Brand.TaBrushes.Success,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    TaPrimitives.Text("模型配置", Brand.TaTypography.Callout, Brand.TaBrushes.Ink),
                    picker,
                    selected is null
                        ? null
                        : TaPrimitives.Badge("可用", Brand.TaBrushes.Success, Brand.TaBrushes.Success10),
                    manage,
                    test),
                selected is null
                    ? null
                    : TaLayout.H(
                        10,
                        BuildModelValue("文字", selected.TextModel),
                        new Border { Width = 1, Background = Brand.TaBrushes.Hairline, Height = 20 },
                        BuildModelValue("视觉", selected.VisionModel)),
                state.Profiles.Count > eligible.Count
                    ? TaPrimitives.Text(
                        $"另有 {state.Profiles.Count - eligible.Count} 套配置尚未完成。",
                        Brand.TaTypography.Caption2,
                        Brand.TaBrushes.MutedInk)
                    : null),
        };
    }

    private FrameworkElement BuildEmptyState()
    {
        var open = TaButton.Create("去配置 AI 模型", TaButtonKind.Primary, () => Services.RequestOpenAiModelSettings());
        return new TaCard(
            TaLayout.H(
                10,
                new Brand.TaIcon
                {
                    Kind = Brand.TaIconKind.WarningTriangle,
                    Size = 17,
                    Brush = Brand.TaBrushes.Warning,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                TaLayout.V(
                    2,
                    TaPrimitives.Text(
                        "暂无可用于翻译的模型",
                        Brand.TaTypography.Callout,
                        Brand.TaBrushes.Ink,
                        FontWeights.SemiBold),
                    TaPrimitives.Text(
                        "先完成一套 AI 模型配置，翻译页会自动复用。",
                        Brand.TaTypography.Caption,
                        Brand.TaBrushes.MutedInk)),
                open),
            new Thickness(13),
            12,
            Brand.TaBrushes.ElevatedPaper84);
    }

    private static FrameworkElement BuildModelValue(string title, string value)
        => TaLayout.H(
            6,
            TaPrimitives.Text(title, Brand.TaTypography.Caption2, Brand.TaBrushes.MutedInk),
            TaPrimitives.Text(value, Brand.TaTypography.Caption, Brand.TaBrushes.Ink, mono: true));

    private async Task TestAsync(AiProviderProfile? profile)
    {
        if (profile is null)
        {
            return;
        }

        _status.Child = new TaStatusLine("正在测试文字与视觉能力…", Brand.TaIconKind.InfoCircle, Brand.TaBrushes.MutedInk);
        var text = await Services.Translation.TestTextModelAsync(profile);
        if (!text.Ok)
        {
            ShowStatus(text.Message, isError: true);
            return;
        }

        var vision = await Services.Translation.TestVisionModelAsync(profile);
        ShowStatus(
            vision.Ok ? "文字模型与视觉模型均可用。" : vision.Message,
            isError: !vision.Ok);
    }

    private void ShowStatus(string message, bool isError)
    {
        _statusLine.Child = new TaStatusLine(
            message,
            isError ? Brand.TaIconKind.OctagonX : Brand.TaIconKind.CheckCircle,
            isError ? Brand.TaBrushes.Danger : Brand.TaBrushes.Success,
            wrap: true);
    }

    private void Services_OpenAiModelSettingsRequested()
    {
        // 「去配置 AI 模型」/「管理模型」两个按钮都会走到这里，
        // 由 SettingsWindow 订阅后切到 AI 模型标签（对应 Ta.OpenAIModelSettings 通知）。
    }
}
