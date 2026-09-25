using Ta.Core.Capture;

namespace Ta.LongSession;

/// <summary>HUD 按钮。对应 Mac 版 ScrollingCaptureHUDView 的四个按钮（:593-598）。</summary>
public enum HudButton
{
    AutoScroll,
    Pause,
    Cancel,
    Finish,
}

/// <summary>HUD 实时状态。对应 Mac: ScrollingCaptureHUDModel（:553-564）。</summary>
public sealed record LongCaptureHudState
{
    public bool IsPaused { get; init; }
    public bool IsAutoScrolling { get; init; }
    public int AcceptedFrames { get; init; }
    public int SkippedFrames { get; init; }
    public int PixelHeight { get; init; }
    public string Status { get; init; } = string.Empty;
    public bool FinishEnabled { get; init; }
}

/// <summary>HUD 视图接口 —— 控制器只依赖它，便于测试替身。</summary>
public interface ILongCaptureHud
{
    void Start(CaptureSelection selection);
    void Update(LongCaptureHudState state);
    void Close();
    event Action<HudButton>? ButtonPressed;
}
