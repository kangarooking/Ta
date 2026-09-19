using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ta.Settings.Services;

namespace Ta.Settings.UI;

/// <summary>
/// 纸感背景。对应 macOS <c>TaPaperBackground</c>（TaDesignSystem.swift:59-81）：
/// paper 底色 + 右下角一枚 2.5% 不透明度的超大品牌图标水印。
/// </summary>
public sealed class TaPaperBackground : FrameworkElement
{
    /// <inheritdoc />
    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        dc.DrawRectangle(Brand.TaBrushes.Paper, null, new Rect(0, 0, size.Width, size.Height));

        // TaDesignSystem.swift:70-73 —— 右下角超大图标，不透明度 0.025
        var watermarkSize = Math.Min(size.Width, size.Height) * 0.62;
        var rect = new Rect(
            size.Width - watermarkSize * 0.5,
            size.Height - watermarkSize * 0.5,
            watermarkSize,
            watermarkSize);

        dc.PushOpacity(0.025);
        var image = Brand.TaBrandAssets.AppIconImage();
        if (image is not null)
        {
            dc.DrawImage(image, rect);
        }
        else
        {
            dc.DrawRoundedRectangle(
                Brand.TaBrushes.Cinnabar,
                null,
                rect,
                watermarkSize * 0.22,
                watermarkSize * 0.22);
        }

        dc.Pop();
    }
}

/// <summary>设置窗口的 7 个标签页。</summary>
public enum SettingsTab
{
    /// <summary>权限。</summary>
    Permissions,

    /// <summary>常规。</summary>
    General,

    /// <summary>快捷键。</summary>
    HotKeys,

    /// <summary>识别。</summary>
    Recognition,

    /// <summary>翻译。</summary>
    Translation,

    /// <summary>AI 模型。</summary>
    Models,

    /// <summary>Agent。</summary>
    Agent,
}

/// <summary>标签页的标题与图标。</summary>
public static class SettingsTabs
{
    /// <summary>全部标签页，顺序对应 SettingsView.swift:85-92。</summary>
    public static IReadOnlyList<SettingsTab> All { get; } = new[]
    {
        SettingsTab.Permissions,
        SettingsTab.General,
        SettingsTab.HotKeys,
        SettingsTab.Recognition,
        SettingsTab.Translation,
        SettingsTab.Models,
        SettingsTab.Agent,
    };

    /// <summary>标题（SettingsView.swift:96-106）。</summary>
    public static string Title(SettingsTab tab) => tab switch
    {
        SettingsTab.Permissions => "权限",
        SettingsTab.General => "常规",
        SettingsTab.HotKeys => "快捷键",
        SettingsTab.Recognition => "识别",
        SettingsTab.Translation => "翻译",
        SettingsTab.Models => "AI 模型",
        SettingsTab.Agent => "Agent",
        _ => string.Empty,
    };

    /// <summary>
    /// 图标。对应 SettingsView.swift:108-118 的 SF Symbol
    /// （lock.shield / gearshape / command / text.viewfinder /
    /// character.book.closed / sparkles / cpu）。
    /// </summary>
    public static Brand.TaIconKind Icon(SettingsTab tab) => tab switch
    {
        SettingsTab.Permissions => Brand.TaIconKind.LockShield,
        SettingsTab.General => Brand.TaIconKind.Gear,
        SettingsTab.HotKeys => Brand.TaIconKind.Command,
        SettingsTab.Recognition => Brand.TaIconKind.TextViewfinder,
        SettingsTab.Translation => Brand.TaIconKind.BookClosed,
        SettingsTab.Models => Brand.TaIconKind.Sparkles,
        SettingsTab.Agent => Brand.TaIconKind.Cpu,
        _ => Brand.TaIconKind.Gear,
    };
}

