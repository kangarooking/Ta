using System.Runtime.InteropServices;
using System.Text;

namespace Ta.Pinning.Interop;

/// <summary>
/// 钉图所需的 Win32 互操作 —— 全部手写 <c>[DllImport]</c>，集中在**这一个文件**。
///
/// 为什么不用 CsWin32 / <c>[LibraryImport]</c>：参考文档 §15.4 已实测过
/// CsWin32 生成器在 net8.0-windows 上不稳定；而本文件的结构体含 BOOL 字段、
/// 多个函数返回 BOOL，经典封送更直接（与 <c>Ta.Platform/NativeMethods.cs</c> 同一判断）。
///
/// 每个常量都在注释里标注对应的 macOS API，便于逐条 review。
/// </summary>
internal static class PinInterop
{
    public const string User32 = "user32.dll";
    public const string Gdi32 = "gdi32.dll";
    public const string Kernel32 = "kernel32.dll";
    public const string Shell32 = "shell32.dll";
    public const string Shcore = "shcore.dll";

    // ── 窗口样式 ────────────────────────────────────────────────────
    /// <summary>对应 macOS: styleMask: [.borderless]（:154）—— 无边框无标题。</summary>
    public const uint WS_POPUP = 0x8000_0000;

    public const uint WS_CLIPCHILDREN = 0x0200_0000;
    public const uint WS_CLIPSIBLINGS = 0x0400_0000;

    /// <summary>对应 macOS: panel.level = .floating（:158）</summary>
    public const uint WS_EX_TOPMOST = 0x0000_0008;

    /// <summary>不进任务栏、不进 Alt+Tab。钉图不该占任务栏。</summary>
    public const uint WS_EX_TOOLWINDOW = 0x0000_0080;

    /// <summary>逐像素 alpha 表面 —— 圆角与阴影的前提。</summary>
    public const uint WS_EX_LAYERED = 0x0008_0000;

    /// <summary>鼠标穿透。对应 macOS: panel.ignoresMouseEvents = true（:182）</summary>
    public const uint WS_EX_TRANSPARENT = 0x0000_0020;

    /// <summary>对应 macOS: [.nonactivatingPanel]（:154）+ canBecomeKey/Main = false（:246-247）</summary>
    public const uint WS_EX_NOACTIVATE = 0x0800_0000;

    public const int GWL_EXSTYLE = -20;
    public const int SWP_NOSIZE = 0x0001;
    public const int SWP_NOMOVE = 0x0002;
    public const int SWP_NOZORDER = 0x0004;
    public const int SWP_NOACTIVATE = 0x0010;
    public const int SWP_SHOWWINDOW = 0x0040;
    public const int SWP_FRAMECHANGED = 0x0020;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    /// <summary>对应 macOS: orderFrontRegardless()（:186）但**不激活**。</summary>
    public const int SW_SHOWNOACTIVATE = 4;

    // ── 类样式 ──────────────────────────────────────────────────────
    public const uint CS_HREDRAW = 0x0002;
    public const uint CS_VREDRAW = 0x0001;
    /// <summary>双击判定。Windows 只在类带 CS_DBLCLKS 时才发 WM_LBUTTONDBLCLK。</summary>
    public const uint CS_DBLCLKS = 0x0008;

    // ── 分层窗口 ────────────────────────────────────────────────────
    public const uint ULW_ALPHA = 0x0000_0002;
    public const byte AC_SRC_OVER = 0x00;
    public const byte AC_SRC_ALPHA = 0x01;

    // ── 消息 ────────────────────────────────────────────────────────
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_MOUSEWHEEL = 0x020A;
    public const uint WM_MOVING = 0x0216;
    public const uint WM_DPICHANGED = 0x02E0;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_QUIT = 0x0012;
    public const int WHEEL_DELTA = 120;
    public const int MK_LBUTTON = 0x0001;

    /// <summary>应用自定义消息起点。</summary>
    public const uint WM_APP = 0x8000;
    public const uint WM_APP_RUN = WM_APP + 1;

