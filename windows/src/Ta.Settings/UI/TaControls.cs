using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Dom = Ta.Settings.Core;

namespace Ta.Settings.UI;

/// <summary>布局便捷方法：对应 SwiftUI 的 <c>VStack/HStack/ZStack</c>。</summary>
public static class TaLayout
{
    /// <summary>纵向堆叠（<c>VStack(spacing:)</c>）。</summary>
    public static StackPanel V(double spacing = 0, params FrameworkElement?[] children)
        => Stack(Orientation.Vertical, spacing, children);

    /// <summary>横向排列（<c>HStack(spacing:)</c>）。</summary>
    public static StackPanel H(double spacing = 0, params FrameworkElement?[] children)
        => Stack(Orientation.Horizontal, spacing, children);

    /// <summary>
    /// 带左侧弹性空隙的横向排列（对应 <c>HStack { Spacer(); ... }</c>）。
    /// 用 Grid 的 Star 列实现弹性空隙 —— StackPanel 不会拉伸子元素，Spacer 必须靠 Grid。
    /// </summary>
    public static Grid HWithSpacer(double spacing, params FrameworkElement?[] children)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(TaPrimitives.Spacer(), 0);
        grid.Children.Add(TaPrimitives.Spacer());
        var panel = Stack(Orientation.Horizontal, spacing, children);
        Grid.SetColumn(panel, 1);
        grid.Children.Add(panel);
        return grid;
    }

    /// <summary>
    /// 横向排列 + 末尾弹性空隙（对应 <c>HStack { ...; Spacer() }</c>）。
    /// </summary>
    public static Grid HTrailingSpacer(double spacing, params FrameworkElement?[] children)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var panel = Stack(Orientation.Horizontal, spacing, children);
        Grid.SetColumn(panel, 0);
        grid.Children.Add(panel);
        Grid.SetColumn(TaPrimitives.Spacer(), 1);
        grid.Children.Add(TaPrimitives.Spacer());
        return grid;
    }

    /// <summary>居中容器。</summary>
    public static StackPanel CenterH(params FrameworkElement?[] children)
    {
        var panel = Stack(Orientation.Horizontal, 0, children);
        panel.HorizontalAlignment = HorizontalAlignment.Center;
        return panel;
    }

    private static StackPanel Stack(Orientation orientation, double spacing, FrameworkElement?[] children)
    {
        var panel = new StackPanel { Orientation = orientation };
        foreach (var child in children)
        {
            // 条件拼装的子元素可能是 null（对应 SwiftUI 的 if 视图修饰符）
            if (child is not null)
            {
                panel.Children.Add(child);
            }
        }

        // 子元素全部入栈之后再补 spacing，否则拿不到最后一个元素
        if (spacing > 0)
        {
            ApplySpacing(panel, spacing);
        }

        return panel;
    }

    private static void ApplySpacing(StackPanel panel, double spacing)
    {
        // StackPanel 没有 spacing 概念，用子元素 margin 模拟；
        // 最后一个元素不补 margin，和 SwiftUI 的 spacing 语义一致。
        if (panel.Children.Count == 0)
        {
            return;
        }

        for (var i = 0; i < panel.Children.Count; i++)
        {
            if (i == panel.Children.Count - 1)
            {
                continue;
            }

            if (panel.Children[i] is FrameworkElement element)
            {
                var margin = element.Margin;
                if (panel.Orientation == Orientation.Vertical)
                {
                    element.Margin = new Thickness(margin.Left, margin.Top, margin.Right, margin.Bottom + spacing);
                }
                else
                {
                    element.Margin = new Thickness(margin.Left, margin.Top, margin.Right + spacing, margin.Bottom);
                }
            }
        }
    }
}

/// <summary>最基础的可视元素工厂。</summary>
public static class TaPrimitives
{
    /// <summary>一段文字。</summary>
    /// <param name="text">文本。</param>
    /// <param name="size">字号（DIP）。</param>
    /// <param name="brush">颜色。</param>
    /// <param name="weight">字重。</param>
    /// <param name="serif">是否用标题衬线体。</param>
    /// <param name="mono">是否用等宽字体。</param>
    /// <param name="wrap">是否允许换行。</param>
    public static TextBlock Text(
        string text,
        double size,
        Brush? brush = null,
        FontWeight? weight = null,
        bool serif = false,
        bool mono = false,
        bool wrap = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = brush ?? Brand.TaBrushes.Ink,
            FontWeight = weight ?? FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        };

