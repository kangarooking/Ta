using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Ta.Settings.Services;

namespace Ta.Settings.UI.Tabs;

/// <summary>
/// AI 模型页 —— 3 步引导向导。对应 macOS <c>ModelSettingsView</c>（ModelSettingsView.swift:1-1097）。
///
/// 步骤：<c>.provider</c>（选择服务商）→ <c>.credentials</c>（填 API Key + 推荐模型）→
/// <c>.complete</c>（测试并保存）。左栏宽 154，圆点 24px、连接线 52px（参考文档 §9.9）。
///
/// 关键行为（全部 1:1 复现，有单元测试锁定）：
/// - 保存门禁：校验通过 **且**（Key 非空 **或** 已有存储），否则 <c>请粘贴 API Key。</c>；
/// - 测试流程：**先持久化再测试** → 文字模型 → 设 <c>visionVerifiedAt</c> → 若支持视觉则视觉模型；
/// - 成功 <c>两种模型连接成功。文字：&lt;前24字符&gt; · 视觉：&lt;前24字符&gt;</c>；
/// - 失败 <c>配置已保存，但连接测试失败：&lt;err&gt;</c> 并退回第 2 步。
/// </summary>
public sealed class ModelsPage : TaTabPage
{
    /// <summary>左栏宽度（参考文档 §9.9）。</summary>
    public const double RailWidth = 154;

    /// <summary>步骤圆点直径（ModelSettingsView.swift:166）。</summary>
    public const double StepDotSize = 24;

    /// <summary>步骤连接线高度（ModelSettingsView.swift:171）。</summary>
    public const double StepConnectorHeight = 52;

    /// <summary>左栏内边距（ModelSettingsView.swift:192-194）。</summary>
    public static readonly Thickness RailPadding = new(14, 18, 14, 18);

    // ---- 状态（对应 ModelSettingsView.swift:8-21 的 @State）----
    private AiProviderProfileState _state = new();
    private AiProviderProfile _draft = new("新模型");
    private string _apiKey = string.Empty;
    private bool _hasStoredKey;
    private ProviderPreset _selectedPreset = ProviderPreset.Custom;
    private bool _providerChosen;
    private ModelSetupStep _currentStep = ModelSetupStep.Provider;
    private bool _isCreatingProfile = true;
    private bool _isShowingAdvanced;
    private bool _isTesting;
    private bool _syncing;
    private readonly List<System.Windows.Controls.Button> _stepSaveButtons = new();
    private string? _statusMessage;
    private StatusStyle _statusStyle = StatusStyle.Neutral;
    private string _taskTemplate = MultimodalTasks.Raw(MultimodalTask.General);

    // ---- 视图宿主 ----
    private readonly Border _headerRight = new() { Background = Brushes.Transparent };
    private readonly StackPanel _rail = new() { Orientation = Orientation.Vertical };
    private readonly Border _stepHost = new() { Background = Brushes.Transparent };
    // ⚠️ 控件必须在字段初始化器里创建（先于基类 TaTabPage 构造执行）：
    // 基类构造会虚调用 Build() → Reload → SyncInputs，此时构造函数体还没跑，
    // 字段若在构造体里赋值，SyncInputs 读到的全是 null → NullReferenceException。
    private readonly TaTextBox _nameBox = new("例如：DeepSeek 日常", string.Empty, 350);
    private readonly TaTextBox _baseUrlBox = new("https://api.example.com/v1", string.Empty, 350);
    private readonly TaTextBox _visionModelBox = new("视觉模型名", string.Empty, 300);
    private readonly TaTextBox _textModelBox = new("文字模型名", string.Empty, 300);
    private readonly TaPasswordBox _keyBox = new();
    private readonly Border _validationHost = new() { Background = Brushes.Transparent };
    private readonly Border _advancedBody = new() { Background = Brushes.Transparent };
    private readonly TaPicker _providerKindPicker = new(
        ProviderKinds.All.Select(ProviderKinds.DisplayName).ToArray(),
        ProviderKinds.DisplayName(ProviderKind.OpenAICompatible),
        210);
    private readonly TaPicker _taskTemplatePicker = new(
        MultimodalTasks.All.Select(MultimodalTasks.DisplayName).ToArray(),
        MultimodalTasks.DisplayName(MultimodalTask.General),
        210);
    private readonly Border _actionsHost = new() { Background = Brushes.Transparent };

    private enum StatusStyle
    {
        Neutral,
        Success,
        Error,
    }

    /// <summary>构造。</summary>
    public ModelsPage(TaServices services)
        : base(services)
    {
        // 控件创建已上移到字段初始化器（基类构造会虚调用 Build()，见字段处注释）。

        _providerKindPicker.SelectionChanged += (_, _) =>
        {
            var selected = ProviderKinds.All.FirstOrDefault(
                k => string.Equals(ProviderKinds.DisplayName(k), _providerKindPicker.SelectedItem, StringComparison.Ordinal));
            _draft.ProviderKind = selected;
        };

        _taskTemplatePicker.SelectionChanged += (_, _) =>
        {
            var selected = MultimodalTasks.All.FirstOrDefault(
                t => string.Equals(MultimodalTasks.DisplayName(t), _taskTemplatePicker.SelectedItem, StringComparison.Ordinal));
            _taskTemplate = MultimodalTasks.Raw(selected);
            Services.Settings.SetString(SettingsKeys.MultimodalTaskTemplate, _taskTemplate);
        };

        // 输入框改动即时回写草稿并刷新校验行（不重画整步，避免抢焦点）
        _nameBox.Input.TextChanged += (_, _) => OnDraftFieldChanged();
        _baseUrlBox.Input.TextChanged += (_, _) => OnDraftFieldChanged();
        _visionModelBox.Input.TextChanged += (_, _) => OnDraftFieldChanged();
        _textModelBox.Input.TextChanged += (_, _) => OnDraftFieldChanged();
        _keyBox.Input.PasswordChanged += (_, _) =>
        {
            _apiKey = _keyBox.Password;
            RefreshStepButtons();
        };
    }

    private void OnDraftFieldChanged()
    {
        // SyncInputs 程序化回写控件也会触发 TextChanged —— 那不是用户输入。
        // 不挡住的话，回写「名称」的事件会把其它输入框的旧值（如空的 BaseUrl）
        // 抄回 _draft，把 Apply 刚设置的厂商地址/推荐模型全部覆盖掉 → 永远保存不了。
        if (_syncing)
        {
            return;
        }

        PullInputs();
        RebuildValidation();
        RefreshStepButtons();
    }

