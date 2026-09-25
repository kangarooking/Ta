using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Drawing;
using Ta.Core.Capture;

namespace Ta.Platform.Overlay;

/// <summary>
/// 全屏选择覆盖层。
///
/// 对齐 Mac 版 SelectionOverlayPanel + SelectionOverlayView 的行为契约：
///   1. 覆盖鼠标所在显示器
///   2. 接收鼠标与键盘输入
///   3. 但**永不使本应用成为前台**
///
/// 实现要点：
///   · <c>WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST</c> + <c>SW_SHOWNOACTIVATE</c>
///   · 键盘经 <see cref="KeyboardHook"/> 接收（窗口不被激活时唯一的可行路径）
///   · 自身经 <c>WDA_EXCLUDEFROMCAPTURE</c> 排除出屏幕捕获
///   · 窗口在**独立线程**上跑自己的消息循环，不占用调用方线程
///
/// 对应 Mac 版的窗口标记映射见 <see cref="Native"/> 中各常量的注释。
/// </summary>
public sealed class SelectionOverlay : IDisposable
{
    private const string WindowClassName = "TaSelectionOverlay";

    private static readonly ConcurrentDictionary<IntPtr, OverlayState> States = new();
    private static readonly object ClassLock = new();
    private static Native.WndProcDelegate? _classWndProc;
    private static bool _classRegistered;

    private readonly OverlayOptions _options;
    private readonly KeyboardHook _hook;
    private readonly TaskCompletionSource<OverlayOutcome> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _uiThread;
    private readonly ManualResetEventSlim _ready = new(false);

    private IntPtr _hwnd;
    private bool _disposed;

    public SelectionOverlay(OverlayOptions? options = null)
    {
        _options = options ?? new OverlayOptions();
        _hook = new KeyboardHook();
        _hook.KeyPressed += OnHookKey;

        // 覆盖层在自己的线程上跑消息循环：钩子必须装在「有消息循环的线程」上，
        // 而调用方（主 App）不该被这个消息循环阻塞。
        _uiThread = new Thread(UiThreadMain) { IsBackground = true, Name = "Ta.Overlay" };
        _uiThread.SetApartmentState(ApartmentState.STA);
    }

    /// <summary>诊断信息。焦点是否保持不变可自动回归验证。</summary>
    public OverlayDiagnostics Diagnostics { get; private set; }

    /// <summary>完成时触发。与 <see cref="WaitAsync"/> 等价，供异步调用方使用。</summary>
    public event Action<OverlayOutcome>? Completed;

    /// <summary>显示覆盖层并等待用户操作。返回结果前不阻塞调用方线程。</summary>
    public Task<OverlayOutcome> ShowAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var foregroundBefore = Native.GetForegroundWindow();
        Diagnostics = new OverlayDiagnostics { ForegroundBefore = foregroundBefore };