        if (serif)
        {
            block.FontFamily = Brand.TaTypography.SerifFamily;
        }
        else if (mono)
        {
            block.FontFamily = new FontFamily(Brand.TaTypography.MonoFamily);
        }

        return block;
    }

    /// <summary>弹性空隙（对应 SwiftUI 的 <c>Spacer()</c>）。必须放在 Grid 的 Star 列里才会真的撑开。</summary>
    public static FrameworkElement Spacer() => new System.Windows.Controls.Border
    {
        Background = Brushes.Transparent,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>1px 分隔线（对应 <c>Divider().overlay(TaPalette.hairline)</c>）。</summary>
    /// <param name="horizontal">true 画横线，false 画竖线。</param>
    public static Border Hairline(bool horizontal = true, double thickness = 1)
        => new()
        {
            Background = Brand.TaBrushes.Hairline,
            Height = horizontal ? thickness : double.NaN,
            Width = horizontal ? double.NaN : thickness,
            HorizontalAlignment = horizontal ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
            VerticalAlignment = horizontal ? VerticalAlignment.Center : VerticalAlignment.Stretch,
            SnapsToDevicePixels = true,
        };

    /// <summary>实心圆点。</summary>
    public static Border Dot(double diameter, Brush brush)
        => new()
        {
            Width = diameter,
            Height = diameter,
            CornerRadius = new CornerRadius(diameter / 2),
            Background = brush,
        };

    /// <summary>
    /// 把 Border 变成真胶囊：圆角跟随实际高度的一半。
    /// WPF 对过大的 CornerRadius（如 999）按 X/Y 两轴独立缩放截断，四角渲染成
    /// 椭圆弧 —— 宽高比一失衡就是「蛋形」。半高圆角才是真正的半圆端胶囊。
    /// </summary>
    public static Border MakeCapsule(Border border)
    {
        // 胶囊紧抱内容（SwiftUI Capsule() 语义）：放在 Grid/Stack 里时禁止被
        // 默认 Stretch 拉到行高 —— 拉高后宽高比失衡，半高圆角会退化成正圆/蛋形。
        if (border.VerticalAlignment == VerticalAlignment.Stretch)
        {
            border.VerticalAlignment = VerticalAlignment.Center;
        }

        border.CornerRadius = new CornerRadius(Math.Max(1, border.ActualHeight / 2));
        border.SizeChanged += (_, _) => border.CornerRadius = new CornerRadius(Math.Max(1, border.ActualHeight / 2));
        return border;
    }

    /// <summary>胶囊容器（对应 SwiftUI 的 <c>Capsule()</c>）。</summary>
    public static Border Capsule(Brush background, Brush? stroke = null, params FrameworkElement?[] children)
    {
        var border = new Border
        {
            Background = background,
            Padding = new Thickness(7, 4, 7, 4),
            Child = TaLayout.H(6, children),
        };
        MakeCapsule(border);
        if (stroke is not null)
        {
            border.BorderBrush = stroke;
            border.BorderThickness = new Thickness(1);
        }

        return border;
    }

    /// <summary>小徽章（如「推荐」「可用」「仅文字」）。</summary>
    public static Border Badge(string text, Brush foreground, Brush background, double size = 0)
    {
        var block = Text(text, size > 0 ? size : Brand.TaTypography.Caption2, foreground, FontWeights.Bold);
        var border = new Border
        {
            Background = background,
            Padding = new Thickness(6, 1, 6, 1),
            Child = block,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return TaPrimitives.MakeCapsule(border);
    }

    /// <summary>可滚动内容区（对应 SwiftUI 的 <c>ScrollView</c>）。</summary>
    public static ScrollViewer Scroll(FrameworkElement content, Thickness padding)
    {
        var viewer = new ScrollViewer
        {
            Content = content,
            Padding = padding,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        return viewer;
    }
}

/// <summary>按钮外观。</summary>
public enum TaButtonKind
{
    /// <summary>主按钮（cinnabar 实底）。</summary>
    Primary,

    /// <summary>次按钮（paper 底 + hairline 描边）。</summary>
    Secondary,

    /// <summary>链接按钮（无底色，cinnabar 文字）。</summary>
    Link,

    /// <summary>危险按钮（红色文字）。</summary>
    Destructive,
}

/// <summary>按钮工厂。</summary>
public static class TaButton
{
    /// <summary>建一个按钮。</summary>
    public static Button Create(
        string text,
        TaButtonKind kind = TaButtonKind.Secondary,
        Action? onClick = null,
        double fontSize = 0,
        double? cornerRadius = null)
    {
        var size = fontSize > 0 ? fontSize : Brand.TaTypography.Callout;
        var foreground = kind switch
        {
            TaButtonKind.Primary => Brand.TaBrushes.Paper,
            TaButtonKind.Link => Brand.TaBrushes.Cinnabar,
            TaButtonKind.Destructive => Brand.TaBrushes.Danger,
            _ => Brand.TaBrushes.Ink,
        };

        var button = new Button
        {
            Content = TaPrimitives.Text(text, size, foreground, FontWeights.Medium),
            Padding = new Thickness(14, 6, 14, 6),
            Cursor = Cursors.Hand,
            SnapsToDevicePixels = true,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        switch (kind)
        {
            case TaButtonKind.Primary:
                button.Background = Brand.TaBrushes.Cinnabar;
                button.BorderBrush = Brand.TaBrushes.Cinnabar;
                break;
            case TaButtonKind.Secondary:
                button.Background = Brand.TaBrushes.ElevatedPaper;
                button.BorderBrush = Brand.TaBrushes.Hairline;
                break;
            case TaButtonKind.Link:
                button.Background = Brushes.Transparent;
                button.BorderBrush = Brushes.Transparent;
                button.Padding = new Thickness(4, 2, 4, 2);
                break;
            case TaButtonKind.Destructive:
                button.Background = Brushes.Transparent;
                button.BorderBrush = Brand.TaBrushes.Hairline;
                button.Padding = new Thickness(4, 2, 4, 2);
                break;
        }

        button.BorderThickness = kind == TaButtonKind.Link ? new Thickness(0) : new Thickness(1);

        // 圆角边框 + 内容居中，配合 IsEnabled / IsMouseOver 触发器
        var visualBorder = new FrameworkElementFactory(typeof(Border));
        visualBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        visualBorder.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        visualBorder.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        // cornerRadius==999 是「胶囊」哨兵：不能用 999 直接当圆角（WPF 会截成椭圆弧），
        // 改为跟随实际高度的一半。
        if (cornerRadius is 999)
        {
            // visualBorder 是模板工厂（FrameworkElementFactory），用 AddHandler 挂胶囊化逻辑。
            visualBorder.AddHandler(
                FrameworkElement.SizeChangedEvent,
                new SizeChangedEventHandler((sender, _) =>
                {
                    var border = (Border)sender;
                    border.CornerRadius = new CornerRadius(Math.Max(1, border.ActualHeight / 2));
                }));
        }
        else
        {
            visualBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(cornerRadius ?? (kind is TaButtonKind.Link or TaButtonKind.Destructive ? 6 : 9)));
        }
        visualBorder.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        visualBorder.SetValue(Border.SnapsToDevicePixelsProperty, true);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        visualBorder.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = visualBorder };

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.OpacityProperty, 0.88));
        template.Triggers.Add(hover);

        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(Control.OpacityProperty, 0.42));
        template.Triggers.Add(disabled);

        button.Template = template;

        if (onClick is not null)
        {
            button.Click += (_, _) => onClick();
        }

        return button;
    }

    /// <summary>带图标 + 文字的按钮。</summary>
    public static Button CreateWithIcon(
        string text,
        Brand.TaIconKind icon,
        TaButtonKind kind = TaButtonKind.Secondary,
        Action? onClick = null,
        double iconSize = 13)
    {
        var button = Create(text, kind, onClick);
        var row = TaLayout.H(
            6,
            new Brand.TaIcon
            {
                Kind = icon,
                Size = iconSize,
                Brush = ((TextBlock)button.Content).Foreground,
                VerticalAlignment = VerticalAlignment.Center,
            },
            TaPrimitives.Text(text, ((TextBlock)button.Content).FontSize, ((TextBlock)button.Content).Foreground, FontWeights.Medium));
        button.Content = row;
        return button;
    }

    /// <summary>纯图标按钮（如模型页右上角的「⋯」）。</summary>
    public static Button Icon(Brand.TaIconKind icon, Action? onClick = null, double size = 16, string? toolTip = null)
    {
        var button = new Button
        {
            Content = new Brand.TaIcon { Kind = icon, Size = size, Brush = Brand.TaBrushes.Ink },
            Padding = new Thickness(7),
            Cursor = Cursors.Hand,
            Background = Brand.TaBrushes.ElevatedPaper,
            BorderBrush = Brand.TaBrushes.Hairline,
            BorderThickness = new Thickness(1),
            ToolTip = toolTip,
        };

        var visualBorder = new FrameworkElementFactory(typeof(Border));
        visualBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        visualBorder.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        visualBorder.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        visualBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        visualBorder.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        visualBorder.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = visualBorder };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.OpacityProperty, 0.88));
        template.Triggers.Add(hover);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(Control.OpacityProperty, 0.42));
        template.Triggers.Add(disabled);
        button.Template = template;

        if (onClick is not null)
        {
            button.Click += (_, _) => onClick();
        }

        return button;
    }
}

