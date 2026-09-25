using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Ta.Core.Capture;
using Ta.Shell.Contracts;

namespace Ta.Shell.UI;

/// <summary>
/// 结果反馈条 —— 一个非激活的分层面板。
///
/// 逐行对齐 Mac 版 <c>ResultBarView</c> + <c>ResultBarController</c>
/// （ResultBarView.swift / ResultBarController.swift）。
///
/// 与 Mac 版的面板属性对照（移植参考文档 §5.7）：
/// | Mac 版 | 本实现 |
/// |---|---|
/// | 326×574 之类固定尺寸 | 由 <see cref="ResultBarLayout"/> 计算 |
/// | <c>level = .floating</c> | <c>WS_EX_TOPMOST</c> |
/// | <c>hidesOnDeactivate = false</c> | 不因失焦而隐藏 |
/// | <c>.transient</c> | 点击面板外不收起（结果必须能读完） |
///
/// 位置：鼠标所在屏水平居中，<c>y = visibleFrame.minY + 28</c>
/// （ResultBarController.swift:75-85）。Windows 屏坐标 Y 向下，故为 <c>top + 28</c>。
/// </summary>
public sealed class ResultBarView : IResultBarSink, IDisposable
{
    private readonly LayeredSurfaceWindow _window;
    private readonly Func<string, double, double> _measure;
    private readonly double _defaultDuration;
    private readonly object _sync = new();

    private ResultBarState _state = new(ResultBarKind.Processing, string.Empty);
    private bool _disposed;

    public ResultBarView(
        Func<string, double, double>? measure = null,
        double resultBarDuration = ResultBarLayout.DefaultAutoHideSeconds)
    {
        // 测宽默认用 GDI TextRenderer —— 与 Mac 版的 NSString.size(withAttributes:) 对应。
        _measure = measure ?? MeasureWithGdi;
        _defaultDuration = ResultBarLayout.NormalizeDuration(resultBarDuration);

        _window = new LayeredSurfaceWindow($"{TaPalette.BrandName} · {TaPalette.BrandEnglishName}");
        _window.Create(new Size((int)ResultBarLayout.MinimumWidth, (int)ResultBarLayout.Height));
        _window.MouseUp += OnMouseUp;

        // 用窗口自己的 Win32 定时器（投递到窗口线程）而不是线程池定时器 ——
        // 隐藏窗口必须在创建它的线程上做。
        _window.Timer += Hide;

        // 对应 Mac 版 hidesOnDeactivate = false：结果必须能读完，不因失焦而消失。
        // 这里刻意不订阅 Activated。
    }

    /// <summary>当前显示的状态；未显示时为 null。</summary>
    public ResultBarState? CurrentState => IsVisible ? _state : null;

    public bool IsVisible { get; private set; }

    /// <summary>
    /// 把结果条排除出屏幕捕获。
    ///
    /// 任务书 E 项：截图时不能把 Ta 自身截进图里。结果条虽然是「截图完成之后」才出现的，
    /// 但连续截图时它可能还挂在屏幕上 —— 排除掉更稳妥。
    /// </summary>
    public void ExcludeFromCapture() => _window.ExcludeFromCapture();

    /// <summary>恢复参与屏幕捕获。</summary>
    public void IncludeInCapture() => _window.IncludeInCapture();

    /// <summary>
    /// 显示一条反馈。
    ///
    /// 对应 Mac 版 <c>ResultBarController.show(_:autoHide:dismissalOverrideSeconds:)</c>
    /// （ResultBarController.swift:9-45）。
    /// </summary>
    public void Show(ResultBarState state, ResultBarDisplayOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(state);
        Ta.Shell.Program.Log($"[resultbar] Show: {state.Kind} \"{state.Title}\" autoHide={options.AutoHide}");

        lock (_sync)
        {
            _state = state;

            var width = ResultBarLayout.PreferredWidthFor(state, _measure);
            var bitmap = Render(state, (int)width);

            _window.Present(bitmap, PositionFor(width));
            _window.Show();
            IsVisible = true;

            ScheduleAutoHide(state, options);
        }
    }

