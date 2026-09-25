using System.Runtime.InteropServices;
using Ta.Core.Capture;
using Ta.Core.Imaging;
using Ta.Shell.Contracts;
using Ta.Shell.Models;
using WinForms = System.Windows.Forms;

namespace Ta.Shell.Composition;

/// <summary>
/// <see cref="IScreenshotExporter"/> 的真实实现：SaveFileDialog + WIC 编码落盘。
/// 对应 Mac 版 <c>ImageExportService</c>（NSSavePanel + PNG/JPEG 0.92）。
/// </summary>
public sealed class FileScreenshotExporter : IScreenshotExporter
{
    private readonly Ta.Encoding.WicImageEncoder _encoder = new();

    /// <inheritdoc />
    public Task<string?> SaveAsync(
        RgbaBitmap image,
        string suggestedBaseName,
        CancellationToken cancellationToken = default)
    {
        // ⚠️ 不用 WinForms.SaveFileDialog：动作链跑在**线程池线程**（await 续体无线程封送），
        // WinForms 对话框依赖 STA + 消息泵，在这种线程上 ShowDialog 静默失败
        // （实测：点「保存」毫无反应、无对话框无报错）。原生 GetSaveFileName 自带
        // 模态消息循环，任意线程可用。
        var target = NativeSaveFileDialog.Show(
            "保存截图", BuildFileName(suggestedBaseName, "png"));
        if (target is null)
        {
            return Task.FromResult<string?>(null);
        }

        _encoder.Save(image, target);
        return Task.FromResult<string?>(target);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>?> SaveManyAsync(
        IReadOnlyList<RgbaBitmap> images,
        string suggestedBaseName,
        CancellationToken cancellationToken = default)
    {
        var target = NativeSaveFileDialog.Show(
            "保存长截图", BuildFileName(suggestedBaseName, "png"));
        if (target is null)
        {
            return Task.FromResult<IReadOnlyList<string>?>(null);
        }

        // 单文件导出整段拼接图：多段合一时用户预期一个文件（与 Mac 的导出行为一致）。
        _encoder.Save(images[0], target);
        IReadOnlyList<string> paths = new[] { target };
        return Task.FromResult<IReadOnlyList<string>?>(paths);
    }

    private static string BuildFileName(string suggestedBaseName, string extension)
    {
        var baseName = string.IsNullOrWhiteSpace(suggestedBaseName)
            ? "AI-Screenshot"
            : Sanitize(suggestedBaseName);
        return $"{baseName}-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}";
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return clean.Length > 0 ? clean : "AI-Screenshot";
    }
}

/// <summary>
/// <see cref="IPinController"/> → <see cref="Ta.Pinning.PinnedImageController"/> 适配。
/// Pin 的 anchor 语义与 Mac 版 <c>pin(image:near:)</c> 一致：窗口出现在原选区附近。
/// </summary>
public sealed class PinControllerAdapter : IPinController, IDisposable
{
    private readonly Ta.Pinning.PinnedImageController _controller;

    public PinControllerAdapter(Ta.Pinning.PinnedImageController? controller = null)
    {
        _controller = controller ?? new Ta.Pinning.PinnedImageController();
    }

    /// <summary>真实控制器（钉图管理面板等未来功能用）。</summary>
    public Ta.Pinning.PinnedImageController Controller => _controller;

    /// <inheritdoc />
    public void Pin(RgbaBitmap image, CaptureSelection near) =>
        _controller.Pin(image, near);

    /// <inheritdoc />
    public bool PinFromClipboard() => _controller.PinFromClipboard();

    /// <inheritdoc />
    public void HideAll() => _controller.HideAll();

    /// <inheritdoc />
    public void ShowAll() => _controller.ShowAll();

    /// <inheritdoc />
    public void EnableInteractionForAll() => _controller.EnableInteractionForAll();

    /// <inheritdoc />
    public bool RestoreLastClosed() => _controller.RestoreLastClosed();

    public void Dispose() => _controller.Dispose();
}

/// <summary>
/// <see cref="ILongCaptureSession"/> → <see cref="Ta.LongSession.LongCaptureSessionController"/> 适配。
///
/// 控制器是「Start + 事件」模型（HUD 按钮驱动 Cancel/Finish），
/// 编排层要的是「一次 RunAsync 返回结局」—— 用任务完成源把两者接起来。
/// </summary>
public sealed class LongCaptureSessionAdapter : ILongCaptureSession, IDisposable
{
    private readonly Func<IScreenCapture> _captureFactory;