/// <summary>
/// 卡片容器。对应各视图里的 <c>compactSurface</c> / <c>surface</c>
/// （ModelSettingsView.swift:619-623、TranslationSettingsView.swift:234-238）：
/// 圆角 11/12 + paper 半透明填充 + 1px hairline 描边。
/// </summary>
public sealed class TaCard : Border
{
    /// <summary>建一张卡片。</summary>
    /// <param name="child">内容。</param>
    /// <param name="padding">内边距。</param>
    /// <param name="radius">圆角。</param>
    /// <param name="fill">底色。</param>
    /// <param name="stroke">描边。</param>
    public TaCard(FrameworkElement child, Thickness? padding = null, double radius = 11, Brush? fill = null, Brush? stroke = null)
    {
        Background = fill ?? Brand.TaBrushes.Paper54;
        BorderBrush = stroke ?? Brand.TaBrushes.Hairline;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(radius);
        Padding = padding ?? new Thickness(12);
        Child = child;
        SnapsToDevicePixels = true;
    }
}

/// <summary>
/// 区块小标题。对应 <c>TaSectionLabel</c>（TaDesignSystem.swift:83-93）
/// 与各页的 <c>sectionTitle(_:detail:)</c>：
/// 18×2 的朱砂短横 + 标题 + 一行说明。
/// </summary>
public sealed class TaSectionHeader : StackPanel
{
    /// <summary>建一个区块标题。</summary>
    public TaSectionHeader(string title, string? detail = null)
    {
        Orientation = Orientation.Vertical;

        var row = TaLayout.H(
            8,
            new Border
            {
                Width = 18,
                Height = 2,
                Background = Brand.TaBrushes.Cinnabar,
                VerticalAlignment = VerticalAlignment.Center,
            },
            TaPrimitives.Text(title, Brand.TaTypography.Callout, Brand.TaBrushes.MutedInk, FontWeights.SemiBold));

        if (detail is not null)
        {
            row.Children.Add(TaPrimitives.Text(detail, Brand.TaTypography.Caption2, Brand.TaBrushes.MutedInk, wrap: true));
        }

        Children.Add(row);
    }
}