        _uiThread.Start();
        _ready.Wait();

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(Cancel);
        }

        return _completion.Task;
    }

    /// <summary>同步等待结果。仅在非 UI 线程调用。</summary>
    public OverlayOutcome Show(CancellationToken cancellationToken = default) =>
        ShowAsync(cancellationToken).GetAwaiter().GetResult();

    /// <summary>取消选择，产生与 Esc 等价的取消结果。</summary>
    public void Cancel()
    {
        if (_hwnd != IntPtr.Zero)
        {
            Native.PostMessage(_hwnd, Native.WM_APP_CANCEL, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void UiThreadMain()
    {
        // 窗口过程运行在本线程，需让静态转发找到当前实例。
        // 单实例约定见 Current 的注释。
        Current = this;

        try
        {
            RunUiThread();
        }
        finally
        {
            Current = null;
        }
    }

    private void RunUiThread()
    {
        EnsureClassRegistered();

        // 目标显示器 = 鼠标当前所在的那一块。
        // 对应 Mac: SelectionOverlayController.screenUnderPointer()
        Native.GetCursorPos(out var cursor);
        var monitor = Native.MonitorFromPoint(cursor, Native.MONITOR_DEFAULTTONULL);
        var rect = monitor == IntPtr.Zero ? GetPrimaryRect() : GetMonitorRect(monitor);

        _hwnd = Native.CreateWindowEx(
            Native.WS_EX_LAYERED | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST,
            WindowClassName,
            "Ta Selection Overlay",
            Native.WS_POPUP,
            rect.left, rect.top, rect.Width, rect.Height,
            IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            _completion.TrySetException(new InvalidOperationException(
                $"CreateWindowEx 失败，Win32 错误 {Marshal.GetLastWin32Error()}"));
            _ready.Set();
            return;
        }

        var state = new OverlayState
        {
            Owner = this,
            Hwnd = _hwnd,
            Options = _options,
            ScreenOriginX = rect.left,
            ScreenOriginY = rect.top,
            ScreenWidth = rect.Width,
            ScreenHeight = rect.Height,
        };

        // 用字典按句柄反查状态，而非 GCHandle + GWLP_USERDATA ——
        // 后者需要手动固定/释放，容易在异常路径上泄漏。
        States[_hwnd] = state;

        if (_options.ExcludeSelfFromCapture)
        {
            // 对应 Mac 的 excludedWindowIDs；一行标志比排除清单更干净。
            Native.SetWindowDisplayAffinity(_hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
        }

        // ⚠️ 必须 WS_EX_LAYERED（上面 CreateWindowEx 已带），否则这里静默失败 ——
        // 窗口完全不透明：先显示未初始化表面（蓝块垃圾），填黑后就是全黑屏。
        if (!Native.SetLayeredWindowAttributes(_hwnd, 0, _options.DimAlpha, Native.LWA_ALPHA))
        {
            var err = Marshal.GetLastWin32Error();
            Native.DestroyWindow(_hwnd);
            _completion.TrySetException(new InvalidOperationException(
                $"SetLayeredWindowAttributes 失败，Win32 错误 {err}（缺 WS_EX_LAYERED？）"));
            _ready.Set();
            return;
        }

        if (_options.PresetSelection is { } preset)
        {
            state.Preset = preset;
        }

        // 快捷键按下即变十字光标 —— 对应 Mac 在覆盖层出现之前就设好光标。
        Native.SetCursor(Native.LoadCursor(IntPtr.Zero, Native.IDC_CROSS));

        // SW_SHOWNOACTIVATE —— 显示但**不激活**，这是方案的命门。
        // 对应 Mac: orderFrontRegardless() + canBecomeMain = false
        Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
        Native.UpdateWindow(_hwnd);

        // 覆盖层已就位，此时取样前台窗口才能判断焦点是否被抢。
        Diagnostics = Diagnostics with { ForegroundDuring = Native.GetForegroundWindow() };

        _ready.Set();
        RunMessageLoop(state);
    }

    private void RunMessageLoop(OverlayState state)
    {
        while (true)
        {
            // MsgWaitForMultipleObjects 让线程在无消息时休眠，避免空转烧 CPU。
            Native.MsgWaitForMultipleObjects(0, null, false, 100, Native.QS_ALLINPUT);

            while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, Native.PM_REMOVE))
            {
                if (msg.message == Native.WM_QUIT)
                {
                    Complete(OverlayOutcome.Cancelled());
                    return;
                }

                Native.TranslateMessage(in msg);
                Native.DispatchMessage(in msg);

                if (state.IsFinished)
                {
                    return;
                }
            }

            if (state.IsFinished)
            {
                return;
            }
        }
    }

    private void OnHookKey(int virtualKey)
    {
        Diagnostics.SawKeyViaHook = true;

        if (virtualKey != KeyboardHook.VkEscape || _hwnd == IntPtr.Zero)
        {
            return;
        }

        // 钩子跑在本线程上，直接投递自定义消息以切回窗口线程收尾。
        Native.PostMessage(_hwnd, Native.WM_APP_CANCEL, IntPtr.Zero, IntPtr.Zero);
    }

    private void Complete(OverlayOutcome outcome)
    {
        Diagnostics = Diagnostics with { ForegroundAfter = Native.GetForegroundWindow() };

        if (_hwnd != IntPtr.Zero)
        {
            States.TryRemove(_hwnd, out _);
            Native.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        _hook.Dispose();

        if (_completion.TrySetResult(outcome))
        {
            Completed?.Invoke(outcome);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (!States.TryGetValue(hwnd, out var state))
        {
            return Native.DefWindowProc(hwnd, message, wParam, lParam);
        }

        switch (message)
        {
            case Native.WM_SETCURSOR:
                // 低字节为命中测试码；仅当光标位于客户区时才覆盖。
                var hitTest = lParam.ToInt64() & 0xFFFF;
                if (hitTest == 1)
                {
                    Native.SetCursor(state.CursorForCurrentPoint());
                    return IntPtr.Zero;
                }

                break;

            case Native.WM_MOUSEMOVE:
                state.Cursor = LParamToPoint(lParam);
                if (state.ToolbarVisible)
                {
                    var hover = HitTestToolbar(state, state.Cursor);
                    if (hover != state.ToolbarHover)
                    {
                        state.ToolbarHover = hover;
                        Invalidate(state);
                    }

                    return IntPtr.Zero;
                }

                if (state.IsDragging)
                {
                    state.DragCurrent = state.Cursor;
                    Invalidate(state);
                }

                return IntPtr.Zero;

            case Native.WM_LBUTTONDOWN:
                if (state.ToolbarVisible)
                {
                    // 操作栏模式：命中按钮 → 以对应动作完成；点空白 → 忽略（防误触丢图）。
                    var hitIndex = HitTestToolbar(state, LParamToPoint(lParam));
                    OverlayTrace.Write(
                        $"工具栏点击 hit={hitIndex} 按钮数={state.ToolbarButtons.Count}");
                    if (hitIndex >= 0)
                    {
                        var action = state.ToolbarButtons[hitIndex].Action;
                        OverlayTrace.Write(
                            $"工具栏动作索引={action} 标签={state.ToolbarButtons[hitIndex].Label}");
                        Complete(OverlayOutcome.Selected(
                            state.NormalizedSelectionAsRect(), OverlayTrigger.Drag, action));
                    }

                    return IntPtr.Zero;
                }

                state.IsDragging = true;
                state.DragStart = state.DragCurrent = LParamToPoint(lParam);
                Native.SetCapture(hwnd);
                Invalidate(state);
                return IntPtr.Zero;

            case Native.WM_LBUTTONUP:
                if (!state.IsDragging)
                {
                    return IntPtr.Zero;
                }

                state.IsDragging = false;
                Native.ReleaseCapture();
                state.DragCurrent = LParamToPoint(lParam);

                // 对应 Mac：拖拽距离 < 3pt 视为点击而非框选；
                // 小于 4×4 时静默重置为无选区（不是取消）。
                var distance = Math.Abs(state.DragCurrent.x - state.DragStart.x)
                               + Math.Abs(state.DragCurrent.y - state.DragStart.y);
                if (distance < Native.DragThreshold)
                {
                    state.DragStart = default;
                    Invalidate(state);
                    return IntPtr.Zero;
                }

                // 配置了操作栏 → 拖选完成后不立即结束：浮出动作按钮等待点选
                //（对应 Mac 版选区下方浮出工具栏）。Esc / 右键仍可取消。
                if (state.Options.ShowsActionToolbar
                    && state.Options.ToolbarActions is { Count: > 0 })
                {
                    EnterToolbarMode(state);
                    return IntPtr.Zero;
                }

                Complete(OverlayOutcome.Selected(state.NormalizedSelectionAsRect(), OverlayTrigger.Drag));
                return IntPtr.Zero;

            case Native.WM_RBUTTONDOWN:
                // 右键任意位置取消。对应 Mac: SelectionOverlayView.rightMouseDown()
                Complete(OverlayOutcome.Cancelled());
                return IntPtr.Zero;

            case Native.WM_KEYDOWN:
                // 覆盖层通常收不到按键（窗口未激活），此分支是兜底；
                // 正常路径由键盘钩子处理。
                HandleKey(state, (int)wParam.ToInt64());
                return IntPtr.Zero;

            case Native.WM_APP_CANCEL:
                Complete(OverlayOutcome.Cancelled());
                return IntPtr.Zero;

            case Native.WM_APP_ENTER:
                if (state.CurrentSelection() is { } rect)
                {
                    Complete(OverlayOutcome.Selected(rect, OverlayTrigger.EnterKey));
                }

                return IntPtr.Zero;

            case Native.WM_ERASEBKGND:
                // 遮罩 = 黑色窗口 + LWA_ALPHA(107)（Mac: black 0.42 dim）。
                // 类画刷为 NULL 且 Paint 只画交互元素 —— 背景不自绘的话，
                // 客户区就是未初始化的 DWM 表面（整屏蓝块 + 竖条纹垃圾）。
                Native.GetClientRect(hwnd, out var bgRect);
                Native.FillRect(wParam, ref bgRect,
                    Native.GetStockObject(Native.BLACK_BRUSH));
                return (IntPtr)1;

            case Native.WM_PAINT:
                Paint(hwnd, state);
                return IntPtr.Zero;

            case Native.WM_DESTROY:
                Native.PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return Native.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void HandleKey(OverlayState state, int virtualKey)
    {
        if (virtualKey == KeyboardHook.VkEscape)
        {
            Complete(OverlayOutcome.Cancelled());
            return;
        }

        // 对应 Mac: showsActionToolbar && (36 || 76) 且在选区内 → 提交为 copyImage。
        // 无操作栏的快捷键直达模式不接受 Enter。
        if (state.Options.ShowsActionToolbar
            && (virtualKey == KeyboardHook.VkReturn || virtualKey == KeyboardHook.VkNumpadEnter)
            && state.CurrentSelection() is not null)
        {
            Complete(OverlayOutcome.Selected(state.CurrentSelection()!.Value, OverlayTrigger.EnterKey));
        }
    }

    private void Paint(IntPtr hwnd, OverlayState state)
    {
        var hdc = Native.BeginPaint(hwnd, out var ps);
        try
        {
            var full = ps.rcPaint;

            // 遮罩已由 LWA_ALPHA 提供，这里只画交互元素。
            if (state.ToolbarVisible)
            {
                // 操作栏模式：保留选区框 + 画动作按钮排（不再画十字准线）。
                DrawSelection(hdc, state.NormalizedSelection());
                DrawToolbar(hdc, state);
            }
            else
            {
                DrawCrosshair(hdc, state, in full);
                if (state.IsDragging)
                {
                    DrawSelection(hdc, state.NormalizedSelection());
                }
            }
        }
        finally
        {
            Native.EndPaint(hwnd, in ps);
        }
    }

    private static void DrawCrosshair(IntPtr hdc, OverlayState state, in Native.RECT full)
    {
        var pen = Native.CreatePen(Native.PS_SOLID, 1, Native.AccentColorRef);
        var old = Native.SelectObject(hdc, pen);

        Native.MoveToEx(hdc, full.left, state.Cursor.y, IntPtr.Zero);
        Native.LineTo(hdc, full.right, state.Cursor.y);

        Native.MoveToEx(hdc, state.Cursor.x, full.top, IntPtr.Zero);
        Native.LineTo(hdc, state.Cursor.x, full.bottom);

        Native.SelectObject(hdc, old);
        Native.DeleteObject(pen);
    }

    private void EnterToolbarMode(OverlayState state)
    {
        state.FinishedSelection = state.NormalizedSelection();
        LayoutToolbar(state);
        OverlayTrace.Write(
            $"进入操作栏 按钮数={state.ToolbarButtons.Count} 选区={state.FinishedSelection.Width}x{state.FinishedSelection.Height}");
        state.ToolbarVisible = true;
        state.ToolbarHover = -1;
        Invalidate(state);
    }

    /// <summary>布局动作按钮：水平排列，选区下方；越出屏幕则翻到选区上方。</summary>
    private static void LayoutToolbar(OverlayState state)
    {
        state.ToolbarButtons.Clear();
        var actions = state.Options.ToolbarActions;
        if (actions is null || actions.Count == 0)
        {
            return;
        }

        const int buttonWidth = 48;
        const int buttonHeight = 32;
        const int gap = 3;
        const int padding = 5;

        var totalWidth = padding * 2 + actions.Count * buttonWidth + (actions.Count - 1) * gap;
        var sel = state.FinishedSelection;

        var x = sel.left + (sel.Width - totalWidth) / 2;
        x = Math.Max(4, Math.Min(x, state.ScreenWidth - totalWidth - 4));

        var y = sel.bottom + 10;
        if (y + buttonHeight + padding * 2 > state.ScreenHeight)
        {
            y = Math.Max(4, sel.top - 10 - buttonHeight - padding * 2);
        }

        state.ToolbarBounds = new Native.RECT(x, y, x + totalWidth, y + buttonHeight + padding * 2);

        for (var i = 0; i < actions.Count; i++)
        {
            var bx = x + padding + i * (buttonWidth + gap);
            var by = y + padding;
            state.ToolbarButtons.Add(
                (new Native.RECT(bx, by, bx + buttonWidth, by + buttonHeight),
                    actions[i].Label, actions[i].Action));
        }
    }

    /// <summary>绘制操作栏：深色圆角底 + 动作按钮（悬停高亮）。</summary>
    private static void DrawToolbar(IntPtr hdc, OverlayState state)
    {
        using var graphics = System.Drawing.Graphics.FromHdc(hdc);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var bounds = state.ToolbarBounds;
        var rect = new RectangleF(bounds.left, bounds.top, bounds.Width, bounds.Height);
        using var path = RoundedRectPath(rect, 10);
        using var background = new SolidBrush(System.Drawing.Color.FromArgb(246, 24, 24, 30));
        graphics.FillPath(background, path);
        using var outline = new Pen(System.Drawing.Color.FromArgb(60, 255, 255, 255), 1);
        graphics.DrawPath(outline, path);

        using var font = new Font("Microsoft YaHei", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        for (var i = 0; i < state.ToolbarButtons.Count; i++)
        {
            var (buttonRect, label, _) = state.ToolbarButtons[i];
            var hovered = i == state.ToolbarHover;
            var buttonBounds = new RectangleF(buttonRect.left, buttonRect.top,
                buttonRect.Width, buttonRect.Height);

            if (hovered)
            {
                using var hoverBrush = new SolidBrush(System.Drawing.Color.FromArgb(210, 200, 60, 50));
                using var hoverPath = RoundedRectPath(buttonBounds, 7);
                graphics.FillPath(hoverBrush, hoverPath);
            }

            using var textBrush = new SolidBrush(
                hovered ? System.Drawing.Color.White : System.Drawing.Color.FromArgb(225, 235, 240));
            var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
            };
            graphics.DrawString(label, font, textBrush, buttonBounds, format);
        }
    }

    /// <summary>连续圆角矩形路径（GDI+）。</summary>
    private static System.Drawing.Drawing2D.GraphicsPath RoundedRectPath(RectangleF bounds, float radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
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

    private static int HitTestToolbar(OverlayState state, Native.POINT point)
    {
        for (var i = 0; i < state.ToolbarButtons.Count; i++)
        {
            var r = state.ToolbarButtons[i].Rect;
            if (point.x >= r.left && point.x < r.right && point.y >= r.top && point.y < r.bottom)
            {
                return i;
            }
        }

        return -1;
    }

    private static void DrawSelection(IntPtr hdc, Native.RECT rect)
    {
        // 空心矩形（NULL_BRUSH），不盖住选区内的真实内容。
        var oldBrush = Native.SelectObject(hdc, Native.GetStockObject(Native.NULL_BRUSH));
        var pen = Native.CreatePen(Native.PS_SOLID, 2, Native.AccentColorRef);
        var oldPen = Native.SelectObject(hdc, pen);

        Native.Rectangle(hdc, rect.left, rect.top, rect.right, rect.bottom);

        Native.SelectObject(hdc, oldPen);
        Native.SelectObject(hdc, oldBrush);
        Native.DeleteObject(pen);
    }

    private static void Invalidate(OverlayState state)
    {
        var full = new Native.RECT(0, 0, state.ScreenWidth, state.ScreenHeight);
        // bErase 必须为 true：否则重绘不擦背景，1px 准线在鼠标扫过的每个
        // 位置堆积成巨型十字带（实测：HDR 屏上还带过曝发光）。
        Native.InvalidateRect(state.Hwnd, in full, true);
    }

    private static Native.POINT LParamToPoint(IntPtr lParam)
    {
        var value = lParam.ToInt64();
        return new Native.POINT((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
    }

    private static Native.RECT GetMonitorRect(IntPtr monitor)
    {
        var info = Native.MONITORINFO.Create();
        return Native.GetMonitorInfo(monitor, ref info) ? info.rcMonitor : GetPrimaryRect();
    }

    private static Native.RECT GetPrimaryRect()
    {
        var info = Native.MONITORINFO.Create();
        var primary = Native.MonitorFromPoint(new Native.POINT(0, 0), Native.MONITOR_DEFAULTTOPRIMARY);
        return Native.GetMonitorInfo(primary, ref info) ? info.rcMonitor : new Native.RECT(0, 0, 1920, 1080);
    }

    private static void EnsureClassRegistered()
    {
        lock (ClassLock)
        {
            if (_classRegistered)
            {
                return;
            }

            _classWndProc = StaticWndProc;

            unsafe
            {
                fixed (char* className = WindowClassName)
                {
                    var wc = new Native.WNDCLASSEXW
                    {
                        cbSize = (uint)sizeof(Native.WNDCLASSEXW),
                        style = 0x0003,   // CS_HREDRAW | CS_VREDRAW
                        lpfnWndProc = _classWndProc,
                        cbClsExtra = 0,
                        cbWndExtra = 0,
                        hInstance = Native.GetModuleHandle(null),
                        hIcon = IntPtr.Zero,
                        hCursor = Native.LoadCursor(IntPtr.Zero, Native.IDC_CROSS),
                        hbrBackground = IntPtr.Zero,
                        lpszMenuName = null,
                        lpszClassName = WindowClassName,
                        hIconSm = IntPtr.Zero,
                    };

                    if (Native.RegisterClassEx(in wc) == 0)
                    {
                        // 已注册（ERROR_CLASS_ALREADY_EXISTS）时视为成功 ——
                        // 静态类名在多次 Show 之间复用是预期行为。
                        if (Marshal.GetLastWin32Error() != 1410)
                        {
                            throw new InvalidOperationException(
                                $"RegisterClassEx 失败，Win32 错误 {Marshal.GetLastWin32Error()}");
                        }
                    }
                }
            }

            _classRegistered = true;
        }
    }

    private static IntPtr StaticWndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam) =>
        Current?.WndProc(hwnd, message, wParam, lParam) ?? Native.DefWindowProc(hwnd, message, wParam, lParam);

    // 单实例约定：同一时刻只允许一个覆盖层（Mac 版同样如此，
    // SelectionOverlayController 用 panel == nil && !isStarting 做重入保护）。
    [ThreadStatic]
    private static SelectionOverlay? Current;

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hook.Dispose();
        _ready.Dispose();
    }
}
