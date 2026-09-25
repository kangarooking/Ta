using System.Runtime.InteropServices;

namespace Ta.LongSession.Win32;

/// <summary>
/// 长截图 UI（HUD、接缝复查）与滚动驱动所需的 Win32 互操作声明。
///
/// 沿用 Ta.Platform.Native 的约定：经典 [DllImport] + 常量集中一处。
/// 与 Mac 版窗口标记的对应关系标注在各常量注释里。
/// </summary>
internal static class Native
{
    public const string User32 = "user32.dll";
    public const string Gdi32 = "gdi32.dll";
    public const string Kernel32 = "kernel32.dll";
    public const string Msimg32 = "msimg32.dll";

    // ── 扩展窗口样式 ──────────────────────────────────────────────
    // 对应 macOS HUD: styleMask: [.borderless, .nonactivatingPanel]
    public const uint WS_EX_NOACTIVATE = 0x0800_0000;
    public const uint WS_EX_TOOLWINDOW = 0x0000_0080;
    public const uint WS_EX_TOPMOST = 0x0000_0008;
    public const uint WS_EX_LAYERED = 0x0008_0000;   // per-pixel alpha 圆角面板

    public const uint WS_POPUP = 0x8000_0000;
    public const uint WS_OVERLAPPEDWINDOW = 0x00CF_0000;
    public const uint WS_CAPTION = 0x00C0_0000;
    public const uint WS_SYSMENU = 0x0008_0000;
    public const uint WS_THICKFRAME = 0x0004_0000;
    public const uint WS_MINIMIZEBOX = 0x0002_0000;
    public const uint WS_MAXIMIZEBOX = 0x0001_0000;
    public const uint WS_CLIPCHILDREN = 0x0200_0000;
    public const uint WS_VSCROLL = 0x0020_0000;
    public const uint WS_HSCROLL = 0x0010_0000;

    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_SHOW = 5;
    public const int SWP_NOSIZE = 0x0001;
    public const int SWP_NOMOVE = 0x0002;
    public const int SWP_NOZORDER = 0x0004;
    public const int SWP_NOACTIVATE = 0x0010;
    public const int SWP_FRAMECHANGED = 0x0020;
    public const int HWND_TOPMOST = -1;

    public const uint LWA_ALPHA = 0x0000_0002;
    public const uint LWA_COLORKEY = 0x0000_0001;
    public const byte HudAlpha = 235;                // ≈ 0.92 —— 对应 .ultraThickMaterial 的近似
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x0000_0011;

    // ── 窗口消息 ──────────────────────────────────────────────────
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_ERASEBKGND = 0x0014;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_MOUSELEAVE = 0x02A3;
    public const uint WM_MOUSEWHEEL = 0x020A;
    public const uint WM_VSCROLL = 0x0115;
    public const uint WM_HSCROLL = 0x0114;
    public const uint WM_TIMER = 0x0113;
    public const uint WM_SIZE = 0x0005;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_CHAR = 0x0102;
    public const uint WM_GETMINMAXINFO = 0x0024;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_QUIT = 0x0012;
    public const uint WM_SETCURSOR = 0x0020;
    public const uint WM_NCHITTEST = 0x0084;
    public const uint QS_ALLINPUT = 0x04FF;
    public const uint PM_REMOVE = 0x0001;
    public const uint PM_NOREMOVE = 0x0000;

    public const uint WM_APP = 0x8000;
    public const uint WM_APP_CLOSE = WM_APP + 0x10;
    public const uint WM_APP_UPDATE = WM_APP + 0x11;
    public const uint WM_APP_REFRESH = WM_APP + 0x12;

    public const int ERROR_CLASS_ALREADY_EXISTS = 1410;

    // ── 滚动条（标准 Win32 滚动窗口）────────────────────────────
    public const int SB_VERT = 1;
    public const int SB_HORZ = 0;
    public const int SB_LINEUP = 0;
    public const int SB_LINEDOWN = 1;
    public const int SB_PAGEUP = 2;
    public const int SB_PAGEDOWN = 3;
    public const int SB_THUMBPOSITION = 4;
    public const int SB_THUMBTRACK = 5;
    public const int SB_TOP = 6;
    public const int SB_BOTTOM = 7;
    public const int SB_ENDSCROLL = 8;

