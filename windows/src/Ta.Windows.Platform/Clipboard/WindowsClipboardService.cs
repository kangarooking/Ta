using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using Ta.Windows.Core.Clipboard;
using Ta.Windows.Platform.Interop;

namespace Ta.Windows.Platform.Clipboard;

public interface IWindowsClipboardService
{
    uint GetSequenceNumber();

    bool TrySetText(
        string text,
        uint expectedSequence,
        Guid requestedJobId,
        Guid latestJobId,
        out string? failureMessage);

    void SetImageExplicit(Bitmap image);

    void SetTextExplicit(string text);
}

public sealed class WindowsClipboardService : IWindowsClipboardService, IDisposable
{
    private const int RetryCount = 5;
    private const int RetryDelayMilliseconds = 80;
    private readonly HwndSource clipboardOwnerSource;
    private bool disposed;

    public WindowsClipboardService()
    {
        clipboardOwnerSource = new HwndSource(new HwndSourceParameters("Ta.Windows.ClipboardOwner")
        {
            ParentWindow = NativeMethods.MessageOnlyWindow,
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        });
    }

    public uint GetSequenceNumber() => NativeMethods.GetClipboardSequenceNumber();

    public bool TrySetText(
        string text,
        uint expectedSequence,
        Guid requestedJobId,
        Guid latestJobId,
        out string? failureMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        try
        {
            for (var attempt = 0; attempt < RetryCount; attempt++)
            {
                if (!NativeMethods.OpenClipboard(clipboardOwnerSource.Handle))
                {
                    if (attempt < RetryCount - 1)
                    {
                        Thread.Sleep(RetryDelayMilliseconds);
                        continue;
                    }

                    break;
                }

                try
                {
                    var currentSequence = GetSequenceNumber();
                    if (!ClipboardCommitPolicy.CanCommit(
                            expectedSequence,
                            currentSequence,
                            requestedJobId,
                            latestJobId))
                    {
                        failureMessage = requestedJobId != latestJobId
                            ? "已有更新的截图任务，本次迟到结果没有覆盖剪贴板。"
                            : "处理期间剪贴板已经变化，文件路径仍然安全保留，请点击复制路径。";
                        return false;
                    }

                    WriteUnicodeTextWhileClipboardOpen(text);
                    failureMessage = null;
                    return true;
                }
                finally
                {
                    NativeMethods.CloseClipboard();
                }
            }

            failureMessage = "Windows 剪贴板持续被其他程序占用，文件路径仍然安全保留，请点击复制路径重试。";
            return false;
        }
        catch (Win32Exception exception)
        {
            failureMessage = $"Windows 剪贴板写入失败，文件路径仍然安全保留。下一步：{exception.Message}";
            return false;
        }
    }

    public void SetImageExplicit(Bitmap image)
    {
        ArgumentNullException.ThrowIfNull(image);
        RetryClipboardWrite(() => System.Windows.Forms.Clipboard.SetDataObject(image, true));
    }

    public void SetTextExplicit(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        for (var attempt = 0; attempt < RetryCount; attempt++)
        {
            if (!NativeMethods.OpenClipboard(clipboardOwnerSource.Handle))
            {
                if (attempt < RetryCount - 1)
                {
                    Thread.Sleep(RetryDelayMilliseconds);
                    continue;
                }

                throw new ExternalException("Windows 剪贴板持续被占用，未写入任何新内容。");
            }

            try
            {
                WriteUnicodeTextWhileClipboardOpen(text);
                return;
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        clipboardOwnerSource.Dispose();
        disposed = true;
    }

    private static void WriteUnicodeTextWhileClipboardOpen(string text)
    {
        var bytes = Encoding.Unicode.GetBytes($"{text}\0");
        var memoryHandle = NativeMethods.GlobalAlloc(
            NativeMethods.GlobalMemoryMoveable,
            (nuint)bytes.Length);
        if (memoryHandle == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法为剪贴板文本分配内存。");
        }

        var ownershipTransferred = false;
        try
        {
            var pointer = NativeMethods.GlobalLock(memoryHandle);
            if (pointer == nint.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法锁定剪贴板文本内存。");
            }

            try
            {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
            }
            finally
            {
                NativeMethods.GlobalUnlock(memoryHandle);
            }

            if (!NativeMethods.EmptyClipboard())
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法清空旧剪贴板内容。");
            }

            if (NativeMethods.SetClipboardData(
                    NativeMethods.ClipboardFormatUnicodeText,
                    memoryHandle) == nint.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法提交新的剪贴板文本。");
            }

            ownershipTransferred = true;
        }
        finally
        {
            if (!ownershipTransferred)
            {
                NativeMethods.GlobalFree(memoryHandle);
            }
        }
    }

    private static void RetryClipboardWrite(Action operation)
    {
        ExternalException? lastException = null;
        for (var attempt = 0; attempt < RetryCount; attempt++)
        {
            try
            {
                operation();
                return;
            }
            catch (ExternalException exception)
            {
                lastException = exception;
                if (attempt < RetryCount - 1)
                {
                    Thread.Sleep(RetryDelayMilliseconds);
                }
            }
        }

        throw new ExternalException("Windows 剪贴板持续被占用，未写入任何新内容。", lastException);
    }
}