    /// <inheritdoc />
    protected override FrameworkElement Build()
    {
        _taskTemplate = Services.Settings.GetString(SettingsKeys.MultimodalTaskTemplate)
            ?? MultimodalTasks.Raw(MultimodalTask.General);
        Reload();

        var header = BuildHeader();

        // ⚠️ 这里必须用 Grid 而不是纵向 StackPanel：
        // 纵向 StackPanel 会在垂直方向给子元素「无限」高度，
        // 里面的 ScrollViewer 就不会滚动，整个向导会溢出 600px 的窗口。
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        root.Children.Add(header);
        var headerDivider = new Border { Height = 1, Background = Brand.TaBrushes.Hairline };
        Grid.SetRow(headerDivider, 1);
        root.Children.Add(headerDivider);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(RailWidth) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // 左栏的 padding/底色放在包一层的 Border 上（StackPanel 没有 Padding）
        var railHost = new Border
        {
            Background = Brand.TaBrushes.Paper40,
            Padding = RailPadding,
            Child = _rail,
        };
        Grid.SetColumn(railHost, 0);
        body.Children.Add(railHost);
        var divider = new Border { Width = 1, Background = Brand.TaBrushes.Hairline };
        Grid.SetColumn(divider, 1);
        body.Children.Add(divider);
        Grid.SetColumn(_stepHost, 2);
        body.Children.Add(_stepHost);
        Grid.SetRow(body, 2);
        root.Children.Add(body);

        // ModelSettingsView.swift:60-65 —— 圆角 16 + elevatedPaper@72% + hairline 描边
        return new TaCard(root, new Thickness(0), 16, Brand.TaBrushes.ElevatedPaper72);
    }

    /// <inheritdoc />
    public override void Refresh() => Reload();

    // ================= 顶部紧凑头（ModelSettingsView.swift:79-107）=================

    private FrameworkElement BuildHeader()
    {
        var title = TaLayout.V(
            1,
            TaPrimitives.Text("AI 模型", Brand.TaTypography.Headline, Brand.TaBrushes.Ink, FontWeights.Bold),
            TaPrimitives.Text(
                "配置一次，即可用于 AI 识图和截图翻译",
                Brand.TaTypography.Caption2,
                Brand.TaBrushes.MutedInk));

        var icon = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(9),
            Background = Brand.TaBrushes.Cinnabar10,
            Child = new Brand.TaIcon
            {
                Kind = Brand.TaIconKind.Sparkles,
                Size = 15,
                Brush = Brand.TaBrushes.Cinnabar,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        return new Border
        {
            Padding = new Thickness(14, 0, 14, 0),
            Height = 48,
            Child = TaLayout.H(
                10,
                icon,
                title,
                _headerRight,
                TaButton.Create("新增", TaButtonKind.Secondary, BeginNewProfile)),
        };
    }

    private void RebuildHeaderRight()
    {
        if (_state.Profiles.Count == 0)
        {
            _headerRight.Child = TaPrimitives.Text("尚未配置", Brand.TaTypography.Caption, Brand.TaBrushes.MutedInk);
            return;
        }

        var picker = new TaPicker(
            _state.Profiles.Select(p => p.TrimmedName).ToArray(),
            _isCreatingProfile ? "新配置" : _draft.TrimmedName,
            200);
        picker.SelectionChanged += (_, _) =>
        {
            var target = _state.Profiles.FirstOrDefault(
                p => string.Equals(p.TrimmedName, picker.SelectedItem, StringComparison.Ordinal));
            if (target is not null)
            {
                Select(target.Id);
            }
        };

        _headerRight.Child = picker;
    }

    // ================= 左栏步骤轨（ModelSettingsView.swift:142-197）=================

    /// <summary>步骤圆点里的数字 —— 必须水平垂直居中，否则数字贴左上角（用户实测反馈）。</summary>
    private static System.Windows.Controls.TextBlock StepNumber(string text, System.Windows.Media.Brush brush)
    {
        var label = TaPrimitives.Text(text, Brand.TaTypography.Caption2, brush, FontWeights.Bold);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        return label;
    }

    private void RebuildRail()
    {
        _rail.Children.Clear();
        _rail.Margin = new Thickness(0);
        _rail.HorizontalAlignment = HorizontalAlignment.Stretch;
        _rail.VerticalAlignment = VerticalAlignment.Stretch;

        var steps = ModelSetupSteps.All;
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            var isCurrent = step == _currentStep;
            var isDone = (int)step < (int)_currentStep;

            var dot = new Border
            {
                Width = StepDotSize,
                Height = StepDotSize,
                CornerRadius = new CornerRadius(StepDotSize / 2),
                Background = isCurrent ? Brand.TaBrushes.Cinnabar : Brand.TaBrushes.Paper,
                BorderBrush = isDone ? Brand.TaBrushes.Cinnabar35 : Brand.TaBrushes.Hairline,
                BorderThickness = new Thickness(1),
                Child = isDone
                    ? new Brand.TaIcon
                    {
                        Kind = Brand.TaIconKind.Check,
                        Size = 10,
                        Brush = Brand.TaBrushes.Cinnabar,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    }
                    : StepNumber(((int)step + 1).ToString(),
                        isCurrent ? Brand.TaBrushes.Paper : Brand.TaBrushes.MutedInk),
            };

            var column = new StackPanel { Orientation = Orientation.Vertical };
            column.Children.Add(dot);
            if (index < steps.Count - 1)
            {
                // ModelSettingsView.swift:171 —— 连接线高 52
                column.Children.Add(new Border
                {
                    Width = 1,
                    Height = StepConnectorHeight,
                    Background = isDone ? Brand.TaBrushes.Cinnabar24 : Brand.TaBrushes.Hairline,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
            }

            var title = TaPrimitives.Text(
                ModelSetupSteps.Title(step),
                Brand.TaTypography.Caption,
                isCurrent ? Brand.TaBrushes.Cinnabar : Brand.TaBrushes.MutedInk,
                isCurrent ? FontWeights.SemiBold : FontWeights.Medium);
            title.Margin = new Thickness(0, 4, 0, 0);

            var row = TaLayout.H(9, column, title);
            _rail.Children.Add(row);
        }

        // 「我已经配置过」入口（ModelSettingsView.swift:181-190）
        if (_state.Profiles.Count > 0 && _currentStep == ModelSetupStep.Provider)
        {
            var picker = new TaPicker(
                _state.Profiles.Select(p => p.TrimmedName).ToArray(),
                null,
                120);
            picker.SelectionChanged += (_, _) =>
            {
                var target = _state.Profiles.FirstOrDefault(
                    p => string.Equals(p.TrimmedName, picker.SelectedItem, StringComparison.Ordinal));
                if (target is not null)
                {
                    Select(target.Id);
                }
            };
            picker.Margin = new Thickness(0, 8, 0, 0);
            _rail.Children.Add(picker);
        }
    }

    // ================= 第 1 步：选择服务商（ModelSettingsView.swift:211-289）=================

    private FrameworkElement BuildProviderStep()
    {
        var list = new StackPanel { Orientation = Orientation.Vertical };
        foreach (var preset in ProviderPresets.GuidedCases)
        {
            var rowPreset = preset;
            var isSelected = _providerChosen && _selectedPreset == preset;
            var icon = new ProviderBrandIcon(preset, 23) { Width = 25 };

            var titleRow = TaLayout.H(
                7,
                TaPrimitives.Text(
                    ProviderPresets.Title(preset),
                    Brand.TaTypography.Callout,
                    Brand.TaBrushes.Ink,
                    FontWeights.SemiBold),
                ProviderPresets.IsRecommended(preset)
                    ? TaPrimitives.Badge("推荐", Brand.TaBrushes.Cinnabar, Brand.TaBrushes.Cinnabar10)
                    : null);

            var texts = TaLayout.V(
                1,
                titleRow,
                TaPrimitives.Text(
                    ProviderPresets.GuidedSubtitle(preset),
                    Brand.TaTypography.Caption2,
                    Brand.TaBrushes.MutedInk));

            var content = TaLayout.H(
                10,
                icon,
                texts,
                isSelected
                    ? new Brand.TaIcon
                    {
                        Kind = Brand.TaIconKind.CheckCircle,
                        Size = 17,
                        Brush = Brand.TaBrushes.Cinnabar,
                        VerticalAlignment = VerticalAlignment.Center,
                    }
                    : null);

            var row = new TaSelectableRow(content, isSelected);
            row.Margin = new Thickness(0, 3, 0, 3);
            row.Clicked += (_, _) => Apply(rowPreset);
            list.Children.Add(row);
        }

        var scroll = new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 4, 0),
        };

        var next = TaButton.Create("下一步", TaButtonKind.Primary, () => GoTo(ModelSetupStep.Credentials));
        next.IsEnabled = _providerChosen;

        var heading = TaLayout.V(
            3,
            TaPrimitives.Text(
                "选择 AI 服务商",
                Brand.TaTypography.Title3,
                Brand.TaBrushes.Ink,
                FontWeights.SemiBold),
            TaPrimitives.Text(
                "选择你常用的服务，拓会自动填写接口和推荐模型。",
                Brand.TaTypography.Caption,
                Brand.TaBrushes.MutedInk));
        var footer = BuildRightAlignedRow(next);

        // 第 1 步由外层 Grid 约束高度：标题 auto / 列表 star / 按钮 auto
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        return new Border
        {
            Padding = new Thickness(16),
            Child = root,
        };
    }

