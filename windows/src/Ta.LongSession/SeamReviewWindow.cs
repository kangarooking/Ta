using System.Runtime.InteropServices;
using Ta.Core.Imaging;
using Ta.Core.LongCapture;
using Ta.LongSession.Win32;

namespace Ta.LongSession;

/// <summary>
/// 接缝复查窗口。对应 Mac 版 ScrollingSeamReviewWindowController + ScrollingSeamReviewView
/// （ScrollingSeamReviewWindowController.swift）。
///
/// 形态映射：
/// · 1040×720（min 820×560），标题「检查长截图接缝」，居中
/// · 左预览：ScrollView，缩放 0.2…1.5 默认 0.55，>1 段时有分段选择器，
///   上限 maximumPixelHeight = 8_000；最终导出 30_000（由控制器决定）
/// · 右列表：8×8 圆点（confidence &lt; 0.58 红）、「接缝 N · 向上|向下」、百分比、
///   「新增 N px · 固定顶 N · 固定底 N」、微调按钮 [-10, -1, +1, +10]
/// · 按钮：丢弃 / 确认并保存；窗口关闭即取消（Mac: windowWillClose，:71-79）
///
/// 窗口在独立 STA 线程 + 消息循环上运行（照抄 SelectionOverlay.cs 模式）。
/// </summary>
public sealed class SeamReviewWindow : UiThreadWindow, ISeamReviewView
{
    // ── 布局常量（对应 Mac: :42-49） ──
    public const int WindowWidth = 1040;
    public const int WindowHeight = 720;
    public const int MinWindowWidth = 820;
    public const int MinWindowHeight = 560;
    public const double DefaultZoom = 0.55;
    public const double MinZoom = 0.2;
    public const double MaxZoom = 1.5;
    public const int PreviewMaximumPixelHeight = 8_000;
    public const int ExportMaximumPixelHeight = 30_000;

    private const int TopBarHeight = 64;
    private const int RightPanelWidth = 340;
    private const int RowHeight = 92;
    private const int ScrollbarWidth = 10;
    private const int SegmentTabHeight = 34;

    private readonly object _sync = new();
    private SeamReviewModel _model = new();
    private Action<Guid, int>? _onAdjust;
    private Action? _onConfirm;
    private Action? _onCancel;
    private bool _closingProgrammatically;

    private PixelCanvas? _canvas;
    private IntPtr _fontTitle;
    private IntPtr _fontSection;
    private IntPtr _fontBody;
    private IntPtr _fontCaption;
    private IntPtr _fontButton;

    // 视图状态
    private double _zoom = DefaultZoom;
    private int _selectedPart;
    private int _previewScrollX;
    private int _previewScrollY;
    private int _seamScrollY;
    private int _hoverRow = -1;
    private int _hoverButton = -1;   // 行内微调按钮索引（row*4 + button）
    private bool _hoverDiscard;
    private bool _hoverConfirm;
    private int _hoverTab = -1;

    // 拖拽状态
    private enum DragKind { None, ZoomSlider, PreviewScroll, SeamScroll }
    private DragKind _drag = DragKind.None;
    private int _dragOffset;

    // 命中区（Render 时重算）
    private Native.RECT _discardButton;
    private Native.RECT _confirmButton;
    private Native.RECT _zoomTrack;
    private readonly List<Native.RECT> _tabs = new();
    private readonly List<Native.RECT> _rowButtons = new();   // 与 Segments 对齐，每行 4 个
    private Native.RECT _previewViewport;
    private Native.RECT _seamViewport;
    private Native.RECT _previewScrollbar;
    private Native.RECT _seamScrollbar;

    // 每段预览位图（窗口线程持有 GDI 对象）
    private readonly List<(IntPtr Dc, IntPtr Bitmap, int Width, int Height)> _partBitmaps = new();
    private bool _bitmapsDirty = true;

    public SeamReviewWindow() : base("TaSeamReviewWindow")
    {
    }

    // Closed 事件继承自 UiThreadWindow（WM_DESTROY 时触发），
    // 同时满足 ISeamReviewView.Closed —— 无需重复声明。

    protected override string ThreadName => "检查长截图接缝";

    protected override uint Style =>
        Native.WS_OVERLAPPEDWINDOW | Native.WS_CLIPCHILDREN;

