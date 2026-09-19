using Ta.Core.Imaging;
using Ta.Core.LongCapture;

namespace Ta.Core.Imaging;

/// <summary>
/// 把 <see cref="RgbaBitmap"/> 适配为拼接器的帧类型，
/// 使 <see cref="ScrollingImageStitcher"/> 可以直接在真实位图上运行。
///
/// 这补上了之前留出的那道缝：记账逻辑已在 Ta.Core 测好，
/// 现在只需提供采样器即可让整条链路端到端跑通。
/// </summary>
public sealed class BitmapStitchFrame : IStitchFrame
{
    public required RgbaBitmap Bitmap { get; init; }
    public int Width => Bitmap.Width;
    public int Height => Bitmap.Height;

    /// <summary>
    /// 采样器：按长截图的采样规格转灰度。
    /// 采样尺寸与 Mac 版一致（96×720，宽高各自独立夹取）。
    /// </summary>
    public static GrayscaleFrame Sample(IStitchFrame frame)
    {
        if (frame is not BitmapStitchFrame bitmapFrame)
        {
            throw new ArgumentException("只支持 BitmapStitchFrame。", nameof(frame));
        }

        return bitmapFrame.Bitmap.ToGrayscale(DefaultSampleWidth, DefaultSampleHeight);
    }

    public const int DefaultSampleWidth = 96;
    public const int DefaultSampleHeight = 720;
}

/// <summary>
/// 在 <see cref="RgbaBitmap"/> 上驱动长截图拼接的门面。
///
/// 把「记账」与「像素合成」接起来：前者已在 Ta.Core 完整测试，
/// 后者由 <see cref="VerticalCompositor"/> 提供纯托管实现。
/// </summary>
public sealed class BitmapLongCapture
{
    private readonly ScrollingImageStitcher _stitcher;

    public BitmapLongCapture(VerticalScrollMatcher? matcher = null)
    {
        _stitcher = new ScrollingImageStitcher(
            BitmapStitchFrame.Sample,
            matcher,
            BitmapStitchFrame.DefaultSampleWidth,
            BitmapStitchFrame.DefaultSampleHeight);
    }

    public ScrollingImageStitcher Stitcher => _stitcher;

    public void Reset() => _stitcher.Reset();

    public FrameDisposition Append(RgbaBitmap bitmap, int? preferredPixelShift = null) =>
        _stitcher.Append(new BitmapStitchFrame { Bitmap = bitmap }, preferredPixelShift: preferredPixelShift);

    public FrameDisposition AppendFallback(RgbaBitmap bitmap, int requestedSignedPixelShift) =>
        _stitcher.AppendFallback(new BitmapStitchFrame { Bitmap = bitmap }, requestedSignedPixelShift);

    public FrameDisposition AppendTerminal(RgbaBitmap bitmap, int? preferredPixelShift = null) =>
        _stitcher.AppendTerminalFrame(new BitmapStitchFrame { Bitmap = bitmap }, preferredPixelShift);

    /// <summary>合成完整长图。超出像素上限时抛错。</summary>
    public RgbaBitmap Compose()
    {
        if (_stitcher.ExceedsOutputLimit())
        {
            throw new StitchException(StitchError.OutputTooLarge);
        }

        var strips = _stitcher.MakeStrips()
            .Select(s => (s.Frame is BitmapStitchFrame b ? b.Bitmap : throw new InvalidOperationException(), s.SourceY, s.Height))
            .ToList();

        return VerticalCompositor.Compose(strips);
    }

    /// <summary>
    /// 分段合成。超长输出不必一次性生成整张图 —— 每段独立合成后即可
    /// 交给编码器写盘，显著降低峰值内存。
    /// </summary>
    public IReadOnlyList<RgbaBitmap> ComposeParts(int maximumPixelHeight = ScrollingImageStitcher.DefaultMaximumPartPixelHeight)
    {
        var strips = _stitcher.MakeStrips()
            .Select(s => (s.Frame is BitmapStitchFrame b ? b.Bitmap : throw new InvalidOperationException(), s.SourceY, s.Height))
            .ToList();

        return _stitcher.PlanParts(maximumPixelHeight)
            .Select(part => VerticalCompositor.ComposeRange(strips, part.StartY, part.Height))
            .ToList();
    }
}
