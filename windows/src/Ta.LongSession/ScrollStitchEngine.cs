using Ta.Core.Imaging;
using Ta.Core.LongCapture;

namespace Ta.LongSession;

/// <summary>
/// 拼接引擎抽象。控制器只依赖本接口，测试可注入假实现。
/// 默认实现见 <see cref="BitmapStitchEngine"/>（基于 Ta.Core 的
/// BitmapLongCapture —— 记账已在 143 个测试中锁定）。
/// </summary>
public interface IScrollStitchEngine
{
    void Reset();

    /// <summary>追加一帧。返回 Mac 版一致的四种 disposition。</summary>
    FrameDisposition Append(
        RgbaBitmap bitmap,
        ScrollConstraint constraint = ScrollConstraint.Any,
        int? preferredPixelShift = null);

    FrameDisposition AppendFallback(RgbaBitmap bitmap, int requestedSignedPixelShift);

    FrameDisposition AppendTerminal(RgbaBitmap bitmap, int? preferredPixelShift = null);

    bool AdjustSegment(Guid id, int pixelDelta);

    IReadOnlyList<StitchSegment> Segments { get; }

    int OutputPixelHeight { get; }

    int FrameCount { get; }

    /// <summary>低置信度接缝数量（对应 Mac: lowConfidenceCount）。</summary>
    int LowConfidenceCount { get; }

    IReadOnlyList<RgbaBitmap> MakeImages(int maximumPixelHeight = ScrollingImageStitcher.DefaultMaximumPartPixelHeight);
}

/// <summary>
/// 基于 Ta.Core.Imaging.BitmapLongCapture 的默认实现。
/// </summary>
public sealed class BitmapStitchEngine : IScrollStitchEngine
{
    private readonly BitmapLongCapture _capture;

    public BitmapStitchEngine(VerticalScrollMatcher? matcher = null)
    {
        _capture = new BitmapLongCapture(matcher);
    }

    public ScrollingImageStitcher Stitcher => _capture.Stitcher;

    public void Reset() => _capture.Reset();

    public FrameDisposition Append(RgbaBitmap bitmap, ScrollConstraint constraint, int? preferredPixelShift)
    {
        var frame = new BitmapStitchFrame { Bitmap = bitmap };
        return _capture.Stitcher.Append(frame, constraint, preferredPixelShift);
    }

    public FrameDisposition AppendFallback(RgbaBitmap bitmap, int requestedSignedPixelShift) =>
        _capture.AppendFallback(bitmap, requestedSignedPixelShift);

    public FrameDisposition AppendTerminal(RgbaBitmap bitmap, int? preferredPixelShift) =>
        _capture.AppendTerminal(bitmap, preferredPixelShift);

    public bool AdjustSegment(Guid id, int pixelDelta) => _capture.Stitcher.AdjustSegment(id, pixelDelta);

    public IReadOnlyList<StitchSegment> Segments => _capture.Stitcher.Segments;

    public int OutputPixelHeight => _capture.Stitcher.OutputPixelHeight;

    public int FrameCount => _capture.Stitcher.FrameCount;

    public int LowConfidenceCount
    {
        get
        {
            var count = 0;
            foreach (var issue in _capture.Stitcher.QualityIssues)
            {
                if (issue is LowConfidenceIssue)
                {
                    count++;
                }
            }

            return count;
        }
    }

    public IReadOnlyList<RgbaBitmap> MakeImages(int maximumPixelHeight) =>
        _capture.ComposeParts(maximumPixelHeight);
}
