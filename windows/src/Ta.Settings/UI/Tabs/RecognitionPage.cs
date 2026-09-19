using System.Windows;
using System.Windows.Controls;
using Ta.Settings.Services;

namespace Ta.Settings.UI.Tabs;

/// <summary>
/// 识别页。对应 macOS <c>RecognitionSettingsView</c>（SettingsView.swift:228-424）
/// 与 <c>DeepSeekOCRConfigurationRows</c>（SettingsView.swift:426-538）。
///
/// 三个区块：默认识别路径 / OCR（引擎 + 语言 + 断行合并 + 增强包或 DeepSeek 配置）/ 数据路径。
/// </summary>
public sealed class RecognitionPage : TaTabPage
{
    /// <summary>DeepSeek-OCR-2 最新官方模型名（DeepSeekOCR2Client.swift:44）。</summary>
    public const string LatestOfficialModel = "deepseek-ai/DeepSeek-OCR-2";

    /// <summary>推荐的本机服务地址（DeepSeekOCR2Client.swift:45）。</summary>
    public const string RecommendedLocalBaseUrl = "http://127.0.0.1:8000/v1";

    /// <summary>DeepSeek-OCR-2 的密钥账号名。</summary>
    public const string DeepSeekOcrAccount = "ta.deepSeekOCR";

    private Border _host = new() { Background = Brushes.Transparent };
    private TaPicker _routePicker = null!;
    private TextBlock _routeDescription = null!;
    private TaPicker _enginePicker = null!;
    private TaTextBox _languagesBox = null!;
    private TextBlock _languageDescription = null!;
    private TaToggleRow _mergeWrappedLines = null!;
    private TextBlock _dataPathStatus = null!;
    private TextBlock _dataPathHint = null!;

    /// <summary>构造。</summary>
    public RecognitionPage(TaServices services)
        : base(services)
    {
        _host = new Border { Background = Brushes.Transparent };
    }

    /// <inheritdoc />
    protected override FrameworkElement Build()
    {
        var route = RecognitionRoutes.FromRaw(Services.Settings.GetString(SettingsKeys.RecognitionRoute));
        _routePicker = new TaPicker(
            RecognitionRoutes.All.Select(RecognitionRoutes.DisplayName).ToArray(),
            RecognitionRoutes.DisplayName(route),
            260);
        _routePicker.SelectionChanged += (_, _) =>
        {
            var selected = RecognitionRoutes.All.FirstOrDefault(
                r => string.Equals(RecognitionRoutes.DisplayName(r), _routePicker.SelectedItem, StringComparison.Ordinal));
            Services.Settings.SetString(SettingsKeys.RecognitionRoute, RecognitionRoutes.Raw(selected));
            UpdateRouteDescription();
        };

        _routeDescription = TaPrimitives.Text(string.Empty, Brand.TaTypography.Caption, Brand.TaBrushes.MutedInk, wrap: true);

        _enginePicker = new TaPicker(
            OcrEngines.All.Select(OcrEngines.DisplayName).ToArray(),
            OcrEngines.DisplayName(CurrentEngine),
            260);
        _enginePicker.SelectionChanged += (_, _) =>
        {
            var selected = OcrEngines.All.FirstOrDefault(
                e => string.Equals(OcrEngines.DisplayName(e), _enginePicker.SelectedItem, StringComparison.Ordinal));
            Services.Settings.SetString(SettingsKeys.OcrEngine, OcrEngines.Raw(selected));
            RebuildEngineSection();
            UpdateRouteDescription();
            UpdateDataPath();
        };

        _languagesBox = new TaTextBox(
            null,
            Services.Settings.GetString(SettingsKeys.RecognitionLanguages) ?? SettingsDefaults.RecognitionLanguages,
            320);
        _languagesBox.Input.TextChanged += (_, _) =>
            Services.Settings.SetString(SettingsKeys.RecognitionLanguages, _languagesBox.Text);

        _languageDescription = TaPrimitives.Text(string.Empty, Brand.TaTypography.Caption, Brand.TaBrushes.MutedInk, wrap: true);

        _mergeWrappedLines = new TaToggleRow(
            "自动合并疑似断行",
            detail: null,
            isOn: Services.Settings.GetBool(SettingsKeys.MergeWrappedLines, SettingsDefaults.MergeWrappedLines));
        _mergeWrappedLines.Toggled += (_, _) =>
            Services.Settings.SetBool(SettingsKeys.MergeWrappedLines, _mergeWrappedLines.IsOn);

        _dataPathStatus = TaPrimitives.Text(string.Empty, Brand.TaTypography.Callout, Brand.TaBrushes.Ink, FontWeights.Medium);
        _dataPathHint = TaPrimitives.Text(string.Empty, Brand.TaTypography.Caption, Brand.TaBrushes.MutedInk, wrap: true);

        RebuildEngineSection();

        return Page(
            new TaCard(
                TaLayout.V(
                    10,
                    new TaFieldRow("识别方式", _routePicker),
                    _routeDescription),
                new Thickness(14),
                12,
                Brand.TaBrushes.ElevatedPaper84),
            new TaCard(
                TaLayout.V(
                    10,
                    new TaFieldRow("OCR 引擎", _enginePicker),
                    new TaFieldRow("识别语言", _languagesBox),
                    _languageDescription,
                    _mergeWrappedLines,
                    _host),
                new Thickness(14),
                12,
                Brand.TaBrushes.ElevatedPaper84),
            new TaCard(
                TaLayout.V(
                    8,
                    TaLayout.H(
                        8,
                        new Brand.TaIcon { Kind = Brand.TaIconKind.LockShield, Size = 15, VerticalAlignment = VerticalAlignment.Center },
                        _dataPathStatus),
                    _dataPathHint),
                new Thickness(14),
                12,
                Brand.TaBrushes.ElevatedPaper84));
    }

