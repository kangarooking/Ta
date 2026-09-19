using System.Windows.Controls;
using Ta.Settings.Core;
using Ta.Settings.Services;

namespace Ta.Settings.UI.Tabs;

/// <summary>
/// 快捷键页。对应 macOS <c>HotKeySettingsView</c>（HotKeySettingsView.swift:1-86）
/// 与 <c>HotKeyRecorderView</c> / <c>HotKeyRecorderButton</c>（HotKeyRecorderView.swift:47-165）。
///
/// 录制行为完全照抄 Mac 版：点击进入录制 → 显示「请按新快捷键…」；
/// Escape / 失焦取消并恢复原值；识别不了的按键提示「无法识别这个按键」。
/// 真正的 <c>RegisterHotKey</c> 注册由快捷键那一路的实现负责（<see cref="IHotKeyService"/>）。
/// </summary>
public sealed class HotKeysPage : TaTabPage
{
    private readonly StackPanel _rows = new() { Orientation = Orientation.Vertical };
    private readonly Border _status = new();
    private readonly Dictionary<GlobalHotKeyAction, TaHotKeyRecorder> _recorders = new();

    /// <summary>构造。</summary>
    public HotKeysPage(TaServices services)
        : base(services)
    {
        Services.HotKeys.Changed += (_, _) => Dispatcher.BeginInvoke(Reload);
        Services.HotKeys.RegistrationFailed += (_, message) =>
            Dispatcher.BeginInvoke(() => ShowStatus(message ?? "快捷键注册失败，已恢复上一组配置。", isError: true));
    }

    /// <inheritdoc />
    protected override FrameworkElement Build()
    {
        RebuildRows();

        var reset = TaButton.Create("恢复默认快捷键", TaButtonKind.Secondary, OnResetAll);
        var hint = TaPrimitives.Text(
            "点击当前快捷键后，直接按下新的单键或组合键",
            Brand.TaTypography.Caption,
            Brand.TaBrushes.MutedInk);

        var footer = new TaCard(
            TaLayout.V(
                8,
                TaLayout.H(9, reset, hint),
                _status),
            new System.Windows.Thickness(14),
            12,
            Brand.TaBrushes.ElevatedPaper84);

        return Page(
            new TaSectionHeader("全局截图快捷键", "6 个动作都可以重绑定"),
            new TaCard(_rows, new System.Windows.Thickness(14), 12, Brand.TaBrushes.ElevatedPaper84),
            footer,
            TaPrimitives.Text(
                "支持单键和组合键。单独使用字母或数字会占用它在所有应用中的正常输入，请优先选择不常用按键。Escape 取消录制；重复或被系统占用的快捷键不会覆盖当前配置。框选截图时可按右键或 Escape 退出。",
                Brand.TaTypography.Caption,
                Brand.TaBrushes.MutedInk,
                wrap: true));
    }

    /// <inheritdoc />
    public override void Refresh() => Reload();

    private void RebuildRows()
    {
        _rows.Children.Clear();
        _recorders.Clear();

        foreach (var action in GlobalHotKeyActions.All)
        {
            var shortcut = Services.HotKeys.ShortcutFor(action);
            var recorder = new TaHotKeyRecorder(shortcut);
            recorder.ShortcutChanged += (_, newShortcut) => OnShortcutChanged(newShortcut, action);
            _recorders[action] = recorder;

            var left = TaLayout.V(
                3,
                TaPrimitives.Text(
                    GlobalHotKeyActions.DisplayName(action),
                    Brand.TaTypography.Callout,
                    Brand.TaBrushes.Ink,
                    FontWeights.Medium),
                TaPrimitives.Text(
                    GlobalHotKeyActions.Detail(action),
                    Brand.TaTypography.Caption,
                    Brand.TaBrushes.MutedInk,
                    wrap: true));

            var row = new Grid { Margin = new System.Windows.Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(left, 0);
            row.Children.Add(left);
            Grid.SetColumn(recorder, 1);
            row.Children.Add(recorder);
            _rows.Children.Add(row);
        }
    }

    private void Reload()
    {
        foreach (var (action, recorder) in _recorders)
        {
            recorder.Shortcut = Services.HotKeys.ShortcutFor(action);
        }
    }

    private void OnShortcutChanged(HotKeyShortcut shortcut, GlobalHotKeyAction action)
    {
        try
        {
            Services.HotKeys.Save(shortcut, action);
            ShowStatus(
                $"“{GlobalHotKeyActions.DisplayName(action)}”已改为 {shortcut.DisplayText}。",
                isError: false);
        }
        catch (Exception error)
        {
            Reload();
            ShowStatus(error.Message, isError: true);
        }
    }

    private void OnResetAll()
    {
        Services.HotKeys.ResetAll();
        Reload();
        ShowStatus("已恢复默认快捷键。", isError: false);
    }

    private void ShowStatus(string message, bool isError)
    {
        _status.Child = new TaStatusLine(
            message,
            isError ? Brand.TaIconKind.WarningTriangle : Brand.TaIconKind.CheckCircle,
            isError ? Brand.TaBrushes.Danger : Brand.TaBrushes.Success,
            wrap: true);
    }
}