    /// <summary>左留白 + 右侧控件的行（对应 <c>HStack { Spacer(); Button(...) }</c>）。</summary>
    private static Grid BuildRightAlignedRow(params FrameworkElement[] right)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var _ in right)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        var spacer = TaPrimitives.Spacer();
        Grid.SetColumn(spacer, 0);
        grid.Children.Add(spacer);
        for (var i = 0; i < right.Length; i++)
        {
            if (i > 0)
            {
                right[i].Margin = new Thickness(9, 0, 0, 0);
            }

            Grid.SetColumn(right[i], i + 1);
            grid.Children.Add(right[i]);
        }

        return grid;
    }

    // ================= 第 2 步：填 API Key（ModelSettingsView.swift:291-376）=================

    private FrameworkElement BuildCredentialsStep()
    {
        var supportsVision = ProviderPresets.SupportsVisionDirectly(_selectedPreset);

        var keyCard = new TaCard(
            TaLayout.V(
                7,
                TaPrimitives.Text("API Key", Brand.TaTypography.Callout, Brand.TaBrushes.Ink, FontWeights.SemiBold),
                _keyBox,
                new TaStatusLine(
                    _hasStoredKey ? "已安全保存在本机密钥库" : "仅保存在本机（DPAPI 加密），不会写入普通配置文件",
                    _hasStoredKey ? Brand.TaIconKind.ShieldCheck : Brand.TaIconKind.LockShield,
                    _hasStoredKey ? Brand.TaBrushes.Success : Brand.TaBrushes.MutedInk)),
            new Thickness(12));

        var modelCardChildren = new List<FrameworkElement>
        {
            TaPrimitives.Text("推荐模型", Brand.TaTypography.Callout, Brand.TaBrushes.Ink, FontWeights.SemiBold),
        };
        if (supportsVision)
        {
            modelCardChildren.Add(BuildModelField("视觉模型", _visionModelBox, ProviderPresets.SuggestedVisionModel(_selectedPreset)));
        }

        modelCardChildren.Add(BuildModelField("文字模型", _textModelBox, ProviderPresets.SuggestedTextModel(_selectedPreset)));
        if (!supportsVision)
        {
            modelCardChildren.Add(new TaStatusLine(
                ProviderPresets.CodingPlanNotice,
                Brand.TaIconKind.InfoCircle,
                Brand.TaBrushes.Warning,
                wrap: true));
        }

        var modelCard = new TaCard(TaLayout.V(8, modelCardChildren.ToArray()), new Thickness(12));

        var back = TaButton.Create("上一步", TaButtonKind.Secondary, () => GoTo(ModelSetupStep.Provider));
        var saveOnly = TaButton.Create("仅保存", TaButtonKind.Secondary, SaveOnly);
        var saveAndTest = TaButton.Create("测试并保存", TaButtonKind.Primary, () => _ = SaveAndTestAsync());
        _stepSaveButtons.Add(saveOnly);
        _stepSaveButtons.Add(saveAndTest);
        RefreshStepButtons();

        var testing = _isTesting
            ? TaLayout.H(
                7,
                new ProgressBar { IsIndeterminate = true, Width = 60, Height = 4, VerticalAlignment = VerticalAlignment.Center },
                TaPrimitives.Text("正在验证…", Brand.TaTypography.Caption, Brand.TaBrushes.MutedInk))
            : null;

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = TaLayout.V(
                12,
                TaLayout.H(
                    10,
                    TaLayout.V(
                        3,
                        TaPrimitives.Text(
                            $"配置 {ProviderPresets.Title(_selectedPreset)}",
                            Brand.TaTypography.Title3,
                            Brand.TaBrushes.Ink,
                            FontWeights.SemiBold),
                        TaPrimitives.Text(
                            "填写 API Key；推荐模型已自动带入，可按需修改。",
                            Brand.TaTypography.Caption,
                            Brand.TaBrushes.MutedInk)),
                    IsDraftSaved ? _actionsHost : null),
                keyCard,
                modelCard,
                BuildAdvancedSettings(),
                _validationHost),
            Padding = new Thickness(16, 14, 16, 10),
        };

        // 同样必须用 Grid：footer 固定 48，滚动区吃掉剩余高度
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        Grid.SetRow(scroll, 0);
        root.Children.Add(scroll);
        var footerDivider = new Border { Height = 1, Background = Brand.TaBrushes.Hairline };
        Grid.SetRow(footerDivider, 1);
        root.Children.Add(footerDivider);

        var footerRow = new Grid { VerticalAlignment = VerticalAlignment.Center };
        footerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(back, 0);
        footerRow.Children.Add(back);
        var footerSpacer = TaPrimitives.Spacer();
        Grid.SetColumn(footerSpacer, 1);
        footerRow.Children.Add(footerSpacer);
        if (testing is not null)
        {
            Grid.SetColumn(testing, 2);
            footerRow.Children.Add(testing);
        }

        saveOnly.Margin = new Thickness(9, 0, 0, 0);
        Grid.SetColumn(saveOnly, 3);
        footerRow.Children.Add(saveOnly);
        saveAndTest.Margin = new Thickness(9, 0, 0, 0);
        Grid.SetColumn(saveAndTest, 4);
        footerRow.Children.Add(saveAndTest);

        var footer = new Border
        {
            Padding = new Thickness(16, 0, 16, 0),
            Child = footerRow,
        };
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        return new Border { Child = root };
    }

    private FrameworkElement BuildModelField(string title, TaTextBox box, string? suggestion)
    {
        var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = TaPrimitives.Text(title, Brand.TaTypography.Callout, Brand.TaBrushes.Ink);
        label.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(label, 0);
        row.Children.Add(label);
        box.Margin = new Thickness(0, 0, 7, 0);
        Grid.SetColumn(box, 1);
        row.Children.Add(box);
        if (suggestion is not null)
        {
            var recommend = TaButton.Create("推荐", TaButtonKind.Secondary, () => box.Text = suggestion);
            recommend.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(recommend, 2);
            row.Children.Add(recommend);
        }

        return row;
    }

    // ---- 高级设置（ModelSettingsView.swift:392-438）----

    private FrameworkElement BuildAdvancedSettings()
    {
        RebuildAdvancedBody();

        var chevron = new Brand.TaIcon
        {
            Kind = _isShowingAdvanced ? Brand.TaIconKind.ChevronDown : Brand.TaIconKind.ArrowRight,
            Size = 13,
            Brush = Brand.TaBrushes.Ink,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var headerText = TaPrimitives.Text(
            "高级设置 · 配置名称、服务地址与默认任务",
            Brand.TaTypography.Callout,
            Brand.TaBrushes.Ink,
            FontWeights.Medium);

        var header = new Border
        {
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = TaLayout.H(8, new Brand.TaIcon { Kind = Brand.TaIconKind.Sliders, Size = 14, VerticalAlignment = VerticalAlignment.Center }, headerText, chevron),
        };
        header.MouseLeftButtonUp += (_, _) =>
        {
            _isShowingAdvanced = !_isShowingAdvanced;
            RebuildStep();
        };

        var panel = new StackPanel { Orientation = Orientation.Vertical };
        panel.Children.Add(header);
        if (_isShowingAdvanced)
        {
            var body = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 7, 0, 0) };
            body.Children.Add(new Border { Height = 1, Background = Brand.TaBrushes.Hairline, Margin = new Thickness(0, 0, 0, 7) });
            body.Children.Add(_advancedBody);
            panel.Children.Add(body);
        }

        return new TaCard(panel, new Thickness(12));
    }

    private void RebuildAdvancedBody()
    {
        var removeKey = _hasStoredKey
            ? TaButton.Create("移除这套配置的 API Key", TaButtonKind.Destructive, RemoveKey)
            : null;

        _advancedBody.Child = TaLayout.V(
            9,
            new TaFieldRow("配置名称", _nameBox),
            new TaFieldRow("接口协议", _providerKindPicker),
            new TaFieldRow("服务地址", _baseUrlBox),
            new TaFieldRow("识图默认任务", _taskTemplatePicker),
            removeKey);
    }

    // ================= 第 3 步：测试并保存（ModelSettingsView.swift:467-582）=================

    private FrameworkElement BuildCompletionStep()
    {
        var supportsVision = ProviderPresets.SupportsVisionDirectly(_selectedPreset);
        var verified = _draft.HasVerifiedConnection;

        var head = TaLayout.H(
            10,
            new Brand.TaIcon
            {
                Kind = Brand.TaIconKind.CheckCircle,
                Size = 22,
                Brush = Brand.TaBrushes.Success,
                VerticalAlignment = VerticalAlignment.Center,
            },
            TaLayout.V(
                2,
                TaPrimitives.Text(
                    verified ? "连接成功" : "配置已保存",
                    Brand.TaTypography.Title3,
                    Brand.TaBrushes.Ink,
                    FontWeights.SemiBold),
                TaPrimitives.Text(
                    verified
                        ? "配置已保存到本机，后续升级不需要重新填写。"
                        : "模型信息已经完整，建议先测试一次连接。",
                    Brand.TaTypography.Caption,
                    Brand.TaBrushes.MutedInk)),
            _actionsHost);

        var summaryChildren = new List<FrameworkElement>
        {
            TaLayout.H(
                10,
                new ProviderBrandIcon(_selectedPreset, 24),
                TaLayout.V(
                    2,
                    TaPrimitives.Text(_draft.TrimmedName, Brand.TaTypography.Headline, Brand.TaBrushes.Ink, FontWeights.Bold),
                    TaPrimitives.Text(
                        ProviderPresets.Title(_selectedPreset),
                        Brand.TaTypography.Caption,
                        Brand.TaBrushes.MutedInk)),
                supportsVision
                    ? TaPrimitives.Badge("可用", Brand.TaBrushes.Success, Brand.TaBrushes.Success10)
                    : TaPrimitives.Badge("仅文字", Brand.TaBrushes.Warning, Brand.TaBrushes.Warning12)),
            new Border { Height = 1, Background = Brand.TaBrushes.Hairline, Margin = new Thickness(0, 4, 0, 4) },
        };
        if (supportsVision)
        {
            summaryChildren.Add(BuildSummaryRow("视觉模型", _draft.VisionModel));
        }

        summaryChildren.Add(BuildSummaryRow("文字模型", _draft.TextModel));

        var summary = new TaCard(TaLayout.V(10, summaryChildren.ToArray()), new Thickness(14), 11, Brand.TaBrushes.Paper54);

        FrameworkElement applySection = supportsVision
            ? new TaCard(
                TaLayout.V(
                    10,
                    TaPrimitives.Text("应用到功能", Brand.TaTypography.Callout, Brand.TaBrushes.Ink, FontWeights.SemiBold),
                    new TaFieldRow("识图默认", BuildProfilePicker(_state.ActiveProfileId, ActiveReadyProfiles(), id => _ = SetActiveAsync(id))),
                    new TaFieldRow("翻译使用", BuildProfilePicker(_state.TranslationProfileId, TranslationReadyProfiles(), id => _ = SetTranslationAsync(id)))),
                new Thickness(14),
                11,
                Brand.TaBrushes.Paper54)
            : new TaStatusLine(ProviderPresets.CodingPlanCompletionNotice, Brand.TaIconKind.InfoCircle, Brand.TaBrushes.Warning, wrap: true);

        var addAnother = TaButton.Create("添加另一套", TaButtonKind.Secondary, BeginNewProfile);
        var test = TaButton.Create(verified ? "重新测试" : "测试连接", TaButtonKind.Secondary, () => _ = SaveAndTestAsync());
        test.IsEnabled = !_isTesting;
        var done = TaButton.Create("完成", TaButtonKind.Primary, OnDone);

        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(addAnother, 0);
        footer.Children.Add(addAnother);
        Grid.SetColumn(TaPrimitives.Spacer(), 1);
        footer.Children.Add(TaPrimitives.Spacer());
        test.Margin = new Thickness(0, 0, 9, 0);
        Grid.SetColumn(test, 2);
        footer.Children.Add(test);
        Grid.SetColumn(done, 3);
        footer.Children.Add(done);

        var stack = new List<FrameworkElement> { head, summary, applySection };
        if (_statusMessage is not null)
        {
            stack.Add(new TaStatusLine(_statusMessage, StatusIcon(_statusStyle), StatusBrush(_statusStyle), wrap: true));
        }

        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(16),
            Content = TaLayout.V(12, stack.ToArray()),
        };
    }

    private FrameworkElement BuildSummaryRow(string title, string value)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = TaPrimitives.Text(title, Brand.TaTypography.Caption, Brand.TaBrushes.MutedInk);
        Grid.SetColumn(label, 0);
        row.Children.Add(label);
        var text = TaPrimitives.Text(value, Brand.TaTypography.Caption, Brand.TaBrushes.Ink, mono: true);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    private TaPicker BuildProfilePicker(
        string? selectedId,
        IReadOnlyList<AiProviderProfile> profiles,
        Action<string?> onSelected)
    {
        var picker = new TaPicker(
            profiles.Select(p => p.TrimmedName).ToArray(),
            profiles.FirstOrDefault(p => string.Equals(p.Id, selectedId, StringComparison.Ordinal))?.TrimmedName,
            250);
        picker.SelectionChanged += (_, _) =>
        {
            var target = profiles.FirstOrDefault(
                p => string.Equals(p.TrimmedName, picker.SelectedItem, StringComparison.Ordinal));
            onSelected(target?.Id);
        };
        return picker;
    }

    // ================= 状态与行为 =================

    private ModelSetupProgress SetupProgress => new()
    {
        HasEndpoint = !string.IsNullOrWhiteSpace(_draft.BaseUrl),
        HasApiKey = _hasStoredKey || !string.IsNullOrWhiteSpace(_apiKey),
        HasVisionModel = ProviderPresets.SupportsVisionDirectly(_selectedPreset) || _draft.HasVisionModel,
        HasTextModel = _draft.HasTextModel,
    };

    private bool IsDraftSaved => _state.Profiles.Any(p => string.Equals(p.Id, _draft.Id, StringComparison.Ordinal));

    private IReadOnlyList<AiProviderProfile> ActiveReadyProfiles()
        => _state.Profiles
            .Where(p => p.ValidationMessage() is null && Services.Providers.HasApiKey(p.Id))
            .ToList();

    private IReadOnlyList<AiProviderProfile> TranslationReadyProfiles()
        => Services.Providers.EligibleTranslationProfiles(_state);

    private void RebuildStep()
    {
        // ⚠️ 挂新树之前必须递归拆掉旧树：_nameBox/_keyBox/_visionModelBox 等是
        // 字段级单例控件，每次重建步骤都会复用 —— 它们还挂在旧树的深层时直接
        // Add 进新树，WPF 抛「指定的元素已经是另一个元素的逻辑子元素」
        // （实测：第 2 步内点「高级设置」/ 来回切步骤必崩）。
        // 所有承载字段单例控件的宿主都要拆 —— _advancedBody（高级设置面板）
        // 也嵌在步骤树内，漏拆就会在 RebuildAdvancedBody 复现同款崩溃。
        DetachTree(_stepHost.Child);
        DetachTree(_advancedBody.Child);
        DetachTree(_validationHost.Child);
        DetachTree(_headerRight.Child);
        DetachTree(_actionsHost.Child);
        _stepSaveButtons.Clear();

        _stepHost.Child = _currentStep switch
        {
            ModelSetupStep.Provider => BuildProviderStep(),
            ModelSetupStep.Credentials => BuildCredentialsStep(),
            _ => BuildCompletionStep(),
        };

        RebuildActionsMenu();
        RebuildHeaderRight();
        RebuildRail();
        SyncInputs();
        RebuildValidation();
        RefreshStepButtons();
    }

    /// <summary>
    /// 递归断开一棵 WPF 逻辑树，让所有深层控件（含字段级单例）脱离旧父级。
    /// Panel.Clear / Child=null 只断第一层 —— 单例控件往往在深层，必须递归。
    /// </summary>
    /// <summary>按当前草稿状态刷新「仅保存 / 测试并保存」的可用性 ——
    /// 必须在用户每次输入后调用，否则填完 Key 按钮也不会亮（构建时定死的历史 bug）。</summary>
    private void RefreshStepButtons()
    {
        var blocked = _isTesting
            || ModelSaveGate.PersistFailure(_draft, _selectedPreset, _apiKey, _hasStoredKey) is not null;
        foreach (var button in _stepSaveButtons)
        {
            button.IsEnabled = !blocked;
        }
    }

    /// <summary>字段级单例控件 —— DetachTree 遇到它们只从父级摘下，绝不深入内部：
    /// TaTextBox/TaPasswordBox/TaPicker 是自包含复合控件，拆开内部（如把 Input 从
    /// Border 上摘走）会让它们永久残废（输入框塌成 1px 细线、下拉框显示空白）。</summary>
    private bool IsOwnedSingleton(DependencyObject? element) =>
        ReferenceEquals(element, _nameBox) || ReferenceEquals(element, _baseUrlBox)
        || ReferenceEquals(element, _visionModelBox) || ReferenceEquals(element, _textModelBox)
        || ReferenceEquals(element, _keyBox) || ReferenceEquals(element, _providerKindPicker)
        || ReferenceEquals(element, _taskTemplatePicker);

    private void DetachTree(DependencyObject? root)
    {
        if (root is null || IsOwnedSingleton(root))
        {
            return;
        }

        switch (root)
        {
            case Panel panel:
                var children = panel.Children.OfType<UIElement>().ToList();
                panel.Children.Clear();
                foreach (var child in children)
                {
                    DetachTree(child);
                }

                break;

            case Border border when border.Child is not null:
                var borderChild = border.Child;
                border.Child = null;
                DetachTree(borderChild);
                break;

            case Decorator decorator when decorator.Child is not null:
                var decoratorChild = decorator.Child;
                decorator.Child = null;
                DetachTree(decoratorChild);
                break;

            case ContentControl content when content.Content is FrameworkElement element:
                content.Content = null;
                DetachTree(element);
                break;
        }
    }

    /// <summary>
    /// 程序内冒烟驱动（--smoke 无人值守验证）：完整走一遍厂商选择/高级设置/
    /// 步骤切换/填 Key/保存的每条路径，逐步写日志。假 Key 的「测试并保存」
    /// 预期网络失败 —— 只验证流程不崩且状态行有反馈。
    /// </summary>
    internal async Task SmokeDriveAsync(Action<string> log)
    {
        void Step(string name, Action action)
        {
            try
            {
                action();
                log($"[OK] {name}");
            }
            catch (Exception error)
            {
                log($"[FAIL] {name}: {error.GetType().Name}: {error.Message}");
            }
        }

        foreach (var preset in new[]
                 {
                     ProviderPreset.ZhipuApi, ProviderPreset.ZhipuCodingPlan, ProviderPreset.DeepSeek,
                     ProviderPreset.OpenAi, ProviderPreset.Gemini, ProviderPreset.Anthropic,
                     ProviderPreset.OpenRouter, ProviderPreset.Azure, ProviderPreset.Custom,
                 })
        {
            Step($"选厂商 {preset}", () => Apply(preset));
        }

        Step("第1步 高级设置 展开", () => { _isShowingAdvanced = true; RebuildStep(); });
        Step("第1步 高级设置 收起", () => { _isShowingAdvanced = false; RebuildStep(); });
        Step("下一步 → 凭据步骤", () => GoTo(ModelSetupStep.Credentials));
        Step("粘贴测试 Key", () => _keyBox.Input.Password = "sk-smoke-test-000");
        Step("下一步 → 完成步骤", () => GoTo(ModelSetupStep.Complete));
        Step("仅保存（SaveOnly）", () => SaveOnly());
        log($"[INFO] 保存后状态行: {_statusMessage ?? "(空)"}");
        Step("切回凭据步骤", () => GoTo(ModelSetupStep.Credentials));
        log($"[INFO] Key 字段保留 = {!string.IsNullOrEmpty(_keyBox.Password)}");
        Step("凭据步 高级设置 展开", () => { _isShowingAdvanced = true; RebuildStep(); });
        Step("凭据步 高级设置 收起", () => { _isShowingAdvanced = false; RebuildStep(); });
        Step("选回已保存配置", () => Select(_draft.Id));
        Step("回到完成步骤", () => GoTo(ModelSetupStep.Complete));

        // ── 真实保存成功路径：智谱 + 测试 Key → 仅保存，应显示「配置已保存。」──
        void TraceBaseUrl(string tag) =>
            log($"[TRACE] {tag}: _draft.BaseUrl=\"{_draft.BaseUrl}\" _baseUrlBox.Text=\"{_baseUrlBox.Text}\" "
                + $"ProviderKind={_draft.ProviderKind}");

        Step("选智谱（真实保存路径）", () => Apply(ProviderPreset.ZhipuApi));
        TraceBaseUrl("Apply后");
        Step("进入凭据步骤", () => GoTo(ModelSetupStep.Credentials));
        TraceBaseUrl("GoTo凭据后");
        Step("粘贴测试 Key", () => _keyBox.Input.Password = "sk-smoke-test-000");
        Step("进入完成步骤", () => GoTo(ModelSetupStep.Complete));
        TraceBaseUrl("GoTo完成后");
        Step("仅保存（智谱+Key）", () => SaveOnly());
        TraceBaseUrl("SaveOnly后");
        var saveOk = string.Equals(_statusMessage, "配置已保存。", StringComparison.Ordinal);
        log($"[INFO] 保存状态行: {_statusMessage ?? "(空)"}");
        log(saveOk ? "[OK] 真实保存路径成功（配置已保存。）"
                   : "[FAIL] 真实保存路径失败 —— 保存功能有问题！");

        // 测试并保存：假 Key 预期网络失败 —— 验证「不崩 + 有错误反馈」。
        await SaveAndTestAsync();
        log($"[INFO] 测试并保存结束，状态行: {_statusMessage ?? "(空)"}");
        log("[OK] 测试并保存流程完成（假 Key 网络失败属预期，不崩即通过）");
    }

    private void RebuildActionsMenu()
    {
        if (!IsDraftSaved)
        {
            _actionsHost.Child = null;
            return;
        }

        var menu = new TaMenuButton();
        menu.Add("复制配置", () => DuplicateSelectedProfile());
        if (ProviderPresets.SupportsVisionDirectly(_selectedPreset)
            && !string.Equals(_state.ActiveProfileId, _draft.Id, StringComparison.Ordinal))
        {
            menu.Add("设为 AI 识图默认", () => _ = SetActiveAsync(_draft.Id));
        }

        menu.AddDestructive("删除配置", ConfirmDeleteSelectedProfile);
        _actionsHost.Child = menu;
    }

    private void RebuildValidation()
    {
        var validation = _draft.ValidationMessage(
            requiresVisionModel: ProviderPresets.SupportsVisionDirectly(_selectedPreset),
            requiresTextModel: true);

        if (validation is not null)
        {
            _validationHost.Child = new TaStatusLine(validation, Brand.TaIconKind.WarningTriangle, Brand.TaBrushes.Warning, wrap: true);
            return;
        }

        if (_statusMessage is null)
        {
            _validationHost.Child = new TaStatusLine(
                ProviderPresets.SupportsVisionDirectly(_selectedPreset)
                    ? "这套配置可以同时用于 AI 识图和截图翻译。"
                    : "这套配置可用于文字请求；Coding Plan 不提供截图视觉直连。",
                Brand.TaIconKind.CheckCircle,
                Brand.TaBrushes.Success,
                wrap: true);
            return;
        }

        _validationHost.Child = new TaStatusLine(_statusMessage, StatusIcon(_statusStyle), StatusBrush(_statusStyle), wrap: true);
    }

    private static Brand.TaIconKind StatusIcon(StatusStyle style) => style switch
    {
        StatusStyle.Success => Brand.TaIconKind.CheckCircle,
        StatusStyle.Error => Brand.TaIconKind.OctagonX,
        _ => Brand.TaIconKind.InfoCircle,
    };

    private static Brush StatusBrush(StatusStyle style) => style switch
    {
        StatusStyle.Success => Brand.TaBrushes.Success,
        StatusStyle.Error => Brand.TaBrushes.Danger,
        _ => Brand.TaBrushes.MutedInk,
    };

    private void SyncInputs()
    {
        // 回写控件期间压制 TextChanged → PullInputs 的自毁循环（见 OnDraftFieldChanged）。
        _syncing = true;
        try
        {
            SyncInputsCore();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncInputsCore()
    {
        if (_nameBox.Text != _draft.Name)
        {
            _nameBox.Text = _draft.Name;
        }

        if (_baseUrlBox.Text != _draft.BaseUrl)
        {
            _baseUrlBox.Text = _draft.BaseUrl;
        }

        if (_visionModelBox.Text != _draft.VisionModel)
        {
            _visionModelBox.Text = _draft.VisionModel;
        }

        if (_textModelBox.Text != _draft.TextModel)
        {
            _textModelBox.Text = _draft.TextModel;
        }

        _keyBox.Password = _apiKey;
        _providerKindPicker.Select(ProviderKinds.DisplayName(_draft.ProviderKind));
        _taskTemplatePicker.Select(MultimodalTasks.DisplayName(MultimodalTasks.FromRaw(_taskTemplate)));
        _keyBox.ToolTip = _hasStoredKey
            ? "已保存；不更换可以留空"
            : "粘贴 API Key";
    }

    /// <summary>把输入框里的值抄回草稿（每次操作前调用）。</summary>
    private void PullInputs()
    {
        _draft.Name = _nameBox.Text;
        _draft.BaseUrl = _baseUrlBox.Text.Trim();
        _draft.VisionModel = _visionModelBox.Text.Trim();
        _draft.TextModel = _textModelBox.Text.Trim();
        _apiKey = _keyBox.Password;
    }

    private void Reload()
    {
        try
        {
            _state = Services.Providers.LoadState();
            var target = _state.Find(_state.ActiveProfileId) ?? _state.Profiles.FirstOrDefault();
            if (target is not null)
            {
                Select(target.Id);
            }
            else
            {
                BeginNewProfile();
            }
        }
        catch (Exception error)
        {
            ShowStatus(error.Message, StatusStyle.Error);
            BeginNewProfile();
        }
    }

    private void Select(string id)
    {
        var profile = _state.Find(id);
        if (profile is null)
        {
            return;
        }

        _draft = profile.Clone();
        _apiKey = string.Empty;
        _hasStoredKey = Services.Providers.HasApiKey(id);
        _selectedPreset = ProviderPresets.Matching(profile.ProviderKindRaw, profile.BaseUrl);
        _providerChosen = !string.IsNullOrWhiteSpace(profile.BaseUrl);
        _isCreatingProfile = false;
        _isShowingAdvanced = ProviderPresets.RequiresCustomEndpoint(_selectedPreset);
        _statusMessage = null;
        _currentStep = ModelSetupSteps.Recommended(SetupProgress);
        RebuildStep();
    }

    private void BeginNewProfile()
    {
        _draft = new AiProviderProfile($"新模型 {_state.Profiles.Count + 1}");
        _apiKey = string.Empty;
        _hasStoredKey = false;
        _selectedPreset = ProviderPreset.Custom;
        _providerChosen = false;
        _isCreatingProfile = true;
        _isShowingAdvanced = false;
        _statusMessage = null;
        _currentStep = ModelSetupStep.Provider;
        RebuildStep();
    }

    /// <summary>
    /// 选中一个服务商预设。对应 macOS <c>apply(_:)</c>（ModelSettingsView.swift:738-761）。
    /// </summary>
    private void Apply(ProviderPreset preset)
    {
        var previousPreset = _selectedPreset;
        var wasGenericName = _draft.TrimmedName.Length == 0
            || _draft.TrimmedName.StartsWith("新模型", StringComparison.Ordinal)
            || string.Equals(_draft.TrimmedName, ProviderPresets.SuggestedConfigurationName(previousPreset), StringComparison.Ordinal);

        _selectedPreset = preset;
        _providerChosen = true;
        _draft.ProviderKind = ProviderPresets.ProviderKind(preset);
        if (preset != ProviderPreset.Custom)
        {
            _draft.BaseUrl = ProviderPresets.BaseUrl(preset);
            _draft.VisionModel = ProviderPresets.SuggestedVisionModel(preset) ?? string.Empty;
            _draft.TextModel = ProviderPresets.SuggestedTextModel(preset) ?? string.Empty;
        }
        else
        {
            _draft.BaseUrl = string.Empty;
            _draft.VisionModel = string.Empty;
            _draft.TextModel = string.Empty;
        }

        if (wasGenericName)
        {
            _draft.Name = ProviderPresets.SuggestedConfigurationName(preset);
        }

        _isShowingAdvanced = ProviderPresets.RequiresCustomEndpoint(preset);
        _draft.VisionVerifiedAt = null;
        _statusMessage = null;
        RebuildStep();
    }

    private void GoTo(ModelSetupStep step)
    {
        PullInputs();
        _currentStep = step;
        RebuildStep();
    }

    private void DuplicateSelectedProfile()
    {
        if (!IsDraftSaved)
        {
            return;
        }

        var copy = _draft.Clone();
        copy.Id = AiProviderProfile.NewId();
        copy.Name = $"{_draft.TrimmedName} 副本";
        copy.VisionVerifiedAt = null;
        try
        {
            _ = Services.Providers.SaveProfile(copy, Services.Providers.ApiKey(_draft.Id));
            Reload();
            Select(copy.Id);
            ShowStatus("已复制配置。", StatusStyle.Success);
        }
        catch (Exception error)
        {
            ShowStatus(error.Message, StatusStyle.Error);
        }
    }

    private void ConfirmDeleteSelectedProfile()
    {
        if (!IsDraftSaved)
        {
            BeginNewProfile();
            return;
        }

        var result = MessageBox.Show(
            "此操作只删除这套模型配置，不会影响其他配置。",
            $"删除“{_draft.TrimmedName}”？",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            _state = Services.Providers.DeleteProfile(_draft.Id);
            Reload();
        }
        catch (Exception error)
        {
            ShowStatus(error.Message, StatusStyle.Error);
        }
    }

    private async Task SetActiveAsync(string? id)
    {
        try
        {
            _state = Services.Providers.SetActiveProfile(id);
            ShowStatus("已更新 AI 识图默认模型。", StatusStyle.Success);
        }
        catch (Exception error)
        {
            ShowStatus(error.Message, StatusStyle.Error);
        }

        await Task.CompletedTask;
    }

    private async Task SetTranslationAsync(string? id)
    {
        try
        {
            _state = Services.Providers.SetTranslationProfile(id);
            ShowStatus("已更新截图翻译模型。", StatusStyle.Success);
        }
        catch (Exception error)
        {
            ShowStatus(error.Message, StatusStyle.Error);
        }

        await Task.CompletedTask;
    }

    private void RemoveKey()
    {
        Services.Providers.RemoveApiKey(_draft.Id);
        _apiKey = string.Empty;
        _hasStoredKey = false;
        ShowStatus("API Key 已移除，这套配置暂时不可用。", StatusStyle.Neutral);
        RebuildStep();
    }

    private void SaveOnly()
    {
        PullInputs();
        try
        {
            PersistDraft();
            ShowStatus("配置已保存。", StatusStyle.Success);
            RebuildStep();
        }
        catch (Exception error)
        {
            ShowStatus(error.Message, StatusStyle.Error);
            RebuildStep();
        }
    }

    /// <summary>
    /// 测试并保存。对应 macOS <c>saveAndTest()</c>（ModelSettingsView.swift:772-807）：
    /// **先持久化再测试**，测试失败配置仍然保留。
    /// </summary>
    private async Task SaveAndTestAsync()
    {
        PullInputs();
        try
        {
            PersistDraft();
        }
        catch (Exception error)
        {
            ShowStatus(error.Message, StatusStyle.Error);
            RebuildStep();
            return;
        }

        _isTesting = true;
        ShowStatus(
            ProviderPresets.SupportsVisionDirectly(_selectedPreset)
                ? "已保存，正在测试文字模型和视觉模型…"
                : "已保存，正在测试 Coding Plan 文字模型…",
            StatusStyle.Neutral);
        RebuildStep();

        try
        {
            var text = await Services.Translation.TestTextModelAsync(_draft);
            if (!text.Ok)
            {
                throw new InvalidOperationException(text.Message);
            }

            _draft.VisionVerifiedAt = DateTimeOffset.UtcNow;

            string success;
            if (ProviderPresets.SupportsVisionDirectly(_selectedPreset))
            {
                var vision = await Services.Recognition.TestConnectionAsync(_draft);
                if (!vision.Ok)
                {
                    throw new InvalidOperationException(vision.Message);
                }

                success = ModelSaveGate.SuccessMessage(true, text.Message, vision.Message);
            }
            else
            {
                success = ModelSaveGate.SuccessMessage(false, text.Message, string.Empty);
            }

            _ = Services.Providers.SaveProfile(_draft);
            Reload();
            Select(_draft.Id);
            ShowStatus(success, StatusStyle.Success);
            _currentStep = ModelSetupStep.Complete;
        }
        catch (Exception error)
        {
            ShowStatus(ModelSaveGate.TestFailureMessage(error.Message), StatusStyle.Error);
            _currentStep = ModelSetupStep.Credentials;
        }

        _isTesting = false;
        RebuildStep();
    }

    /// <summary>
    /// 保存门禁。对应 macOS <c>persistDraft()</c>（ModelSettingsView.swift:809-824）：
    /// 校验通过 **且**（Key 非空 **或** 已有存储），否则抛 <c>请粘贴 API Key。</c>
    /// 判定本身抽到了 <see cref="ModelSaveGate"/>，有单元测试逐条锁定。
    /// </summary>
    private void PersistDraft()
    {
        var failure = ModelSaveGate.PersistFailure(
            _draft,
            _selectedPreset,
            _apiKey,
            _hasStoredKey);
        if (failure is not null)
        {
            throw new InvalidOperationException(failure);
        }

        var trimmedKey = _apiKey.Trim();
        _state = Services.Providers.SaveProfile(_draft, trimmedKey.Length == 0 ? null : trimmedKey);
        _apiKey = string.Empty;
        _hasStoredKey = true;
        _isCreatingProfile = false;
    }

    private void ShowStatus(string message, StatusStyle style)
    {
        _statusMessage = message;
        _statusStyle = style;
    }

    private void OnDone()
    {
        // Mac 版这里是 NSApp.keyWindow?.performClose(nil)；
        // 设置窗口的关闭由宿主窗口负责，这里只回到第 3 步。
        _currentStep = ModelSetupStep.Complete;
        RebuildStep();
    }

    /// <summary>
    /// 一个「⋯」下拉菜单按钮。对应 macOS <c>Menu { ... } label: { Image(systemName: "ellipsis.circle") }</c>
    /// （ModelSettingsView.swift:602-617）。
    /// </summary>
    private sealed class TaMenuButton : Border
    {
        private readonly Popup _popup = new()
        {
            StaysOpen = false,
            AllowsTransparency = true,
            Placement = PlacementMode.Bottom,
        };

        private readonly StackPanel _items = new() { Orientation = Orientation.Vertical };

        /// <summary>构造。</summary>
        public TaMenuButton()
        {
            Background = Brushes.Transparent;
            BorderThickness = new Thickness(0);
            Padding = new Thickness(0);
            HorizontalAlignment = HorizontalAlignment.Left;
            Cursor = System.Windows.Input.Cursors.Hand;

            var icon = new Brand.TaIcon
            {
                Kind = Brand.TaIconKind.Ellipsis,
                Size = 17,
                Brush = Brand.TaBrushes.Ink,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Child = icon;

            _popup.Child = new Border
            {
                Background = Brand.TaBrushes.ElevatedPaper,
                BorderBrush = Brand.TaBrushes.Hairline,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(4),
                MinWidth = 180,
                Child = _items,
            };

            MouseLeftButtonUp += (_, _) =>
            {
                _popup.PlacementTarget = this;
                _popup.IsOpen = !_popup.IsOpen;
            };
        }

        /// <summary>加一个普通菜单项。</summary>
        public void Add(string text, Action action)
        {
            var button = TaButton.Create(text, TaButtonKind.Secondary, () =>
            {
                _popup.IsOpen = false;
                action();
            });
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.Margin = new Thickness(0, 1, 0, 1);
            _items.Children.Add(button);
        }

        /// <summary>加一个危险菜单项。</summary>
        public void AddDestructive(string text, Action action)
        {
            var button = TaButton.Create(text, TaButtonKind.Destructive, () =>
            {
                _popup.IsOpen = false;
                action();
            });
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.Margin = new Thickness(0, 1, 0, 1);
            _items.Children.Add(button);
        }
    }

    /// <summary>
    /// 服务商品牌图标。对应 macOS <c>ProviderBrandIcon</c>（ModelSettingsView.swift:843-874）：
    /// 有 PNG 就用 PNG（Resources/Brand/Providers 里已有 6 个），否则退回 SF Symbol 图形。
    /// </summary>
    private sealed class ProviderBrandIcon : Border
    {
        /// <summary>构造。</summary>
        public ProviderBrandIcon(ProviderPreset preset, double size)
        {
            Width = size + 2;
            Height = size;
            CornerRadius = new CornerRadius(size * 0.22);
            Background = Brushes.Transparent;
            BorderThickness = new Thickness(0);
            ClipToBounds = true;

            var image = Brand.TaBrandAssets.ProviderImage(ProviderPresets.BrandAssetName(preset));
            Child = image is not null
                ? new System.Windows.Controls.Image
                {
                    Source = image,
                    Stretch = Stretch.Uniform,
                    Width = size,
                    Height = size,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                }
                : new Brand.TaIcon
                {
                    Kind = FallbackIcon(preset),
                    Size = size,
                    Brush = Brand.TaBrushes.Cinnabar,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
        }

        // ModelSettingsView.swift:937-948 —— 只有 azure / custom 会走到这里
        private static Brand.TaIconKind FallbackIcon(ProviderPreset preset) => preset switch
        {
            ProviderPreset.Azure => Brand.TaIconKind.Cloud,
            ProviderPreset.Custom => Brand.TaIconKind.Sliders,
            ProviderPreset.OpenAi => Brand.TaIconKind.Sparkles,
            ProviderPreset.OpenRouter => Brand.TaIconKind.Network,
            _ => Brand.TaIconKind.Bubble,
        };
    }
}
