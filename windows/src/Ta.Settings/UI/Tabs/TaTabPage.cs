using System.Windows.Controls;
using Ta.Settings.Services;

namespace Ta.Settings.UI.Tabs;

/// <summary>
/// 标签页基类。对应 macOS 里 <c>SettingsView</c> 的
/// <c>Group { switch selectedTab { ... } }</c>（SettingsView.swift:24-34）
/// —— 每个分支都是一个独立的 private struct。这里一个标签页一个文件，结构对齐。
/// </summary>
public abstract class TaTabPage : UserControl
{
    /// <summary>构造。</summary>
    protected TaTabPage(TaServices services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Padding = new System.Windows.Thickness(0);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Content = Build();
    }

    /// <summary>服务容器。</summary>
    protected TaServices Services { get; }

    /// <summary>构建设置内容。</summary>
    protected abstract FrameworkElement Build();

    /// <summary>每次切到这个标签页时调用（对应 SwiftUI 的 <c>onAppear</c>）。</summary>
    public virtual void Refresh()
    {
    }

    /// <summary>
    /// 把内容包进滚动区，间距 13。对应各页的
    /// <c>ScrollView { VStack(spacing: 13) { ... }.padding(.horizontal, 4).padding(.bottom, 8) }</c>
    /// （TranslationSettingsView.swift:22-33）。
    /// </summary>
    protected ScrollViewer Page(params FrameworkElement[] children)
    {
        var stack = TaLayout.V(13, children);
        return TaPrimitives.Scroll(stack, new System.Windows.Thickness(4, 0, 4, 8));
    }
}
