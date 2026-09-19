using Ta.Shell.Models;
using Ta.HotKeys;

namespace Ta.Shell.UI;

/// <summary>
/// 菜单栏 popover 的内容模型。
///
/// 对应 Mac 版 <c>MenuBarContentView.swift</c>（SwiftUI 视图）。
/// Mac 版是声明式视图；Windows 这里拆成「纯数据模型 + 自绘面板」两半：
/// 模型（本文件）可单测，绘制（<c>MenuBarController</c>）只管像素。
///
/// popover 规格（移植参考文档 §5.8）：**326×574**，<c>.transient</c>，<c>animates = true</c>。
/// </summary>

/// <summary>popover 的一个截图入口。</summary>
public sealed record MenuActionEntry
{
    public MenuActionEntry(
        CaptureMode mode,
        string title,
        string subtitle,
        string glyph,
        string shortcut,
        bool emphasized = false)
    {
        Mode = mode;
        Title = title;
        Subtitle = subtitle;
        Glyph = glyph;
        Shortcut = shortcut;
        Emphasized = emphasized;
    }

    /// <summary>点击后要启动的截图模式。</summary>
    public CaptureMode Mode { get; }

    public string Title { get; }

    public string Subtitle { get; }

    /// <summary>
    /// 图标字形。
    /// Mac 版用 SF Symbol（MenuBarContentView.swift:46 等）；
    /// Windows 用等价语义的 Unicode 字形，保持「标题/副标题/符号」三要素。
    /// </summary>
    public string Glyph { get; }

    /// <summary>键面文本，如 <c>Ctrl+Alt+Shift+2</c>。</summary>
    public string Shortcut { get; }

    /// <summary>是否为强调项（「开始拓取」用墨色实底，其余用朱砂悬停）。</summary>
    public bool Emphasized { get; }
}

/// <summary>popover 底部链接。</summary>
public sealed record MenuFooterAction
{
    public MenuFooterAction(string id, string label)
    {
        Id = id;
        Label = label;
    }

    public string Id { get; }
    public string Label { get; }
}

/// <summary>popover 的完整内容。</summary>
public sealed record MenuBarContent
{
    /// <summary>状态文本，如 <c>本地识别就绪</c>。对应 appModel.statusText。</summary>
    public string StatusText { get; init; } = "本地识别就绪";

    /// <summary>六个截图入口。</summary>
    public IReadOnlyList<MenuActionEntry> Entries { get; init; } = Array.Empty<MenuActionEntry>();

    /// <summary>钉图管理区的链接。</summary>
    public IReadOnlyList<MenuFooterAction> PinActions { get; init; } = Array.Empty<MenuFooterAction>();

    /// <summary>版本号。</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>当前悬停的入口序号；-1 表示无。</summary>
    public int HoveredIndex { get; init; } = -1;

    /// <summary>「本地优先」徽章文案。Mac 版恒显示（MenuBarContentView.swift:25）。</summary>
    public string LocalFirstBadge { get; init; } = "本地优先";
}

/// <summary>popover 上的一个可点链接（钉图管理区或底栏）。</summary>
public sealed record MenuBarLink
{
    public MenuBarLink(string id, string label, MenuBarLinkSection kind, RectangleF bounds)
    {
        Id = id;
        Label = label;
        Kind = kind;
        Bounds = bounds;
    }

    public string Id { get; }
    public string Label { get; }

    /// <summary>属于哪个区块 —— 决定点击后分发给哪个事件。</summary>
    public MenuBarLinkSection Kind { get; }

    /// <summary>命中矩形（popover 客户区坐标）。</summary>
    public RectangleF Bounds { get; }
}

public enum MenuBarLinkSection
{
    /// <summary>钉图管理区。点击后派发 <c>PinActionRequested</c>。</summary>
    Pin,

    /// <summary>底栏。点击后派发 <c>FooterActionRequested</c>。</summary>
    Footer,
}

/// <summary>popover 的度量与内容构建。对应 Mac 版 MenuBarContentView 的布局常量。</summary>
public static class MenuBarLayout
{
    /// <summary>popover 宽度。Mac 版 contentSize.width = 326（MenuBarController.swift:34）。</summary>
    public const double Width = 326;

    /// <summary>popover 高度。Mac 版 contentSize.height = 574。</summary>
    public const double Height = 574;

    /// <summary>顶部信息区高度。对应 .padding(14) + 图标 38 + 两行文字。</summary>
    public const double HeaderHeight = 66;

    /// <summary>应用图标尺寸。对应 TaAppIcon(size: 38)（MenuBarContentView.swift:15）。</summary>
    public const double AppIconSize = 38;

    /// <summary>操作按钮高度。对应 .padding(.vertical, 8) + 两行文字 + 图标 22。</summary>
    public const double EntryHeight = 60;

    /// <summary>入口之间的间距。对应 VStack(spacing: 5)（:37）。</summary>
    public const double EntrySpacing = 5;

    /// <summary>入口列表区上下内边距。对应 .padding(6)（:93）。</summary>
    public const double EntryListPadding = 6;