/// <summary>
/// 带标签的一行表单控件（对应 SwiftUI 的 <c>LabeledContent</c>）。
/// </summary>
public sealed class TaFieldRow : Grid
{
    /// <summary>建一行。</summary>
    /// <param name="label">左侧标签。</param>
    /// <param name="control">右侧控件。</param>
    /// <param name="labelWidth">标签列宽；null 表示自适应。</param>
    public TaFieldRow(string label, FrameworkElement control, double? labelWidth = null)
    {
        Margin = new Thickness(0, 2, 0, 2);
        ColumnDefinitions.Add(new ColumnDefinition { Width = labelWidth is { } w ? new GridLength(w) : GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = TaPrimitives.Text(label, Brand.TaTypography.Callout, Brand.TaBrushes.Ink);
        labelBlock.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(labelBlock, 0);
        Children.Add(labelBlock);

        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        Children.Add(control);
    }
}

/// <summary>
/// 状态行：图标 + 文字。对应各视图里的
/// <c>Label(text, systemImage:)</c> + <c>.foregroundStyle(...)</c>。
/// </summary>
public sealed class TaStatusLine : Border
{
    /// <summary>建一个状态行。</summary>
    public TaStatusLine(string message, Brand.TaIconKind icon, Brush color, bool wrap = false)
    {
        Padding = new Thickness(0);
        Background = Brushes.Transparent;
        Child = TaLayout.H(
            7,
            new Brand.TaIcon
            {
                Kind = icon,
                Size = 13,
                Brush = color,
                VerticalAlignment = VerticalAlignment.Center,
            },
            TaPrimitives.Text(message, Brand.TaTypography.Caption, color, wrap: wrap));
    }
}

/// <summary>带边框的文本输入框。</summary>
public sealed class TaTextBox : Border
{
    /// <summary>内部 TextBox。</summary>
    public System.Windows.Controls.TextBox Input { get; }

    /// <summary>建一个文本框。</summary>
    public TaTextBox(string? placeholder = null, string text = "", double minWidth = 0)
    {
        Background = Brand.TaBrushes.ElevatedPaper;
        BorderBrush = Brand.TaBrushes.Hairline;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(9, 6, 9, 6);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        if (minWidth > 0)
        {
            MinWidth = minWidth;
        }

        Input = new System.Windows.Controls.TextBox
        {
            Text = text,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Brand.TaBrushes.Ink,
            FontSize = Brand.TaTypography.Callout,
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        if (!string.IsNullOrEmpty(placeholder))
        {
            // WPF 的 TextBox 没有 placeholder，用附加属性模拟
            TaWatermark.SetPlaceholder(Input, placeholder);
        }

        Child = Input;
    }

    /// <summary>当前文本。</summary>
    public string Text
    {
        get => Input.Text;
        set => Input.Text = value;
    }
}

/// <summary>带边框的密码输入框。</summary>
public sealed class TaPasswordBox : Border
{
    /// <summary>内部 PasswordBox。</summary>
    public PasswordBox Input { get; }

    /// <summary>建一个密码框。</summary>
    public TaPasswordBox()
    {
        Background = Brand.TaBrushes.ElevatedPaper;
        BorderBrush = Brand.TaBrushes.Hairline;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(9, 6, 9, 6);
        HorizontalAlignment = HorizontalAlignment.Stretch;

        Input = new PasswordBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Brand.TaBrushes.Ink,
            FontSize = Brand.TaTypography.Callout,
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        Child = Input;
    }

    /// <summary>当前密码。</summary>
    public string Password
    {
        get => Input.Password;
        set => Input.Password = value;
    }
}

/// <summary>TextBox 的 placeholder 附加属性（WPF 原生没有）。</summary>
public static class TaWatermark
{
    /// <summary>placeholder 附加属性。</summary>
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder",
        typeof(string),
        typeof(TaWatermark),
        new FrameworkPropertyMetadata(string.Empty, OnPlaceholderChanged));

    /// <summary>设置 placeholder。</summary>
    public static void SetPlaceholder(DependencyObject element, string value) => element.SetValue(PlaceholderProperty, value);

    /// <summary>读取 placeholder。</summary>
    public static string GetPlaceholder(DependencyObject element) => (string)element.GetValue(PlaceholderProperty);

    private static void OnPlaceholderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not System.Windows.Controls.TextBox box)
        {
            return;
        }

        box.Loaded -= OnLoaded;
        box.Loaded += OnLoaded;
        box.TextChanged -= OnTextChanged;
        box.TextChanged += OnTextChanged;
        Update(box, (string)e.NewValue);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox box)
        {
            Update(box, GetPlaceholder(box));
        }
    }

    private static void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox box)
        {
            Update(box, GetPlaceholder(box));
        }
    }

    private static void Update(System.Windows.Controls.TextBox box, string placeholder)
    {
        var hasText = box.Text.Length > 0;
        if (hasText)
        {
            if (box.Tag is bool showing && showing)
            {
                box.ClearValue(System.Windows.Controls.TextBox.ForegroundProperty);
                box.ClearValue(System.Windows.Controls.TextBox.FontStyleProperty);
                box.Tag = false;
            }

            return;
        }

        if (box.Tag is bool alreadyShowing && alreadyShowing)
        {
            return;
        }

        box.Tag = true;
        box.Foreground = Brand.TaBrushes.MutedInk;
        box.FontStyle = FontStyles.Italic;
    }
}

