using System.Windows.Controls;
using Ta.Settings.Core;
using Ta.Settings.Services;

namespace Ta.Settings.UI.Tabs;

/// <summary>
/// 权限页。对应 macOS <c>PermissionsSettingsView</c>（SettingsView.swift:121-181）。
///
/// 两个区块：屏幕录制（截图必需）、辅助功能（只有主动开启自动滚动时才用）。
/// Windows 上的差异见各控件的注释。
/// </summary>
public sealed class PermissionsPage : TaTabPage
{
    private readonly TextBlock _screenStatus;
    private readonly TextBlock _screenHint;
    private Button _screenRequest = null!;
    private TextBlock _accessibilityStatus;
    private TextBlock _accessibilityHint;
    private Button _accessibilityRequest = null!;

    /// <summary>构造。</summary>
    public PermissionsPage(TaServices services)
        : base(services)
    {
        _screenStatus = TaPrimitives.Text(
            string.Empty,
            Brand.TaTypography.Callout,
            Brand.TaBrushes.Ink,
            FontWeights.Medium);
        _screenHint = TaPrimitives.Text(
            "只读取你主动框选的区域；应用不会在后台持续录屏。",
            Brand.TaTypography.Caption,
            Brand.TaBrushes.MutedInk,
            wrap: true);

        _accessibilityStatus = TaPrimitives.Text(
            string.Empty,
            Brand.TaTypography.Callout,
            Brand.TaBrushes.Ink,
            FontWeights.Medium);
        _accessibilityHint = TaPrimitives.Text(
            "手动长截图不需要此权限；只有你主动开启自动滚动时才使用。",
            Brand.TaTypography.Caption,
            Brand.TaBrushes.MutedInk,
            wrap: true);
    }

    /// <inheritdoc />
    protected override FrameworkElement Build()
    {
        _screenRequest = TaButton.Create(string.Empty, TaButtonKind.Secondary, OnRequestScreenCapture);
        var screenOpen = TaButton.Create("打开系统设置", TaButtonKind.Secondary, () => Services.ScreenCapturePermission.OpenSystemSettings());

        _accessibilityRequest = TaButton.Create(string.Empty, TaButtonKind.Secondary, OnRequestAccessibility);
        var accessibilityOpen = TaButton.Create("打开系统设置", TaButtonKind.Secondary, () => Services.AccessibilityPermission.OpenSystemSettings());

        var screenCard = new TaCard(
            TaLayout.V(
                8,
                TaLayout.H(
                    8,
                    new Brand.TaIcon { Kind = Brand.TaIconKind.LockShield, Size = 15, VerticalAlignment = VerticalAlignment.Center },
                    _screenStatus),
                _screenHint,
                TaLayout.H(9, _screenRequest, screenOpen)),
            new System.Windows.Thickness(14),
            12,
            Brand.TaBrushes.ElevatedPaper84);

        var accessibilityCard = new TaCard(
            TaLayout.V(
                8,
                TaLayout.H(
                    8,
                    new Brand.TaIcon { Kind = Brand.TaIconKind.HandRaised, Size = 15, VerticalAlignment = VerticalAlignment.Center },
                    _accessibilityStatus),
                _accessibilityHint,
                TaLayout.H(9, _accessibilityRequest, accessibilityOpen)),
            new System.Windows.Thickness(14),
            12,
            Brand.TaBrushes.ElevatedPaper84);

        return Page(
            new TaSectionHeader("屏幕录制", "拓需要它才能读取你框选的屏幕区域"),
            screenCard,
            new TaSectionHeader("辅助功能", "仅自动滚动长截图时需要"),
            accessibilityCard,
            new TaCard(
                TaLayout.H(
                    8,
                    new Brand.TaIcon
                    {
                        Kind = Brand.TaIconKind.ShieldCheck,
                        Size = 15,
                        Brush = Brand.TaBrushes.Success,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    TaPrimitives.Text(
                        "API Key 等密钥保存在本机用户密钥库（DPAPI 加密），只有当前 Windows 用户能解密。",
                        Brand.TaTypography.Caption,
                        Brand.TaBrushes.MutedInk,
                        wrap: true)),
                new System.Windows.Thickness(14),
                12,
                Brand.TaBrushes.ElevatedPaper84));
    }

    /// <inheritdoc />
    public override void Refresh()
    {
        // SettingsView.swift:176-179 的 onAppear 每次都重新读一次权限状态
        var screenGranted = Services.ScreenCapturePermission.IsGranted;
        _screenStatus.Text = screenGranted ? "已允许" : "尚未允许";
        ((TextBlock)_screenStatus).Foreground = screenGranted ? Brand.TaBrushes.Success : Brand.TaBrushes.Warning;
        _screenRequest.Content = TaPrimitives.Text(
            screenGranted ? "重新检测" : "请求权限",
            Brand.TaTypography.Callout,
            Brand.TaBrushes.Ink,
            FontWeights.Medium);

        var accessibilityGranted = Services.AccessibilityPermission.IsGranted;
        _accessibilityStatus.Text = accessibilityGranted ? "已允许" : "仅自动滚动时需要";
        _accessibilityStatus.Foreground = accessibilityGranted ? Brand.TaBrushes.Success : Brand.TaBrushes.MutedInk;
        _accessibilityRequest.Content = TaPrimitives.Text(
            accessibilityGranted ? "重新检测" : "请求权限",
            Brand.TaTypography.Callout,
            Brand.TaBrushes.Ink,
            FontWeights.Medium);
    }

    private void OnRequestScreenCapture()
    {
        if (!Services.ScreenCapturePermission.IsGranted)
        {
            _ = Services.ScreenCapturePermission.Request();
        }

        Refresh();
    }

    private void OnRequestAccessibility()
    {
        if (!Services.AccessibilityPermission.IsGranted)
        {
            _ = Services.AccessibilityPermission.Request();
        }

        Refresh();
    }
}
