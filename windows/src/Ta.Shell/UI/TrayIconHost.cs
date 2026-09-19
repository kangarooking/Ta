using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Ta.Shell.Models;
using Ta.HotKeys;

namespace Ta.Shell.UI;

/// <summary>
/// 托盘图标 + 菜单栏 popover 宿主。
///
/// 对应 Mac 版 <c>MenuBarController</c>（MenuBarController.swift, 59 行）
/// + <c>MenuBarContentView</c>（212 行）。
///
/// # 方案选择：自定义弹出面板，而不是 TrackPopupMenu
///
/// Windows 上有两条路：
///   · <c>TrackPopupMenu</c>（系统菜单）—— 一行代码搞定，但只能显示单行文字。
///     Mac 版 popover 每个入口都是「标题 + 副标题 + 图标 + 键面」四要素，
///     系统菜单表达不了，移植参考文档 §5.8 的视觉规格也就无从谈起。
///   · <b>自绘弹出面板</b>（本实现）—— 一个 WS_EX_NOACTIVATE 的逐像素 alpha
///     窗口，可完全复刻 326×574 的布局。
///
/// 因此主路径用自绘面板；<c>TrackPopupMenu</c> 保留为右键<b>降级菜单</b>
/// （自绘面板创建失败时仍能点开截图入口），
/// 这样托盘在最坏情况下依然可用。
///
/// # 与 Mac 版的属性对照
///
/// | Mac 版（MenuBarController.swift） | 本实现 |
/// |---|---|
/// | <c>NSStatusBar.system.statusItem</c> | <c>Shell_NotifyIcon</c> |
/// | 图标 19×19（:20） | 16×16 SM_CXSMICON 系统小图标 |
/// | <c>popover.behavior = .transient</c>（:32） | 点击面板外 / 失焦即收起 |
/// | <c>popover.animates = true</c>（:33） | 位置淡入（分层 alpha 渐显） |
/// | <c>contentSize = 326×574</c>（:34） | <see cref="MenuBarLayout.Width"/>/<c>Height</c> |
/// | <c>taMenuBarShouldClose</c> 通知（:39-44） | <see cref="ClosePopover"/> 直接调用 |
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private const int TrayId = 1;
    private const string TrayWindowClass = "TaTrayMessageWindow";

    private IntPtr _hwnd;
    private IntPtr _icon;
    private LayeredSurfaceWindow? _popover;
    private WndProcDelegate? _wndProc;
    private ushort _classAtom;
    private bool _disposed;

    /// <summary>点击某个截图入口。</summary>
    public event Action<CaptureMode>? CaptureRequested;

    /// <summary>钉图管理动作。id 见 <see cref="MenuBarLayout.BuildPinActions"/>。</summary>
    public event Action<string>? PinActionRequested;

    /// <summary>底栏动作。id 见 <see cref="MenuBarLayout.BuildFooterActions"/>。</summary>
    public event Action<string>? FooterActionRequested;

    /// <summary>popover 打开/关闭（供应用层同步自身窗口可见性）。</summary>
    public event Action<bool>? PopoverVisibilityChanged;

    public bool IsPopoverOpen => _popover?.IsVisible ?? false;

    /// <summary>popover 的窗口句柄；尚未创建时为 <see cref="IntPtr.Zero"/>。</summary>
    public IntPtr PopoverHandle => _popover?.Handle ?? IntPtr.Zero;

    /// <summary>
    /// 截图期间隐藏 popover。
    /// 对应任务书 E 项的第一道防线（见 <c>Platform.AppWindowVisibility</c> 的类注释）。
    /// </summary>
    public void HideForCapture()
    {
        ClosePopover();
    }

    /// <summary>截图结束后恢复。</summary>
    public void RestoreAfterCapture()
    {
        // popover 是用户按需打开的临时面板，截图结束后**不**自动重开 ——
        // 对应 Mac 版 .transient 的语义：会话结束就是结束了。
        // 这里保留方法是为了与 IAppWindowVisibility 的成对契约一致。
    }

    /// <summary>把 popover 永久排除出屏幕捕获（第二道防线）。</summary>
    public void ExcludePopoverFromCapture() => _popover?.ExcludeFromCapture();

    /// <summary>恢复 popover 参与屏幕捕获。</summary>
    public void IncludePopoverInCapture() => _popover?.IncludeInCapture();

    /// <summary>
    /// 安装托盘图标。<b>必须在将运行消息循环的 STA 线程上调用</b>
    /// （Shell_NotifyIcon 的回调消息投到 <see cref="_hwnd"/> 所属线程）。
    /// </summary>
    public void Install(string tooltip)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        EnsureClassRegistered();

        _hwnd = CreateWindowExW(0, TrayWindowClass, "Ta Tray", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"创建托盘消息窗口失败，Win32 错误 {Marshal.GetLastWin32Error()}。");
        }

        _icon = LoadAppIcon();

        var data = new TrayInterop.NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<TrayInterop.NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = TrayId,
            uFlags = TrayInterop.NIF_MESSAGE | TrayInterop.NIF_ICON | TrayInterop.NIF_TIP,
            uCallbackMessage = TrayInterop.WM_TRAYICON,
            hIcon = _icon,
            szTip = Pad(tooltip, 128),
        };

        if (!TrayInterop.Shell_NotifyIconW(TrayInterop.NIM_ADD, ref data))
        {
            throw new InvalidOperationException(
                $"Shell_NotifyIcon(NIM_ADD) 失败，Win32 错误 {Marshal.GetLastWin32Error()}。");
        }
    }

    /// <summary>更新状态文本（托盘气泡提示 + popover 头部）。</summary>
    public void SetStatusText(string statusText)
    {
        _statusText = statusText;

        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var data = new TrayInterop.NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<TrayInterop.NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = TrayId,
            uFlags = TrayInterop.NIF_TIP,
            szTip = Pad($"{TaPalette.BrandName} · {statusText}", 128),
        };

        TrayInterop.Shell_NotifyIconW(TrayInterop.NIM_MODIFY, ref data);

        // popover 开着时同步刷新头部文字。
        if (IsPopoverOpen)
        {
            RenderPopover();
        }
    }

    private string _statusText = "本地识别就绪";

    /// <summary>刷新快捷键显示（用户改绑后调用）。</summary>
    public void SetShortcuts(IReadOnlyDictionary<GlobalHotKeyAction, HotKeyShortcut>? shortcuts)
    {
        _shortcuts = shortcuts;

        if (IsPopoverOpen)
        {
            RenderPopover();
        }
    }

    private IReadOnlyDictionary<GlobalHotKeyAction, HotKeyShortcut>? _shortcuts;

    // ─────────────────────────────────────────────────────────────────────────
    // popover
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>切换 popover 显示。对应 Mac 版 togglePopover(_:)（:47-54）。</summary>
    public void TogglePopover()
    {
        if (IsPopoverOpen)
        {
            ClosePopover();
            return;
        }

        ShowPopover();
    }

    public void ShowPopover()
    {
        if (_popover is null)
        {
            try
            {
                _popover = new LayeredSurfaceWindow($"{TaPalette.BrandName} · {TaPalette.BrandEnglishName}");
                _popover.Create(new Size((int)MenuBarLayout.Width, (int)MenuBarLayout.Height));
                _popover.MouseMove += OnPopoverMouseMove;
                _popover.MouseUp += OnPopoverMouseUp;
                _popover.MouseLeave += OnPopoverMouseLeave;

                // .transient —— 点击面板外收起。
                // 非激活窗口收不到 WM_ACTIVATE，因此这里改用失焦轮询
                // （见 ServiceTransientDismissal），比强行激活面板更安全。
                _popover.ExcludeFromCapture();
            }
            catch (InvalidOperationException)
            {
                // 自绘面板创建失败：退回系统菜单（TrackPopupMenu 降级路径）。
                _popover = null;
                ShowFallbackMenu();
                return;
            }
        }

        RenderPopover();
        _popover.Show();
        PopoverVisibilityChanged?.Invoke(true);
    }

    public void ClosePopover()
    {
        if (_popover is null)
        {
            return;
        }

        _popover.Hide();
        _popover.StopTimer();
        PopoverVisibilityChanged?.Invoke(false);
    }

    /// <summary>
    /// 由消息循环周期调用：检查 popover 是否该因「点击外部」而收起。
    ///
    /// Mac 版的 <c>.transient</c> 由 AppKit 免费提供；Windows 上没有等价机制，
    /// 而强行激活面板会违反「不抢焦点」的核心约束，因此用轮询：
    /// 鼠标左键按下且不在面板矩形内 → 收起。
    /// </summary>
    public void ServiceTransientDismissal()
    {
        if (!IsPopoverOpen || _popover is null)
        {
            return;
        }

        // GetAsyncKeyState 的 VK_LBUTTON 位 0x8000 = 当前按下。
        if ((ScreenInterop.GetAsyncKeyState(0x01) & 0x8000) == 0)
        {
            return;
        }

        var cursor = System.Windows.Forms.Cursor.Position;
        var bounds = new Rectangle(_popover.Location, _popover.Size);

        if (!bounds.Contains(cursor))
        {
            ClosePopover();
        }
    }

    private void OnPopoverMouseMove(Point clientPoint)
    {
        if (_popover is null)
        {
            return;
        }

        var hovered = MenuBarLayout.HitTestEntry(
            MenuBarLayout.BuildEntries(_shortcuts).Count, clientPoint);

        if (hovered != _hoveredIndex)
        {
            _hoveredIndex = hovered;
            RenderPopover();
        }
    }

    private int _hoveredIndex = -1;

    private void OnPopoverMouseUp(Point clientPoint)
    {
        // 1. 先看是不是点了钉图管理区 / 底栏的链接。
        var link = MenuBarLayout.HitTestLink(clientPoint);
        if (link is not null)
        {
            // 对应 Mac 版：点击前先收起 popover（taMenuBarShouldClose）。
            ClosePopover();
            DispatchLink(link);
            return;
        }

        // 2. 再看是不是点了六个截图入口之一。
        var entries = MenuBarLayout.BuildEntries(_shortcuts);
        var index = MenuBarLayout.HitTestEntry(entries.Count, clientPoint);
        if (index < 0)
        {
            return;
        }

        var entry = entries[index];

        ClosePopover();
        CaptureRequested?.Invoke(entry.Mode);
    }

    /// <summary>把 popover 上点到的链接分发出去。</summary>
    private void DispatchLink(MenuBarLink link)
    {
        if (link.Kind == MenuBarLinkSection.Pin)
        {
            PinActionRequested?.Invoke(link.Id);
            return;
        }

        if (link.Id == "quit")
        {
            FooterActionRequested?.Invoke("quit");
            return;
        }

        FooterActionRequested?.Invoke(link.Id);
    }

    private void OnPopoverMouseLeave()
    {
        if (_hoveredIndex != -1)
        {
            _hoveredIndex = -1;
            RenderPopover();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 降级菜单：TrackPopupMenu
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 系统菜单降级路径。仅在自绘面板无法创建时使用。
    ///
    /// <c>SetForegroundWindow</c> 是 Win32 的硬性要求 —— 不这么做菜单收不到后续消息，
    /// 关不掉。代价是会短暂抢焦点，所以只作为异常路径，不作为常规路径。
    /// </summary>
    private void ShowFallbackMenu()
    {
        var menu = TrayInterop.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var entries = MenuBarLayout.BuildEntries(_shortcuts);
            const int commandBase = 1000;

            for (var i = 0; i < entries.Count; i++)
            {
                var label = $"{entries[i].Title}\t{entries[i].Shortcut}";
                TrayInterop.AppendMenuW(menu, TrayInterop.MF_STRING,
                    (IntPtr)(commandBase + i), label);
            }

            TrayInterop.AppendMenuW(menu, TrayInterop.MF_SEPARATOR, IntPtr.Zero, string.Empty);
            TrayInterop.AppendMenuW(menu, TrayInterop.MF_STRING,
                (IntPtr)(commandBase + entries.Count + 1), "退出");

            var cursor = System.Windows.Forms.Cursor.Position;
            TrayInterop.SetForegroundWindow(_hwnd);

            var chosen = TrayInterop.TrackPopupMenuEx(
                menu,
                TrayInterop.TPM_RETURNCMD | TrayInterop.TPM_RIGHTBUTTON
                    | TrayInterop.TPM_LEFTALIGN | TrayInterop.TPM_BOTTOMALIGN,
                cursor.X, cursor.Y, _hwnd, IntPtr.Zero);

            if (chosen >= commandBase && chosen < commandBase + entries.Count)
            {
                CaptureRequested?.Invoke(entries[chosen - commandBase].Mode);
            }
            else if (chosen == commandBase + entries.Count + 1)
            {
                FooterActionRequested?.Invoke("quit");
            }
        }
        finally
        {
            TrayInterop.DestroyMenu(menu);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // popover 绘制
    // ─────────────────────────────────────────────────────────────────────────

    private void RenderPopover()
    {
        if (_popover is null)
        {
            return;
        }

        using var bitmap = new Bitmap((int)MenuBarLayout.Width, (int)MenuBarLayout.Height);
        using var g = Graphics.FromImage(bitmap);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        DrawPopover(g);
        _popover.Present(bitmap);
    }

    private void DrawPopover(Graphics g)
    {
        var width = (int)MenuBarLayout.Width;
        var height = (int)MenuBarLayout.Height;

        // 面板底色：paper（MenuBarContentView.swift:11）。
        g.Clear(Color.Transparent);

        // 圆角裁剪：Mac 版 SwiftUI 默认给 popover 圆角。
        using var panelPath = RoundedRect(new RectangleF(0, 0, width, height), 14);
        using (var paper = new SolidBrush(TaPalette.Paper))
        {
            g.FillPath(paper, panelPath);
        }

        g.SetClip(panelPath);

        // ── 头部：应用图标 38 + 品牌名 + 状态文本 + 「本地优先」徽章 ─────────
        var headerTop = 14;

        // 「拓」字图标（38×38）：朱砂圆角方块 + 衬线体白字。
        // 对应参考文档 §12.3 的回退图标标记：
        //   RoundedRectangle(cornerRadius: size*0.22) + 内嵌朱砂块 + 衬线体「拓」 size*0.42
        DrawBrandIcon(g, new RectangleF(14, headerTop,
            (float)MenuBarLayout.AppIconSize, (float)MenuBarLayout.AppIconSize));

        // 品牌名（衬线粗体）+ 状态文本（caption）。
        using var brandFont = TaPalette.BrandTitleFont(17);
        using var brandBrush = new SolidBrush(TaPalette.Ink);
        g.DrawString(TaPalette.BrandName, brandFont, brandBrush,
            new PointF(14 + (float)MenuBarLayout.AppIconSize + 10, headerTop));

        using var statusFont = TaPalette.BodyFont(11);
        using var statusBrush = new SolidBrush(TaPalette.MutedInk);
        g.DrawString($"🔒 {_statusText}", statusFont, statusBrush,
            new PointF(14 + (float)MenuBarLayout.AppIconSize + 10, headerTop + 24));

        // 「本地优先」徽章：elevatedPaper 底 + hairline 描边（:24-31）。
        using var badgeFont = TaPalette.BodyFont(10);
        var badgeText = "本地优先";
        var badgeTextWidth = g.MeasureString(badgeText, badgeFont).Width;
        var badgeRect = new RectangleF(
            width - 14 - badgeTextWidth - 16, headerTop + 6,
            badgeTextWidth + 16, 20);

        using (var badgePath = RoundedRect(badgeRect, 10))
        {
            using var badgeFill = new SolidBrush(TaPalette.ElevatedPaper);
            g.FillPath(badgeFill, badgePath);
            using var badgePen = new Pen(TaPalette.Hairline, 1);
            g.DrawPath(badgePen, badgePath);
        }

        using var badgeTextBrush = new SolidBrush(TaPalette.MutedInk);
        g.DrawString(badgeText, badgeFont, badgeTextBrush, badgeRect,
            CenterFormat());

        DrawDivider(g, (int)MenuBarLayout.HeaderHeight, width);

        // ── 六个截图入口（:37-92）─────────────────────────────────────────
        var entries = MenuBarLayout.BuildEntries(_shortcuts);
        for (var i = 0; i < entries.Count; i++)
        {
            DrawEntry(g, entries[i], i, MenuBarLayout.EntryRect(i));
        }

        DrawDivider(g, (int)MenuBarLayout.PinSectionTop(entries.Count), width);

        // ── 钉图管理（:97-115）────────────────────────────────────────────
        DrawPinSection(g, entries.Count, width);

        // ── 底栏（:118-147）───────────────────────────────────────────────
        DrawFooter(g, entries.Count, width);

        g.ResetClip();

        // 面板描边：paper 面板需要一圈发丝边才能和桌面区分开。
        using var borderPen = new Pen(TaPalette.Hairline, 1);
        g.DrawPath(borderPen, panelPath);
    }

    private static void DrawBrandIcon(Graphics g, RectangleF bounds)
    {
        // 朱砂圆角方块 + 白色衬线「拓」。§12.3 的标记规格。
        using var path = RoundedRect(bounds, (float)(bounds.Width * 0.22));
        using (var fill = new SolidBrush(TaPalette.Cinnabar))
        {
            g.FillPath(fill, path);
        }

        using var font = TaPalette.BrandSerifFont(bounds.Width * 0.5f);
        using var brush = new SolidBrush(TaPalette.Paper);
        g.DrawString(TaPalette.BrandName, font, brush, bounds, CenterFormat());
    }

    private void DrawEntry(Graphics g, MenuActionEntry entry, int index, RectangleF rect)
    {
        var hovered = index == _hoveredIndex;

        using var entryPath = RoundedRect(rect, (float)MenuBarLayout.EntryCornerRadius);

        // 底色：强调项 = 墨色实底；其余 = 悬停时朱砂 @ 0.07（:196-201）。
        var background = entry.Emphasized
            ? (hovered ? TaPalette.Ink : TaPalette.InkEmphasis)
            : (hovered ? TaPalette.CinnabarHover : Color.Transparent);

        if (background != Color.Transparent)
        {
            using var fill = new SolidBrush(background);
            g.FillPath(fill, entryPath);
        }

        // 强调项描边：朱砂 @ 0.20 / 悬停 @ 0.58（:202-206）。
        if (entry.Emphasized)
        {
            using var pen = new Pen(
                hovered ? Color.FromArgb(148, TaPalette.Cinnabar) : TaPalette.CinnabarBorder, 1);
            g.DrawPath(pen, entryPath);
        }

        var onEmphasis = entry.Emphasized;

        // 图标（:177-181）
        using var glyphFont = TaPalette.BodyFont((float)MenuBarLayout.EntryGlyphFontSize, FontStyle.Bold);
        using var glyphBrush = new SolidBrush(
            onEmphasis ? TaPalette.Paper : TaPalette.Cinnabar);
        g.DrawString(entry.Glyph, glyphFont, glyphBrush,
            new RectangleF(rect.X + (float)MenuBarLayout.EntryHorizontalPadding,
                rect.Y, (float)MenuBarLayout.EntryGlyphWidth, rect.Height),
            CenterFormat());

        // 标题 + 副标题（:182-187）
        var textLeft = rect.X + (float)MenuBarLayout.EntryHorizontalPadding
                       + (float)MenuBarLayout.EntryGlyphWidth + 10;

        using var titleFont = TaPalette.BodyFont((float)MenuBarLayout.EntryTitleFontSize, FontStyle.Bold);
        using var titleBrush = new SolidBrush(onEmphasis ? TaPalette.Paper : TaPalette.Ink);
        g.DrawString(entry.Title, titleFont, titleBrush,
            new PointF(textLeft, rect.Y + 10));

        using var subtitleFont = TaPalette.BodyFont((float)MenuBarLayout.EntrySubtitleFontSize);
        using var subtitleBrush = new SolidBrush(
            onEmphasis ? Color.FromArgb(158, TaPalette.Paper) : TaPalette.MutedInk);
        g.DrawString(entry.Subtitle, subtitleFont, subtitleBrush,
            new PointF(textLeft, rect.Y + 30));

        // 键面（右对齐，:188-191）
        using var shortcutFont = TaPalette.BodyFont((float)MenuBarLayout.ShortcutFontSize);
        using var shortcutBrush = new SolidBrush(
            onEmphasis ? Color.FromArgb(184, TaPalette.Paper) : TaPalette.MutedInk);

        var shortcutWidth = g.MeasureString(entry.Shortcut, shortcutFont).Width;
        g.DrawString(entry.Shortcut, shortcutFont, shortcutBrush,
            new PointF(rect.Right - 10 - shortcutWidth, rect.Y + 21));
    }

    private static void DrawDivider(Graphics g, int y, int width)
    {
        using var pen = new Pen(TaPalette.Hairline, 1);
        g.DrawLine(pen, 0, y, width, y);
    }

    private void DrawPinSection(Graphics g, int entryCount, int width)
    {
        var top = MenuBarLayout.PinSectionTop(entryCount);

        using var headingFont = TaPalette.BodyFont(11, FontStyle.Bold);
        using var headingBrush = new SolidBrush(TaPalette.MutedInk);
        g.DrawString("钉图管理", headingFont, headingBrush,
            new PointF(14, (float)top + 10));

        using var linkFont = TaPalette.BodyFont(11);
        using var linkBrush = new SolidBrush(TaPalette.Cinnabar);

        // 两行链接，每行间距 10（:101-110）。
        var x = 14f;
        var row = 0;
        foreach (var action in MenuBarLayout.BuildPinActions())
        {
            if (x + linkFont.Size * action.Label.Length + 18 > width - 14)
            {
                row++;
                x = 14f;
            }

            g.DrawString(action.Label, linkFont, linkBrush, new PointF(x, (float)top + 30 + (row * 16)));
            x += g.MeasureString(action.Label, linkFont).Width + 10;
        }
    }

    private void DrawFooter(Graphics g, int entryCount, int width)
    {
        var top = MenuBarLayout.FooterTop(entryCount);

        using var linkFont = TaPalette.BodyFont(11);
        using var linkBrush = new SolidBrush(TaPalette.Cinnabar);
        using var versionFont = TaPalette.BodyFont(9);
        using var versionBrush = new SolidBrush(TaPalette.MutedInk);
        using var sepPen = new Pen(TaPalette.Hairline, 1);

        var x = 12f;
        foreach (var label in new[] { "打开主界面", "设置" })
        {
            g.DrawString(label, linkFont, linkBrush, new PointF(x, (float)top + 16));
            x += g.MeasureString(label, linkFont).Width + 12;
        }

        // 版本号（tertiary，右对齐）
        var version = AppVersion.Current;
        var versionWidth = g.MeasureString(version, versionFont).Width;
        g.DrawString(version, versionFont, versionBrush,
            new PointF(width - 46 - versionWidth, (float)top + 18));

        // 退出（最右）
        g.DrawString("退出", linkFont, linkBrush,
            new PointF(width - 40, (float)top + 16));

        // 竖直分隔线（:126-128）
        g.DrawLine(sepPen, x - 6, (float)top + 12, x - 6, (float)top + 28);
    }

    private static StringFormat CenterFormat() => new()
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
        FormatFlags = StringFormatFlags.NoWrap,
    };

    private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;

        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        return path;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 图标
    // ─────────────────────────────────────────────────────────────────────────

    private static IntPtr LoadAppIcon()
    {
        // 优先取 exe 内嵌的品牌图标（assets/app.ico 经 ApplicationIcon 编译进 exe）。
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is not null)
            {
                var brand = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (brand is not null)
                {
                    return brand.Handle;
                }
            }
        }
        catch
        {
            // 提取失败退回系统默认。
        }

        var systemIcon = TrayInterop.LoadIconW(IntPtr.Zero, (IntPtr)TrayInterop.IDI_APPLICATION);
        if (systemIcon != IntPtr.Zero)
        {
            return systemIcon;
        }

        // 兜底：从内嵌资源画一个 16×16 的朱砂方块。
        return CreateFallbackIcon();
    }

    /// <summary>
    /// 内嵌回退图标：朱砂圆角方块 + 白「拓」。
    ///
    /// 参考文档 §12.3 的回退图标标记，这样即使 exe 旁没有 .ico 文件，
    /// 托盘图标依然是品牌外观而不是 Windows 默认的问号。
    /// </summary>
    private static IntPtr CreateFallbackIcon()
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        using (var fill = new SolidBrush(TaPalette.Cinnabar))
        {
            g.FillPath(fill, RoundedRect(new RectangleF(0, 0, size, size), size * 0.22f));
        }

        using var font = TaPalette.BrandSerifFont(size * 0.55f);
        using var brush = new SolidBrush(TaPalette.Paper);
        g.DrawString(TaPalette.BrandName, font, brush, new RectangleF(0, 0, size, size), CenterFormat());

        var hIcon = bitmap.GetHicon();
        return hIcon;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 消息窗口
    // ─────────────────────────────────────────────────────────────────────────

    private IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == TrayInterop.WM_TRAYICON)
        {
            var lowWord = (uint)(lParam.ToInt64() & 0xFFFF);
            switch (lowWord)
            {
                case TrayInterop.NIN_SELECT:
                case TrayInterop.WM_LBUTTONUP:
                    TogglePopover();
                    return IntPtr.Zero;

                case TrayInterop.NIN_CONTEXTMENU:
                case TrayInterop.WM_RBUTTONUP:
                case TrayInterop.WM_CONTEXTMENU:
                    ShowFallbackMenu();
                    return IntPtr.Zero;

                case TrayInterop.WM_LBUTTONDBLCLK:
                    CaptureRequested?.Invoke(CaptureMode.Interactive);
                    return IntPtr.Zero;
            }

            return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void EnsureClassRegistered()
    {
        _wndProc = WndProc;
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = 0,
            lpfnWndProc = _wndProc,
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = GetModuleHandle(null),
            hIcon = IntPtr.Zero,
            hCursor = IntPtr.Zero,
            hbrBackground = IntPtr.Zero,
            lpszMenuName = null,
            lpszClassName = TrayWindowClass,
            hIconSm = IntPtr.Zero,
        };

        _classAtom = RegisterClassExW(ref wc);
    }

    private static char[] Pad(string value, int length)
    {
        var buffer = new char[length];
        var count = Math.Min(value.Length, length - 1);
        value.AsSpan(0, count).CopyTo(buffer);
        return buffer;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_hwnd != IntPtr.Zero)
        {
            var data = new TrayInterop.NOTIFYICONDATAW
            {
                cbSize = Marshal.SizeOf<TrayInterop.NOTIFYICONDATAW>(),
                hWnd = _hwnd,
                uID = TrayId,
            };

            TrayInterop.Shell_NotifyIconW(TrayInterop.NIM_DELETE, ref data);
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        if (_icon != IntPtr.Zero)
        {
            TrayInterop.DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }

        _popover?.Dispose();
        _popover = null;
    }

    private const uint WM_DESTROY = 0x0002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public WndProcDelegate? lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;

        [MarshalAs(UnmanagedType.LPTStr)]
        public string? lpszMenuName;

        [MarshalAs(UnmanagedType.LPTStr)]
        public string lpszClassName;

        public IntPtr hIconSm;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