/// <summary>
/// 自绘下拉选择框。不用 WPF 原生 ComboBox 的原因：
/// 原生 ComboBox 的弹层和边框走系统主题色，在这个纸感设计系统里会明显跳脱；
/// 自绘可以精确控制圆角、paper 底色、hairline 描边和朱砂选中项。
/// </summary>
public sealed class TaPicker : Border
{
    private readonly TextBlock _valueBlock;
    private readonly Popup _popup;
    private readonly ListBox _list;
    private readonly List<string> _items = new();

    /// <summary>选中项变化。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>建一个下拉框。</summary>
    /// <param name="items">候选项。</param>
    /// <param name="selected">初始选中。</param>
    /// <param name="width">整体宽度；0 表示自适应。</param>
    public TaPicker(IEnumerable<string> items, string? selected = null, double width = 0)
    {
        _items.AddRange(items);

        Background = Brand.TaBrushes.ElevatedPaper;
        BorderBrush = Brand.TaBrushes.Hairline;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(10, 6, 8, 6);
        HorizontalAlignment = HorizontalAlignment.Left;
        if (width > 0)
        {
            Width = width;
        }

        _valueBlock = TaPrimitives.Text(
            selected ?? (_items.Count > 0 ? _items[0] : string.Empty),
            Brand.TaTypography.Callout,
            Brand.TaBrushes.Ink);
        var chevron = new Brand.TaIcon
        {
            Kind = Brand.TaIconKind.ChevronDown,
            Size = 13,
            Brush = Brand.TaBrushes.MutedInk,
            Margin = new Thickness(10, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_valueBlock, 0);
        row.Children.Add(_valueBlock);
        Grid.SetColumn(chevron, 1);
        row.Children.Add(chevron);
        Child = row;

        _list = new ListBox
        {
            Background = Brand.TaBrushes.ElevatedPaper,
            BorderThickness = new Thickness(0),
            Foreground = Brand.TaBrushes.Ink,
            FontSize = Brand.TaTypography.Callout,
            Padding = new Thickness(4),
            MaxHeight = 260,
            ItemsSource = _items,
            SelectedItem = selected,
        };
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is string value)
            {
                SelectedItem = value;
                _valueBlock.Text = value;
            }

            _popup.IsOpen = false;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        };

