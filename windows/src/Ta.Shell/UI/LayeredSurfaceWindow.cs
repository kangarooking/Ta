using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Ta.Shell.UI;

/// <summary>
/// 带逐像素 alpha 的顶层非激活窗口。
///
/// ## 为什么需要它
///
/// Mac 版托盘 popover 与结果反馈条都是 NSPanel：圆角、阴影、半透明由
/// AppKit 免费提供。Win32 上没有等价物 —— 常规窗口只能整窗一个 alpha
/// （<c>SetLayeredWindowAttributes</c>），圆角会出现锯齿黑边。
/// 因此这里用 <c>UpdateLayeredWindow</c> + 32bpp PARGB 位图实现逐像素 alpha，
/// 把 GDI+ 抗锯齿画好的位图整张贴上去。
///
/// ## 与 Mac 版的窗口标记对照
///
/// | Mac 版 | 本实现 |
/// |---|---|
/// | <c>.borderless</c> | <c>WS_POPUP</c>（无边框无标题） |
/// | <c>.nonactivatingPanel</c> | <c>WS_EX_NOACTIVATE</c> |
/// | <c>level = .floating</c> | <c>WS_EX_TOPMOST</c> |
/// | <c>hidesOnDeactivate = false</c> | 不响应 <c>WM_ACTIVATE</c> 隐藏（由子类决定） |
/// | <c>.canJoinAllSpaces/.fullScreenAuxiliary</c> | <c>WS_EX_TOOLWINDOW</c> + 不进任务栏 |
/// | <c>collectionBehavior = .transient</c> | <c>WM_ACTIVATE</c> / 点击外部即收起 |
///
/// ⚠️ <c>WS_EX_NOACTIVATE</c> 是本方案的命门：Ta 自身窗口<b>绝不能抢焦点</b>，
/// 否则用户截到的图里会带上 Ta，且会打断正在被截的应用（任务书 E 项）。
/// </summary>
internal sealed class LayeredSurfaceWindow : IDisposable
{
    private const string ClassName = "TaLayeredSurface";
    private static readonly object ClassLock = new();
    private static ushort _classAtom;
    private static WndProcDelegate? _wndProc;

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_NOACTIVATE = 0x08000000;

    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_MOUSELEAVE = 0x02A3;
    private const uint WM_TIMER = 0x0113;
    private const uint WM_MOUSEHOVER = 0x02A1;
    private const uint WM_ACTIVATE = 0x0006;
    private const uint WM_SETCURSOR = 0x0020;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_NCDESTROY = 0x0082;

    private const uint ULW_ALPHA = 0x00000002;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;

    // SetWindowDisplayAffinity 的取值。
    private const uint WDA_NONE = 0x00000000;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    private const int SW_SHOWNOACTIVATE = 4;
    private const int SW_HIDE = 0;
    private const int IDC_ARROW = 32512;
    private const uint TME_LEAVE = 0x00000002;

    private IntPtr _hwnd;
    private Size _size;
    private Bitmap? _surface;
    private bool _trackingMouse;
    private bool _disposed;

    /// <summary>已注册窗口类的模块句柄（进程级缓存）。</summary>
    private static readonly IntPtr ModuleHandle = GetModuleHandle(null);

    /// <summary>鼠标在窗口内移动。</summary>
    public event Action<Point>? MouseMove;

    /// <summary>鼠标左键按下/抬起。</summary>
    public event Action<Point>? MouseDown;
    public event Action<Point>? MouseUp;

    /// <summary>鼠标右键抬起（用于弹出上下文菜单 / 关闭 popover）。</summary>
    public event Action<Point>? RightClick;

    /// <summary>鼠标离开窗口。</summary>
    public event Action? MouseLeave;

    /// <summary>
    /// Win32 定时器到期（<c>WM_TIMER</c>）。
    ///
    /// 为什么不用 <see cref="System.Threading.Timer"/>：定时器回调跑在线程池线程上，
    /// 而 <c>ShowWindow</c> 等窗口操作<b>线程亲和</b> —— 跨线程调用可能失败甚至死锁。
    /// Win32 定时器把消息投到窗口所属线程，回调天然落在正确的线程上。
    /// </summary>
    public event Action? Timer;

    /// <summary>当前定时器 id；0 表示没有。</summary>
    public IntPtr TimerId { get; private set; }

    /// <summary>启动一次性定时器（毫秒）。重复调用会先取消上一个。</summary>
    public void StartTimer(int milliseconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        StopTimer();
        if (milliseconds <= 0 || _hwnd == IntPtr.Zero)
        {
            return;
        }

        TimerId = SetTimer(_hwnd, IntPtr.Zero, (IntPtr)milliseconds, IntPtr.Zero);
    }

