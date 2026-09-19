using System.Runtime.InteropServices;

namespace Ta.Spike.Overlay;

/// <summary>
/// 全屏选择覆盖层。
///
/// 对齐 Mac 版 SelectionOverlayPanel + SelectionOverlayView 的行为契约：
///   1. 覆盖鼠标所在显示器
///   2. 接收鼠标与键盘输入
///   3. 但**永不使本应用成为前台**
///
/// 坐标约定：本验证程序按整屏单一显示器处理，屏幕原点即客户区原点，
/// 因此无需 Mac 版那样的 panel.convertToScreen() 换算（多点屏与负原点
/// 的处理留到生产版本，见 Windows移植参考文档 §5.1）。
/// </summary>
internal sealed class OverlayWindow : IDisposable
{
    private readonly Native.WndProcDelegate _wndProc;
    private readonly IntPtr _hwnd;
    private readonly GCHandle _selfHandle;

    private bool _isDragging;
    private Native.POINT _dragStart;
    private Native.POINT _dragCurrent;
    private Native.POINT _cursorPos;
    private bool _disposed;

    public OverlayResult? Result { get; private set; }

    public unsafe OverlayWindow()
    {
        _wndProc = WndProc;

        // 类注册与窗口创建都放在本线程；低级钩子同样要求所在线程有消息循环。
        fixed (char* className = "TaOverlaySpike")
        {
            var wc = new Native.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(Native.WNDCLASSEXW),
                style = 0x0003, // CS_HREDRAW | CS_VREDRAW
                lpfnWndProc = _wndProc,
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = Native.GetModuleHandle(null),
                hIcon = IntPtr.Zero,
                hCursor = Native.LoadCursor(IntPtr.Zero, Native.IDC_CROSS),
                hbrBackground = IntPtr.Zero,
                lpszMenuName = null,
                lpszClassName = "TaOverlaySpike",
                hIconSm = IntPtr.Zero,
            };

            if (Native.RegisterClassEx(in wc) == 0)
            {
                throw new InvalidOperationException(
                    $"RegisterClassEx 失败，Win32 错误 {Marshal.GetLastWin32Error()}");
            }
        }

        // 目标显示器 = 鼠标当前所在的那一块。
        // 对应 Mac: SelectionOverlayController.screenUnderPointer()
        Native.GetCursorPos(out _cursorPos);
        var monitor = Native.MonitorFromPoint(_cursorPos, Native.MONITOR_DEFAULTTONULL);
        var monitorRect = monitor == IntPtr.Zero ? GetDesktopRect() : GetMonitorRect(monitor);

