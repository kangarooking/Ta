using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ta.Settings.Services;

namespace Ta.Settings.UI;

/// <summary>
/// 欢迎窗口度量。对应 macOS <c>WelcomeViewMetrics</c>（WelcomeView.swift:5-16）。
/// </summary>
public static class WelcomeViewMetrics
{
    /// <summary>默认窗口尺寸 1000×700（对齐 Mac 版 WelcomeView 的画布比例）。</summary>
    public static readonly Size DefaultWindowSize = new(1000, 724);

    /// <summary>最小窗口尺寸 900×660。</summary>
    public static readonly Size MinimumWindowSize = new(900, 660);

    /// <summary>内容内边距 32。</summary>
    public const double ContentPadding = 32;

    /// <summary>区块间距 24。</summary>
    public const double SectionSpacing = 24;

    /// <summary>网格间距 14。</summary>
    public const double GridSpacing = 14;

    /// <summary>快捷操作列数 3。</summary>
    public const int QuickActionColumnCount = 3;

    /// <summary>快捷操作数量 6。</summary>
    public const int QuickActionCount = 6;

    /// <summary>快捷操作最小行高 104（Mac 版卡片有充足留白）。</summary>
    public const double QuickActionMinimumHeight = 104;

    /// <summary>主卡片圆角 20。</summary>
    public const double PrimaryCornerRadius = 20;

    /// <summary>快捷操作圆角 15。</summary>
    public const double QuickActionCornerRadius = 15;
}

/// <summary>
/// 欢迎窗口。**1000×700**（最小 **900×660**），padding 32，3 列 × 6 个快捷操作，
/// 最小行高 74。主卡片「开始拓取」。权限指示「本地就绪」/「需要权限」。
///
/// 对应 macOS <c>WelcomeView</c>（WelcomeView.swift:60-278）。
/// ⚠️ **没有 AI 模型配置步骤**，只链接到设置（参考文档 §9.10 的 ⚠️ 注释）。
/// </summary>
public sealed class WelcomeWindow : Window
{
    private readonly TaServices _services;
    private readonly Border _permissionPill;
    private readonly TextBlock _permissionLabel;
    private readonly Border _statusBar;
    private readonly TextBlock _statusTitle;
    private readonly TextBlock _statusHint;
    private readonly Border _statusActions = new();

    /// <summary>
    /// 版本号文本。注意必须是<strong>每次新建</strong>：granted / denied 两套操作面板
    /// 都以它结尾，若共享同一实例，第二个面板 Add 时会抛
    /// 「指定的元素已经是另一个元素的逻辑子元素」（WPF 单逻辑父级约束）。
    /// </summary>
    private TextBlock VersionLabel() => TaPrimitives.Text(
        $"v{_services.Version}",
        Brand.TaTypography.Caption2,
        Brand.TaBrushes.MutedInk);

