using System.Runtime.InteropServices;

namespace Ta.Spike.Overlay;

/// <summary>
/// 覆盖层验证所需的 Win32 互操作声明。
///
/// 使用经典 [DllImport] 而非 [LibraryImport]：本程序的结构体含 BOOL 字段、
/// 多个函数返回 BOOL，经典封送处理这些更直接，不必为每个成员补封送特性。
/// 生产代码可再评估是否切到源生成 P/Invoke。
///
/// 所有常量集中在此，便于逐条对照 Mac 版 SelectionOverlayController.swift:121-135
/// 的那组 NSPanel 属性。
/// </summary>
internal static class Native
{
    public const string User32 = "user32.dll";
    public const string Gdi32 = "gdi32.dll";

    // ── 扩展窗口样式 ──────────────────────────────────────────────
    // 对应 macOS: styleMask: [.borderless, .nonactivatingPanel]
    public const uint WS_EX_NOACTIVATE = 0x0800_0000;  // 点击/显示都不激活，拓永不抢前台
    public const uint WS_EX_TOOLWINDOW = 0x0000_0080;  // 不进任务栏、不进 Alt+Tab
    public const uint WS_EX_TOPMOST   = 0x0000_0008;   // 近似 macOS panel.level = .screenSaver

    public const uint WS_POPUP = 0x8000_0000;          // 无边框无标题，等同 .borderless

    // 对应 macOS: orderFrontRegardless() + canBecomeMain = false
    // 整个方案的命门：显示但不激活。
    public const int SW_SHOWNOACTIVATE = 4;

    // 对应 macOS: backgroundColor = .clear + isOpaque = false + 黑 0.42 遮罩
    public const uint LWA_ALPHA = 0x0000_0002;
    public const byte OverlayAlpha = 190;              // ≈ 190/255 ≈ 0.745

    // 对应 macOS: excludedWindowIDs: [CGWindowID(panel.windowNumber)]
    // Windows 用一行标志即可，比 macOS 的排除清单更干净。
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x0000_0011;

    // ── 钩子与按键 ────────────────────────────────────────────────
    public const int WH_KEYBOARD_LL = 13;
    public const int HC_ACTION = 0;
    public const int VK_ESCAPE = 0x1B;

    // ── 窗口消息 ──────────────────────────────────────────────────
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_QUIT = 0x0012;
    public const uint QS_ALLINPUT = 0x04FF;
    public const uint PM_REMOVE = 0x0001;
    public const int GWLP_USERDATA = 0;

    // ── 光标与视觉常量（对齐 Mac 版 SelectionOverlayView.swift）────
    public const nint IDC_CROSS = 32515;
    public const uint AccentColorRef = 0x00FF8000;     // COLORREF = 0x00BBGGRR → #0080FF
    public const int MinSelectionSize = 4;             // Mac: 最小选区 4×4 pt
    public const int DragThreshold = 3;                // Mac: 拖拽阈值 3 pt
    public const int PS_SOLID = 0;
    public const int NULL_BRUSH = 5;

    public const uint MONITOR_DEFAULTTONULL = 0;
    public const uint MONITOR_DEFAULTTOPRIMARY = 1;

    // ── 委托 ──────────────────────────────────────────────────────
    public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // 钩子过程委托。必须由调用方持有引用，否则被 GC 回收后 native 侧函数指针失效。
    public delegate IntPtr HookProcDelegate(int nCode, IntPtr wParam, IntPtr lParam);

    // ── 结构体 ────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;

        public POINT(int x, int y)
        {
            this.x = x;
            this.y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;

        public RECT(int left, int top, int right, int bottom)
        {
            this.left = left;
            this.top = top;
            this.right = right;
            this.bottom = bottom;
        }

        public int Width => right - left;
        public int Height => bottom - top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;                 // BOOL —— 用 int 封送，避免 BOOL 歧义
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        public static MONITORINFO Create() => new() { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
    }

    // ── 窗口 ──────────────────────────────────────────────────────
    [DllImport(User32, EntryPoint = "RegisterClassExW", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassEx(in WNDCLASSEXW lpwcx);

    [DllImport(User32, EntryPoint = "CreateWindowExW", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport(User32)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport(User32)]
    public static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport(User32)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport(User32, SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport(User32, SetLastError = true)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);

    [DllImport(User32, EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport(User32)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport(User32)]
    public static extern bool InvalidateRect(IntPtr hWnd, in RECT lpRect, bool bErase);

    [DllImport(User32)]
    public static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport(User32)]
    public static extern bool ReleaseCapture();

    // ── 显示器与前台窗口 ──────────────────────────────────────────
    [DllImport(User32)]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport(User32, EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport(User32)]
    public static extern IntPtr GetForegroundWindow();

    [DllImport(User32)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport(User32)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    // ── 光标 ──────────────────────────────────────────────────────
    [DllImport(User32, EntryPoint = "LoadCursorW", CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadCursor(IntPtr hInstance, nint lpCursorName);

    [DllImport(User32)]
    public static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport(User32)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    // ── 低级键盘钩子 ──────────────────────────────────────────────
    [DllImport(User32, EntryPoint = "SetWindowsHookExW", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProcDelegate lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport(User32)]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport(User32)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    // ── 合成按键（仅供 --auto 模式自测钩子链路）────────────────────
    private const int KEYEVENTF_KEYUP = 0x0002;
    private const byte VK_ESCAPE_SCAN = 0x1B;

    [DllImport(User32)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

    /// <summary>合成一次 Escape 按下与松开。低级钩子能观察到合成输入。</summary>
    public static void SendSyntheticEscape()
    {
        keybd_event(VK_ESCAPE_SCAN, 0, 0, IntPtr.Zero);
        keybd_event(VK_ESCAPE_SCAN, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
    }

    // ── 消息循环 ──────────────────────────────────────────────────
    [DllImport(User32, EntryPoint = "PeekMessageW", CharSet = CharSet.Unicode)]
    public static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max, uint wRemoveMsg);

    [DllImport(User32, EntryPoint = "GetMessageW", CharSet = CharSet.Unicode)]
    public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    public static extern bool TranslateMessage(in MSG lpMsg);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    public static extern IntPtr DispatchMessage(in MSG lpMsg);

    [DllImport(User32)]
    public static extern void PostQuitMessage(int nExitCode);

    [DllImport(User32)]
    public static extern uint MsgWaitForMultipleObjects(uint nCount, IntPtr[]? pHandles, bool bWaitAll, uint dwMilliseconds, uint dwWakeMask);

    // ── GDI 绘制 ──────────────────────────────────────────────────
    [DllImport(Gdi32, SetLastError = true)]
    public static extern IntPtr CreatePen(int fnPenStyle, int nWidth, uint crColor);

    [DllImport(Gdi32)]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport(Gdi32)]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport(Gdi32)]
    public static extern IntPtr GetStockObject(int i);

    [DllImport(Gdi32)]
    public static extern bool MoveToEx(IntPtr hdc, int x, int y, IntPtr lpPoint);

    [DllImport(Gdi32)]
    public static extern bool LineTo(IntPtr hdc, int x, int y);

    [DllImport(Gdi32)]
    public static extern bool Rectangle(IntPtr hdc, int left, int top, int right, int bottom);

    [DllImport(User32, SetLastError = true)]
    public static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);

    [DllImport(User32)]
    public static extern bool EndPaint(IntPtr hWnd, in PAINTSTRUCT lpPaint);
}