        _hwnd = Native.CreateWindowEx(
            Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST,
            "TaOverlaySpike",
            "Ta Overlay Spike",
            Native.WS_POPUP,
            monitorRect.left,
            monitorRect.top,
            monitorRect.Width,
            monitorRect.Height,
            IntPtr.Zero,
            IntPtr.Zero,
            Native.GetModuleHandle(null),
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"CreateWindowEx 失败，Win32 错误 {Marshal.GetLastWin32Error()}");
        }

        // 自身排除出屏幕捕获 —— 对应 Mac 的 excludedWindowIDs。
        Native.SetWindowDisplayAffinity(_hwnd, Native.WDA_EXCLUDEFROMCAPTURE);

        // 整窗统一透明度，近似 Mac 的黑色遮罩 + isOpaque = false。
        Native.SetLayeredWindowAttributes(_hwnd, 0, Native.OverlayAlpha, Native.LWA_ALPHA);

        // 挂回 this，供 WndProc 反查（生产版应改用 per-window 状态对象）。
        _selfHandle = GCHandle.Alloc(this);
        Native.SetWindowLongPtr(_hwnd, Native.GWLP_USERDATA, GCHandle.ToIntPtr(_selfHandle));
    }

    public void Show()
    {
        // SW_SHOWNOACTIVATE —— 显示但**不激活**，这是方案的命门。
        // 对应 Mac: orderFrontRegardless() + canBecomeMain = false
        Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
        Native.UpdateWindow(_hwnd);
    }

    /// <summary>取消选择，产生与 Escape 等价的取消结果。</summary>
    public void Cancel()
    {
        Complete(null);
    }

    /// <summary>供消息循环判断是否已结束。</summary>
    public bool IsFinished => Result is not null;

    private void Complete(Native.RECT? selection)
    {
        if (Result is not null)
        {
            return;
        }

        Result = selection is null
            ? OverlayResult.Cancelled()
            : OverlayResult.Selected(ToRect(selection.Value));

        Native.DestroyWindow(_hwnd);
    }

    private void Invalidate()
    {
        // 不能把方法调用结果直接作为 in 实参传递，需先落到局部变量。
        var full = GetDesktopRect();
        Native.InvalidateRect(_hwnd, in full, false);
    }

    private static Rect ToRect(Native.RECT r) => new(r.left, r.top, r.right, r.bottom);

    private static Native.RECT GetMonitorRect(IntPtr monitor)
    {
        var info = Native.MONITORINFO.Create();
        if (!Native.GetMonitorInfo(monitor, ref info))
        {
            return GetDesktopRect();
        }

        return info.rcMonitor;
    }

    private static Native.RECT GetDesktopRect()
    {
        // 兜底：拿不到显示器信息时退到整个虚拟桌面。
        var info = Native.MONITORINFO.Create();
        var primary = Native.MonitorFromPoint(new Native.POINT(0, 0), Native.MONITOR_DEFAULTTOPRIMARY);
        return Native.GetMonitorInfo(primary, ref info) ? info.rcMonitor : new Native.RECT(0, 0, 1920, 1080);
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Native.WM_LBUTTONDOWN:
                _isDragging = true;
                _dragStart = _dragCurrent = LParamToPoint(lParam);
                Native.SetCapture(hwnd);
                Invalidate();
                return IntPtr.Zero;

            case Native.WM_MOUSEMOVE:
                _cursorPos = LParamToPoint(lParam);
                if (_isDragging)
                {
                    _dragCurrent = _cursorPos;
                    Invalidate();
                }

                return IntPtr.Zero;

            case Native.WM_LBUTTONUP:
                if (!_isDragging)
                {
                    return IntPtr.Zero;
                }

                _isDragging = false;
                Native.ReleaseCapture();
                _dragCurrent = LParamToPoint(lParam);

                // 对应 Mac：拖拽距离 < 3pt 视为点击而非框选；
                // 小于 4×4 时静默重置为无选区（不是取消）。
                var distance = Math.Abs(_dragCurrent.x - _dragStart.x)
                               + Math.Abs(_dragCurrent.y - _dragStart.y);
                if (distance < Native.DragThreshold)
                {
                    Invalidate();
                    return IntPtr.Zero;
                }

                Complete(NormalizedSelection());
                return IntPtr.Zero;

            case Native.WM_RBUTTONDOWN:
                // 右键任意位置取消。对应 Mac: SelectionOverlayView.rightMouseDown()
                Cancel();
                return IntPtr.Zero;

            case Native.WM_KEYDOWN:
                // 覆盖层通常收不到按键（窗口未激活），此分支是兜底；
                // 正常路径由低级键盘钩子处理。
                if ((int)wParam.ToInt64() == Native.VK_ESCAPE)
                {
                    Cancel();
                    return IntPtr.Zero;
                }

                break;

            case Native.WM_PAINT:
                Paint(hwnd);
                return IntPtr.Zero;

            case Native.WM_DESTROY:
                Native.PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private Native.RECT NormalizedSelection()
    {
        var left = Math.Min(_dragStart.x, _dragCurrent.x);
        var top = Math.Min(_dragStart.y, _dragCurrent.y);
        var right = Math.Max(_dragStart.x, _dragCurrent.x);
        var bottom = Math.Max(_dragStart.y, _dragCurrent.y);

        // 整屏模式下屏幕原点即客户区原点，无需坐标换算。
        return new Native.RECT(left, top, right, bottom);
    }

    private void Paint(IntPtr hwnd)
    {
        var hdc = Native.BeginPaint(hwnd, out var ps);
        try
        {
            var full = ps.rcPaint;

            // 遮罩已由 LWA_ALPHA 提供，这里只画交互元素。
            DrawCrosshair(hdc, in full);
            if (_isDragging)
            {
                DrawSelection(hdc);
            }
        }
        finally
        {
            Native.EndPaint(hwnd, in ps);
        }
    }

    private void DrawCrosshair(IntPtr hdc, in Native.RECT full)
    {
        var pen = Native.CreatePen(Native.PS_SOLID, 1, Native.AccentColorRef);
        var old = Native.SelectObject(hdc, pen);

        Native.MoveToEx(hdc, full.left, _cursorPos.y, IntPtr.Zero);
        Native.LineTo(hdc, full.right, _cursorPos.y);

        Native.MoveToEx(hdc, _cursorPos.x, full.top, IntPtr.Zero);
        Native.LineTo(hdc, _cursorPos.x, full.bottom);

        Native.SelectObject(hdc, old);
        Native.DeleteObject(pen);
    }

    private void DrawSelection(IntPtr hdc)
    {
        var rect = NormalizedSelection();

        // 空心矩形（NULL_BRUSH），这样不会盖住选区内的真实内容。
        var oldBrush = Native.SelectObject(hdc, Native.GetStockObject(Native.NULL_BRUSH));
        var pen = Native.CreatePen(Native.PS_SOLID, 2, Native.AccentColorRef);
        var oldPen = Native.SelectObject(hdc, pen);

        Native.Rectangle(hdc, rect.left, rect.top, rect.right, rect.bottom);

        Native.SelectObject(hdc, oldPen);
        Native.SelectObject(hdc, oldBrush);
        Native.DeleteObject(pen);
    }

    private static Native.POINT LParamToPoint(IntPtr lParam)
    {
        var value = lParam.ToInt64();
        var x = (short)(value & 0xFFFF);
        var y = (short)((value >> 16) & 0xFFFF);
        return new Native.POINT(x, y);
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
            Native.DestroyWindow(_hwnd);
        }

        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }
    }
}
