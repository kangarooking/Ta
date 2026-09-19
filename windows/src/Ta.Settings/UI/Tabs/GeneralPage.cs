using System.Windows.Controls;
using Ta.Settings.Core;
using Ta.Settings.Services;

namespace Ta.Settings.UI.Tabs;

/// <summary>
/// 常规页。对应 macOS <c>GeneralSettingsView</c>（SettingsView.swift:183-226）。
/// 三个区块：截图完成后 / 结果胶囊 / 历史与隐私。
/// 读写全部走 <see cref="SettingsKeys"/> 里的键（参考文档 §10.6）。
/// </summary>
public sealed class GeneralPage : TaTabPage
{
    private TaPicker _postCapturePicker = null!;
    private Slider _durationSlider = null!;
    private TextBlock _durationValue = null!;
    private TaToggleRow _saveHistory = null!;
    private TextBlock _historyHint = null!;

    /// <summary>构造。</summary>
    public GeneralPage(TaServices services)
        : base(services)
    {
    }

    /// <inheritdoc />
    protected override FrameworkElement Build()
    {
        var action = PostCaptureActions.FromRaw(Services.Settings.GetString(SettingsKeys.PostCaptureAction));
        _postCapturePicker = new TaPicker(
            PostCaptureActionsDisplayNames(),
            PostCaptureActions.DisplayName(action),
            320);
        _postCapturePicker.SelectionChanged += (_, _) =>
        {
            var selected = PostCaptureActions.All
                .FirstOrDefault(a => string.Equals(PostCaptureActions.DisplayName(a), _postCapturePicker.SelectedItem, StringComparison.Ordinal));
            Services.Settings.SetString(SettingsKeys.PostCaptureAction, PostCaptureActions.Raw(selected));
        };

        _durationValue = TaPrimitives.Text(
            FormatDuration(Services.Settings.GetDouble(SettingsKeys.ResultBarDuration, SettingsDefaults.ResultBarDuration)),
            Brand.TaTypography.Caption,
            Brand.TaBrushes.Ink,
            mono: true);
        _durationSlider = new Slider
        {
            Minimum = SettingsDefaults.ResultBarDurationMin,
            Maximum = SettingsDefaults.ResultBarDurationMax,
            TickFrequency = SettingsDefaults.ResultBarDurationStep,
            IsSnapToTickEnabled = true,
            Value = Services.Settings.GetDouble(SettingsKeys.ResultBarDuration, SettingsDefaults.ResultBarDuration),
            Width = 220,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _durationSlider.ValueChanged += (_, _) =>
        {
            Services.Settings.SetDouble(SettingsKeys.ResultBarDuration, _durationSlider.Value);
            _durationValue.Text = FormatDuration(_durationSlider.Value);
        };

        _saveHistory = new TaToggleRow(
            "在本机保存截图历史",
            detail: null,
            isOn: Services.Settings.GetBool(SettingsKeys.SaveHistory, false));
        _saveHistory.Toggled += (_, _) =>
            Services.Settings.SetBool(SettingsKeys.SaveHistory, _saveHistory.IsOn);

        _historyHint = TaPrimitives.Text(
            Services.Settings.GetBool(SettingsKeys.SaveHistory, false)
                ? "截图只保存在本机；云端调用仍会单独提示。"
                : "当前不会持久化截图文件。",
            Brand.TaTypography.Caption,
            Brand.TaBrushes.MutedInk,
            wrap: true);

        return Page(
            new TaCard(
                TaLayout.V(
                    10,
                    new TaFieldRow("默认动作", _postCapturePicker),
                    TaPrimitives.Text(
                        "这个设置只影响通用截图；极速识别、截图翻译、复制图片和钉图快捷键会直接执行。",
                        Brand.TaTypography.Caption,
                        Brand.TaBrushes.MutedInk,
                        wrap: true)),
                new System.Windows.Thickness(14),
                12,
                Brand.TaBrushes.ElevatedPaper84),
            new TaCard(
                TaLayout.H(
                    12,
                    TaPrimitives.Text("自动隐藏", Brand.TaTypography.Callout, Brand.TaBrushes.Ink),
                    _durationSlider,
                    _durationValue),
                new System.Windows.Thickness(14),
                12,
                Brand.TaBrushes.ElevatedPaper84),
            new TaCard(
                TaLayout.V(
                    8,
                    _saveHistory,
                    _historyHint),
                new System.Windows.Thickness(14),
                12,
                Brand.TaBrushes.ElevatedPaper84));
    }

    /// <inheritdoc />
    public override void Refresh()
    {
        _historyHint.Text = _saveHistory.IsOn
            ? "截图只保存在本机；云端调用仍会单独提示。"
            : "当前不会持久化截图文件。";
    }

    private static string FormatDuration(double seconds)
        => seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " 秒";

    private static string[] PostCaptureActionsDisplayNames()
        => PostCaptureActions.All.Select(PostCaptureActions.DisplayName).ToArray();
}