        _popup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = Brand.TaBrushes.ElevatedPaper,
                BorderBrush = Brand.TaBrushes.Hairline,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(2),
                MinWidth = 200,
                Child = _list,
            },
        };

        MouseLeftButtonUp += (_, _) => _popup.IsOpen = !_popup.IsOpen;
        Cursor = Cursors.Hand;
    }

    /// <summary>当前选中项。</summary>
    public string? SelectedItem { get; private set; }

    /// <summary>设置选中项（不触发事件）。</summary>
    public void Select(string? value)
    {
        SelectedItem = value;
        _valueBlock.Text = value ?? string.Empty;
        _list.SelectedItem = value;
    }
}

/// <summary>
/// 开关行。对应 SwiftUI 的 <c>Toggle("标题", isOn:)</c>。
/// 用 iOS 风格的滑块而不是 WPF 的 CheckBox，更贴近 Mac 版观感。
/// </summary>
public sealed class TaToggleRow : Border
{
    private readonly Border _track;
    private readonly Border _thumb;
    private readonly TextBlock _label;

    /// <summary>开关状态变化。</summary>
    public event EventHandler? Toggled;

    /// <summary>建一个开关行。</summary>
    /// <param name="title">标题。</param>
    /// <param name="detail">可选的一行说明。</param>
    /// <param name="isOn">初始状态。</param>
    /// <param name="detailWidth">说明文字宽度（防止长文本挤压开关）。</param>
    public TaToggleRow(string title, string? detail = null, bool isOn = false, double detailWidth = 0)
    {
        Background = Brushes.Transparent;
        Padding = new Thickness(0, 3, 0, 3);
        Cursor = Cursors.Hand;
        Margin = new Thickness(0);

        _label = TaPrimitives.Text(title, Brand.TaTypography.Callout, Brand.TaBrushes.Ink);
        var left = new StackPanel { Orientation = Orientation.Vertical };
        left.Children.Add(_label);
        if (detail is not null)
        {
            var detailBlock = TaPrimitives.Text(
                detail,
                Brand.TaTypography.Caption2,
                Brand.TaBrushes.MutedInk,
                wrap: true);
            detailBlock.Margin = new Thickness(0, 2, 0, 0);
            if (detailWidth > 0)
            {
                detailBlock.Width = detailWidth;
            }

            left.Children.Add(detailBlock);
        }

        _track = new Border
        {
            Width = 38,
            Height = 21,
            CornerRadius = new CornerRadius(11),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        _thumb = new Border
        {
            Width = 15,
            Height = 15,
            CornerRadius = new CornerRadius(8),
            Background = Brand.TaBrushes.Paper,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(3, 0, 0, 0),
        };

        _track.Child = _thumb;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);
        Grid.SetColumn(_track, 1);
        grid.Children.Add(_track);
        Child = grid;

        MouseLeftButtonUp += (_, _) => Toggle();
        _track.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Toggle();
        };

