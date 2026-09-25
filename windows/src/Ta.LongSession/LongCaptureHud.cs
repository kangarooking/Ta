using System.Runtime.InteropServices;
using Ta.Core.Capture;
using Ta.LongSession.Win32;

namespace Ta.LongSession;

/// <summary>
/// 长截图 HUD 面板。对应 Mac 版 ScrollingCaptureHUDPanel + ScrollingCaptureHUDView
/// （ScrollingCaptureSessionController.swift:352-609）。
///
/// 形态映射：
/// · 520×124 无边框面板 + 圆角 16 + 白 0.18 描边 + padding 14（Mac: SwiftUI 视觉常量）
/// · .ultraThickMaterial → 深色面板 + 0.92 统一 alpha（acrylic 需未文档化 API，见 PixelCanvas 注释）
/// · level = .screenSaver → WS_EX_TOPMOST + WS_EX_NOACTIVATE
/// · collectionBehavior = [.canJoinAllSpaces, …] → 无等价物（§14 风险 #5），置顶近似
/// · 按钮：自动滚动 / 暂停|继续 / 取消 / 完成并保存（acceptedFrames == 0 时禁用）
/// · 实时读数 "N 帧 · N px"
/// · 位置：选区水平居中夹 visibleFrame ± 12；垂直下方 12pt 否则上方（:366-373）
///
/// 窗口在独立 STA 线程 + 消息循环上运行（照抄 SelectionOverlay.cs 模式），
/// 控制器从捕获循环线程经 PostMessage 推送状态。
/// </summary>
public sealed class LongCaptureHud : UiThreadWindow, ILongCaptureHud
{
    private const int PanelWidth = 520;
    private const int PanelHeight = 124;
    private const int CornerRadius = 16;
    private const int Padding = 14;
    private const int ButtonHeight = 30;
    private const int ButtonGap = 8;

    private readonly object _sync = new();
    private LongCaptureHudState _state = new();
    private PixelCanvas? _canvas;
    private IntPtr _fontTitle;
    private IntPtr _fontCaption;
    private IntPtr _fontButton;
    private IntPtr _fontReadout;
    private int _hoverButton = -1;
    private CaptureSelection _selection;
    private bool _started;

    // 按钮命中区（窗口线程内维护）
    private Native.RECT _autoScrollButton;
    private Native.RECT _pauseButton;
    private Native.RECT _cancelButton;
    private Native.RECT _finishButton;

    public LongCaptureHud() : base("TaLongCaptureHud")
    {
    }

    public event Action<HudButton>? ButtonPressed;

    protected override string ThreadName => "长截图 HUD";

    protected override uint Style => Native.WS_POPUP;

    protected override uint ExStyle =>
        Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_LAYERED;

    protected override (int Left, int Top, int Width, int Height) Placement
    {
        get
        {
            lock (_sync)
            {
                var visible = VisibleFrameFor(_selection.GlobalRect);
                var (x, y) = AutoScrollSessionLogic.ComputeHudPosition(_selection.GlobalRect, visible);
                return ((int)Math.Round(x), (int)Math.Round(y), PanelWidth, PanelHeight);
            }
        }
    }

    public void Start(CaptureSelection selection)
    {
        lock (_sync)
        {
            _selection = selection;
            if (_started)
            {
                return;
            }

            _started = true;
        }

        base.Start();
    }

    public new void Close()
    {
        if (_started)
        {
            base.Close();
        }
    }

    public void Update(LongCaptureHudState state)
    {
        lock (_sync)
        {
            _state = state;
        }

        Post(Native.WM_APP_UPDATE);
    }

    protected override void OnCreated()
    {
        _canvas = new PixelCanvas();
        _canvas.Resize(PanelWidth, PanelHeight);

        _fontTitle = MakeFont(14, 600);
        _fontCaption = MakeFont(11, 400);
        _fontButton = MakeFont(12, 500);
        _fontReadout = MakeFont(11, 500);

        ComputeButtonRects();
        Render();

        // HUD 悬浮在正在滚动的页面之上：若它被截进冻结帧，就会**永久烙进拼好的长图**
        // ——每一帧都带同一块 HUD，接缝对齐时还会被当成页面内容。亲和性排除是机制级保证
        // （WGC/BitBlt 都生效，HUD 对用户照常可见）。同 Ta.Shell 的托盘 popover / 结果条。
        Native.SetWindowDisplayAffinity(Hwnd, Native.WDA_EXCLUDEFROMCAPTURE);

        Native.ShowWindow(Hwnd, Native.SW_SHOWNOACTIVATE);
        Native.UpdateWindow(Hwnd);
    }

    protected override void OnClosing()
    {
        if (_fontTitle != IntPtr.Zero)
        {
            Native.DeleteObject(_fontTitle);
            _fontTitle = IntPtr.Zero;
        }

        if (_fontCaption != IntPtr.Zero)
        {
            Native.DeleteObject(_fontCaption);
            _fontCaption = IntPtr.Zero;
        }

        if (_fontButton != IntPtr.Zero)
        {
            Native.DeleteObject(_fontButton);
            _fontButton = IntPtr.Zero;
        }

        if (_fontReadout != IntPtr.Zero)
        {
            Native.DeleteObject(_fontReadout);
            _fontReadout = IntPtr.Zero;
        }

        _canvas?.Dispose();
        _canvas = null;
    }

