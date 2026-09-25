using Ta.Core.Imaging;

namespace Ta.Core.Capture;

/// <summary>显示器信息。</summary>
public sealed record DisplayInfo
{
    public required int Id { get; init; }
    public required RectD Frame { get; init; }
    public required double PixelScale { get; init; }
    public required bool IsPrimary { get; init; }
}

/// <summary>窗口信息。对应 Mac 版 TaAgentCaptureService 列出的字段。</summary>
public sealed record WindowInfo
{
    public required IntPtr WindowHandle { get; init; }
    public required int ProcessId { get; init; }
    public required string AppName { get; init; }
    public string? BundleOrIdentity { get; init; }
    public string? Title { get; init; }
    public required RectD Frame { get; init; }

    /// <summary>枚举序次即层级序，靠前者更靠上。对应 Mac 的 zOrder。</summary>
    public required int ZOrder { get; init; }
}

/// <summary>捕获失败原因。</summary>
public enum CaptureFailure
{
    DisplayUnavailable,
    WindowUnavailable,
    InvalidSelection,
    PermissionDenied,
}

public sealed class CaptureException : Exception
{
    public CaptureFailure Failure { get; }

    public CaptureException(CaptureFailure failure, string? detail = null)
        : base($"{failure}{(detail is null ? "" : $": {detail}")}")
    {
        Failure = failure;
    }
}

/// <summary>
/// 屏幕捕获。
///
/// 由平台层（Windows.Graphics.Capture）实现。抽成接口同样是为了解耦 ——
/// 覆盖层、长截图、Agent 桥都依赖此接口，不依赖具体捕获实现。
///
/// ⚠️ 关键行为契约（详见参考文档 §3.1）：
/// 通用截图必须**先冻结整屏再框选**。用户看到的选框永远来自「快捷键按下
/// 那一刻」的画面，而不是实时画面。因此 <see cref="CaptureDisplay"/> 是
/// 整条链路的第一帧，不能省。
///
/// Windows.Graphics.Capture 是流式帧池而非一次性静态截图，实现时需
/// 起帧池 → 等一帧 → 拷出 → 拆除，会有 1–2 帧延迟（参考文档 §14 风险 #1）。
/// </summary>
public interface IScreenCapture
{
    IReadOnlyList<DisplayInfo> Displays { get; }

    IReadOnlyList<WindowInfo> Windows { get; }

    /// <summary>捕获整个显示器。像素尺寸需按 <c>pixelScale</c> 精确给出。</summary>
    RgbaBitmap CaptureDisplay(int displayId, double pixelScale);

    /// <summary>从已冻结的整屏图上裁出区域。</summary>
    RgbaBitmap CropFrozen(RgbaBitmap frozenDisplay, CaptureSelection selection);

    /// <summary>捕获单个窗口（不含其他窗口）。</summary>
    RgbaBitmap CaptureWindow(IntPtr windowHandle, double pixelScale);

    /// <summary>捕获前台应用的主窗口。</summary>
    RgbaBitmap CaptureFrontmost(double pixelScale);

    /// <summary>屏幕录制权限是否已授予。</summary>
    bool HasPermission { get; }

    /// <summary>请求权限。无 Windows 等价物时返回当前状态即可。</summary>
    bool RequestPermission();
}