    // ── 合成滚轮 ─────────────────────────────────────────────────
    // ⚠️ Windows 无逐像素滚轮：档位式，WHEEL_DELTA=120 为一档。
    // 这是整个移植中保真度损失最大的一处（参考文档 §14 风险 #2），
    // 驱动策略的实测结论见 AccessibilityAutoScrollService 的策略链。
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const int WHEEL_DELTA = 120;
    public const uint INPUT_MOUSE = 0;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    // ── 窗口枚举 ──────────────────────────────────────────────────
    public const uint GW_HWNDNEXT = 2;
    public const uint GW_CHILD = 5;
    public const uint GA_PARENT = 1;
    public const uint GA_ROOT = 2;

    [DllImport(User32)]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    // ── 光标与视觉 ────────────────────────────────────────────────
    public const nint IDC_ARROW = 32512;
    public const nint IDC_HAND = 32649;
    public const nint IDC_SIZENS = 32645;
    public const nint IDC_SIZEWE = 32644;
    public const int PS_SOLID = 0;
    public const int NULL_BRUSH = 5;
    public const int HOLLOW_BRUSH = 5;
    public const uint TRANSPARENT = 1;
    public const uint OPAQUE = 2;
    public const uint DT_SINGLELINE = 0x0020;
    public const uint DT_VCENTER = 0x0004;
    public const uint DT_CENTER = 0x0001;
    public const uint DT_LEFT = 0x0000;
    public const uint DT_RIGHT = 0x0002;
    public const uint DT_NOPREFIX = 0x0800;
    public const uint DT_END_ELLIPSIS = 0x8000;
    public const uint DT_CALCRECT = 0x0400;
    public const uint BI_RGB = 0;
    public const uint DIB_RGB_COLORS = 0;
    public const int SRCCOPY = 0x00CC0020;
    public const int HALFTONE = 4;

    public const int COLOR_WINDOW = 5;
    public const int COLOR_BTNFACE = 15;
    public const int COLOR_WINDOWTEXT = 8;
    public const int COLOR_BTNTEXT = 18;
    public const int COLOR_GRAYTEXT = 17;
    public const int COLOR_HIGHLIGHT = 13;

    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint MONITOR_DEFAULTTOPRIMARY = 1;

    // 调色板（BGR）
    public const uint PanelBackdrop = 0x00201C18;    // 深色面板底 ≈ #181C20
    public const uint PanelStroke = 0x00B0AFAE;      // 白 0.18 alpha 压到深底上的近似
    public const uint Accent = 0x00FF8000;           // #0080FF（BGR）
    public const uint AccentHover = 0x00D9A000;      // #00A0D9
    public const uint TextPrimary = 0x00F0F0F0;
    public const uint TextSecondary = 0x00A0A0A0;
    public const uint TextDisabled = 0x00707070;
    public const uint SeamGood = 0x0000C000;         // 绿
    public const uint SeamBad = 0x000000FF;          // 红
    public const uint PreviewBackdrop = 0x00302820;

    // ── 委托 ──────────────────────────────────────────────────────
    public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    public delegate int FontEnumProc(IntPtr lpelf, IntPtr lpntm, uint fontType, IntPtr lParam);

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
        public bool Contains(POINT p) => p.x >= left && p.x < right && p.y >= top && p.y < bottom;
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
    public struct SCROLLINFO
    {
        public uint cbSize;
        public uint fMask;
        public int nMin;
        public int nMax;
        public uint nPage;
        public int nPos;
        public int nTrackPos;

