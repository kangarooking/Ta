using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ta.Settings.Services;

namespace Ta.Settings.UI.Tabs;

/// <summary>
/// Agent 页。对应 macOS <c>AgentSettingsView</c>（AgentSettingsView.swift:45-378）。
///
/// 键名与默认值全部对齐参考文档 §10.6：
/// <c>agentAccessEnabled</c>=true、<c>agentAutomaticCaptureAllowed</c>=true、
/// <c>agentCloudPolicy</c>=<c>auto</c>、<c>agentPrivacyDenylist</c>=空、
/// <c>agentAllowCaptureTa</c>=true。
/// </summary>
public sealed class AgentPage : TaTabPage
{
    private readonly StackPanel _auditHost = new() { Orientation = Orientation.Vertical };
    private readonly Border _operationHost = new() { Background = Brushes.Transparent };
    private readonly Border _installationHost = new() { Background = Brushes.Transparent };
    private readonly TaToggleRow _accessEnabled;
    private readonly TextBlock _accessHint;
    private readonly TaToggleRow _automaticCapture;
    private readonly TaToggleRow _allowCaptureTa;
    private TaPicker _cloudPolicyPicker = null!;
    private readonly TextBlock _cloudPolicyHint;
    private readonly TextBox _denylist;

    /// <summary>构造。</summary>
    public AgentPage(TaServices services)
        : base(services)
    {
        _accessEnabled = new TaToggleRow(
            "允许本机 Agent 调用拓",
            detail: null,
            isOn: Services.Settings.GetBool(SettingsKeys.AgentAccessEnabled, SettingsDefaults.AgentAccessEnabled));
        _accessHint = TaPrimitives.Text(string.Empty, Brand.TaTypography.Caption, Brand.TaBrushes.MutedInk, wrap: true);

        _automaticCapture = new TaToggleRow(
            "允许无感自动截图",
            detail: "普通截图不会弹出拓、抢占前台 App、移动鼠标或发送键盘事件。",
            isOn: Services.Settings.GetBool(
                SettingsKeys.AgentAutomaticCaptureAllowed,
                SettingsDefaults.AgentAutomaticCaptureAllowed));

        _allowCaptureTa = new TaToggleRow(
            "允许 Agent 截取拓自身",
            detail: null,
            isOn: Services.Settings.GetBool(SettingsKeys.AgentAllowCaptureTa, SettingsDefaults.AgentAllowCaptureTa));

        _cloudPolicyHint = TaPrimitives.Text(string.Empty, Brand.TaTypography.Caption, Brand.TaBrushes.MutedInk, wrap: true);

        _denylist = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brand.TaBrushes.ElevatedPaper78,
            BorderBrush = Brand.TaBrushes.Hairline,
            BorderThickness = new Thickness(1),
            Foreground = Brand.TaBrushes.Ink,
            FontFamily = new FontFamily(Brand.TaTypography.MonoFamily),
            FontSize = Brand.TaTypography.Caption,
            Padding = new Thickness(8),
            MinHeight = 68,
            Text = Services.Settings.GetString(SettingsKeys.AgentPrivacyDenylist) ?? string.Empty,
        };
        _denylist.TextChanged += (_, _) =>
            Services.Settings.SetString(SettingsKeys.AgentPrivacyDenylist, _denylist.Text);

