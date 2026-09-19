using System.Runtime.InteropServices;

namespace Ta.Capture.Win32;

/// <summary>
/// 捕获所需的 Win32 互操作声明。
///
/// 刻意与 Ta.Platform 的 <c>Native</c> 分开：捕获层只需要枚举显示器/窗口与几个 DWM 属性，
/// 不需要创建窗口、消息循环或 GDI 绘制。分开后捕获层可独立演进，也不会和覆盖层
/// 抢同一份声明（Ta.Platform 是另一个 agent 的范围）。
///
/// 使用经典 <c>[DllImport]</c> 而非 <c>[LibraryImport]</c>：这里的结构体含 BOOL/指针字段、
/// 多个函数返回 BOOL，经典封送更直接。与 Ta.Platform 保持同一风格。
/// </summary>
internal static class NativeMethods
{
    public const string User32 = "user32.dll";
    public const string DwmApi = "dwmapi.dll";
    public const string Kernel32 = "kernel32.dll";
    public const string Shcore = "Shcore.dll";

    // ── 显示器 ──────────────────────────────────────────────────────
    /// <summary>MONITORINFOF_PRIMARY。绝不假设枚举序第一个是主屏 —— 见参考文档 §14 风险 #25。</summary>
    public const uint MONITORINFOF_PRIMARY = 0x0000_0001;

    /// <summary>MONITOR_DPI_TYPE.EffectiveDpi。对应 Mac 的 NSScreen.backingScaleFactor × 96。</summary>
    public const int MDT_EFFECTIVE_DPI = 0;

    // ── 窗口 ────────────────────────────────────────────────────────
    public const int GWL_EXSTYLE = -20;

    /// <summary>WS_EX_TOOLWINDOW：工具窗口。对应 Mac 版 layer >= 0 之外的另一条剔除条件（参考文档 §5.3）。</summary>
    public const uint WS_EX_TOOLWINDOW = 0x0000_0080;

    /// <summary>DWMWA_EXTENDED_FRAME_BOUNDS：窗口边框矩形，**不含**投影阴影。</summary>
    /// <remarks>
    /// 对应 Mac 的 kCGWindowBounds —— 两者都排除阴影，所以尺寸语义一致。
    /// 不能用 GetWindowRect，它含阴影边，会让吸附矩形比实际窗口大一圈。
    /// </remarks>
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    /// <summary>DWMWA_CLOAKED：被「遮蔽」的窗口（挂在其他虚拟桌面、UWP 已隐藏等）。</summary>
    /// <remarks>
    /// 对应 Mac 的 kCGWindowLayer >= 0（排除桌面元素）。被遮蔽的窗口对用户不可见，
    /// 必须剔除，否则吸附会命中看不见的窗口。
    /// </remarks>
    public const int DWMWA_CLOAKED = 14;

    /// <summary>GetLayeredWindowAttributes 的标志位：存在逐窗口透明度。</summary>
    public const uint LWA_ALPHA = 0x0000_0002;

    /// <summary>GW_OWNER。</summary>
    public const uint GW_OWNER = 4;

    // ── 自身窗口排除 ────────────────────────────────────────────────
    /// <summary>
    /// SetWindowDisplayAffinity 的值：该窗口不出现在任何屏幕捕获结果里。
    /// 对应 Mac 版 CaptureSelection.excludedWindowIDs —— Windows 一行标志即可，更干净。
    /// </summary>
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x0000_0011;

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x0000_1000;

    // ── 委托 ────────────────────────────────────────────────────────
    /// <summary>
    /// MonitorEnumProc。必须由调用方在枚举期间持有引用，否则被 GC 回收后
    /// native 侧函数指针失效，会抛 ExecutionEngineException。
    /// </summary>
    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    /// <summary>EnumWindowsProc。同上，必须持有引用。</summary>
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // ── 结构体 ──────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;