    /// <inheritdoc />
    public override void Refresh()
    {
        RebuildEngineSection();
        UpdateRouteDescription();
        UpdateDataPath();
    }

    private OcrEngine CurrentEngine => OcrEngines.FromRaw(Services.Settings.GetString(SettingsKeys.OcrEngine));

    /// <summary>切换引擎时重画「OCR」卡片里跟随引擎变化的那一块。</summary>
    private void RebuildEngineSection()
    {
        var engine = CurrentEngine;
        _languageDescription.Text = engine switch
        {
            OcrEngine.AppleVision => "按优先级填写识别语言代码，以英文逗号分隔。",
            OcrEngine.RapidOcr or OcrEngine.PaddleOcr => "增强包使用内置中英文模型；此语言列表只在回退本机 OCR 时生效。",
            _ => "DeepSeek-OCR-2 自动识别多语言；这里的代码用于结果元数据和回退提示。",
        };

        _host.Child = OcrEngines.UsesOptionalPack(engine)
            ? BuildPackSection(engine)
            : engine == OcrEngine.DeepSeekOcr2
                ? BuildDeepSeekSection()
                : null;
    }

    // ---- 增强包（SettingsView.swift:261-324）----

    private FrameworkElement BuildPackSection(OcrEngine engine)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 10, 0, 0) };
        var installed = Services.OcrPacks.InstalledInfo(engine);

        var statusLabel = TaPrimitives.Text(
            installed is null ? "增强包未安装，当前自动回退本机 OCR" : $"已安装版本 {installed.Value.Version}",
            Brand.TaTypography.Caption,
            installed is null ? Brand.TaBrushes.Warning : Brand.TaBrushes.Success,
            FontWeights.Medium);

        var buttons = TaLayout.H(
            9,
            installed is null
                ? TaButton.Create("下载并安装", TaButtonKind.Primary, () => _ = InstallAsync(engine))
                : TaButton.Create("重新安装", TaButtonKind.Secondary, () => _ = InstallAsync(engine)),
            TaButton.Create("手动导入…", TaButtonKind.Secondary, ImportPack),
            installed is null
                ? null
                : TaButton.Create("卸载", TaButtonKind.Destructive, () => ConfirmUninstall(engine)));

        panel.Children.Add(TaLayout.H(10, statusLabel, buttons));

        panel.Children.Add(TaPrimitives.Text(
            engine == OcrEngine.PaddleOcr
                ? "PaddleOCR 在本机离线运行；选择后会后台预热并复用模型，闲置 5 分钟自动释放临时内存。安装时仍会完整校验增强包。"
                : "增强包必须包含 manifest.json 和可执行适配器；未安装时自动回退本机 OCR。",
            Brand.TaTypography.Caption,
            Brand.TaBrushes.MutedInk,
            wrap: true));

        return panel;
    }

    private async Task InstallAsync(OcrEngine engine)
    {
        try
        {
            var info = await Services.OcrPacks.InstallRecommendedAsync(engine);
            Services.Settings.SetString(SettingsKeys.OcrEngine, OcrEngines.Raw(engine));
            RebuildEngineSection();
            _ = info;
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "拓", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ImportPack()
    {
        var version = Services.OcrPacks.ChooseAndImport(CurrentEngine);
        if (version is not null)
        {
            RebuildEngineSection();
        }
    }

    /// <summary>
    /// 卸载二次确认。对应 SettingsView.swift:354-365 的 <c>confirmationDialog</c>：
    /// 标题 <c>卸载 &lt;engine&gt;？</c>，正文 <c>卸载后截图识别会立即回退到 Apple Vision，主功能仍可使用。</c>
    /// </summary>
    private void ConfirmUninstall(OcrEngine engine)
    {
        var displayName = OcrEngines.DisplayName(engine);
        var result = MessageBox.Show(
            "卸载后截图识别会立即回退到本机 OCR，主功能仍可使用。",
            $"卸载 {displayName}？",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK)
        {
            return;
        }

        Services.OcrPacks.Remove(engine);
        RebuildEngineSection();
    }

    // ---- DeepSeek-OCR-2（SettingsView.swift:426-538）----

    private FrameworkElement BuildDeepSeekSection()
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 10, 0, 0) };

        var baseUrl = Services.Settings.GetString(SettingsKeys.DeepSeekOcrBaseUrl) ?? string.Empty;
        var model = Services.Settings.GetString(SettingsKeys.DeepSeekOcrModel) ?? LatestOfficialModel;
        var promptMode = DeepSeekOcrPromptModes.FromRaw(Services.Settings.GetString(SettingsKeys.DeepSeekOcrPromptMode));

        var ready = EndpointValidator.ValidationMessage(baseUrl) is null && model.Trim().Length > 0;
        panel.Children.Add(TaLayout.H(
            8,
            new Brand.TaIcon
            {
                Kind = ready ? Brand.TaIconKind.CheckCircle : Brand.TaIconKind.WarningTriangle,
                Size = 14,
                Brush = ready ? Brand.TaBrushes.Success : Brand.TaBrushes.Warning,
                VerticalAlignment = VerticalAlignment.Center,
            },
            TaPrimitives.Text(
                ready ? "DeepSeek-OCR-2 服务已配置" : "需要配置 DeepSeek-OCR-2 服务",
                Brand.TaTypography.Caption,
                ready ? Brand.TaBrushes.Success : Brand.TaBrushes.Warning,
                FontWeights.Medium)));

        var baseUrlBox = new TaTextBox("服务地址，例如 " + RecommendedLocalBaseUrl, baseUrl, 340);
        baseUrlBox.Input.TextChanged += (_, _) =>
            Services.Settings.SetString(SettingsKeys.DeepSeekOcrBaseUrl, baseUrlBox.Text.Trim());
        panel.Children.Add(new TaFieldRow("服务地址", baseUrlBox));

        var modelBox = new TaTextBox(null, model, 340);
        modelBox.Input.TextChanged += (_, _) =>
            Services.Settings.SetString(SettingsKeys.DeepSeekOcrModel, modelBox.Text.Trim());
        panel.Children.Add(new TaFieldRow("模型名", modelBox));

        var modePicker = new TaPicker(
            DeepSeekOcrPromptModes.All.Select(DeepSeekOcrPromptModes.DisplayName).ToArray(),
            DeepSeekOcrPromptModes.DisplayName(promptMode),
            260);
        modePicker.SelectionChanged += (_, _) =>
        {
            var selected = DeepSeekOcrPromptModes.All.FirstOrDefault(
                m => string.Equals(DeepSeekOcrPromptModes.DisplayName(m), modePicker.SelectedItem, StringComparison.Ordinal));
            Services.Settings.SetString(SettingsKeys.DeepSeekOcrPromptMode, DeepSeekOcrPromptModes.Raw(selected));
        };
        panel.Children.Add(new TaFieldRow("输出格式", modePicker));

        var hasStoredKey = Services.Secrets.Contains(DeepSeekOcrAccount);
        var keyBox = new TaPasswordBox();
        keyBox.Margin = new Thickness(0, 0, 0, 0);
        var keyLabel = hasStoredKey ? "输入新 Key 以更新（本机服务可留空）" : "API Key（本机服务可留空）";
        var keyRow = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var keyCaption = TaPrimitives.Text(keyLabel, Brand.TaTypography.Caption, Brand.TaBrushes.MutedInk, wrap: true);
        Grid.SetColumn(keyCaption, 0);
        keyRow.Children.Add(keyCaption);
        Grid.SetColumn(keyBox, 1);
        keyRow.Children.Add(keyBox);
        panel.Children.Add(keyRow);

        panel.Children.Add(TaLayout.H(
            9,
            TaButton.Create(
                "填入本机默认地址",
                TaButtonKind.Secondary,
                () =>
                {
                    Services.Settings.SetString(SettingsKeys.DeepSeekOcrBaseUrl, RecommendedLocalBaseUrl);
                    Services.Settings.SetString(SettingsKeys.DeepSeekOcrModel, LatestOfficialModel);
                    baseUrlBox.Text = RecommendedLocalBaseUrl;
                    modelBox.Text = LatestOfficialModel;
                }),
            TaButton.Create(
                hasStoredKey ? "更新 Key" : "保存 Key",
                TaButtonKind.Secondary,
                () =>
                {
                    var value = keyBox.Password.Trim();
                    if (value.Length == 0)
                    {
                        return;
                    }

                    Services.Secrets.Save(value, DeepSeekOcrAccount);
                    keyBox.Password = string.Empty;
                })));

        panel.Children.Add(TaPrimitives.Text(
            "当前最新专用模型为 DeepSeek-OCR-2。官方推理方案面向 CUDA，因此本应用连接 vLLM/SGLang 或兼容服务，不会在 Windows 上静默下载模型。DeepSeek 官方聊天 API 地址不能代替 OCR-2 服务地址。",
            Brand.TaTypography.Caption,
            Brand.TaBrushes.MutedInk,
            wrap: true));
        panel.Children.Add(TaPrimitives.Text(
            "服务地址、模型名和 Key 在同一台机器上覆盖升级拓时会继续保留。",
            Brand.TaTypography.Caption,
            Brand.TaBrushes.MutedInk,
            wrap: true));

        return panel;
    }

    private void UpdateRouteDescription()
    {
        var route = RecognitionRoutes.FromRaw(Services.Settings.GetString(SettingsKeys.RecognitionRoute));
        var isDeepSeek = CurrentEngine == OcrEngine.DeepSeekOcr2;
        _routeDescription.Text = route switch
        {
            RecognitionRoute.LocalOcr => isDeepSeek
                ? "使用 DeepSeek-OCR-2 服务识别，本次截图会发送到你配置的端点。"
                : "使用所选 OCR 引擎在本机识别；增强包未安装时回退本机 OCR，不需要 API Key。",
            RecognitionRoute.Multimodal => "把主动框选的图片发送给已配置的 OpenAI-compatible 视觉模型，并复制模型结果。",
            RecognitionRoute.Smart => isDeepSeek
                ? "DeepSeek-OCR-2 本身是远程识别；如需“本机优先、低置信度再上传”，请选择本机 OCR 引擎。"
                : "先在本机 OCR；低置信度时提示 AI 增强，不会静默上传图片。",
            _ => string.Empty,
        };
    }

    private void UpdateDataPath()
    {
        var isLocal = OcrEngines.IsLocalEngine(CurrentEngine);
        _dataPathStatus.Text = isLocal ? "普通识别始终在本机完成" : "截图会发送到你配置的 DeepSeek OCR 服务";
        _dataPathStatus.Foreground = isLocal ? Brand.TaBrushes.Ink : Brand.TaBrushes.Warning;
        _dataPathHint.Text = isLocal
            ? "低置信度结果只会提示增强，不会静默上传。"
            : "只有选择 DeepSeek-OCR-2 时才上传；切回本机引擎即恢复离线识别。";
    }
}