        WireToggles();
    }

    /// <inheritdoc />
    protected override FrameworkElement Build()
    {
        var policy = AgentCloudPolicies.FromRaw(Services.Settings.GetString(SettingsKeys.AgentCloudPolicy));
        _cloudPolicyPicker = new TaPicker(
            AgentCloudPolicies.All.Select(AgentCloudPolicies.DisplayName).ToArray(),
            AgentCloudPolicies.DisplayName(policy),
            280);
        _cloudPolicyPicker.SelectionChanged += (_, _) =>
        {
            var selected = AgentCloudPolicies.All.FirstOrDefault(
                p => string.Equals(AgentCloudPolicies.DisplayName(p), _cloudPolicyPicker.SelectedItem, StringComparison.Ordinal));
            Services.Settings.SetString(SettingsKeys.AgentCloudPolicy, AgentCloudPolicies.Raw(selected));
            UpdateDependentText();
        };

        var detect = TaButton.Create("重新检测", TaButtonKind.Secondary, RefreshInstallation);

        return Page(
            _installationHost,
            new TaCard(
                TaLayout.V(
                    10,
                    new TaSectionHeader("Agent 与自动化", "CLI、Skill 与 DeepSeek Harness 都走仅限当前用户的本机桥"),
                    _accessEnabled,
                    _accessHint,
                    _automaticCapture),
                new Thickness(14),
                12,
                Brand.TaBrushes.ElevatedPaper84),
            new TaCard(
                TaLayout.V(
                    10,
                    new TaSectionHeader("云端与模型"),
                    new TaFieldRow("默认云端策略", _cloudPolicyPicker),
                    _cloudPolicyHint,
                    new TaStatusLine(
                        "Agent 只能请求拓执行模型任务，无法读取本机密钥库中的 API Key。",
                        Brand.TaIconKind.Key,
                        Brand.TaBrushes.MutedInk,
                        wrap: true)),
                new Thickness(14),
                12,
                Brand.TaBrushes.ElevatedPaper84),
            new TaCard(
                TaLayout.V(
                    10,
                    new TaSectionHeader("隐私 App 黑名单"),
                    _denylist,
                    TaPrimitives.Text(
                        "每行填写一个应用标识（Bundle ID / 可执行文件名），例如 com.1password.1password。截显示器时，只要画面包含黑名单应用就会拒绝截图。",
                        Brand.TaTypography.Caption,
                        Brand.TaBrushes.MutedInk,
                        wrap: true),
                    _allowCaptureTa),
                new Thickness(14),
                12,
                Brand.TaBrushes.ElevatedPaper84),
            new TaCard(
                TaLayout.V(
                    10,
                    new TaSectionHeader("缓存与调用记录"),
                    TaLayout.H(
                        9,
                        TaLayout.V(
                            3,
                            TaPrimitives.Text("临时截图缓存", Brand.TaTypography.Callout, Brand.TaBrushes.Ink),
                            TaPrimitives.Text(
                                "图片保存在本机缓存，默认 24 小时后自动清理。",
                                Brand.TaTypography.Caption,
                                Brand.TaBrushes.MutedInk)),
                        TaButton.Create("立即清理", TaButtonKind.Secondary, () => _ = ClearArtifactsAsync())),
                    TaLayout.H(
                        9,
                        TaPrimitives.Text("最近调用", Brand.TaTypography.Callout, Brand.TaBrushes.Ink),
                        TaButton.Create("刷新", TaButtonKind.Link, () => _ = RefreshAuditAsync()),
                        TaButton.Create("清除记录", TaButtonKind.Link, () => _ = ClearAuditAsync())),
                    _auditHost,
                    _operationHost),
                new Thickness(14),
                12,
                Brand.TaBrushes.ElevatedPaper84),
            new Border
            {
                Padding = new Thickness(0, 6, 0, 0),
                Child = detect,
            });
    }

    /// <inheritdoc />
    public override void Refresh()
    {
        RefreshInstallation();
        _ = RefreshAuditAsync();
        UpdateDependentText();
    }

    private void WireToggles()
    {
        _accessEnabled.Toggled += (_, _) =>
        {
            Services.Settings.SetBool(SettingsKeys.AgentAccessEnabled, _accessEnabled.IsOn);
            UpdateDependentText();
        };
        _automaticCapture.Toggled += (_, _) =>
            Services.Settings.SetBool(SettingsKeys.AgentAutomaticCaptureAllowed, _automaticCapture.IsOn);
        _allowCaptureTa.Toggled += (_, _) =>
            Services.Settings.SetBool(SettingsKeys.AgentAllowCaptureTa, _allowCaptureTa.IsOn);
    }

    private void UpdateDependentText()
    {
        var enabled = _accessEnabled.IsOn;
        _automaticCapture.IsEnabled = enabled;
        _allowCaptureTa.IsEnabled = enabled;
        _cloudPolicyPicker.IsEnabled = enabled;
        _cloudPolicyPicker.Opacity = enabled ? 1 : 0.45;
        _denylist.IsEnabled = enabled;
        _denylist.Opacity = enabled ? 1 : 0.45;

        _accessHint.Text = enabled
            ? "CLI、Agent Skill 和 DeepSeek Harness 可以通过仅限当前用户的本机 Bridge 调用拓。"
            : "除状态与权限检查外，所有 Agent 能力都会被拒绝。";

        var policy = AgentCloudPolicies.FromRaw(Services.Settings.GetString(SettingsKeys.AgentCloudPolicy));
        _cloudPolicyHint.Text = AgentCloudPolicies.Description(policy);
    }

    // ---- 安装状态（AgentSettingsView.swift:17-43, 172-228）----

    private void RefreshInstallation()
    {
        var status = Services.AgentBridge.DetectInstallation();
        var cliBadge = TaPrimitives.Badge(
            "ta CLI",
            status.CliInstalled ? Brand.TaBrushes.Success : Brand.TaBrushes.MutedInk,
            status.CliInstalled ? Brand.TaBrushes.Success10 : Brand.TaBrushes.Ink60);
        var skillBadge = TaPrimitives.Badge(
            status.SkillInstalled ? $"Ta Skill · {status.InstalledSkillLocations.Count} 处" : "Ta Skill",
            status.SkillInstalled ? Brand.TaBrushes.Success : Brand.TaBrushes.MutedInk,
            status.SkillInstalled ? Brand.TaBrushes.Success10 : Brand.TaBrushes.Ink60);

        _installationHost.Child = new TaCard(
            TaLayout.V(
                9,
                TaLayout.H(8, TaPrimitives.Text("安装 CLI 与 Skill", Brand.TaTypography.Headline, Brand.TaBrushes.Ink, FontWeights.Bold), cliBadge, skillBadge),
                TaPrimitives.Text(
                    "一条命令同时安装或更新 CLI 与 Skill；自动校验下载文件，并适配 Codex 和通用 Agent Skills 目录。",
                    Brand.TaTypography.Caption,
                    Brand.TaBrushes.MutedInk,
                    wrap: true),
                TaLayout.H(
                    10,
                    BuildCopyBlock("方式一 · 终端", "复制命令并执行", Brand.TaIconKind.Terminal, "ta.exe status --json"),
                    BuildCopyBlock("方式二 · 交给 Agent", "复制完整安装提示词", Brand.TaIconKind.Sparkles, "请帮我在这台 Windows 安装拓（Ta）的 CLI 和 Agent Skill。")),
                new TaStatusLine(
                    "安装后重启 Agent，再运行 ta.exe status --json 验证连接。",
                    Brand.TaIconKind.ShieldCheck,
                    Brand.TaBrushes.MutedInk,
                    wrap: true)),
            new Thickness(14),
            12,
            Brand.TaBrushes.Paper55);
    }

    private FrameworkElement BuildCopyBlock(string title, string subtitle, Brand.TaIconKind icon, string content)
    {
        var copy = TaButton.Create("复制", TaButtonKind.Secondary, () =>
        {
            if (Services.Clipboard.SetText(content))
            {
                _operationHost.Child = new TaStatusLine("已复制到剪贴板。", Brand.TaIconKind.CheckCircle, Brand.TaBrushes.Success);
            }
            else
            {
                _operationHost.Child = new TaStatusLine("复制失败，请手动选择并复制。", Brand.TaIconKind.WarningTriangle, Brand.TaBrushes.Warning);
            }
        });

        return new TaCard(
            TaLayout.V(
                8,
                TaLayout.H(
                    8,
                    new Brand.TaIcon { Kind = icon, Size = 15, Brush = Brand.TaBrushes.Cinnabar, VerticalAlignment = VerticalAlignment.Center },
                    TaLayout.V(
                        1,
                        TaPrimitives.Text(title, Brand.TaTypography.Subheadline, Brand.TaBrushes.Ink, FontWeights.SemiBold),
                        TaPrimitives.Text(subtitle, Brand.TaTypography.Caption2, Brand.TaBrushes.MutedInk)),
                    copy),
                new Border
                {
                    Background = Brand.TaBrushes.Ink45,
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10),
                    Child = TaPrimitives.Text(content, Brand.TaTypography.Caption2, Brand.TaBrushes.Ink82, mono: true, wrap: true),
                }),
            new Thickness(11),
            11,
            Brand.TaBrushes.ElevatedPaper72);
    }

    // ---- 审计与缓存（AgentSettingsView.swift:331-377）----

    private async Task RefreshAuditAsync()
    {
        try
        {
            var entries = await Services.AgentBridge.RecentAsync(20);
            _auditHost.Children.Clear();
            if (entries.Count == 0)
            {
                _auditHost.Children.Add(TaPrimitives.Text(
                    "暂无调用记录。审计不会保存 API Key、OCR/翻译正文或图片数据。",
                    Brand.TaTypography.Caption,
                    Brand.TaBrushes.MutedInk,
                    wrap: true));
            }
            else
            {
                foreach (var entry in entries.Take(8))
                {
                    _auditHost.Children.Add(BuildAuditRow(entry));
                }

                _auditHost.Children.Add(TaPrimitives.Text(
                    "仅保留最近 100 次调用；不记录请求参数和识别内容。",
                    Brand.TaTypography.Caption,
                    Brand.TaBrushes.MutedInk,
                    wrap: true));
            }

            _operationHost.Child = null;
        }
        catch (Exception error)
        {
            _operationHost.Child = new TaStatusLine(
                $"无法读取调用记录：{error.Message}",
                Brand.TaIconKind.WarningTriangle,
                Brand.TaBrushes.Danger,
                wrap: true);
        }
    }

    private FrameworkElement BuildAuditRow(AgentAuditEntry entry)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });

        var icon = new Brand.TaIcon
        {
            Kind = entry.Succeeded ? Brand.TaIconKind.CheckCircle : Brand.TaIconKind.XCircle,
            Size = 13,
            Brush = entry.Succeeded ? Brand.TaBrushes.Success : Brand.TaBrushes.Danger,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);

        var middle = TaLayout.V(
            2,
            TaPrimitives.Text(entry.Method, Brand.TaTypography.Caption, Brand.TaBrushes.Ink, FontWeights.SemiBold, mono: true),
            TaPrimitives.Text(
                $"{entry.ClientName} · {entry.OccurredAt.ToLocalTime():HH:mm}",
                Brand.TaTypography.Caption2,
                Brand.TaBrushes.MutedInk));
        middle.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(middle, 1);
        row.Children.Add(middle);

        var cloud = entry.CloudUploaded
            ? TaLayout.H(
                5,
                new Brand.TaIcon { Kind = Brand.TaIconKind.CloudUpload, Size = 12, Brush = Brand.TaBrushes.Warning, VerticalAlignment = VerticalAlignment.Center },
                TaPrimitives.Text("云端", Brand.TaTypography.Caption2, Brand.TaBrushes.Warning))
            : (FrameworkElement)TaPrimitives.Text("本地", Brand.TaTypography.Caption2, Brand.TaBrushes.MutedInk);
        Grid.SetColumn(cloud, 2);
        row.Children.Add(cloud);

        var duration = TaPrimitives.Text(
            $"{entry.DurationMs} ms",
            Brand.TaTypography.Caption2,
            Brand.TaBrushes.MutedInk,
            mono: true);
        duration.HorizontalAlignment = HorizontalAlignment.Right;
        duration.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(duration, 3);
        row.Children.Add(duration);

        return row;
    }

    private async Task ClearAuditAsync()
    {
        try
        {
            await Services.AgentBridge.ClearAuditAsync();
            _auditHost.Children.Clear();
            _operationHost.Child = new TaStatusLine("调用记录已清除。", Brand.TaIconKind.CheckCircle, Brand.TaBrushes.Success);
        }
        catch (Exception error)
        {
            _operationHost.Child = new TaStatusLine(
                $"清除失败：{error.Message}",
                Brand.TaIconKind.WarningTriangle,
                Brand.TaBrushes.Danger,
                wrap: true);
        }
    }

    private async Task ClearArtifactsAsync()
    {
        try
        {
            var count = await Services.AgentBridge.ClearArtifactsAsync();
            _operationHost.Child = new TaStatusLine(
                count == 0 ? "当前没有临时截图缓存。" : $"已清理 {count} 组临时截图。",
                Brand.TaIconKind.CheckCircle,
                Brand.TaBrushes.Success);
        }
        catch (Exception error)
        {
            _operationHost.Child = new TaStatusLine(
                $"缓存清理失败：{error.Message}",
                Brand.TaIconKind.WarningTriangle,
                Brand.TaBrushes.Danger,
                wrap: true);
        }
    }
}