        public int Width => right - left;
        public int Height => bottom - top;
    }

    /// <summary>
    /// MONITORINFOEXW。带 szDevice 才能拿到设备名（如 <c>\\.\DISPLAY1</c>），
    /// 这是跨会话唯一稳定的显示器标识 —— HMONITOR 只是指针，重启即变（参考文档 §14 风险 #39）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEXW
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;

        public static MONITORINFOEXW Create() => new() { cbSize = Marshal.SizeOf<MONITORINFOEXW>() };
    }

    // ── 显示器枚举 ──────────────────────────────────────────────────
    [DllImport(User32, SetLastError = true)]
    public static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport(User32, EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW lpmi);

    /// <summary>
    /// 每显示器 DPI。对应 Mac 的 backingScaleFactor × 96。
    /// 注意：本函数返回的是**该显示器的实际 DPI**，不受进程 DPI 感知模式影响；
    /// 而 GetMonitorInfo 的 rcMonitor 会受影响。两者相除才是「rcMonitor → 物理像素」的倍率。
    /// </summary>
    [DllImport(Shcore, PreserveSig = true)]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    // ── 显示模式（真实物理像素） ────────────────────────────────────
    /// <summary>ENUM_CURRENT_SETTINGS：取显示器**当前**模式。</summary>
    public const int ENUM_CURRENT_SETTINGS = -1;

    /// <summary>
    /// DEVMODEW（220 字节）。这里只用 dmPelsWidth/dmPelsHeight，但结构必须**完整** ——
    /// <c>EnumDisplaySettingsW</c> 会校验 <c>dmSize</c>，尺寸不符直接返回 false。
    /// 中间那个 union 用「打印分支」的 8 个 short 占位即可：两个分支同为 16 字节。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODEW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;

        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;

        public short dmOrientation;
        public short dmPaperSize;
        public short dmPaperLength;
        public short dmPaperWidth;
        public short dmScale;
        public short dmCopies;
        public short dmDefaultSource;
        public short dmPrintQuality;

        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;

        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    /// <summary>
    /// 读显示器当前模式。**这是唯一不受 DPI 虚拟化影响的尺寸来源** ——
    /// GetMonitorInfo 的 rcMonitor 会随调用进程的 DPI 感知模式变化（感知=物理像素，
    /// 不感知=逻辑像素），而显示模式永远报真实物理像素。做「rcMonitor → 物理像素」
    /// 的倍率换算必须拿它当分子。
    /// </summary>
    [DllImport(User32, EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool EnumDisplaySettingsW(string lpszDeviceName, int iModeNum, ref DEVMODEW lpDevMode);

    // ── 窗口枚举 ────────────────────────────────────────────────────
    [DllImport(User32, SetLastError = true)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport(User32)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport(User32)]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    // GetWindowLongPtr 才是 64 位下的正确入口；GetWindowLong 在 64 位下会截断。
    // 但 GWL_EXSTYLE 取值本身是 32 位，两者结果一致，按进程位宽挑一个即可。
    [DllImport(User32, EntryPoint = "GetWindowLongW", SetLastError = true)]
    public static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport(User32, EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    public static uint GetWindowExStyle(IntPtr hWnd) => IntPtr.Size == 8
        ? unchecked((uint)GetWindowLongPtrW(hWnd, GWL_EXSTYLE).ToInt64())
        : unchecked((uint)GetWindowLongW(hWnd, GWL_EXSTYLE));

    [DllImport(DwmApi, PreserveSig = true)]
    public static extern int DwmGetWindowAttribute(
        IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport(DwmApi, PreserveSig = true)]
    public static extern int DwmGetWindowAttribute(
        IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport(User32)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport(User32)]
    public static extern IntPtr GetForegroundWindow();

    [DllImport(User32, CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport(User32, EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    /// <summary>分层窗口的逐窗口透明度。未分层或未设 LWA_ALPHA 时视为不透明（1.0）。</summary>
    [DllImport(User32)]
    public static extern bool GetLayeredWindowAttributes(
        IntPtr hwnd, out uint crKey, out byte bAlpha, out uint dwFlags);

    // ── 进程标识 ────────────────────────────────────────────────────
    /// <summary>
    /// 取进程映像全路径。对应 Mac 的 owningApplication.bundleIdentifier ——
    /// 参考文档 §14 风险 #38 要求这个身份必须是**稳定字面量**，不能换成 PID 或窗口句柄。
    /// </summary>
    [DllImport(Kernel32, SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport(Kernel32, EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageNameW(
        IntPtr hProcess, uint dwFlags, System.Text.StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport(Kernel32)]
    public static extern bool CloseHandle(IntPtr hObject);

    public static int GetProcessId(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out var pid);
        return unchecked((int)pid);
    }

    /// <summary>窗口标题。取不到时返回 null 而非空串，便于与「真的空标题」区分。</summary>
    public static string? GetWindowTitle(IntPtr hWnd)
    {
        var length = GetWindowTextLengthW(hWnd);
        if (length <= 0)
        {
            return null;
        }

        var builder = new System.Text.StringBuilder(length + 1);
        GetWindowTextW(hWnd, builder, builder.Capacity);
        var text = builder.ToString();
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// 进程映像全路径。提权进程、已退出进程、PPL 进程都可能取不到，
    /// 此时返回 null —— 调用方必须能容忍（AppName 退回用 PID 兜底）。
    /// </summary>
    public static string? TryGetProcessImagePath(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, unchecked((uint)processId));
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var builder = new System.Text.StringBuilder(1024);
            uint size = (uint)builder.Capacity;
            if (!QueryFullProcessImageNameW(handle, 0, builder, ref size) || size == 0)
            {
                return null;
            }

            return builder.ToString(0, (int)size);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // ── 自身窗口排除 ────────────────────────────────────────────────
    /// <summary>
    /// 把自身窗口标记为「不进任何屏幕捕获」。
    ///
    /// 对应 Mac 版 CaptureSelection.excludedWindowIDs —— 覆盖层面板必须在
    /// CaptureDisplay 时被排除，否则冻结帧里会带一块半透明遮罩（参考文档 §3.1）。
    /// </summary>
    [DllImport(User32, SetLastError = true)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);
}
