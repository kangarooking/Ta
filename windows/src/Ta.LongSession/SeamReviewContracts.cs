using Ta.Core.Imaging;
using Ta.Core.LongCapture;
using Ta.LongSession.Win32;

namespace Ta.LongSession;

/// <summary>接缝复查窗口的一行数据。</summary>
public sealed record SeamReviewRow(
    Guid Id,
    ScrollDirection Direction,
    int NewPixelHeight,
    double Confidence,
    int StableTopHeight,
    int StableBottomHeight)
{
    public bool IsLowConfidence => Confidence < ScrollingImageStitcher.LowConfidenceThreshold;
}

/// <summary>
/// 复查视图的输入模型。对应 Mac 版 ScrollingSeamReviewModel（:115-126）。
/// </summary>
public sealed class SeamReviewModel
{
    public IReadOnlyList<SeamReviewRow> Segments { get; init; } = Array.Empty<SeamReviewRow>();

    /// <summary>预览分段（每段 ≤ 8000px）。</summary>
    public IReadOnlyList<RgbaBitmap> PreviewParts { get; init; } = Array.Empty<RgbaBitmap>();

    public int OutputPixelHeight { get; init; }
    public int LowConfidenceCount { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>复查视图接口 —— 控制器只依赖它。</summary>
public interface ISeamReviewView
{
    void Present(SeamReviewModel model, Action<Guid, int> onAdjust, Action onConfirm, Action onCancel);
    void Refresh(SeamReviewModel model);
    void Close();
    event Action? Closed;
}