/// <summary>
/// 设置窗口。**780×600**，**7 个标签页**（参考文档 §9.10，SettingsView.swift:4-79）。
///
/// 跨页导航：监听 <see cref="TaServices.OpenAiModelSettingsRequested"/>
/// （对应 macOS 的 <c>Notification.Name("Ta.OpenAIModelSettings")</c>，SettingsView.swift:82），
/// 收到就切到 AI 模型页。
/// </summary>
public sealed class SettingsWindow : Window
{
    /// <summary>窗口宽度（SettingsView.swift:38）。</summary>
    public const double WindowWidth = 780;

    /// <summary>
    /// 窗口高度。Mac 版 600pt，但 WPF 版控件度量（tab 图标 25 + 双层边距、内容
    /// Padding 18×2、ModelsPage 向导的 header + 服务列表 + 底部按钮）更高，
    /// 600 会把 AI 模型页的「下一步」按钮裁出窗口外 —— 实测 680 才放得下。
    /// </summary>
    public const double WindowHeight = 680;

    /// <summary>标签栏期望高度（仅作参考值；实际行高由 Auto 行按内容自适应）。</summary>
    public const double TabBarHeight = 104;

    /// <summary>内容区内边距（SettingsView.swift:35 的 <c>.padding(18)</c>）。</summary>
    public const double ContentPadding = 18;

    private readonly TaServices _services;
    private readonly Dictionary<SettingsTab, Border> _tabButtons = new();
    private readonly Dictionary<SettingsTab, Border> _tabCapsules = new();
    private readonly Border _contentHost = new();
    private SettingsTab _selectedTab = SettingsTab.Permissions;
    private readonly Dictionary<SettingsTab, Tabs.TaTabPage> _pages = new();