    /// <summary>入口图标宽度。对应 .frame(width: 22)（:179）。</summary>
    public const double EntryGlyphWidth = 22;

    /// <summary>入口图标字号。对应 .font(.system(size: 16))（:178）。</summary>
    public const double EntryGlyphFontSize = 16;

    /// <summary>入口标题字号。</summary>
    public const double EntryTitleFontSize = 13;

    /// <summary>入口副标题字号。对应 .font(.caption)。</summary>
    public const double EntrySubtitleFontSize = 11;

    /// <summary>键面字号。对应 .font(.system(.caption, design: .rounded))（:190）。</summary>
    public const double ShortcutFontSize = 11;

    /// <summary>入口圆角。对应 RoundedRectangle(cornerRadius: 10)（:200）。</summary>
    public const double EntryCornerRadius = 10;

    /// <summary>入口水平内边距。对应 .padding(.horizontal, 10)（:194）。</summary>
    public const double EntryHorizontalPadding = 10;

    /// <summary>钉图管理区高度。</summary>
    public const double PinSectionHeight = 84;

    /// <summary>底栏高度。对应 .padding(12)（:147）。</summary>
    public const double FooterHeight = 46;

    /// <summary>分隔线高度。</summary>
    public const double DividerHeight = 1;

    /// <summary>入口列表起始 Y（头部之后）。</summary>
    public static double EntryListTop => HeaderHeight + DividerHeight + EntryListPadding;

    /// <summary>钉图管理区起始 Y。</summary>
    public static double PinSectionTop(int entryCount) =>
        EntryListTop + (entryCount * EntryHeight) + ((entryCount - 1) * EntrySpacing)
        + EntryListPadding + DividerHeight;

    /// <summary>底栏起始 Y。</summary>
    public static double FooterTop(int entryCount) =>
        PinSectionTop(entryCount) + PinSectionHeight + DividerHeight;

    /// <summary>
    /// 某个序号的入口矩形。
    /// </summary>
    public static RectangleF EntryRect(int index) => new(
        (float)EntryListPadding,
        (float)(EntryListTop + (index * (EntryHeight + EntrySpacing))),
        (float)(Width - (EntryListPadding * 2)),
        (float)EntryHeight);

    /// <summary>命中测试：某点落在哪个入口上；不在任何入口上返回 -1。</summary>
    public static int HitTestEntry(int entryCount, System.Drawing.Point clientPoint)
    {
        for (var i = 0; i < entryCount; i++)
        {
            if (EntryRect(i).Contains(clientPoint))
            {
                return i;
            }
        }

        return -1;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 链接区（钉图管理 + 底栏）的布局与命中测试
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>链接行高。与 DrawPinSection / DrawFooter 的排版一致。</summary>
    public const double LinkLineHeight = 16;

    /// <summary>链接字号。</summary>
    public const double LinkFontSize = 11;

    /// <summary>链接之间追加一点命中余量，小目标太难点中。</summary>
    public const double LinkHitPadding = 5;

    /// <summary>底栏链接字号。</summary>
    public const double FooterLinkFontSize = 11;

    /// <summary>
    /// 算出全部链接的命中矩形。
    ///
    /// 布局刻意与绘制（<c>TrayIconHost.DrawPinSection</c> / <c>DrawFooter</c>）
    /// <b>用同一套常量</b>推出来，而不是在绘制里记一份、在命中测试里再记一份 ——
    /// 两份布局漂移是这类自绘面板最常见的 bug（用户点到「隐藏全部」却触发了「显示全部」）。
    /// </summary>
    /// <param name="measure">测宽函数：文本 → 像素宽。</param>
    /// <param name="entryCount">入口数量，决定分区纵向位置。</param>
    public static IReadOnlyList<MenuBarLink> BuildLinks(
        Func<string, double, double> measure,
        int entryCount)
    {
        ArgumentNullException.ThrowIfNull(measure);

        var links = new List<MenuBarLink>();

        // ── 钉图管理区：两行，行内间距 10（:101-110）────────────────────────
        var pinTop = PinSectionTop(entryCount) + 30;
        var x = 14f;
        var row = 0;

        foreach (var action in BuildPinActions())
        {
            var width = measure(action.Label, LinkFontSize);

            if (x + width + 18 > Width - 14)
            {
                row++;
                x = 14f;
            }

            var top = (float)(pinTop + (row * LinkLineHeight));
            var pad = (float)LinkHitPadding;

            links.Add(new MenuBarLink(action.Id, action.Label, MenuBarLinkSection.Pin,
                new RectangleF((float)x - pad, top - pad,
                    (float)width + (pad * 2),
                    (float)LinkLineHeight + (pad * 2))));

            x += (float)width + 10;
        }

        // ── 底栏：打开主界面 | 设置 …… 退出（:118-146）────────────────────
        var footerTop = (float)(FooterTop(entryCount) + 16);
        var fx = 12f;
        var footerPad = (float)LinkHitPadding;

        foreach (var label in new[] { "打开主界面", "设置" })
        {
            var width = measure(label, FooterLinkFontSize);
            links.Add(new MenuBarLink(
                label == "设置" ? "settings" : "open-main",
                label, MenuBarLinkSection.Footer,
                new RectangleF(fx - footerPad, footerTop - footerPad,
                    (float)width + (footerPad * 2),
                    (float)LinkLineHeight + (footerPad * 2))));

            fx += (float)width + 12;
        }

        var quitWidth = measure("退出", FooterLinkFontSize);
        links.Add(new MenuBarLink("quit", "退出", MenuBarLinkSection.Footer,
            new RectangleF((float)Width - 40 - footerPad, footerTop - footerPad,
                (float)quitWidth + (footerPad * 2),
                (float)LinkLineHeight + (footerPad * 2))));

        return links;
    }

    /// <summary>命中测试：某点落在哪个链接上；没有则返回 null。</summary>
    public static MenuBarLink? HitTestLink(System.Drawing.Point clientPoint) =>
        BuildLinks(MeasureWithGdi, BuildEntries().Count)
            .FirstOrDefault(link => link.Bounds.Contains(clientPoint));

    /// <summary>GDI 测宽。与 TrayIconHost 的绘制用同一字体族，保证布局一致。</summary>
    private static double MeasureWithGdi(string text, double pointSize)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        using var font = new System.Drawing.Font("Microsoft YaHei", (float)pointSize,
            System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);

        using var scratch = new System.Drawing.Bitmap(1, 1);
        using var g = System.Drawing.Graphics.FromImage(scratch);

        return System.Windows.Forms.TextRenderer.MeasureText(
            g, text, font, new System.Drawing.Size(int.MaxValue, int.MaxValue),
            System.Windows.Forms.TextFormatFlags.NoPadding).Width;
    }

