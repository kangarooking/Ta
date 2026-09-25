namespace Ta.Spike.Overlay;

/// <summary>整数矩形，单位：屏幕像素，原点为屏幕左上。</summary>
internal readonly record struct Rect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>
/// 覆盖层结果。对应 Mac 版 CaptureCoordinator 的 CaptureOutcome：
/// 取消不产生文件、也不修改剪贴板。
/// </summary>
internal readonly record struct OverlayResult
{
    public bool WasCancelled { get; private init; }
    public Rect Selection { get; private init; }

    public static OverlayResult Cancelled() => new() { WasCancelled = true };

    public static OverlayResult Selected(Rect rect) => new() { Selection = rect };

    public override string ToString() => WasCancelled
        ? "已取消"
        : $"({Selection.Left}, {Selection.Top}) {Selection.Width}×{Selection.Height}";
}