    /// <summary>构造。</summary>
    public SettingsWindow(TaServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));

        Title = $"{Brand.TaBrand.Name} {Brand.TaBrand.EnglishName} 设置";
        Width = WindowWidth;
        Height = WindowHeight;
        MinWidth = WindowWidth;
        MinHeight = WindowHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brand.TaBrushes.Paper;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        TrySetBrandIcon(this);

        Content = BuildRoot();
        _services.OpenAiModelSettingsRequested += OnOpenAiModelSettingsRequested;
        SelectTab(SettingsTab.Permissions);
    }

    /// <summary>当前选中的标签页。</summary>
    public SettingsTab SelectedTab => _selectedTab;

    /// <summary>切换到指定标签页。</summary>
    public void SelectTab(SettingsTab tab)
    {
        _selectedTab = tab;

        if (!_pages.TryGetValue(tab, out var page))
        {
            page = CreatePage(tab);
            _pages[tab] = page;
        }

        page.Refresh();
        _contentHost.Child = page;

        foreach (var (candidate, button) in _tabButtons)
        {
            var isSelected = candidate == tab;
            button.Background = isSelected ? Brand.TaBrushes.Cinnabar10 : Brushes.Transparent;
            if (_tabCapsules.TryGetValue(candidate, out var capsule))
            {
                capsule.Background = isSelected ? Brand.TaBrushes.Cinnabar : Brushes.Transparent;
            }

            if (button.Child is Grid grid
                && grid.Children.Count >= 2
                && grid.Children[0] is Grid inner
                && inner.Children.Count >= 2)
            {
                if (inner.Children[0] is StackPanel iconPanel && iconPanel.Children[0] is Brand.TaIcon icon)
                {
                    icon.Brush = isSelected ? Brand.TaBrushes.Cinnabar : Brand.TaBrushes.MutedInk;
                }

                if (inner.Children[1] is TextBlock label)
                {
                    label.Foreground = isSelected ? Brand.TaBrushes.Cinnabar : Brand.TaBrushes.MutedInk;
                    label.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Medium;
                }
            }
        }
    }

    /// <summary>冒烟用：取已构建的标签页实例。</summary>
    internal Tabs.TaTabPage? PageFor(SettingsTab tab) =>
        _pages.TryGetValue(tab, out var page) ? page : null;

    private Tabs.TaTabPage CreatePage(SettingsTab tab) => tab switch
    {
        SettingsTab.Permissions => new Tabs.PermissionsPage(_services),
        SettingsTab.General => new Tabs.GeneralPage(_services),
        SettingsTab.HotKeys => new Tabs.HotKeysPage(_services),
        SettingsTab.Recognition => new Tabs.RecognitionPage(_services),
        SettingsTab.Translation => new Tabs.TranslationPage(_services),
        SettingsTab.Models => new Tabs.ModelsPage(_services),
        SettingsTab.Agent => new Tabs.AgentPage(_services),
        _ => new Tabs.PermissionsPage(_services),
    };

    /// <summary>给 WPF 窗口设置品牌图标（标题栏 / 任务栏）。失败静默（不影响启动）。</summary>
    private static void TrySetBrandIcon(System.Windows.Window window)
    {
        try
        {
            var exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrEmpty(exe))
            {
                var uri = new Uri(exe);
                window.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(uri);
            }
        }
        catch
        {
            // 图标加载失败不阻塞窗口。
        }
    }

    private FrameworkElement BuildRoot()
    {
        var root = new Grid();
        // ⚠️ 行高必须 Auto：tab 内容（图标 25 + 文字 + 胶囊 + 双层边距）实际需要 ~100px，
        // 固定 90 会把图标和文字的下半截裁掉。
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // 纸感底 + 水印（SettingsView.swift:9 的 ZStack { TaPaperBackground() ... }）
        var paper = new TaPaperBackground();
        Grid.SetRowSpan(paper, 3);
        root.Children.Add(paper);

        var tabBar = BuildTabBar();
        Grid.SetRow(tabBar, 0);
        root.Children.Add(tabBar);

        var divider = new Border { Height = 1, Background = Brand.TaBrushes.Hairline };
        Grid.SetRow(divider, 1);
        root.Children.Add(divider);

        _contentHost.Padding = new Thickness(ContentPadding);
        _contentHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        _contentHost.VerticalAlignment = VerticalAlignment.Stretch;
        Grid.SetRow(_contentHost, 2);
        root.Children.Add(_contentHost);

        return root;
    }

    private FrameworkElement BuildTabBar()
    {
        var grid = new UniformGrid { Rows = 1, Columns = SettingsTabs.All.Count };
        grid.Background = Brand.TaBrushes.ElevatedPaper96;

        foreach (var tab in SettingsTabs.All)
        {
            var icon = new Brand.TaIcon
            {
                Kind = SettingsTabs.Icon(tab),
                Size = Brand.TaTypography.TabIcon,
                Brush = Brand.TaBrushes.MutedInk,
                HorizontalAlignment = HorizontalAlignment.Center,
                Height = 25,
            };
            var label = TaPrimitives.Text(
                SettingsTabs.Title(tab),
                Brand.TaTypography.TabTitle,
                Brand.TaBrushes.MutedInk,
                FontWeights.Medium);
            label.HorizontalAlignment = HorizontalAlignment.Center;

            var content = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 6, 0, 0) };
            content.Children.Add(icon);
            content.Children.Add(label);

            var capsule = new Border
            {
                Width = 18,
                Height = 2,
                CornerRadius = new CornerRadius(1),
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 2, 0, 0),
            };

            var stack = new Grid();
            stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(content, 0);
            stack.Children.Add(content);
            Grid.SetRow(capsule, 1);
            stack.Children.Add(capsule);

            var button = new Border
            {
                Child = stack,
                CornerRadius = new CornerRadius(11),
                Margin = new Thickness(3, 8, 3, 8),
                Background = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = SettingsTabs.Title(tab),
            };

            var target = tab;
            button.MouseLeftButtonUp += (_, _) => SelectTab(target);
            _tabButtons[tab] = button;
            _tabCapsules[tab] = capsule;
            grid.Children.Add(button);
        }

        return new Border
        {
            Padding = new Thickness(22, 14, 22, 14),
            Child = grid,
        };
    }

    private void OnOpenAiModelSettingsRequested() => Dispatcher.BeginInvoke(() => SelectTab(SettingsTab.Models));
}