        IsOn = isOn;
        ApplyVisual();
    }

    /// <summary>当前状态。</summary>
    public bool IsOn { get; private set; }

    /// <summary>设置状态（不触发事件）。</summary>
    public void SetSilently(bool value)
    {
        IsOn = value;
        ApplyVisual();
    }

    /// <summary>切换状态。</summary>
    public void Toggle()
    {
        IsOn = !IsOn;
        ApplyVisual();
        Toggled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>是否可交互。</summary>
    public new bool IsEnabled
    {
        get => base.IsEnabled;
        set
        {
            base.IsEnabled = value;
            Opacity = value ? 1 : 0.45;
            Cursor = value ? Cursors.Hand : Cursors.Arrow;
        }
    }

    private void ApplyVisual()
    {
        _track.Background = IsOn ? Brand.TaBrushes.Cinnabar : Brand.TaBrushes.Ink60;
        _thumb.HorizontalAlignment = IsOn ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        _thumb.Margin = IsOn ? new Thickness(0, 0, 3, 0) : new Thickness(3, 0, 0, 0);
    }
}

/// <summary>
/// 可点击的选择行。用于「选择 AI 服务商」列表（ModelSettingsView.swift:246-289）：
/// 圆角 9 + 朱砂 7% 选中底 / paper 42% 未选中底，选中时描边加粗到 1.5。
/// </summary>
public sealed class TaSelectableRow : Border
{
    /// <summary>行被点击。</summary>
    public event EventHandler? Clicked;