        public static SCROLLINFO Create(uint mask = 0x17) => new()
        {
            cbSize = (uint)Marshal.SizeOf<SCROLLINFO>(),
            fMask = mask,   // SIF_RANGE | SIF_PAGE | SIF_POS | SIF_TRACKPOS
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors_rgbBlue;
        public uint bmiColors_rgbGreen;
        public uint bmiColors_rgbRed;
        public uint bmiColors_rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;      // WM_MOUSEWHEEL 时即 wheel delta
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
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
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LOGFONTW
    {
        public int lfHeight;
        public int lfWidth;
        public int lfEscapement;
        public int lfOrientation;
        public int lfWeight;
        public byte lfItalic;
        public byte lfUnderline;
        public byte lfStrikeOut;
        public byte lfCharSet;
        public byte lfOutPrecision;
        public byte lfClipPrecision;
        public byte lfQuality;
        public byte lfPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string lfFaceName;
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
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport(User32, SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport(User32, SetLastError = true)]
    public static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport(User32)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);

    [DllImport(User32)]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport(User32)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport(User32)]
    public static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    [DllImport(User32)]
    public static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    [DllImport(User32)]
    public static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport(User32)]
    public static extern bool ReleaseCapture();

    [DllImport(User32)]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport(User32)]
    public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport(User32)]
    public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);

    [DllImport(Kernel32, CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    // ── 窗口枚举 / 命中 ───────────────────────────────────────────
    [DllImport(User32)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport(User32)]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(
        IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport(User32)]
    public static extern IntPtr WindowFromPoint(POINT Point);

    [DllImport(User32)]
    public static extern IntPtr ChildWindowFromPointEx(IntPtr hWndParent, POINT Point, uint uFlags);

    [DllImport(User32)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport(User32)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport(User32)]
    public static extern IntPtr GetForegroundWindow();

    [DllImport(User32)]
    public static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport(User32)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport(User32)]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport(User32)]
    public static extern bool IsWindow(IntPtr hWnd);

    // ── 光标 ──────────────────────────────────────────────────────
    [DllImport(User32, EntryPoint = "LoadCursorW", CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadCursor(IntPtr hInstance, nint lpCursorName);

    [DllImport(User32)]
    public static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport(User32)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport(User32)]
    public static extern bool SetCursorPos(int x, int y);

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

    [DllImport(User32, CharSet = CharSet.Unicode)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport(User32)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport(User32)]
    public static extern uint MsgWaitForMultipleObjects(uint nCount, IntPtr[]? pHandles, bool bWaitAll, uint dwMilliseconds, uint dwWakeMask);

    [DllImport(User32)]
    public static extern bool KillTimer(IntPtr hWnd, IntPtr nIDEvent);

    [DllImport(User32)]
    public static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    // ── 滚动条（标准 Win32 滚动窗口）────────────────────────────
    [DllImport(User32)]
    public static extern int SetScrollPos(IntPtr hWnd, int nBar, int nPos, bool bRedraw);

    [DllImport(User32)]
    public static extern int GetScrollPos(IntPtr hWnd, int nBar);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    public static extern bool GetScrollInfo(IntPtr hWnd, int nBar, ref SCROLLINFO lpsi);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    public static extern bool SetScrollInfo(IntPtr hWnd, int nBar, ref SCROLLINFO lpsi, bool redraw);

    [DllImport(User32)]
    public static extern bool EnableScrollBar(IntPtr hWnd, uint wSBflags, uint wArrows);

    // ── 合成滚轮 ─────────────────────────────────────────────────
    [DllImport(User32, SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport(User32)]
    public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);

    public static void PostWheel(IntPtr hwnd, int wheelDelta, int clientX, int clientY)
    {
        var wParam = (IntPtr)(((uint)(wheelDelta & 0xFFFF)) << 16);
        var lParam = (IntPtr)((ushort)clientX | ((uint)(ushort)clientY << 16));
        PostMessage(hwnd, WM_MOUSEWHEEL, wParam, lParam);
    }

    // ── GDI 绘制 ──────────────────────────────────────────────────
    [DllImport(Gdi32, SetLastError = true)]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport(Gdi32)]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport(Gdi32, SetLastError = true)]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport(Gdi32)]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport(Gdi32)]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport(Gdi32)]
    public static extern IntPtr GetStockObject(int i);

    [DllImport(Gdi32, SetLastError = true)]
    public static extern IntPtr CreateSolidBrush(uint crColor);

    [DllImport(Gdi32, SetLastError = true)]
    public static extern IntPtr CreatePen(int fnPenStyle, int nWidth, uint crColor);

    [DllImport(Gdi32, SetLastError = true)]
    public static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);

    [DllImport(Gdi32, SetLastError = true)]
    public static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);

    [DllImport(Gdi32)]
    public static extern int CombineRgn(IntPtr hrgnDst, IntPtr hrgnSrc1, IntPtr hrgnSrc2, int fnCombineMode);

    [DllImport(Gdi32)]
    public static extern bool FillRgn(IntPtr hdc, IntPtr hrgn, IntPtr hbr);

    [DllImport(Gdi32)]
    public static extern bool FrameRgn(IntPtr hdc, IntPtr hrgn, IntPtr hbr, int w, int h);

    [DllImport(Gdi32, SetLastError = true)]
    public static extern IntPtr CreateFontW(
        int nHeight, int nWidth, int nEscapement, int nOrientation, int fnWeight,
        uint fdwItalic, uint fdwUnderline, uint fdwStrikeOut, uint fdwCharSet,
        uint fdwOutputPrecision, uint fdwClipPrecision, uint fdwQuality,
        uint fdwPitchAndFamily, string lpszFace);

    [DllImport(Gdi32, SetLastError = true)]
    public static extern bool GetTextExtentPoint32W(IntPtr hdc, string lpString, int c, out SIZE lpSize);

    [DllImport(Gdi32)]
    public static extern uint SetBkMode(IntPtr hdc, uint mode);

    [DllImport(Gdi32)]
    public static extern uint SetTextColor(IntPtr hdc, uint crColor);

    [DllImport(Gdi32)]
    public static extern uint SetBkColor(IntPtr hdc, uint crColor);

    [DllImport(Gdi32, CharSet = CharSet.Unicode)]
    public static extern bool TextOutW(IntPtr hdc, int x, int y, string lpString, int c);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    public static extern int DrawTextW(IntPtr hdc, string lpchText, int cchText, ref RECT lprc, uint format);

    [DllImport(Gdi32)]
    public static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);

    [DllImport(Gdi32)]
    public static extern bool StretchBlt(
        IntPtr hdc, int xDest, int yDest, int wDest, int hDest,
        IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, uint rop);

    [DllImport(Gdi32)]
    public static extern int SetStretchBltMode(IntPtr hdc, int mode);

    [DllImport(Gdi32)]
    public static extern bool Rectangle(IntPtr hdc, int left, int top, int right, int bottom);

    [DllImport(Gdi32)]
    public static extern bool Ellipse(IntPtr hdc, int left, int top, int right, int bottom);

    [DllImport(Gdi32)]
    public static extern int SelectClipRgn(IntPtr hdc, IntPtr hrgn);

    [DllImport(Gdi32)]
    public static extern bool MoveToEx(IntPtr hdc, int x, int y, IntPtr lpPoint);

    [DllImport(Gdi32)]
    public static extern bool LineTo(IntPtr hdc, int x, int y);

    [DllImport(Gdi32)]
    public static extern bool RoundRect(IntPtr hdc, int left, int top, int right, int bottom, int width, int height);

    [DllImport(User32, SetLastError = true)]
    public static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);

    [DllImport(User32)]
    public static extern bool EndPaint(IntPtr hWnd, in PAINTSTRUCT lpPaint);

    [DllImport(User32)]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport(User32)]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport(User32)]
    public static extern bool FillRect(IntPtr hdc, in RECT lprc, IntPtr hbr);

    [DllImport(User32, SetLastError = true)]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport(User32, SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

    [DllImport(User32)]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport(User32, EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport(User32)]
    public static extern IntPtr MonitorFromRect(Native.RECT lprc, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int cx;
        public int cy;
    }
}