    public LongCaptureSessionAdapter(Func<IScreenCapture> captureFactory)
    {
        _captureFactory = captureFactory;
    }

    /// <inheritdoc />
    public async Task<LongCaptureOutcome> RunAsync(
        CaptureSelection selection,
        CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<LongCaptureOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var controller = new Ta.LongSession.LongCaptureSessionController(_captureFactory());

        void OnCompleted(Ta.LongSession.LongCaptureSessionResult result) =>
            completion.TrySetResult(LongCaptureOutcome.Completed(
                result.Images, result.AcceptedFrames, result.SkippedFrames, result.ReviewedSeams));

        void OnCancelled()
        {
            cancellation.Cancel();
            completion.TrySetResult(LongCaptureOutcome.Cancelled());
        }

        void OnFailed(string message) => completion.TrySetResult(LongCaptureOutcome.Failed(message));

        controller.Completed += OnCompleted;
        controller.Cancelled += OnCancelled;
        controller.Failed += OnFailed;

        try
        {
            controller.Start(selection);

            // 用户在 HUD 上按取消 / 编排层取消（Esc）都要退出。
            using var registration = cancellation.Token.Register(
                static state => ((Ta.LongSession.LongCaptureSessionController)state!).Cancel(), controller);

            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            controller.Completed -= OnCompleted;
            controller.Cancelled -= OnCancelled;
            controller.Failed -= OnFailed;
            controller.Dispose();
        }
    }

    public void Dispose()
    {
    }
}



/// <summary>
/// 原生保存对话框（comdlg32 <c>GetSaveFileNameW</c>，自写 P/Invoke）。
///
/// ⚠️ 三种方案实测对比（2026-09-19）：
///   · <c>WinForms.SaveFileDialog</c>：依赖 STA + 消息泵，动作链在线程池线程 →
///     <b>静默失败</b>（点保存毫无反应）；
///   · <c>WPF SaveFileDialog</c>：依赖 Dispatcher/STA → 在无 Dispatcher 的线程上
///     <b>挂起</b>（无窗口、无异常、流程卡死）；
///   · <b>comdlg32 GetSaveFileNameW</b>：原生通用对话框自带模态消息循环，
///     不依赖调用线程的消息泵 —— 本条路径可用。
///
/// lStructSize 用标准常量（x64=152 / x86=76）：OPENFILENAME 含 string 字段，
/// <c>Marshal.SizeOf</c> 会抛「cannot be marshaled as an unmanaged structure」。
/// </summary>
internal static class NativeSaveFileDialog
{
    private const int OFN_OVERWRITEPROMPT = 0x0000_0002;
    private const int OFN_NOCHANGEDIR = 0x0000_0008;
    private const int OFN_PATHMUSTEXIST = 0x0000_0800;
    private const int OFN_EXPLORER = 0x0008_0000;

    /// <summary>OPENFILENAMEW 的结构尺寸（标准常量，不依赖 Marshal.SizeOf）。</summary>
    private static readonly int StructSize = IntPtr.Size == 8 ? 152 : 76;

    public static string? Show(string title, string defaultFileName)
    {
        const int MaxPath = 260;

        // ⚠️ lpstrFile 必须用非托管缓冲：struct 字段不能用 StringBuilder
        //（TypeLoadException: Struct or class fields cannot be of type StringBuilder），
        // string 字段又会因不可变而无法回写用户输入。手分配 + 读回最可控。
        var filePtr = Marshal.AllocHGlobal(MaxPath * sizeof(char));
        try
        {
            var initial = defaultFileName.ToCharArray();
            if (initial.Length > MaxPath - 1)
            {
                Array.Resize(ref initial, MaxPath - 1);
            }

            Marshal.Copy(initial, 0, filePtr, initial.Length);
            Marshal.WriteInt16(filePtr, initial.Length * sizeof(char), 0);

            var ofn = new OPENFILENAME
            {
                lStructSize = StructSize,
                hwndOwner = IntPtr.Zero,
                lpstrFilter = "PNG 图片 (*.png)\0*.png\0JPEG 图片 (*.jpg)\0*.jpg\0\0",
                nFilterIndex = 1,
                lpstrFile = filePtr,
                nMaxFile = MaxPath,
                lpstrTitle = title,
                Flags = OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST | OFN_EXPLORER | OFN_NOCHANGEDIR,
                lpstrDefExt = "png",
            };

            return GetSaveFileNameW(ref ofn) ? Marshal.PtrToStringUni(filePtr) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(filePtr);
        }
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetSaveFileNameW(ref OPENFILENAME ofn);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAME
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public string? lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public string? lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string? lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }
}