    protected override IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case Native.WM_APP_UPDATE:
                Render();
                return IntPtr.Zero;

            case Native.WM_MOUSEMOVE:
                var point = LParamToPoint(lParam);
                _lastMouse = point;
                var hover = HitButton(point);
                if (hover != _hoverButton)
                {
                    _hoverButton = hover;
                    Native.SetCursor(Native.LoadCursor(IntPtr.Zero, hover >= 0 ? Native.IDC_HAND : Native.IDC_ARROW));
                    Render();
                }

                return IntPtr.Zero;

            case Native.WM_LBUTTONUP:
                var click = LParamToPoint(lParam);
                var hit = HitButton(click);
                if (hit >= 0)
                {
                    ButtonPressed?.Invoke((HudButton)hit);
                    Render();
                }

                return IntPtr.Zero;

            case Native.WM_PAINT:
                Render();
                var ps = default(Native.PAINTSTRUCT);
                Native.BeginPaint(hwnd, out ps);
                Native.EndPaint(hwnd, in ps);
                return IntPtr.Zero;

            case Native.WM_ERASEBKGND:
                return IntPtr.Zero;
        }

        return Native.DefWindowProc(hwnd, message, wParam, lParam);
    }

    // ── 绘制 ──────────────────────────────────────────────────────

    private void Render()
    {
        var canvas = _canvas;
        if (canvas is null || _canvas is null)
        {
            return;
        }

        LongCaptureHudState state;
        lock (_sync)
        {
            state = _state;
        }

        canvas.Clear(Native.PanelBackdrop);
        canvas.FillRoundRect(new Native.RECT(0, 0, PanelWidth, PanelHeight), CornerRadius, Native.PanelBackdrop);
        canvas.FrameRect(
            new Native.RECT(0, 0, PanelWidth, PanelHeight),
            Native.PanelStroke,
            1);

        // ── 第一行：图标 + 标题 + 状态 + 实时读数 ──
        var iconColor = state.IsPaused ? Orange : Native.Accent;
        DrawStatusDot(canvas, new Native.RECT(Padding, Padding + 2, Padding + 24, Padding + 26), iconColor);

        DrawText(canvas, _fontTitle, Native.TextPrimary,
            new Native.RECT(Padding + 32, Padding, PanelWidth - 200, Padding + 18), "长截图采集中", Native.DT_LEFT | Native.DT_SINGLELINE | Native.DT_NOPREFIX);

        DrawText(canvas, _fontCaption, Native.TextSecondary,
            new Native.RECT(Padding + 32, Padding + 20, PanelWidth - 220, Padding + 38),
            string.IsNullOrEmpty(state.Status) ? "正在采集第一帧…" : state.Status,
            Native.DT_LEFT | Native.DT_SINGLELINE | Native.DT_NOPREFIX | Native.DT_END_ELLIPSIS);

        var readout = $"{state.AcceptedFrames} 帧 · {state.PixelHeight} px";
        DrawText(canvas, _fontReadout, Native.TextSecondary,
            new Native.RECT(PanelWidth - 200, Padding + 4, PanelWidth - Padding, Padding + 24),
            readout, Native.DT_RIGHT | Native.DT_SINGLELINE | Native.DT_NOPREFIX);

        // ── 第二行：提示 + 按钮 ──
        var hint = state.SkippedFrames > 0
            ? $"已跳过 {state.SkippedFrames} 个不连续画面"
            : "慢速、连续滚动效果最好";
        var hintColor = state.SkippedFrames > 0 ? Orange : Native.TextSecondary;
        DrawText(canvas, _fontCaption, hintColor,
            new Native.RECT(Padding, PanelHeight - Padding - ButtonHeight + 4, 300, PanelHeight - Padding),
            hint, Native.DT_LEFT | Native.DT_SINGLELINE | Native.DT_NOPREFIX);

        DrawButton(canvas, HudButton.AutoScroll, _autoScrollButton, state.IsAutoScrolling ? "停止自动滚动" : "自动滚动", false);
        DrawButton(canvas, HudButton.Pause, _pauseButton, state.IsPaused ? "继续" : "暂停", false);
        DrawButton(canvas, HudButton.Cancel, _cancelButton, "取消", false);
        DrawButton(canvas, HudButton.Finish, _finishButton, "完成并保存", !state.FinishEnabled);

        canvas.CommitLayered(Hwnd, CornerRadius, Native.HudAlpha);
    }

    private void DrawStatusDot(PixelCanvas canvas, Native.RECT rect, uint color)
    {
        var brush = Native.CreateSolidBrush(color);
        var old = Native.SelectObject(canvas.Hdc, brush);
        Native.Ellipse(canvas.Hdc, rect.left, rect.top, rect.right, rect.bottom);
        Native.SelectObject(canvas.Hdc, old);
        Native.DeleteObject(brush);
    }

    private void DrawButton(PixelCanvas canvas, HudButton button, Native.RECT rect, string label, bool disabled)
    {
        var hovered = _hoverButton == (int)button;
        var isFinish = button == HudButton.Finish;

        uint fill;
        uint text;
        if (disabled)
        {
            fill = 0x00282622u;
            text = Native.TextDisabled;
        }
        else if (isFinish)
        {
            fill = hovered ? Native.AccentHover : Native.Accent;
            text = 0x00FFFFFFu;
        }
        else
        {
            fill = hovered ? 0x00423C36u : 0x00332E2Au;
            text = Native.TextPrimary;
        }

        canvas.FillRoundRect(rect, 8, fill);
        canvas.FrameRect(rect, Native.PanelStroke, 1);
        DrawText(canvas, _fontButton, text, rect, label,
            Native.DT_CENTER | Native.DT_VCENTER | Native.DT_SINGLELINE | Native.DT_NOPREFIX);
    }

    private void ComputeButtonRects()
    {
        // 从右往左排：完成并保存(96) / 取消(64) / 暂停(64) / 自动滚动(88)
        var top = PanelHeight - Padding - ButtonHeight;
        var bottom = top + ButtonHeight;

        var finishWidth = 96;
        var finishLeft = PanelWidth - Padding - finishWidth;
        _finishButton = new Native.RECT(finishLeft, top, finishLeft + finishWidth, bottom);

        var cancelWidth = 64;
        var cancelLeft = finishLeft - ButtonGap - cancelWidth;
        _cancelButton = new Native.RECT(cancelLeft, top, cancelLeft + cancelWidth, bottom);

        var pauseWidth = 64;
        var pauseLeft = cancelLeft - ButtonGap - pauseWidth;
        _pauseButton = new Native.RECT(pauseLeft, top, pauseLeft + pauseWidth, bottom);

        var autoWidth = 104;
        var autoLeft = pauseLeft - ButtonGap - autoWidth;
        _autoScrollButton = new Native.RECT(autoLeft, top, autoLeft + autoWidth, bottom);
    }

    private int HitButton(Win32.Native.POINT point)
    {
        if (_autoScrollButton.Contains(point))
        {
            return (int)HudButton.AutoScroll;
        }

        if (_pauseButton.Contains(point))
        {
            return (int)HudButton.Pause;
        }

        if (_cancelButton.Contains(point))
        {
            return (int)HudButton.Cancel;
        }

        if (_finishButton.Contains(point))
        {
            return (int)HudButton.Finish;
        }

        return -1;
    }

    // 记录最后一次鼠标位置（hover 高亮需要）
    private Win32.Native.POINT _lastMouse;

    private static Win32.Native.POINT LParamToPoint(IntPtr lParam)
    {
        var value = lParam.ToInt64();
        return new Win32.Native.POINT((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
    }

    // 橙色（暂停态图标/提示）。BGR 序：橙 #FFA500 → 0x0000A5FF。
    private const uint Orange = 0x0000A5FF;

    private static IntPtr MakeFont(int height, int weight) => Native.CreateFontW(
        -height, 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei");

    private static void DrawText(
        PixelCanvas canvas,
        IntPtr font,
        uint color,
        Native.RECT rect,
        string text,
        uint format)
    {
        var oldFont = Native.SelectObject(canvas.Hdc, font);
        var oldBk = Native.SetBkMode(canvas.Hdc, Native.TRANSPARENT);
        var oldColor = Native.SetTextColor(canvas.Hdc, color);
        var r = rect;
        Native.DrawTextW(canvas.Hdc, text, text.Length, ref r, format | Native.DT_NOPREFIX);
        Native.SetTextColor(canvas.Hdc, oldColor);
        Native.SetBkMode(canvas.Hdc, oldBk);
        Native.SelectObject(canvas.Hdc, oldFont);
    }

    /// <summary>
    /// 选区所在的可见区域。对应 Mac: NSScreen.visibleFrame（:361-365）。
    /// Windows 侧用显示器工作区（排除任务栏）。
    /// </summary>
    private static RectD VisibleFrameFor(RectD selection)
    {
        var monitor = Native.MonitorFromRect(
            new Native.RECT(
                (int)Math.Round(selection.MinX),
                (int)Math.Round(selection.MinY),
                (int)Math.Round(selection.MaxX),
                (int)Math.Round(selection.MaxY)),
            Native.MONITOR_DEFAULTTONEAREST);

        var info = Native.MONITORINFO.Create();
        if (monitor != IntPtr.Zero && Native.GetMonitorInfo(monitor, ref info))
        {
            return new RectD(info.rcWork.left, info.rcWork.top, info.rcWork.Width, info.rcWork.Height);
        }

        var primary = Native.MONITORINFO.Create();
        Native.GetMonitorInfo(
            Native.MonitorFromPoint(new Native.POINT(0, 0), Native.MONITOR_DEFAULTTOPRIMARY), ref primary);
        return new RectD(primary.rcWork.left, primary.rcWork.top, primary.rcWork.Width, primary.rcWork.Height);
    }

    public new void Dispose()
    {
        Close();
    }
}