    /// <summary>取消定时器。</summary>
    public void StopTimer()
    {
        if (TimerId == IntPtr.Zero || _hwnd == IntPtr.Zero)
        {
            TimerId = IntPtr.Zero;
            return;
        }

        KillTimer(_hwnd, TimerId);
        TimerId = IntPtr.Zero;
    }

    /// <summary>
    /// 窗口被激活（对非激活窗口罕见，但 NoActivate 窗口仍可能因键盘导航被激活）。
    /// 对应 Mac 版 <c>applicationDidResignActive</c> 触发 popover 收起。
    /// </summary>
    public event Action? Activated;

    public bool IsVisible { get; private set; }

    /// <summary>窗口左上角位置（屏幕坐标）。</summary>
    public Point Location { get; private set; }

    public Size Size => _size;

    public IntPtr Handle => _hwnd;

    /// <summary>
    /// 创建窗口。<b>必须在 STA 线程上调用</b>（创建窗口的线程即消息循环线程）。
    /// </summary>
    public LayeredSurfaceWindow(string title = "Ta")
    {
        EnsureClassRegistered();
        _size = new Size(1, 1);
        Title = title;
    }

    public string Title { get; }

    /// <summary>创建并隐藏窗口。尺寸会在 <see cref="Present"/> 时按位图大小更新。</summary>
    public void Create(Size size)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _size = size;
        _hwnd = CreateWindowExW(
            WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE,
            ClassName,
            Title,
            WS_POPUP,
            0, 0, Math.Max(1, size.Width), Math.Max(1, size.Height),
            IntPtr.Zero, IntPtr.Zero, ModuleHandle, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"CreateWindowExW 失败，Win32 错误 {error}。");
        }

        RegisterInstance();
    }

    /// <summary>
    /// 把位图贴到窗口上（逐像素 alpha），并按需调整窗口尺寸与位置。
    /// </summary>
    public void Present(Bitmap bitmap, Point? location = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(bitmap);

        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("窗口尚未创建，请先调用 Create()。");
        }

        _surface?.Dispose();
        // 复制一份：调用方可能复用或释放传入的位图（GDI+ 位图不能跨线程随便用）。
        _surface = new Bitmap(bitmap);

        _size = new Size(Math.Max(1, _surface.Width), Math.Max(1, _surface.Height));
        if (location is { } at)
        {
            Location = at;
        }

        SetWindowPos(_hwnd, IntPtr.Zero, Location.X, Location.Y, _size.Width, _size.Height,
            SWP_NOACTIVATE | SWP_NOZORDER | SWP_SHOWWINDOW);

        var screen = Graphics.FromHwnd(IntPtr.Zero);
        var sourcePoint = new System.Drawing.Point(0, 0);
        var topPos = new System.Drawing.Point(Location.X, Location.Y);
        var blend = new BLENDFUNCTION
        {
            BlendOp = AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = AC_SRC_ALPHA,
        };

        // 位图的 HDC 要通过 Graphics 取 —— Image.GetHdc() 在部分 TFM 上不可见。
        using var surfaceGraphics = Graphics.FromImage(_surface);

        var surfaceDc = surfaceGraphics.GetHdc();
        var screenDc = screen.GetHdc();
        try
        {
            var sizeRef = new Size(_size.Width, _size.Height);
            var success = UpdateLayeredWindow(
                _hwnd, screenDc, ref topPos, ref sizeRef,
                surfaceDc, ref sourcePoint, 0, ref blend, ULW_ALPHA);

            if (!success)
            {
                var error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"UpdateLayeredWindow 失败，Win32 错误 {error}。");
            }
        }
        finally
        {
            surfaceGraphics.ReleaseHdc(surfaceDc);
            screen.ReleaseHdc(screenDc);
            screen.Dispose();
        }

        IsVisible = true;
    }

    /// <summary>移动到新位置（不重绘内容）。</summary>
    public void MoveTo(Point location)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        Location = location;
        SetWindowPos(_hwnd, IntPtr.Zero, location.X, location.Y, 0, 0,
            SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOSIZE);
    }

    public void Show()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        // SW_SHOWNOACTIVATE —— 显示但不激活。
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        IsVisible = true;
    }

    public void Hide()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        ShowWindow(_hwnd, SW_HIDE);
        IsVisible = false;
    }

    /// <summary>
    /// 把本窗口排除出屏幕捕获。
    ///
    /// 对应 Mac 版的 <c>excludedWindowIDs</c>（移植参考文档 §5.4 面板配置），
    /// Windows 上一行 <c>WDA_EXCLUDEFROMCAPTURE</c> 即可 ——
    /// 与 <c>Ta.Platform.Overlay.SelectionOverlay</c> 用的是同一个标志。
    ///
    /// 这是任务书 E 项（「不把自身截进图」）的第二道防线：
    /// 第一道是截图前隐藏面板，但隐藏有间隙（隐藏到冻结之间窗口仍可能被 WGC 抓到），
    /// 显示亲和性标志则从机制上保证「永远抓不到」。
    /// </summary>
    public void ExcludeFromCapture()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE);
    }

    /// <summary>恢复参与屏幕捕获（退出捕获流程时调用，避免永久影响用户）。</summary>
    public void IncludeInCapture()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        SetWindowDisplayAffinity(_hwnd, WDA_NONE);
    }

    /// <summary>供子类判断某点是否命中可交互区域（透明区域不接鼠标）。</summary>
    public Point ScreenToClient(Point screen) => new(screen.X - Location.X, screen.Y - Location.Y);

    // ─────────────────────────────────────────────────────────────────────────
    // 消息处理
    // ─────────────────────────────────────────────────────────────────────────

    private IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WM_MOUSEMOVE:
                OnMouseMove(lParam);
                return IntPtr.Zero;

            case WM_LBUTTONDOWN:
                MouseDown?.Invoke(ToPoint(lParam));
                return IntPtr.Zero;

            case WM_LBUTTONUP:
                MouseUp?.Invoke(ToPoint(lParam));
                return IntPtr.Zero;

            case WM_RBUTTONUP:
                RightClick?.Invoke(ToPoint(lParam));
                return IntPtr.Zero;

            case WM_MOUSELEAVE:
                _trackingMouse = false;
                MouseLeave?.Invoke();
                return IntPtr.Zero;

            case WM_TIMER:
                // 一次性定时器：触发后立即撤销，避免重复回调。
                StopTimer();
                Timer?.Invoke();
                return IntPtr.Zero;

            case WM_ACTIVATE:
                // 对 NoActivate 窗口来说仍可能因键盘 Tab 导航被激活；
                // 一旦被激活立刻退回，绝不抢前台。
                Activated?.Invoke();
                return IntPtr.Zero;

            case WM_SETCURSOR:
                SetCursor(LoadCursorW(IntPtr.Zero, IDC_ARROW));
                return IntPtr.Zero;

            case WM_NCDESTROY:
            case WM_DESTROY:
                return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void OnMouseMove(IntPtr lParam)
    {
        if (!_trackingMouse)
        {
            var tme = new TRACKMOUSEEVENT
            {
                cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(),
                dwFlags = TME_LEAVE,
                hwndTrack = _hwnd,
                dwHoverTime = 0,
            };
            TrackMouseEvent(ref tme);
            _trackingMouse = true;
        }

        MouseMove?.Invoke(ToPoint(lParam));
    }

    private static Point ToPoint(IntPtr lParam)
    {
        var value = lParam.ToInt64();
        return new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
    }

    private static void EnsureClassRegistered()
    {
        lock (ClassLock)
        {
            if (_classAtom != 0)
            {
                return;
            }

            _wndProc = StaticWndProc;
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                style = 0x0003,   // CS_HREDRAW | CS_VREDRAW
                lpfnWndProc = _wndProc,
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = ModuleHandle,
                hIcon = IntPtr.Zero,
                hCursor = IntPtr.Zero,
                hbrBackground = IntPtr.Zero,
                lpszMenuName = null,
                lpszClassName = ClassName,
                hIconSm = IntPtr.Zero,
            };

            _classAtom = RegisterClassExW(ref wc);
            if (_classAtom == 0)
            {
                var error = Marshal.GetLastWin32Error();
                // 1410 = ERROR_CLASS_ALREADY_EXISTS：多实例共存时视为成功。
                if (error != 1410)
                {
                    throw new InvalidOperationException($"RegisterClassExW 失败，Win32 错误 {error}。");
                }
            }
        }
    }

    // 进程内可能同时存在多个 LayeredSurfaceWindow（托盘 popover + 结果条）。
    // 用按句柄的字典反查实例，避免 GWLP_USERDATA 的手动固定/释放。
    private static readonly Dictionary<IntPtr, LayeredSurfaceWindow> Instances = new();
    private static readonly object InstancesLock = new();

    private static IntPtr StaticWndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        lock (InstancesLock)
        {
            if (Instances.TryGetValue(hwnd, out var owner))
            {
                return owner.WndProc(hwnd, message, wParam, lParam);
            }
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    /// <summary>由 <see cref="Create"/> 在成功后调用，注册句柄映射。</summary>
    private void RegisterInstance()
    {
        lock (InstancesLock)
        {
            Instances[_hwnd] = this;
        }
    }

    private void UnregisterInstance()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        lock (InstancesLock)
        {
            Instances.Remove(_hwnd);
        }
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
            UnregisterInstance();
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        _surface?.Dispose();
        _surface = null;
    }

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public int cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, ref System.Drawing.Point pptDst, ref Size psize,
        IntPtr hdcSrc, ref System.Drawing.Point pptSrc, int crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, IntPtr uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadCursorW(IntPtr hInstance, int lpCursorName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