    public const uint QS_ALLINPUT = 0x04FF;
    public const uint PM_REMOVE = 0x0001;
    public const uint PM_NOREMOVE = 0x0000;
    public const uint MWMO_INPUTAVAILABLE = 0x0004;
    public const uint WAIT_OBJECT_0 = 0x0000_0000;
    public const uint WAIT_TIMEOUT = 0x0000_0102;
    public const uint INFINITE = 0xFFFFFFFF;

    // ── 光标 ────────────────────────────────────────────────────────
    public const nint IDC_ARROW = 32512;

    // ── DPI ─────────────────────────────────────────────────────────
    /// <summary>per-monitor-v2。不在清单里声明时靠这一行兜住。</summary>
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);
    public const int MDT_EFFECTIVE_DPI = 0;

    // ── 菜单 ────────────────────────────────────────────────────────
    public const uint MF_STRING = 0x0000_0000;
    public const uint MF_SEPARATOR = 0x0000_0800;
    public const uint MF_BYCOMMAND = 0x0000_0000;
    /// <summary>按**位置**索引菜单项（GetMenuString / GetMenuState 用）。</summary>
    public const uint MF_BYPOSITION = 0x0000_0400;
    public const uint MF_CHECKED = 0x0000_0008;
    public const uint MF_UNCHECKED = 0x0000_0000;
    public const uint MF_GRAYED = 0x0000_0001;
    public const uint TPM_LEFTALIGN = 0x0000;
    public const uint TPM_TOPALIGN = 0x0000;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_RETURNCMD = 0x0100;
    public const uint TPM_NONOTIFY = 0x0080;

    // ── 加速键（对应 Mac 的 NSMenuItem.keyEquivalent，:321-345）──────
    public const byte FVIRTKEY = 0x01;
    public const byte FNOINVERT = 0x02;
    public const ushort VK_C = 0x43;
    public const ushort VK_W = 0x57;
    public const ushort VK_0 = 0x30;
    public const ushort VK_OEM_4 = 0xDB;   // [
    public const ushort VK_OEM_6 = 0xDD;   // ]

    // ── 剪贴板 ──────────────────────────────────────────────────────
    public const uint CF_TEXT = 1;
    public const uint CF_BITMAP = 2;
    public const uint CF_DIB = 8;
    public const uint CF_UNICODETEXT = 13;
    public const uint CF_HDROP = 15;
    public const uint CF_DIBV5 = 17;
    public const uint CF_OWNERDISPLAY = 0x0080;
    public const uint GMEM_MOVEABLE = 0x0002;
    public const uint GMEM_ZEROINIT = 0x0040;
    public const uint BI_RGB = 0;
    public const uint DIB_RGB_COLORS = 0;

    // ── GDI 文本 ────────────────────────────────────────────────────
    public const int TRANSPARENT = 1;
    public const uint DT_LEFT = 0x0000_0000;
    public const uint DT_TOP = 0x0000_0000;
    public const uint DT_CALCRECT = 0x0000_0400;
    public const uint DT_WORDBREAK = 0x0000_0010;
    public const uint DT_NOPREFIX = 0x0000_0800;
    public const uint DT_END_ELLIPSIS = 0x0000_8000;
    public const int FW_NORMAL = 400;
    public const int FW_SEMIBOLD = 600;
    public const uint DEFAULT_CHARSET = 1;
    public const uint OUT_TT_PRECIS = 4;
    public const uint CLIP_DEFAULT_PRECIS = 0;
    public const uint PROOF_QUALITY = 2;
    public const uint DEFAULT_PITCH = 0;
    public const uint FF_DONTCARE = 0;

    // ── 委托 ────────────────────────────────────────────────────────
    public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ── 结构体 ──────────────────────────────────────────────────────
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
    public struct SIZE
    {
        public int cx;
        public int cy;

        public SIZE(int cx, int cy)
        {
            this.cx = cx;
            this.cy = cy;
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
    public struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ACCEL
    {
        public byte fVirt;
        public ushort key;
        public ushort cmd;

        public ACCEL(byte fVirt, ushort key, ushort cmd)
        {
            this.fVirt = fVirt;
            this.key = key;
            this.cmd = cmd;
        }
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

    /// <summary>
    /// 只带头部的 BITMAPINFO。BI_RGB + 16/32bpp 不需要调色板，
    /// 与 Windows SDK 里 BITMAPINFO 的实际内存布局在前 40 字节完全一致。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
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
    public struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        public static MONITORINFO Create() => new() { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
    }

    // ── 窗口 ────────────────────────────────────────────────────────
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
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport(User32)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport(User32)]
    public static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport(User32)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport(User32)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport(User32, SetLastError = true)]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport(User32)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport(User32, EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport(User32, EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport(User32, EntryPoint = "GetClassLongPtrW", SetLastError = true)]
    public static extern UIntPtr GetClassLongPtr(IntPtr hWnd, int nIndex);

    /// <summary>GCL_STYLE —— 用来确认窗口类真的带了 CS_DBLCLKS（双击判定的前提）。</summary>
    public const int GCL_STYLE = -26;

    [DllImport(User32, SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    /// <summary>
    /// 分层窗口的**位置、尺寸与内容**一次性更新。圆角/阴影的逐像素 alpha 只能走这里。
    /// </summary>
    [DllImport(User32, SetLastError = true)]
    public static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport(User32)]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport(User32)]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport(User32)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport(User32)]
    public static extern IntPtr GetForegroundWindow();

    [DllImport(User32)]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport(User32)]
    public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport(User32)]
    public static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport(User32)]
    public static extern bool ReleaseCapture();

    [DllImport(User32)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport(User32)]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport(User32)]
    public static extern short GetKeyState(int nVirtKey);

    /// <summary>VK_CONTROL。</summary>
    public const int VK_CONTROL = 0x11;

    /// <summary>SW_HIDE —— 对应 macOS: orderOut(nil)。</summary>
    public const int SW_HIDE = 0;

    [DllImport(User32, EntryPoint = "LoadCursorW", CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadCursor(IntPtr hInstance, nint lpCursorName);

    [DllImport(User32)]
    public static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport(User32, SetLastError = true)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport(User32)]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    // ── 显示器 ──────────────────────────────────────────────────────
    [DllImport(User32)]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport(User32, EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    /// <summary>shcore 提供窗口创建前的 DPI 查询（GetDpiForWindow 需要窗口句柄）。</summary>
    [DllImport(Shcore)]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    // ── 菜单 ────────────────────────────────────────────────────────
    [DllImport(User32)]
    public static extern IntPtr CreatePopupMenu();

    [DllImport(User32, EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport(User32)]
    public static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport(User32, EntryPoint = "CheckMenuItem", SetLastError = true)]
    public static extern int CheckMenuItem(IntPtr hMenu, uint uIDCheckItem, uint uCheck);

    [DllImport(User32, EntryPoint = "EnableMenuItem", SetLastError = true)]
    public static extern int EnableMenuItem(IntPtr hMenu, uint uIDEnableItem, uint uEnable);

    [DllImport(User32)]
    public static extern int GetMenuItemCount(IntPtr hMenu);

    [DllImport(User32, EntryPoint = "GetMenuStringW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetMenuString(IntPtr hMenu, uint uIDItem, StringBuilder? lpString, int nMaxCount, uint uFlag);

    [DllImport(User32)]
    public static extern uint GetMenuState(IntPtr hMenu, uint uId, uint uFlags);

    [DllImport(User32, EntryPoint = "TrackPopupMenuEx", SetLastError = true)]
    public static extern int TrackPopupMenuEx(
        IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    // ── 加速键 ──────────────────────────────────────────────────────
    [DllImport(User32, EntryPoint = "CreateAcceleratorTableW", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateAcceleratorTable([In] ACCEL[] paccel, int cAccel);

    [DllImport(User32)]
    public static extern bool DestroyAcceleratorTable(IntPtr hAccel);

    [DllImport(User32, EntryPoint = "TranslateAcceleratorW", CharSet = CharSet.Unicode)]
    public static extern int TranslateAccelerator(IntPtr hWnd, IntPtr hAccTable, in MSG lpMsg);

    // ── 消息循环 ────────────────────────────────────────────────────
    [DllImport(User32, EntryPoint = "PeekMessageW", CharSet = CharSet.Unicode)]
    public static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport(User32, EntryPoint = "GetMessageW", CharSet = CharSet.Unicode)]
    public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    public static extern bool TranslateMessage(in MSG lpMsg);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    public static extern IntPtr DispatchMessage(in MSG lpMsg);

    [DllImport(User32)]
    public static extern void PostQuitMessage(int nExitCode);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport(User32, SetLastError = true)]
    public static extern uint MsgWaitForMultipleObjectsEx(
        uint nCount, IntPtr[]? pHandles, bool bWaitAll, uint dwMilliseconds, uint dwWakeMask, uint dwFlags);

    // ── 内核：事件与全局内存 ────────────────────────────────────────
    [DllImport(Kernel32, CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport(Kernel32, EntryPoint = "CreateEventW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport(Kernel32)]
    public static extern bool SetEvent(IntPtr hEvent);

    [DllImport(Kernel32)]
    public static extern bool ResetEvent(IntPtr hEvent);

    [DllImport(Kernel32)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport(Kernel32)]
    public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport(Kernel32)]
    public static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport(Kernel32)]
    public static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport(Kernel32)]
    public static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport(Kernel32)]
    public static extern UIntPtr GlobalSize(IntPtr hMem);

    // ── 剪贴板 ──────────────────────────────────────────────────────
    [DllImport(User32, SetLastError = true)]
    public static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport(User32)]
    public static extern bool CloseClipboard();

    [DllImport(User32)]
    public static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport(User32)]
    public static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport(User32, SetLastError = true)]
    public static extern bool EmptyClipboard();

    [DllImport(User32, SetLastError = true)]
    public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport(User32)]
    public static extern uint EnumClipboardFormats(uint format);

    [DllImport(User32, EntryPoint = "RegisterClipboardFormatW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport(User32, EntryPoint = "GetClipboardFormatNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClipboardFormatName(uint format, StringBuilder lpszFormatName, int cchMax);

    [DllImport(Shell32, EntryPoint = "DragQueryFileW", CharSet = CharSet.Unicode)]
    public static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);

    // ── GDI ─────────────────────────────────────────────────────────
    [DllImport(Gdi32)]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport(Gdi32)]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport(Gdi32, SetLastError = true)]
    public static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport(Gdi32)]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport(Gdi32)]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport(Gdi32, EntryPoint = "GetObjectW", CharSet = CharSet.Unicode)]
    public static extern int GetObject(IntPtr h, int c, out BITMAP pv);

    [DllImport(Gdi32, SetLastError = true)]
    public static extern int GetDIBits(
        IntPtr hdc, IntPtr hbm, uint start, uint cLines, IntPtr lpvBits, ref BITMAPINFO lpbmi, uint usage);

    [DllImport(Gdi32, EntryPoint = "CreateFontW", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFont(
        int nHeight, int nWidth, int nEscapement, int nOrientation, int fnWeight,
        uint fdwItalic, uint fdwUnderline, uint fdwStrikeOut, uint fdwCharSet,
        uint fdwOutputPrecision, uint fdwClipPrecision, uint fdwQuality,
        uint fdwPitchAndFamily, string lpszFace);

    [DllImport(User32, EntryPoint = "DrawTextW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int DrawText(IntPtr hdc, string lpchText, int cchText, ref RECT lprc, uint format);

    [DllImport(Gdi32)]
    public static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport(Gdi32)]
    public static extern uint SetTextColor(IntPtr hdc, uint color);

    // ── 辅助 ────────────────────────────────────────────────────────

    /// <summary>WM_MOUSEWHEEL 的 wParam 高 16 位是滚动量（每档 120）。</summary>
    public static int GetWheelDelta(IntPtr wParam) => (short)((wParam.ToInt64() >> 16) & 0xFFFF);

    /// <summary>WM_LBUTTONDOWN 等的 lParam 低/高 16 位是客户区坐标（有符号）。</summary>
    public static POINT LParamToPoint(IntPtr lParam)
    {
        var value = lParam.ToInt64();
        return new POINT((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
    }

    /// <summary>把客户区坐标换算到屏幕坐标。</summary>
    public static POINT ClientToScreen(IntPtr hwnd, POINT client)
    {
        var point = client;
        ClientToScreen(hwnd, ref point);
        return point;
    }

    /// <summary>MAKELPARAM。</summary>
    public static IntPtr MakeLParam(int low, int high) => new((low & 0xFFFF) | ((high & 0xFFFF) << 16));
}