    protected override uint ExStyle => Native.WS_EX_TOOLWINDOW;

    protected override (int Left, int Top, int Width, int Height) Placement
    {
        get
        {
            // 居中到主显示器工作区（对应 Mac: window.center()）。
            var info = Native.MONITORINFO.Create();
            Native.GetMonitorInfo(
                Native.MonitorFromPoint(new Native.POINT(0, 0), Native.MONITOR_DEFAULTTOPRIMARY), ref info);
            var x = info.rcWork.left + (info.rcWork.Width - WindowWidth) / 2;
            var y = info.rcWork.top + (info.rcWork.Height - WindowHeight) / 2;
            return (x, y, WindowWidth, WindowHeight);
        }
    }

    public void Present(
        SeamReviewModel model,
        Action<Guid, int> onAdjust,
        Action onConfirm,
        Action onCancel)
    {
        lock (_sync)
        {
            _model = model;
            _onAdjust = onAdjust;
            _onConfirm = onConfirm;
            _onCancel = onCancel;
            _closingProgrammatically = false;
            _selectedPart = 0;
            _zoom = DefaultZoom;
            _previewScrollX = 0;
            _previewScrollY = 0;
            _seamScrollY = 0;
            _bitmapsDirty = true;
        }

        base.Start();
        Native.ShowWindow(Hwnd, Native.SW_SHOW);
        Native.SetForegroundWindow(Hwnd);
        Render();
    }

    public new void Close()
    {
        lock (_sync)
        {
            _closingProgrammatically = true;
        }

        base.Close();
    }

    public void Refresh(SeamReviewModel model)
    {
        lock (_sync)
        {
            _model = model;
            _bitmapsDirty = true;
            _selectedPart = Math.Min(_selectedPart, Math.Max(0, model.PreviewParts.Count - 1));
        }

        Post(Native.WM_APP_REFRESH);
    }

    protected override void OnCreated()
    {
        _canvas = new PixelCanvas();
        _fontTitle = MakeFont(17, 700);
        _fontSection = MakeFont(13, 600);
        _fontBody = MakeFont(12, 400);
        _fontCaption = MakeFont(10, 400);
        _fontButton = MakeFont(11, 400);

        Native.GetClientRect(Hwnd, out var client);
        _canvas.Resize(client.Width, client.Height);
    }

    protected override void OnClosing()
    {
        ReleasePartBitmaps();
        _canvas?.Dispose();
        _canvas = null;
        DeleteFonts();
    }

    protected override void OnUserCloseRequested()
    {
        // 用户关闭 = 取消（对应 Mac: windowWillClose → 取消，:71-79）。
        lock (_sync)
        {
            if (_closingProgrammatically)
            {
                _closingProgrammatically = false;
                Native.DestroyWindow(Hwnd);
                return;
            }

            var cancel = _onCancel;
            _onCancel = null;
            Native.DestroyWindow(Hwnd);
            cancel?.Invoke();
        }
    }

    protected override IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case Native.WM_APP_REFRESH:
                EnsurePartBitmaps();
                Render();
                return IntPtr.Zero;

            case Native.WM_SIZE:
                var canvas = _canvas;
                if (canvas is not null)
                {
                    Native.GetClientRect(hwnd, out var client);
                    canvas.Resize(Math.Max(1, client.Width), Math.Max(1, client.Height));
                    Render();
                }

                return IntPtr.Zero;

            case Native.WM_ERASEBKGND:
                return IntPtr.Zero;

            case Native.WM_PAINT:
                Render();
                var ps = default(Native.PAINTSTRUCT);
                Native.BeginPaint(hwnd, out ps);
                Native.EndPaint(hwnd, in ps);
                return IntPtr.Zero;

            case Native.WM_GETMINMAXINFO:
                ClampMinTrackSize(lParam);
                return IntPtr.Zero;

            case Native.WM_MOUSEMOVE:
                OnMouseMove(LParamToPoint(lParam));
                return IntPtr.Zero;

            case Native.WM_LBUTTONDOWN:
                OnMouseDown(LParamToPoint(lParam));
                return IntPtr.Zero;

            case Native.WM_LBUTTONUP:
                OnMouseUp(LParamToPoint(lParam));
                return IntPtr.Zero;

            case Native.WM_MOUSEWHEEL:
                var wheel = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                OnWheel(LParamToPoint(lParam), wheel);
                return IntPtr.Zero;