    public void Hide()
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            _window.StopTimer();
            _window.Hide();
            IsVisible = false;
        }
    }

    /// <summary>
    /// 自动隐藏调度。
    ///
    /// 关键规则（ResultBarController.swift:35）：<b>processing 永不自动隐藏</b>，
    /// 它一直留在屏幕上直到被下一个状态替换。其余状态按
    /// <see cref="ResultBarLayout.ResolveAutoHideSeconds"/> 计算时长。
    /// </summary>
    private void ScheduleAutoHide(ResultBarState state, ResultBarDisplayOptions options)
    {
        _window.StopTimer();

        if (!ResultBarLayout.ShouldAutoHide(state.Kind, options.AutoHide))
        {
            return;
        }

        var seconds = ResultBarLayout.ResolveAutoHideSeconds(
            _defaultDuration,
            options.AutoHide,
            options.DismissalOverrideSeconds);

        if (seconds <= 0)
        {
            return;
        }

        _window.StartTimer((int)Math.Ceiling(seconds * 1000));
    }

    private void OnMouseUp(Point clientPoint)
    {
        // 关闭按钮只在非 processing 状态显示；命中它才关闭。
        if (!_state.Kind.ShowsCloseButton())
        {
            return;
        }

        if (CloseButtonHitTest(clientPoint))
        {
            Hide();
        }
    }

    /// <summary>关闭按钮命中测试（相对位图左上角）。</summary>
    private bool CloseButtonHitTest(Point point)
    {
        var width = ResultBarLayout.PreferredWidthFor(_state, _measure);
        var rect = CloseButtonRect((int)width);
        return rect.Contains(point);
    }

    private static Rectangle CloseButtonRect(int width) => new(
        width - (int)ResultBarLayout.HorizontalPadding - (int)ResultBarLayout.CloseButtonSize,
        ((int)ResultBarLayout.Height - (int)ResultBarLayout.CloseButtonSize) / 2,
        (int)ResultBarLayout.CloseButtonSize,
        (int)ResultBarLayout.CloseButtonSize);

    /// <summary>
    /// 定位到鼠标所在屏的水平居中、<c>top + 28</c> 处。
    /// 对应 Mac 版 ResultBarController.position(_:)（:75-85）。
    /// </summary>
    private static Point PositionFor(double width)
    {
        var cursor = new System.Drawing.Point(System.Windows.Forms.Cursor.Position.X,
            System.Windows.Forms.Cursor.Position.Y);

        var screenFrame = ScreenUnderPoint(cursor) ?? PrimaryScreenFrame();
        var visible = ScreenInterop.VisibleFrameFor(screenFrame);

        var x = visible.Left + (int)Math.Round((visible.Width - width) / 2.0);
        var y = visible.Top + (int)ResultBarLayout.TopInset;

        return new Point(x, y);
    }

    private static RectD? ScreenUnderPoint(System.Drawing.Point point) =>
        ScreenInterop.DisplayUnderCursor()?.Frame;

    private static RectD PrimaryScreenFrame()
    {
        var primary = ScreenInterop.EnumerateDisplays().FirstOrDefault(d => d.IsPrimary)
                      ?? ScreenInterop.EnumerateDisplays().FirstOrDefault();

        return primary?.Frame ?? new RectD(0, 0, 1920, 1080);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 绘制
    // ─────────────────────────────────────────────────────────────────────────

    private Bitmap Render(ResultBarState state, int width)
    {
        var bitmap = new Bitmap(Math.Max(1, width), (int)ResultBarLayout.Height);
        using var g = Graphics.FromImage(bitmap);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // 圆角背景：paper 底 + cornerRadius 18（ResultBarView.swift:108）。
        using var path = RoundedRect(new RectangleF(0, 0, width, (float)ResultBarLayout.Height),
            (float)ResultBarLayout.CornerRadius);

        // UpdateLayeredWindow 用的位图必须是 32bpp ARGB 且背景完全透明，
        // 否则圆角之外会带上一圈底色。Bitmap 默认 Format32bppArgb，这里显式填充。
        g.Clear(Color.Transparent);

        using (var fill = new SolidBrush(TaPalette.Paper))
        {
            g.FillPath(fill, path);
        }

        // 状态图标（21pt）—— 对应 ResultBarView.swift:63-67 的 font(.system(size: 21))。
        var iconColor = state.Kind.Color();
        using (var iconBrush = new SolidBrush(iconColor))
        {
            using var iconFont = TaPalette.BodyFont(18, FontStyle.Bold);
            var iconRect = new RectangleF(
                (float)ResultBarLayout.HorizontalPadding,
                ((float)ResultBarLayout.Height - (float)ResultBarLayout.IconSize) / 2,
                (float)ResultBarLayout.IconSize,
                (float)ResultBarLayout.IconSize);

            using var iconFormat = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
            };

            g.DrawString(state.Kind.Glyph(), iconFont, iconBrush, iconRect, iconFormat);
        }

        // 标题 13pt semibold / 说明 11pt regular —— 对应 ResultBarView.swift:70-79。
        var textLeft = (float)ResultBarLayout.HorizontalPadding + (float)ResultBarLayout.IconSize + 10;
        var textTop = state.Detail is null
            ? ((float)ResultBarLayout.Height - 18) / 2
            : 14;

        using var titleFont = TaPalette.BodyFont(13, FontStyle.Bold);
        using var titleBrush = new SolidBrush(TaPalette.Ink);
        g.DrawString(state.Title, titleFont, titleBrush, textLeft, textTop);

        if (state.Detail is { } detail)
        {
            using var detailFont = TaPalette.BodyFont(11);
            using var detailBrush = new SolidBrush(TaPalette.MutedInk);

            // 单行截断（.lineLimit(1) + .truncationMode(.tail)）。
            var available = width - textLeft
                - (float)ResultBarLayout.BrandBadgeSize - 12
                - (float)ResultBarLayout.CloseButtonSize - 8;

            using var clipFormat = new StringFormat
            {
                FormatFlags = StringFormatFlags.NoWrap,
                Trimming = StringTrimming.EllipsisCharacter,
            };

            g.DrawString(detail, detailFont, detailBrush,
                new RectangleF(textLeft, textTop + 20, Math.Max(1, available), 18), clipFormat);
        }

        // 「拓」字徽章：22×22，衬线体，朱砂描边（ResultBarView.swift:84-93）。
        var badgeRect = new RectangleF(
            width - (float)ResultBarLayout.BrandBadgeSize - 12
                - (float)ResultBarLayout.CloseButtonSize - (state.Kind.ShowsCloseButton() ? 8 : 0),
            ((float)ResultBarLayout.Height - (float)ResultBarLayout.BrandBadgeSize) / 2,
            (float)ResultBarLayout.BrandBadgeSize,
            (float)ResultBarLayout.BrandBadgeSize);

        using (var badgePath = RoundedRect(badgeRect, 5))
        {
            using var badgePen = new Pen(Color.FromArgb(209, TaPalette.Cinnabar), 1);
            g.DrawPath(badgePen, badgePath);
        }

        using (var brandFont = TaPalette.BrandSerifFont(10))
        using (var brandBrush = new SolidBrush(TaPalette.Cinnabar))
        {
            using var brandFormat = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
            };

            g.DrawString(TaPalette.BrandName, brandFont, brandBrush, badgeRect, brandFormat);
        }

        // 关闭按钮 —— processing 状态不显示（ResultBarView.swift:94）。
        if (state.Kind.ShowsCloseButton())
        {
            var closeRect = CloseButtonRect(width);
            using (var closePath = new GraphicsPath())
            {
                closePath.AddEllipse(closeRect);
                using var closeFill = new SolidBrush(TaPalette.InkCloseButton);
                g.FillPath(closeFill, closePath);
            }

            using var closeFont = TaPalette.BodyFont(10, FontStyle.Bold);
            using var closeBrush = new SolidBrush(TaPalette.MutedInk);
            using var closeFormat = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
            };

            g.DrawString("✕", closeFont, closeBrush,
                new RectangleF(closeRect.X, closeRect.Y, closeRect.Width, closeRect.Height),
                closeFormat);
        }

        return bitmap;
    }

    /// <summary>连续圆角矩形路径。</summary>
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

    /// <summary>GDI 测宽。对应 Mac 版 NSString.size(withAttributes:)。</summary>
    private static double MeasureWithGdi(string text, double pointSize)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        using var font = new Font("Microsoft YaHei", (float)pointSize,
            pointSize >= 13 ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);

        using var scratch = new Bitmap(1, 1);
        using var g = Graphics.FromImage(scratch);

        var size = TextRenderer.MeasureText(g, text, font,
            new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);

        return size.Width;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.Dispose();
    }
}
