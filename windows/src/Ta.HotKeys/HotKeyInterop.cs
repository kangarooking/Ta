using System.Runtime.InteropServices;

namespace Ta.HotKeys;

/// <summary>
/// 全局快捷键所需的 Win32 互操作声明。
///
/// 写法沿用同仓库 <c>Ta.Platform/NativeMethods.cs</c> 的手写 <c>[DllImport]</c> 风格：
/// 不用 CsWin32 源生成，也不用 WinForms 的 Application.Run（那样会引入 STA 线程模型假设），
/// 自己注册窗口类 + 消息循环，宿主线程完全由调用方控制。
///
/// 只收录 <c>RegisterHotKey</c> / <c>UnregisterHotKey</c> / 消息循环 / 消息窗口这几组，
/// 对应 macOS 的 <c>RegisterEventHotKey</c> + <c>InstallEventHandler</c> + <c>kEventHotKeyPressed</c>
/// （GlobalHotKeyManager.swift:53-91、:125-143）。
/// </summary>
internal static class HotKeyInterop
{
    public const string User32 = "user32.dll";
    public const string Kernel32 = "kernel32.dll";

    // ── 消息 ──────────────────────────────────────────────────────
    public const uint WM_HOTKEY = HotKeyConstants.WM_HOTKEY;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_NCCREATE = 0x0081;
    public const uint WM_QUIT = 0x0012;
    public const uint PM_NOREMOVE = 0x0000;
    public const uint PM_REMOVE = 0x0001;
    public const uint QS_ALLINPUT = 0x04FF;

    /// <summary>消息专属窗口（不可见、不进任务栏、不参与 Tab 切换）的父窗口。</summary>
    public static readonly IntPtr HWND_MESSAGE = new(-3);

    // ── 委托 ──────────────────────────────────────────────────────
    // 必须由调用方长期持有（静态字段），否则 GC 回收后 native 侧函数指针失效。
    public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
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

    // ── 注册 / 注销 ────────────────────────────────────────────────
    /// <summary>
    /// 定义一个全局热键。冲突时返回 false 且 <c>GetLastError()</c> 为
    /// <see cref="HotKeyConstants.ERROR_HOTKEY_ALREADY_REGISTERED"/>。
    /// </summary>
    [DllImport(User32, SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport(User32, SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ── 消息循环 ──────────────────────────────────────────────────
    [DllImport(User32, EntryPoint = "RegisterClassExW", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassEx(in WNDCLASSEXW lpwcx);

    [DllImport(User32, EntryPoint = "CreateWindowExW", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport(User32, SetLastError = true)]
    public static extern bool DestroyWindow(IntPtr hWnd);

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

    // GetCurrentThreadId 属于 kernel32，不是 user32 —— 放错库会在运行时才炸
    // EntryPointNotFoundException（编译期完全不报错）。
    [DllImport(Kernel32)]
    public static extern uint GetCurrentThreadId();

    /// <summary>
    /// 有超时的等待：队列里出现任意输入时立即返回，避免消息泵空转。
    /// 用于 <see cref="HotKeyMessageHost.PumpPending"/> 的轮询上界。
    /// </summary>
    [DllImport(User32)]
    public static extern uint MsgWaitForMultipleObjects(
        uint nCount, IntPtr[]? pHandles, bool bWaitAll, uint dwMilliseconds, uint dwWakeMask);

    [DllImport(User32)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport(Kernel32, CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    // ⚠️ 不要直接 P/Invoke GetLastError：CLR 在后续 P/Invoke 期间会重置线程的
    // last error。请用 Marshal.GetLastWin32Error()（它配合 DllImport 的
    // SetLastError = true 才是可靠的）。
    [DllImport(Kernel32)]
    public static extern uint GetLastError();

    [DllImport(User32, SetLastError = true)]
    public static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
}