            case Native.WM_SETCURSOR:
                var hitTest = lParam.ToInt64() & 0xFFFF;
                if (hitTest == 1)
                {
                    Native.SetCursor(Native.LoadCursor(
                        IntPtr.Zero,
                        _drag == DragKind.None && _hoverButton >= 0 ? Native.IDC_HAND : Native.IDC_ARROW));
                }

                return Native.DefWindowProc(hwnd, message, wParam, lParam);

            case Native.WM_KEYDOWN:
                if (wParam.ToInt64() == 0x1B)   // Esc = 丢弃
                {
                    TriggerCancel();
                }

                return IntPtr.Zero;
        }

        return Native.DefWindowProc(hwnd, message, wParam, lParam);
    }

    // ── 交互 ──────────────────────────────────────────────────────

    private void OnMouseMove(Win32.Native.POINT point)
    {
        switch (_drag)
        {
            case DragKind.ZoomSlider:
                SetZoomFromTrack(point.x);
                return;
            case DragKind.PreviewScroll:
                _previewScrollY = Clamp(_previewScrollY + _dragOffset - point.y, 0, PreviewMaxScrollY());
                _dragOffset = point.y;
                Render();
                return;
            case DragKind.SeamScroll:
                _seamScrollY = Clamp(_seamScrollY + _dragOffset - point.y, 0, SeamMaxScrollY());
                _dragOffset = point.y;
                Render();
                return;
        }

        var changed = false;
        var row = RowIndexAt(point);
        var button = ButtonIndexAt(point);
        var hoverDiscard = _discardButton.Contains(point);
        var hoverConfirm = _confirmButton.Contains(point);
        var hoverTab = TabIndexAt(point);

        if (row != _hoverRow || button != _hoverButton || hoverDiscard != _hoverDiscard
            || hoverConfirm != _hoverConfirm || hoverTab != _hoverTab)
        {
            _hoverRow = row;
            _hoverButton = button;
            _hoverDiscard = hoverDiscard;
            _hoverConfirm = hoverConfirm;
            _hoverTab = hoverTab;
            changed = true;
        }

        if (changed)
        {
            Render();
        }
    }

    private void OnMouseDown(Win32.Native.POINT point)
    {
        if (_zoomTrack.Contains(point) || NearSliderKnob(point))
        {
            _drag = DragKind.ZoomSlider;
            SetZoomFromTrack(point.x);
            Native.SetCapture(Hwnd);
            return;
        }

        if (_previewScrollbar.Contains(point))
        {
            _drag = DragKind.PreviewScroll;
            _dragOffset = point.y;
            Native.SetCapture(Hwnd);
            return;
        }

        if (_seamScrollbar.Contains(point))
        {
            _drag = DragKind.SeamScroll;
            _dragOffset = point.y;
            Native.SetCapture(Hwnd);
            return;
        }

        var tab = TabIndexAt(point);
        if (tab >= 0)
        {
            _selectedPart = tab;
            _previewScrollX = 0;
            _previewScrollY = 0;
            Render();
            return;
        }

        if (_discardButton.Contains(point))
        {
            TriggerCancel();
            return;
        }

        if (_confirmButton.Contains(point))
        {
            TriggerConfirm();
            return;
        }

        var buttonIndex = ButtonIndexAt(point);
        if (buttonIndex >= 0)
        {
            var rowIndex = buttonIndex / 4;
            var step = buttonIndex % 4;
            var deltas = new[] { -10, -1, 1, 10 };
            SeamReviewRow row;
            lock (_sync)
            {
                if (rowIndex >= _model.Segments.Count)
                {
                    return;
                }

                row = _model.Segments[rowIndex];
            }

            _onAdjust?.Invoke(row.Id, deltas[step]);
            return;
        }
    }

    private void OnMouseUp(Win32.Native.POINT point)
    {
        if (_drag != DragKind.None)
        {
            _drag = DragKind.None;
            Native.ReleaseCapture();
        }
    }

    private void OnWheel(Win32.Native.POINT point, short delta)
    {
        var step = delta / 120 * 48;
        if (_seamViewport.Contains(point) || _seamScrollbar.Contains(point))
        {
            _seamScrollY = Clamp(_seamScrollY - step, 0, SeamMaxScrollY());
        }
        else
        {
            _previewScrollY = Clamp(_previewScrollY - step, 0, PreviewMaxScrollY());
        }

        Render();
    }

    private void TriggerCancel()
    {
        Action? cancel;
        lock (_sync)
        {
            cancel = _onCancel;
            _onCancel = null;
            _closingProgrammatically = true;
        }

        Close();
        cancel?.Invoke();
    }

    private void TriggerConfirm()
    {
        Action? confirm;
        lock (_sync)
        {
            confirm = _onConfirm;
            _closingProgrammatically = true;
        }

        Close();
        confirm?.Invoke();
    }

    private void SetZoomFromTrack(int x)
    {
        var width = _zoomTrack.Width;
        var ratio = width <= 0 ? 0 : Math.Clamp((double)(x - _zoomTrack.left) / width, 0, 1);
        var zoom = MinZoom + ratio * (MaxZoom - MinZoom);
        if (Math.Abs(zoom - _zoom) > 0.001)
        {
            _zoom = zoom;
            _previewScrollX = 0;
            _previewScrollY = 0;
            Render();
        }
    }

    private bool NearSliderKnob(Win32.Native.POINT point)
    {
        var knob = KnobRect();
        return point.x >= knob.left - 8 && point.x <= knob.right + 8
            && point.y >= knob.top - 8 && point.y <= knob.bottom + 8;
    }

    private Native.RECT KnobRect()
    {
        var ratio = (float)((_zoom - MinZoom) / (MaxZoom - MinZoom));
        var cx = _zoomTrack.left + (int)(ratio * _zoomTrack.Width);
        return new Native.RECT(cx - 7, _zoomTrack.top - 5, cx + 7, _zoomTrack.bottom + 5);
    }

    private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));

    private int PreviewMaxScrollY()
    {
        if (_model.PreviewParts.Count == 0 || _selectedPart >= _model.PreviewParts.Count)
        {
            return 0;
        }

        var displayH = (int)(_model.PreviewParts[_selectedPart].Height * _zoom);
        return Math.Max(0, displayH - _previewViewport.Height);
    }

    private int PreviewMaxScrollX()
    {
        if (_model.PreviewParts.Count == 0 || _selectedPart >= _model.PreviewParts.Count)
        {
            return 0;
        }

        var displayW = (int)(_model.PreviewParts[_selectedPart].Width * _zoom);
        return Math.Max(0, displayW - _previewViewport.Width);
    }

    private int SeamMaxScrollY()
    {
        Native.GetClientRect(Hwnd, out var client);
        lock (_sync)
        {
            var content = _model.Segments.Count * RowHeight;
            var viewportHeight = client.Height - TopBarHeight - 1;
            return Math.Max(0, content - viewportHeight + 20);
        }
    }

    private int RowIndexAt(Win32.Native.POINT point)
    {
        Native.GetClientRect(Hwnd, out var client);
        var left = client.Width - RightPanelWidth;
        if (point.x < left)
        {
            return -1;
        }

        var y = point.y - TopBarHeight + _seamScrollY - 40;
        if (y < 0)
        {
            return -1;
        }

        lock (_sync)
        {
            var index = y / RowHeight;
            return index >= 0 && index < _model.Segments.Count ? index : -1;
        }
    }

    private int ButtonIndexAt(Win32.Native.POINT point)
    {
        for (var i = 0; i < _rowButtons.Count; i++)
        {
            if (_rowButtons[i].Contains(point))
            {
                return i;
            }
        }

        return -1;
    }

    private int TabIndexAt(Win32.Native.POINT point)
    {
        for (var i = 0; i < _tabs.Count; i++)
        {
            if (_tabs[i].Contains(point))
            {
                return i;
            }
        }

        return -1;
    }

    private static Win32.Native.POINT LParamToPoint(IntPtr lParam)
    {
        var value = lParam.ToInt64();
        return new Win32.Native.POINT((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
    }

    private static void ClampMinTrackSize(IntPtr lParam)
    {
        var info = Marshal.PtrToStructure<Native.MINMAXINFO>(lParam);
        info.ptMinTrackSize = new Win32.Native.POINT(MinWindowWidth, MinWindowHeight);
        Marshal.StructureToPtr(info, lParam, false);
    }

    // ── 位图管理 ──────────────────────────────────────────────────

    private void EnsurePartBitmaps()
    {
        if (!_bitmapsDirty)
        {
            return;
        }

        ReleasePartBitmaps();

        SeamReviewModel model;
        lock (_sync)
        {
            model = _model;
        }

        foreach (var part in model.PreviewParts)
        {
            var dc = Native.CreateCompatibleDC(IntPtr.Zero);
            var info = new Native.BITMAPINFO
            {
                bmiHeader = new Native.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                    biWidth = part.Width,
                    biHeight = -part.Height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = Native.BI_RGB,
                },
            };

            var bitmap = Native.CreateDIBSection(dc, ref info, Native.DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
            {
                continue;
            }

            // RgbaBitmap 是 RGBA（premultiplied-last）；Windows DIB 是 BGRA，
            // 逐像素交换 R/B 通道即可。
            var pixels = new byte[part.Width * part.Height * 4];
            Array.Copy(part.Pixels, pixels, pixels.Length);
            for (var i = 0; i < pixels.Length; i += 4)
            {
                (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            }

            Marshal.Copy(pixels, 0, bits, pixels.Length);
            Native.SelectObject(dc, bitmap);
            _partBitmaps.Add((dc, bitmap, part.Width, part.Height));
        }

        _bitmapsDirty = false;
    }

    private void ReleasePartBitmaps()
    {
        foreach (var (dc, bitmap, _, _) in _partBitmaps)
        {
            if (dc != IntPtr.Zero)
            {
                Native.DeleteDC(dc);
            }

            if (bitmap != IntPtr.Zero)
            {
                Native.DeleteObject(bitmap);
            }
        }

        _partBitmaps.Clear();
    }

    // ── 绘制 ──────────────────────────────────────────────────────

    private void Render()
    {
        var canvas = _canvas;
        if (canvas is null)
        {
            return;
        }

        EnsurePartBitmaps();
        Native.GetClientRect(Hwnd, out var client);
        if (client.Width <= 0 || client.Height <= 0)
        {
            return;
        }

        if (canvas.Width != client.Width || canvas.Height != client.Height)
        {
            canvas.Resize(client.Width, client.Height);
        }

        SeamReviewModel model;
        lock (_sync)
        {
            model = _model;
        }

        canvas.Clear(Native.PanelBackdrop);

        ComputeLayout(client, model);

        // ── 顶栏 ──
        DrawText(canvas, _fontTitle, Native.TextPrimary,
            new Native.RECT(16, 12, 320, 34), "检查接缝",
            Native.DT_LEFT | Native.DT_SINGLELINE | Native.DT_NOPREFIX);
        DrawText(canvas, _fontCaption, Native.TextSecondary,
            new Native.RECT(16, 36, 460, 56),
            "红色接缝建议重点查看。负值会移除重复行，正值会补回遗漏行。",
            Native.DT_LEFT | Native.DT_SINGLELINE | Native.DT_NOPREFIX);

        DrawText(canvas, _fontBody, Native.TextSecondary,
            new Native.RECT(_zoomTrack.left - 120, 26, _zoomTrack.left - 12, 44),
            $"总高 {model.OutputPixelHeight} px",
            Native.DT_RIGHT | Native.DT_SINGLELINE | Native.DT_NOPREFIX);

        DrawZoomSlider(canvas);

        DrawButton(canvas, _discardButton, "丢弃", _hoverDiscard, false);
        DrawButton(canvas, _confirmButton, "确认并保存", _hoverConfirm, true);

        // 分割线
        canvas.FillRect(new Native.RECT(0, TopBarHeight, client.Width, TopBarHeight + 1), Native.PanelStroke);

        // ── 左：预览 ──
        canvas.FillRect(_previewViewport, Native.PreviewBackdrop);
        DrawPreview(canvas, model);
        DrawVerticalScrollbar(canvas, _previewScrollbar, _previewScrollY, PreviewMaxScrollY(), _previewViewport.Height);

        // ── 右：接缝列表 ──
        canvas.FillRect(
            new Native.RECT(client.Width - RightPanelWidth, TopBarHeight + 1, client.Width, client.Height),
            Native.PanelBackdrop);

        var listHeader = new Native.RECT(client.Width - RightPanelWidth + 12, TopBarHeight + 12, client.Width - 12, TopBarHeight + 36);
        DrawText(canvas, _fontSection, Native.TextPrimary, listHeader, "接缝列表",
            Native.DT_LEFT | Native.DT_SINGLELINE | Native.DT_NOPREFIX);

        if (model.LowConfidenceCount > 0)
        {
            var badge = new Native.RECT(
                client.Width - 12 - 110, TopBarHeight + 12, client.Width - 12, TopBarHeight + 32);
            DrawText(canvas, _fontCaption, Native.SeamBad, badge,
                $"{model.LowConfidenceCount} 个需检查",
                Native.DT_RIGHT | Native.DT_SINGLELINE | Native.DT_NOPREFIX);
        }

        DrawSeamRows(canvas, model, client);
        DrawVerticalScrollbar(canvas, _seamScrollbar, _seamScrollY, SeamMaxScrollY(), _seamViewport.Height);

        if (model.ErrorMessage is { } error)
        {
            DrawText(canvas, _fontCaption, Native.SeamBad,
                new Native.RECT(16, client.Height - 28, client.Width - RightPanelWidth - 16, client.Height - 10),
                error, Native.DT_LEFT | Native.DT_SINGLELINE | Native.DT_NOPREFIX | Native.DT_END_ELLIPSIS);
        }

        var hdc = Native.GetDC(Hwnd);
        try
        {
            canvas.BlitTo(hdc, new Native.RECT(0, 0, client.Width, client.Height));
        }
        finally
        {
            Native.ReleaseDC(Hwnd, hdc);
        }
    }

    private void ComputeLayout(Native.RECT client, SeamReviewModel model)
    {
        // 顶栏控件（从右往左）
        var confirmWidth = 110;
        var confirmLeft = client.Width - 16 - confirmWidth;
        _confirmButton = new Native.RECT(confirmLeft, TopBarHeight / 2 - 15, confirmLeft + confirmWidth, TopBarHeight / 2 + 15);

        var discardWidth = 72;
        var discardLeft = confirmLeft - 8 - discardWidth;
        _discardButton = new Native.RECT(discardLeft, TopBarHeight / 2 - 15, discardLeft + discardWidth, TopBarHeight / 2 + 15);

        var zoomWidth = 130;
        var zoomLeft = discardLeft - 12 - zoomWidth;
        _zoomTrack = new Native.RECT(zoomLeft, TopBarHeight / 2 - 2, zoomLeft + zoomWidth, TopBarHeight / 2 + 2);

        // 左预览（含分段选择器）
        var leftWidth = client.Width - RightPanelWidth;
        var pickerHeight = model.PreviewParts.Count > 1 ? SegmentTabHeight : 0;
        _previewViewport = new Native.RECT(
            8, TopBarHeight + 1 + pickerHeight + 8,
            leftWidth - ScrollbarWidth - 4, client.Height - 8);
        _previewScrollbar = new Native.RECT(
            _previewViewport.right + 2, _previewViewport.top + 2,
            _previewViewport.right + 2 + ScrollbarWidth, _previewViewport.bottom - 2);

        // 分段标签
        _tabs.Clear();
        if (model.PreviewParts.Count > 1)
        {
            var tabWidth = Math.Min(140, (leftWidth - 16) / model.PreviewParts.Count);
            for (var i = 0; i < model.PreviewParts.Count; i++)
            {
                _tabs.Add(new Native.RECT(
                    8 + i * (tabWidth + 6), TopBarHeight + 5,
                    8 + i * (tabWidth + 6) + tabWidth, TopBarHeight + 5 + 26));
            }
        }

        // 右侧列表
        _seamViewport = new Native.RECT(
            client.Width - RightPanelWidth + 2, TopBarHeight + 40,
            client.Width - 4, client.Height - 4);
        _seamScrollbar = new Native.RECT(
            _seamViewport.right - ScrollbarWidth - 2, _seamViewport.top + 2,
            _seamViewport.right - 2, _seamViewport.bottom - 2);

        // 行内按钮
        _rowButtons.Clear();
        lock (_sync)
        {
            for (var i = 0; i < _model.Segments.Count; i++)
            {
                var rowTop = _seamViewport.top + 6 + i * RowHeight - _seamScrollY;
                var buttonTop = rowTop + 56;
                var deltas = new[] { -10, -1, 1, 10 };
                var buttonWidth = 52;
                var x = _seamViewport.left + 12;
                foreach (var _ in deltas)
                {
                    _rowButtons.Add(new Native.RECT(x, buttonTop, x + buttonWidth, buttonTop + 26));
                    x += buttonWidth + 6;
                }
            }
        }
    }

    private void DrawZoomSlider(PixelCanvas canvas)
    {
        canvas.FillRoundRect(_zoomTrack, 2, 0x00423C36u);
        var knob = KnobRect();
        canvas.FillRoundRect(
            new Native.RECT(knob.left, knob.top, knob.right, knob.bottom),
            knob.Width / 2, Native.Accent);
    }

    private void DrawVerticalScrollbar(
        PixelCanvas canvas,
        Native.RECT track,
        int offset,
        int maxOffset,
        int viewportHeight)
    {
        canvas.FillRoundRect(track, track.Width / 2, 0x00201814u);
        if (maxOffset <= 0)
        {
            return;
        }

        var thumbHeight = Math.Max(24, track.Height * viewportHeight / (viewportHeight + maxOffset));
        var travel = track.Height - thumbHeight;
        var thumbTop = track.top + (int)(travel * (offset / (double)maxOffset));
        canvas.FillRoundRect(
            new Native.RECT(track.left, thumbTop, track.right, thumbTop + thumbHeight),
            track.Width / 2, 0x00504844u);
    }

    private void DrawPreview(PixelCanvas canvas, SeamReviewModel model)
    {
        if (model.PreviewParts.Count == 0 || _selectedPart >= _partBitmaps.Count)
        {
            DrawText(canvas, _fontBody, Native.TextSecondary,
                new Native.RECT(
                    _previewViewport.left + 20, _previewViewport.top + 20,
                    _previewViewport.right - 20, _previewViewport.bottom - 20),
                "暂无预览", Native.DT_CENTER | Native.DT_SINGLELINE | Native.DT_NOPREFIX);
            return;
        }

        // 分段选择器（对应 Mac: >1 段时的 Picker）
        for (var i = 0; i < _tabs.Count && i < model.PreviewParts.Count; i++)
        {
            var selected = i == _selectedPart;
            var hovered = _hoverTab == i;
            canvas.FillRoundRect(_tabs[i], 6, selected ? Native.Accent : (hovered ? 0x00423C36u : 0x00332E2Au));
            DrawText(canvas, _fontCaption, selected ? 0x00FFFFFFu : Native.TextSecondary,
                _tabs[i], $"第 {i + 1} / {model.PreviewParts.Count} 段",
                Native.DT_CENTER | Native.DT_VCENTER | Native.DT_SINGLELINE | Native.DT_NOPREFIX);
        }

        var (dc, _, srcWidth, srcHeight) = _partBitmaps[_selectedPart];
        var displayW = (int)(srcWidth * _zoom);
        var displayH = (int)(srcHeight * _zoom);

        _previewScrollX = Clamp(_previewScrollX, 0, PreviewMaxScrollX());
        _previewScrollY = Clamp(_previewScrollY, 0, PreviewMaxScrollY());

        var destX = _previewViewport.left - _previewScrollX;
        var destY = _previewViewport.top - _previewScrollY;

        var clip = canvas.Hdc;
        var region = Native.CreateRectRgn(
            _previewViewport.left, _previewViewport.top, _previewViewport.right, _previewViewport.bottom);
        var oldMode = Native.SetStretchBltMode(clip, Native.HALFTONE);
        Native.SelectClipRgn(clip, region);
        Native.StretchBlt(
            clip, destX, destY, displayW, displayH,
            dc, 0, 0, srcWidth, srcHeight, Native.SRCCOPY);
        Native.SelectClipRgn(clip, IntPtr.Zero);
        Native.SetStretchBltMode(clip, oldMode);
        Native.DeleteObject(region);
    }

    private void DrawSeamRows(PixelCanvas canvas, SeamReviewModel model, Native.RECT client)
    {
        lock (_sync)
        {
            for (var i = 0; i < _model.Segments.Count; i++)
            {
                var segment = _model.Segments[i];
                var rowTop = _seamViewport.top + 6 + i * RowHeight - _seamScrollY;
                if (rowTop + RowHeight < _seamViewport.top || rowTop > _seamViewport.bottom)
                {
                    continue;   // 视口裁剪
                }

                var row = new Native.RECT(
                    _seamViewport.left + 10, rowTop,
                    client.Width - ScrollbarWidth - 12, rowTop + RowHeight - 6);

                var background = segment.IsLowConfidence
                    ? 0x00281820u     // 红 0.08 压深底的近似
                    : (_hoverRow == i ? 0x00302C28u : 0x00262422u);
                canvas.FillRoundRect(row, 10, background);

                // 8×8 圆点
                var dot = new Native.RECT(row.left + 12, row.top + 14, row.left + 20, row.top + 22);
                var dotBrush = Native.CreateSolidBrush(segment.IsLowConfidence ? Native.SeamBad : Native.SeamGood);
                var oldBrush = Native.SelectObject(canvas.Hdc, dotBrush);
                Native.Ellipse(canvas.Hdc, dot.left, dot.top, dot.right, dot.bottom);
                Native.SelectObject(canvas.Hdc, oldBrush);
                Native.DeleteObject(dotBrush);

                // 标题行
                DrawText(canvas, _fontBody, Native.TextPrimary,
                    new Native.RECT(row.left + 28, row.top + 6, row.right - 70, row.top + 24),
                    $"接缝 {i + 1} · {(segment.Direction == ScrollDirection.Up ? "向上" : "向下")}",
                    Native.DT_LEFT | Native.DT_SINGLELINE | Native.DT_NOPREFIX);

                DrawText(canvas, _fontCaption,
                    segment.IsLowConfidence ? Native.SeamBad : Native.TextSecondary,
                    new Native.RECT(row.right - 70, row.top + 8, row.right - 12, row.top + 24),
                    $"{segment.Confidence * 100:0}%",
                    Native.DT_RIGHT | Native.DT_SINGLELINE | Native.DT_NOPREFIX);

                // 明细行
                DrawText(canvas, _fontCaption, Native.TextSecondary,
                    new Native.RECT(row.left + 28, row.top + 28, row.right - 12, row.top + 44),
                    $"新增 {segment.NewPixelHeight} px · 固定顶 {segment.StableTopHeight} · 固定底 {segment.StableBottomHeight}",
                    Native.DT_LEFT | Native.DT_SINGLELINE | Native.DT_NOPREFIX | Native.DT_END_ELLIPSIS);

                // 微调按钮 [-10, -1, +1, +10]
                var deltas = new[] { -10, -1, 1, 10 };
                for (var b = 0; b < deltas.Length; b++)
                {
                    var buttonIndex = i * 4 + b;
                    if (buttonIndex >= _rowButtons.Count)
                    {
                        break;
                    }

                    var rect = _rowButtons[buttonIndex];
                    var hovered = _hoverButton == buttonIndex;
                    canvas.FillRoundRect(rect, 6, hovered ? 0x00423C36u : 0x00332E2Au);
                    canvas.FrameRect(rect, Native.PanelStroke, 1);
                    DrawText(canvas, _fontButton, Native.TextPrimary, rect,
                        deltas[b] > 0 ? $"+{deltas[b]}" : $"{deltas[b]}",
                        Native.DT_CENTER | Native.DT_VCENTER | Native.DT_SINGLELINE | Native.DT_NOPREFIX);
                }
            }
        }
    }

    private void DrawButton(PixelCanvas canvas, Native.RECT rect, string label, bool hovered, bool primary)
    {
        var fill = primary
            ? (hovered ? Native.AccentHover : Native.Accent)
            : (hovered ? 0x00423C36u : 0x00332E2Au);
        var text = primary ? 0x00FFFFFFu : Native.TextPrimary;
        canvas.FillRoundRect(rect, 8, fill);
        canvas.FrameRect(rect, Native.PanelStroke, 1);
        DrawText(canvas, _fontBody, text, rect, label,
            Native.DT_CENTER | Native.DT_VCENTER | Native.DT_SINGLELINE | Native.DT_NOPREFIX);
    }

    private static IntPtr MakeFont(int height, int weight) => Native.CreateFontW(
        -height, 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei");

    private void DeleteFonts()
    {
        foreach (var font in new[] { _fontTitle, _fontSection, _fontBody, _fontCaption, _fontButton })
        {
            if (font != IntPtr.Zero)
            {
                Native.DeleteObject(font);
            }
        }

        _fontTitle = _fontSection = _fontBody = _fontCaption = _fontButton = IntPtr.Zero;
    }

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
}