    /// <summary>
    /// 六个截图入口。标题、副标题、符号、快捷键一一对应
    /// Mac 版 <c>MenuBarContentView.swift:37-92</c>。
    ///
    /// ⚠️ 副标题采用任务书给出的文案。与 Mac 版有一处刻意差异：
    ///    Mac 版「长截图」的副标题是「适用于浏览器、微信和 AI 对话」，
    ///    而 §4.1 的模式表与任务书都用「从起点框到滚动区域底部」。
    ///    后者与 <see cref="ModeActionMapper.StatusTextFor"/> 的提示语一致，
    ///    用户在托盘菜单里看到的就是框选时显示的那句话，因此取后者。
    /// </summary>
    public static IReadOnlyList<MenuActionEntry> BuildEntries(
        IReadOnlyDictionary<GlobalHotKeyAction, HotKeyShortcut>? shortcuts = null)
    {
        string Shortcut(GlobalHotKeyAction action) =>
            (shortcuts is not null && shortcuts.TryGetValue(action, out var value)
                ? value.DisplayText
                : action.DefaultShortcut().DisplayText);

        return new[]
        {
            new MenuActionEntry(CaptureMode.Interactive, "开始拓取",
                "截图后选择取字、复制、钉图或编辑", "⌖",
                Shortcut(GlobalHotKeyAction.InteractiveCapture), emphasized: true),

            new MenuActionEntry(CaptureMode.Intelligent, "极速识别内容",
                "按设置选择 OCR 或多模态", "⌸",
                Shortcut(GlobalHotKeyAction.IntelligentCapture)),

            new MenuActionEntry(CaptureMode.Translation, "截图翻译",
                "识别、翻译并复制文字", "文",
                Shortcut(GlobalHotKeyAction.TranslationCapture)),

            new MenuActionEntry(CaptureMode.Image, "截图图片",
                "始终复制原图", "▧",
                Shortcut(GlobalHotKeyAction.ImageCapture)),

            new MenuActionEntry(CaptureMode.Pin, "截图并钉住",
                "保持在其他窗口上方", "◈",
                Shortcut(GlobalHotKeyAction.PinCapture)),

            new MenuActionEntry(CaptureMode.Long, "滚动长截图",
                "从起点框到滚动区域底部", "⇕",
                Shortcut(GlobalHotKeyAction.LongCapture)),
        };
    }

    /// <summary>钉图管理区链接。对应 MenuBarContentView.swift:101-110。</summary>
    public static IReadOnlyList<MenuFooterAction> BuildPinActions() => new[]
    {
        new MenuFooterAction("pin-clipboard", "钉剪贴板"),
        new MenuFooterAction("hide-all", "隐藏全部"),
        new MenuFooterAction("show-all", "显示全部"),
        new MenuFooterAction("restore-interaction", "恢复穿透"),
        new MenuFooterAction("restore-last", "恢复关闭"),
    };

    /// <summary>底栏动作。对应 MenuBarContentView.swift:118-146。</summary>
    public static IReadOnlyList<MenuFooterAction> BuildFooterActions() => new[]
    {
        new MenuFooterAction("open-main", "打开主界面"),
        new MenuFooterAction("settings", "设置"),
        new MenuFooterAction("quit", "退出"),
    };
}
