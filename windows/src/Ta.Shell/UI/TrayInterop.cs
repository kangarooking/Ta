using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Ta.Shell.UI;

/// <summary>
/// 托盘与弹出面板所需的 Win32 互操作。
///
/// 刻意自己 P/Invoke <c>Shell_NotifyIcon</c> 而不是用 WinForms 的
/// <c>NotifyIcon</c>：后者会拖进整个 <c>System.Windows.Forms</c> 的消息泵与
/// 一个隐藏的窗口类，而我们需要的是「同一个 STA 消息循环里既收托盘消息、
/// 又自己管一个弹出面板」—— 自己声明窗口类更可控。
/// </summary>
internal static class TrayInterop
{
    public const int WM_APP = 0x8000;

    /// <summary>托盘图标回调消息。用 WM_APP 段避免与系统消息冲突。</summary>
    public const int WM_TRAYICON = WM_APP + 1;

    public const int NIM_ADD = 0x00000000;
    public const int NIM_MODIFY = 0x00000001;
    public const int NIM_DELETE = 0x00000002;
    public const int NIF_MESSAGE = 0x00000001;
    public const int NIF_ICON = 0x00000002;
    public const int NIF_TIP = 0x00000004;

    public const uint NIN_SELECT = 0x0400;          // 左键单击 / 回车
    public const uint NIN_CONTEXTMENU = 0x0402;     // 右键（Win7+ 先于 WM_CONTEXTMENU）
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint WM_CONTEXTMENU = 0x007B;

    public const int IDI_APPLICATION = 32512;
    public const int IMAGE_ICON = 1;
    public const uint LR_LOADFROMFILE = 0x00000010;
    public const uint LR_DEFAULTSIZE = 0x00000040;
    public const uint LR_SHARED = 0x00008000;

    public const int TPM_RETURNCMD = 0x0100;
    public const int TPM_RIGHTBUTTON = 0x0002;
    public const int TPM_LEFTALIGN = 0x0000;
    public const int TPM_BOTTOMALIGN = 0x0020;
    public const uint MF_STRING = 0x00000000;
    public const uint MF_SEPARATOR = 0x00000800;

    public const int SW_HIDE = 0;
    public const int SW_SHOWNOACTIVATE = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
        public char[] szTip;

        public int dwState;
        public int dwStateFlags;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public char[] szInfo;

        public uint uVersion;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public char[] szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool Shell_NotifyIconW(int dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool InsertMenuW(
        IntPtr hMenu, uint uPosition, uint uFlags, IntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int TrackPopupMenuEx(
        IntPtr hMenu, uint fuFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr LoadImageW(
        IntPtr hInst, IntPtr name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    public static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>把 Win32 错误码转成语义化异常消息。</summary>
    public static string LastError(string operation)
    {
        var code = Marshal.GetLastWin32Error();
        return code == 0
            ? operation
            : $"{operation}（Win32 错误 {code}：{new Win32Exception(code).Message}）";
    }
}