    /// <summary>建一个选择行。</summary>
    public TaSelectableRow(FrameworkElement content, bool isSelected)
    {
        Background = isSelected ? Brand.TaBrushes.Cinnabar07 : Brand.TaBrushes.Paper42;
        BorderBrush = isSelected ? Brand.TaBrushes.Cinnabar72 : Brand.TaBrushes.Hairline;
        BorderThickness = new Thickness(isSelected ? 1.5 : 1);
        CornerRadius = new CornerRadius(9);
        Padding = new Thickness(11, 0, 11, 0);
        MinHeight = 40;
        Child = content;
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
        MouseLeftButtonUp += (_, _) => Clicked?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// 全局快捷键录制按钮。对应 macOS <c>HotKeyRecorderView</c> /
/// <c>HotKeyRecorderButton</c>（HotKeyRecorderView.swift:47-165）。
///
/// 行为照抄 Mac 版：
/// - 常态显示当前快捷键文本，tooltip「可设置单键或组合键；按 Escape 取消」；
/// - 点击进入录制，显示「请按新快捷键…」，tooltip「按 Escape 取消录制」；
/// - Escape / 右键取消录制，恢复原值；
/// - 识别不了的按键显示「无法识别这个按键」。
/// </summary>
public sealed class TaHotKeyRecorder : Border
{
    private readonly TextBlock _label;
    private bool _isRecording;

    /// <summary>录到新快捷键。</summary>
    public event EventHandler<Dom.HotKeyShortcut>? ShortcutChanged;

    /// <summary>建一个录制按钮。</summary>
    /// <param name="shortcut">初始快捷键。</param>
    /// <param name="minWidth">最小宽度。</param>
    public TaHotKeyRecorder(Dom.HotKeyShortcut shortcut, double minWidth = 126)
    {
        _shortcut = shortcut;

        Background = Brand.TaBrushes.ElevatedPaper;
        BorderBrush = Brand.TaBrushes.Hairline;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(12, 6, 12, 6);
        MinWidth = minWidth;
        MinHeight = 28;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        Cursor = Cursors.Hand;
        Focusable = true;

        _label = TaPrimitives.Text(
            shortcut.DisplayText,
            Brand.TaTypography.Caption,
            Brand.TaBrushes.Ink,
            FontWeights.Medium,
            mono: true);
        Child = _label;
        ToolTip = "可设置单键或组合键；按 Escape 取消";

        MouseLeftButtonUp += (_, _) =>
        {
            StartRecording();
            Focus();
            Keyboard.Focus(this);
        };

        PreviewKeyDown += OnPreviewKeyDown;
        LostFocus += (_, _) => CancelRecording();
    }

    private Dom.HotKeyShortcut _shortcut;

    /// <summary>当前快捷键。</summary>
    public Dom.HotKeyShortcut Shortcut
    {
        get => _shortcut;
        set
        {
            if (_isRecording)
            {
                return;
            }

            _shortcut = value;
            _label.Text = value.DisplayText;
        }
    }

    /// <summary>进入录制态。</summary>
    public void StartRecording()
    {
        _isRecording = true;
        _label.Text = "请按新快捷键…";
        ToolTip = "按 Escape 取消录制";
        BorderBrush = Brand.TaBrushes.Cinnabar;
        BorderThickness = new Thickness(1.5);
    }

    /// <summary>取消录制并恢复显示。</summary>
    public void CancelRecording()
    {
        if (!_isRecording)
        {
            return;
        }

        _isRecording = false;
        _label.Text = _shortcut.DisplayText;
        ToolTip = "可设置单键或组合键；按 Escape 取消";
        BorderBrush = Brand.TaBrushes.Hairline;
        BorderThickness = new Thickness(1);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isRecording)
        {
            return;
        }

        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            CancelRecording();
            return;
        }

        var modifiers = Dom.HotKeyModifiers.None;
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            modifiers |= Dom.HotKeyModifiers.Control;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            modifiers |= Dom.HotKeyModifiers.Shift;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
        {
            modifiers |= Dom.HotKeyModifiers.Alt;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Windows) == ModifierKeys.Windows)
        {
            modifiers |= Dom.HotKeyModifiers.Win;
        }

        // 单独按修饰键不算组合键，继续等
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System)
        {
            return;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(e.Key);
        var label = KeyLabel(virtualKey);
        if (label.Length == 0)
        {
            _label.Text = "无法识别这个按键";
            return;
        }

        var shortcut = new Dom.HotKeyShortcut((ushort)virtualKey, modifiers, label);
        _shortcut = shortcut;
        _isRecording = false;
        _label.Text = shortcut.DisplayText;
        ToolTip = "可设置单键或组合键；按 Escape 取消";
        BorderBrush = Brand.TaBrushes.Hairline;
        BorderThickness = new Thickness(1);
        ShortcutChanged?.Invoke(this, shortcut);
    }

    private static string KeyLabel(int virtualKey)
    {
        if (virtualKey is >= 0x30 and <= 0x39)
        {
            return ((char)('0' + (virtualKey - 0x30))).ToString();
        }

        if (virtualKey is >= 0x41 and <= 0x5A)
        {
            return ((char)('A' + (virtualKey - 0x41))).ToString();
        }

        if (virtualKey is >= 0x70 and <= 0x7B)
        {
            return "F" + (virtualKey - 0x70 + 1);
        }

        return virtualKey switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "Page Up",
            0x22 => "Page Down",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "←",
            0x26 => "↑",
            0x27 => "→",
            _ => string.Empty,
        };
    }
}
