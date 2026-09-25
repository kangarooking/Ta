using System.Runtime.InteropServices;
using Ta.Shell.Orchestration;

namespace Ta.Shell.Platform;

/// <summary>
/// Win32 剪贴板服务。
///
/// 对应 Mac 版 <c>ClipboardService</c>（<c>NSPasteboard</c>）。
///
/// # 竞态保护的分工
///
/// 本类<b>只负责「怎么写」</b>；「该不该写」由
/// <see cref="ClipboardCommitPolicy"/> 判定，判定发生在调用本类<b>之前</b>。
/// 因此不存在「写了又撤回」的中间态 —— 用户要么拿到新内容，要么保留自己的，
/// 不会出现「复制成功了但内容被换掉了」这种最难排查的情况。
///
/// # 两个必须注意的 Win32 细节
///
///   1. <b>必须是 STA 线程</b>。OLE 剪贴板要求 STA，MTA 线程上
///      <c>OpenClipboard</c> 会静默失败。
///   2. <b>句柄所有权移交</b>。<c>SetClipboardData</c> 成功后系统接管 HGLOBAL，
///      调用方不能再释放它 —— 也不能再写它。因此失败路径必须释放，
///      成功路径必须<b>不</b>释放。
/// </summary>
public sealed class Win32ClipboardService : IClipboardService, IDisposable
{
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_DIB = 8;
    private const uint CF_PNG = 17;   // "PNG" 注册格式，可用 RegisterClipboardFormat("PNG")
    private uint _pngFormat;
    private bool _disposed;

    /// <summary>当前剪贴板序号。对应 NSPasteboard.changeCount。</summary>
    public int ChangeCount => (int)GetClipboardSequenceNumber();

    public void Write(ClipboardPayload payload)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(Win32ClipboardService));
        }

        switch (payload.Kind)
        {
            case ClipboardContentKind.Text:
                WriteText(payload.Text ?? string.Empty);
                break;

            case ClipboardContentKind.Image:
                if (payload.ImagePng is not { Length: > 0 } png)
                {
                    throw new InvalidOperationException("图片剪贴板内容缺少 PNG 字节。");
                }

                WritePng(png);
                break;
        }
    }

    private static void WriteText(string text)
    {
        var bytes = (text + "\0").AsSpan();

        // GMEM_MOVEABLE：剪贴板必须用可移动内存块。
        var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(bytes.Length * sizeof(char)));
        if (handle == IntPtr.Zero)
        {
            throw new OutOfMemoryException("分配剪贴板文本内存失败。");
        }

        try
        {
            var target = GlobalLock(handle);
            if (target == IntPtr.Zero)
            {
                throw new InvalidOperationException("GlobalLock 失败，无法写入剪贴板。");
            }

            try
            {
                Marshal.Copy(bytes.ToArray(), 0, target, bytes.Length);
            }
            finally
            {
                GlobalUnlock(handle);
            }

            CommitToClipboard(CF_UNICODETEXT, handle);
        }
        catch
        {
            GlobalFree(handle);
            throw;
        }
    }

    private void WritePng(byte[] png)
    {
        var format = _pngFormat != 0 ? _pngFormat : (_pngFormat = RegisterClipboardFormatW("PNG"));

        var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)png.Length);
        if (handle == IntPtr.Zero)
        {
            throw new OutOfMemoryException("分配剪贴板图片内存失败。");
        }

        try
        {
            var target = GlobalLock(handle);
            if (target == IntPtr.Zero)
            {
                throw new InvalidOperationException("GlobalLock 失败，无法写入剪贴板。");
            }

            try
            {
                Marshal.Copy(png, 0, target, png.Length);
            }
            finally
            {
                GlobalUnlock(handle);
            }

            CommitToClipboard(format, handle);
        }
        catch
        {
            GlobalFree(handle);
            throw;
        }
    }

    /// <summary>
    /// 打开剪贴板 → 清空 → 设置 → 关闭。
    ///
    /// <b>重试</b>是必要的：别的应用短时间占着剪贴板时 OpenClipboard 会失败，
    /// 这正是 Mac 版结果条里「剪贴板被其他应用占用」那条文案对应的真实场景。
    /// </summary>
    private static void CommitToClipboard(uint format, IntPtr handle)
    {
        const int maxAttempts = 10;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    if (!EmptyClipboard())
                    {
                        throw new InvalidOperationException(
                            $"EmptyClipboard 失败，Win32 错误 {Marshal.GetLastWin32Error()}。");
                    }

                    if (SetClipboardData(format, handle) == IntPtr.Zero)
                    {
                        // 失败：系统没有接管句柄，调用方仍要释放。
                        throw new InvalidOperationException(
                            $"SetClipboardData 失败，Win32 错误 {Marshal.GetLastWin32Error()}。");
                    }

                    // 成功：系统已接管 handle，**绝不能**释放。
                    return;
                }
                finally
                {
                    CloseClipboard();
                }
            }

            if (attempt == maxAttempts - 1)
            {
                throw new InvalidOperationException("剪贴板被其他应用占用，写入失败。");
            }

            Thread.Sleep(15);
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormatW(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);
}