    /// <summary>
    /// 把系统标题栏刷成纸色（#FAF6EE），消除 Windows 截图里「灰条 + 系统白底」的割裂感。
    /// DWMWA_CAPTION_COLOR = 35（Windows 11 22000+；旧系统调用失败则维持系统默认，不影响功能）。
    /// </summary>
    private void ApplyPaperCaption()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            // COLORREF 是 0x00BBGGRR，Paper = #FAF6EE。
            var colorref = 0x00EEF6FA;
            _ = DwmSetWindowAttribute(hwnd, 35, ref colorref, sizeof(int));
        }
        catch (Exception)
        {
            // 老 Windows 没有 35 号属性：保持系统标题栏原色即可。
        }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>构造。</summary>
    /// <param name="services">服务容器。</param>
    /// <param name="onOpenSettings">点「设置」时的回调（由宿主决定是开设置窗口还是聚焦已有窗口）。</param>
    public WelcomeWindow(TaServices services, Action? onOpenSettings = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));

        Title = Brand.TaBrand.Name;
        Width = WelcomeViewMetrics.DefaultWindowSize.Width;
        Height = WelcomeViewMetrics.DefaultWindowSize.Height;
        MinWidth = WelcomeViewMetrics.MinimumWindowSize.Width;
        MinHeight = WelcomeViewMetrics.MinimumWindowSize.Height;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brand.TaBrushes.Paper;
        UseLayoutRounding = true;
        SourceInitialized += (_, _) => ApplyPaperCaption();

        _permissionLabel = TaPrimitives.Text(string.Empty, Brand.TaTypography.Caption, Brand.TaBrushes.MutedInk, FontWeights.Medium);
        _permissionPill = TaPrimitives.MakeCapsule(new Border
        {
            Background = Brand.TaBrushes.ElevatedPaper,
            BorderBrush = Brand.TaBrushes.Hairline,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 6, 10, 6),
            Child = TaLayout.H(6, TaPrimitives.Dot(7, Brand.TaBrushes.Warning), _permissionLabel),
        });

        _statusTitle = TaPrimitives.Text(string.Empty, Brand.TaTypography.Callout, Brand.TaBrushes.Ink, FontWeights.Medium);
        _statusHint = TaPrimitives.Text(string.Empty, Brand.TaTypography.Caption, Brand.TaBrushes.MutedInk, wrap: true);
        _statusActions = new Border { Background = Brushes.Transparent };

        _statusBar = new Border
        {
            Padding = new Thickness(14, 11, 14, 11),
            CornerRadius = new CornerRadius(14),
            Background = Brand.TaBrushes.ElevatedPaper,
            BorderBrush = Brand.TaBrushes.Hairline,
            BorderThickness = new Thickness(1),
        };

        Content = BuildRoot(onOpenSettings);
        RefreshPermission();
        Activated += (_, _) => RefreshPermission();
    }

    /// <summary>刷新权限指示（对应 <c>refreshPermission()</c>，WelcomeView.swift:271-273）。</summary>
    public void RefreshPermission()
    {
        var granted = _services.ScreenCapturePermission.IsGranted;
        _permissionLabel.Text = granted ? "本地就绪" : "需要权限";
        if (_permissionPill.Child is StackPanel pill && pill.Children[0] is Border dot)
        {
            dot.Background = granted ? Brand.TaBrushes.Success : Brand.TaBrushes.Warning;
        }

        _statusTitle.Text = _services.Capture.StatusText;
        _statusHint.Text = granted
            ? "屏幕录制权限已开启 · 默认在本机识别"
            : "首次使用需要允许读取你主动框选的屏幕区域";

        var grantedActions = TaLayout.H(
            9,
            TaButton.Create("重新检测", TaButtonKind.Link, RefreshPermission),
            new Border { Width = 1, Height = 22, Background = Brand.TaBrushes.Hairline },
            VersionLabel());

        var deniedActions = TaLayout.H(
            9,
            TaButton.Create(
                "开启权限",
                TaButtonKind.Primary,
                () =>
                {
                    _ = _services.ScreenCapturePermission.Request();
                    RefreshPermission();
                }),
            TaButton.Create("系统设置", TaButtonKind.Secondary, () => _services.ScreenCapturePermission.OpenSystemSettings()),
            new Border { Width = 1, Height = 22, Background = Brand.TaBrushes.Hairline },
            VersionLabel());

        _statusActions.Child = granted ? grantedActions : deniedActions;
    }

    private FrameworkElement BuildRoot(Action? onOpenSettings)
    {
        var root = new Grid();
        var paper = new TaPaperBackground();
        root.Children.Add(paper);

        var content = new StackPanel { Orientation = Orientation.Vertical };
        content.Margin = new Thickness(WelcomeViewMetrics.ContentPadding);
        content.Children.Add(BuildHeader(onOpenSettings));
        content.Children.Add(BuildPrimaryCard());
        content.Children.Add(BuildQuickActions());

        // 状态条贴底（WelcomeView.swift:129-130 的 Spacer + statusBar）
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(content, 0);
        grid.Children.Add(content);
        RebuildStatusBar();
        // Mac 版状态条是浮在内容流末尾的圆角卡片（四周有留白），不是贴窗口边的通栏。
        _statusBar.Margin = new Thickness(
            WelcomeViewMetrics.ContentPadding,
            8,
            WelcomeViewMetrics.ContentPadding,
            WelcomeViewMetrics.ContentPadding - 4);
        Grid.SetRow(_statusBar, 1);
        grid.Children.Add(_statusBar);

        root.Children.Add(grid);
        return root;
    }

    private void RebuildStatusBar()
    {
        var iconSize = 32;
        var granted = _services.ScreenCapturePermission.IsGranted;
        var badge = new Border
        {
            Width = iconSize,
            Height = iconSize,
            CornerRadius = new CornerRadius(iconSize / 2),
            Background = granted ? Brand.TaBrushes.Success12 : Brand.TaBrushes.Warning12,
            Child = new Brand.TaIcon
            {
                Kind = granted ? Brand.TaIconKind.ShieldCheck : Brand.TaIconKind.WarningTriangle,
                Size = 14,
                Brush = granted ? Brand.TaBrushes.Success : Brand.TaBrushes.Warning,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(badge, 0);
        row.Children.Add(badge);
        var texts = TaLayout.V(4, _statusTitle, _statusHint);
        texts.Margin = new Thickness(12, 0, 12, 0);
        Grid.SetColumn(texts, 1);
        row.Children.Add(texts);
        Grid.SetColumn(_statusActions, 2);
        row.Children.Add(_statusActions);

        _statusBar.Child = row;
    }

    private FrameworkElement BuildHeader(Action? onOpenSettings)
    {
        var brand = TaLayout.V(
            3,
            TaPrimitives.Text(Brand.TaBrand.Name, 25 * 4.0 / 3.0, Brand.TaBrushes.Ink, FontWeights.Bold, serif: true),
            TaPrimitives.Text(Brand.TaBrand.Tagline, Brand.TaTypography.Callout, Brand.TaBrushes.MutedInk));

        var settings = TaButton.Create("设置", TaButtonKind.Secondary, onOpenSettings ?? (() => { })); // Mac 版顶栏按钮是普通圆角矩形（radius 9），不是胶囊
        // 顶栏辅助按钮刻意做小：默认 padding(14,6) 对顶栏来说太臃肿（用户实测反馈）。
        // Height 显式定高 —— 不然 WPF 默认 chrome 会把按钮撑得比「本地就绪」胶囊高。
        settings.Padding = new Thickness(9, 3, 9, 3);
        settings.Height = 26;
        settings.Content = TaLayout.H(
            5,
            new Brand.TaIcon { Kind = Brand.TaIconKind.Gear, Size = 12, VerticalAlignment = VerticalAlignment.Center },
            TaPrimitives.Text("设置", Brand.TaTypography.Subheadline, Brand.TaBrushes.Ink, FontWeights.Medium));

        var row = new Grid { Margin = new Thickness(0, 0, 0, WelcomeViewMetrics.SectionSpacing) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var appIcon = new Brand.TaAppIcon { Size = 60 };
        Grid.SetColumn(appIcon, 0);
        row.Children.Add(appIcon);
        brand.Margin = new Thickness(15, 0, 0, 0);
        Grid.SetColumn(brand, 1);
        row.Children.Add(brand);
        _permissionPill.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(_permissionPill, 2);
        row.Children.Add(_permissionPill);
        Grid.SetColumn(settings, 3);
        row.Children.Add(settings);

        return row;
    }

    private FrameworkElement BuildPrimaryCard()
    {
        var shortcut = _services.HotKeys.ShortcutFor(GlobalHotKeyAction.InteractiveCapture);

        var iconTile = new Border
        {
            Width = 52,
            Height = 52,
            CornerRadius = new CornerRadius(13),
            Background = Brand.TaBrushes.Cinnabar,
            Child = new Brand.TaIcon
            {
                Kind = Brand.TaIconKind.Viewfinder,
                Size = 24,
                Brush = Brand.TaBrushes.Paper,
                Thickness = 2.2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var texts = TaLayout.V(
            5,
            TaPrimitives.Text("开始拓取", Brand.TaTypography.Title3, Brand.TaBrushes.Paper, FontWeights.Bold),
            TaPrimitives.Text(
                "框选屏幕，再取字、翻译、复制、钉图、标注或美化",
                Brand.TaTypography.Callout,
                Brand.TaBrushes.Paper67));

        var cta = TaPrimitives.MakeCapsule(new Border
        {
            Background = Brand.TaBrushes.Paper,
            Padding = new Thickness(14, 9, 14, 9),
            Child = TaLayout.H(
                6,
                TaPrimitives.Text("开始截图", Brand.TaTypography.Callout, Brand.TaBrushes.Ink, FontWeights.SemiBold),
                new Brand.TaIcon { Kind = Brand.TaIconKind.ArrowRight, Size = 14, VerticalAlignment = VerticalAlignment.Center }),
        });

        var row = new Grid { Margin = new Thickness(0, 0, 0, WelcomeViewMetrics.SectionSpacing) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(iconTile, 0);
        row.Children.Add(iconTile);
        texts.Margin = new Thickness(16, 0, 16, 0);
        Grid.SetColumn(texts, 1);
        row.Children.Add(texts);
        var badge = TaPrimitives.Capsule(Brand.TaBrushes.Paper20, Brand.TaBrushes.Paper67);
        badge.Child = TaPrimitives.Text(shortcut.MacParitySymbolicText, Brand.TaTypography.Caption2, Brand.TaBrushes.Paper, FontWeights.Medium);
        Grid.SetColumn(badge, 2);
        row.Children.Add(badge);
        cta.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(cta, 3);
        row.Children.Add(cta);

        var card = new Border
        {
            CornerRadius = new CornerRadius(WelcomeViewMetrics.PrimaryCornerRadius),
            Background = Brand.TaBrushes.Ink,
            BorderBrush = Brand.TaBrushes.Cinnabar20,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = row,
        };
        card.MouseLeftButtonUp += (_, _) => _services.Capture.StartCapture(CaptureMode.Interactive);
        return card;
    }

    private FrameworkElement BuildQuickActions()
    {
        var actions = new (string Title, string Subtitle, GlobalHotKeyAction? Shortcut, Brand.TaIconKind Icon, Action Run)[]
        {
            ("极速取字", "识别文字并复制", GlobalHotKeyAction.IntelligentCapture, Brand.TaIconKind.TextViewfinder, () => _services.Capture.StartCapture(CaptureMode.Intelligent)),
            ("复制图片", "保留屏幕这一刻", GlobalHotKeyAction.ImageCapture, Brand.TaIconKind.DashedRect, () => _services.Capture.StartCapture(CaptureMode.Image)),
            ("截图翻译", "识别、翻译并复制", GlobalHotKeyAction.TranslationCapture, Brand.TaIconKind.BookClosed, () => _services.Capture.StartCapture(CaptureMode.Translation)),
            ("钉在屏幕", "让参考内容留在眼前", GlobalHotKeyAction.PinCapture, Brand.TaIconKind.Pin, () => _services.Capture.StartCapture(CaptureMode.Pin)),
            ("滚动长图", "自动滚动并拼接", GlobalHotKeyAction.LongCapture, Brand.TaIconKind.DownToLine, () => _services.Capture.StartCapture(CaptureMode.Long)),
            ("钉剪贴板", "从已有内容生成钉图", null, Brand.TaIconKind.Clipboard, () => _services.Capture.PinClipboardContent()),
        };

        var grid = new UniformGrid
        {
            Rows = 2,
            Columns = WelcomeViewMetrics.QuickActionColumnCount,
            Margin = new Thickness(0, 11, 0, 0),
        };

        foreach (var action in actions)
        {
            grid.Children.Add(BuildQuickActionCard(action));
        }

        return TaLayout.V(
            11,
            new TaSectionHeader("快速操作"),
            grid);
    }

    private FrameworkElement BuildQuickActionCard(
        (string Title, string Subtitle, GlobalHotKeyAction? Shortcut, Brand.TaIconKind Icon, Action Run) action)
    {
        var icon = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(9),
            Background = Brand.TaBrushes.Cinnabar09,
            Child = new Brand.TaIcon
            {
                Kind = action.Icon,
                Size = 17,
                Brush = Brand.TaBrushes.Cinnabar,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(icon, 0);
        top.Children.Add(icon);
        Grid.SetColumn(TaPrimitives.Spacer(), 1);
        top.Children.Add(TaPrimitives.Spacer());
        if (action.Shortcut is { } shortcutAction)
        {
            var badge = TaPrimitives.Capsule(Brand.TaBrushes.Paper82, Brand.TaBrushes.Ink10);
            badge.Child = TaPrimitives.Text(
                _services.HotKeys.ShortcutFor(shortcutAction).MacParitySymbolicText,
                Brand.TaTypography.Caption2,
                Brand.TaBrushes.MutedInk,
                FontWeights.Medium);
            Grid.SetColumn(badge, 2);
            top.Children.Add(badge);
        }

        var body = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 12, 0, 0) };
        body.Children.Add(TaPrimitives.Text(action.Title, Brand.TaTypography.Callout, Brand.TaBrushes.Ink, FontWeights.SemiBold));
        var subtitle = TaPrimitives.Text(action.Subtitle, Brand.TaTypography.Caption, Brand.TaBrushes.MutedInk);
        subtitle.Margin = new Thickness(0, 3, 0, 0);
        body.Children.Add(subtitle);

        var card = new Border
        {
            CornerRadius = new CornerRadius(WelcomeViewMetrics.QuickActionCornerRadius),
            Background = Brand.TaBrushes.ElevatedPaper,
            BorderBrush = Brand.TaBrushes.Hairline,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16),
            MinHeight = WelcomeViewMetrics.QuickActionMinimumHeight,
            Margin = new Thickness(WelcomeViewMetrics.GridSpacing / 2),
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = TaLayout.V(0, top, body),
        };
        card.MouseLeftButtonUp += (_, _) => action.Run();
        return card;
    }
}
