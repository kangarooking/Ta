namespace Ta.Core.LongCapture;

/// <summary>拼接器错误。对应 Mac: ScrollingImageStitcherError。</summary>
public enum StitchError
{
    NoFrames,
    OutputTooLarge,
    InvalidImage,
    CanvasCreationFailed,
}

public sealed class StitchException : Exception
{
    public StitchError Error { get; }

    public StitchException(StitchError error) : base(Describe(error))
    {
        Error = error;
    }

    private static string Describe(StitchError error) => error switch
    {
        StitchError.NoFrames => "还没有捕获任何帧。",
        StitchError.OutputTooLarge => "拼接结果超出像素上限。",
        StitchError.InvalidImage => "图像尺寸无效。",
        StitchError.CanvasCreationFailed => "无法创建拼接画布。",
        _ => error.ToString(),
    };
}

/// <summary>单帧的处理结果。</summary>
public enum FrameDisposition
{
    FirstFrame,
    Appended,
    Duplicate,
    Rejected,
}

/// <summary>
/// 一次接缝。对应 Mac: ScrollingStitchSegment。
///
/// confidence &lt; 0.58 会被标记为低置信度，交由人工复查。
/// </summary>
public sealed record StitchSegment
{
    public required Guid Id { get; init; }
    public required ScrollDirection Direction { get; init; }
    public required int NewPixelHeight { get; init; }
    public required double Confidence { get; init; }
    public required int StableTopHeight { get; init; }
    public required int StableBottomHeight { get; init; }
}

/// <summary>质量提示。对应 Mac: ScrollingCaptureQualityIssue。</summary>
public abstract record QualityIssue;

public sealed record LowConfidenceIssue(Guid SegmentId, double Confidence) : QualityIssue;

public sealed record UltraLongOutputIssue(int RecommendedPartCount) : QualityIssue;

/// <summary>
/// 待拼接的帧。
///
/// 抽象成只有宽高的接口，是为了把拼接器的**记账逻辑**与像素操作解耦 ——
/// 这样高度累加、接缝微调、条带组装这些最容易出错的部分可以完全脱离
/// WIC / Direct2D 做单元测试。像素合成留在平台层实现。
/// </summary>
public interface IStitchFrame
{
    int Width { get; }
    int Height { get; }
}

/// <summary>一条待绘制的条带。</summary>
public readonly record struct ImageStrip(IStitchFrame Frame, int SourceY, int Height);

/// <summary>
/// 滚动长截图拼接器。
///
/// 逐行对应 Mac 版 ScrollingImageStitcher.swift。记账部分（高度累加、
/// 上下游走、接缝微调、质量提示、条带组装）已完整移植；
/// <see cref="Render"/> 的像素合成依赖平台图形层，此处只定义契约。
/// </summary>
public sealed class ScrollingImageStitcher
{
    /// <summary>低置信度阈值。对应 Mac: 0.58。</summary>
    public const double LowConfidenceThreshold = 0.58;

    /// <summary>末帧接受的最低置信度。对应 Mac: 0.05。</summary>
    public const double TerminalMinimumConfidence = 0.05;

    /// <summary>末帧置信度上限 —— 末帧本就弱匹配，不应标成高置信。对应 Mac: 0.35。</summary>
    public const double TerminalConfidenceCap = 0.35;

    /// <summary>回退位移的安全上限比例。对应 Mac: image.height * 0.55。</summary>
    public const double FallbackShiftFraction = 0.55;

    /// <summary>回退位移的最小幅度。</summary>
    public const int FallbackMinimumShift = 2;

    /// <summary>可接受的最小新增高度。小于该值视为重复帧。</summary>
    public const int MinimumAppendedHeight = 2;

    /// <summary>分段的默认像素高度上限。</summary>
    public const int DefaultMaximumPartPixelHeight = 30_000;

    private readonly VerticalScrollMatcher _matcher;
    private readonly int _sampleWidth;
    private readonly int _sampleHeight;
    private readonly int _maximumOutputPixels;
    private readonly Func<IStitchFrame, GrayscaleFrame> _sampler;

    private IStitchFrame? _firstFrame;
    private GrayscaleFrame? _previousSample;
    private readonly List<(IStitchFrame Frame, StitchSegment Details)> _capturedSegments = [];
    private readonly List<StitchSegment> _stitchSegments = [];
    private int _accumulatedHeight;
    private int _viewportOffset;
    private int _minimumViewportOffset;
    private int _maximumViewportOffset;

    public ScrollingImageStitcher(
        Func<IStitchFrame, GrayscaleFrame> sampler,
        VerticalScrollMatcher? matcher = null,
        int sampleWidth = 96,
        int sampleHeight = 720,
        int maximumOutputPixels = 120_000_000)
    {
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
        _matcher = matcher ?? new VerticalScrollMatcher();
        // 对应 Mac: sampleWidth = max(32, 96)，sampleHeight = max(120, 720)
        _sampleWidth = Math.Max(32, sampleWidth);
        _sampleHeight = Math.Max(120, sampleHeight);
        _maximumOutputPixels = maximumOutputPixels;
    }

    public int FrameCount => _firstFrame is null ? 0 : _capturedSegments.Count + 1;

    public int OutputPixelHeight => _accumulatedHeight;

    public IReadOnlyList<StitchSegment> Segments => _stitchSegments;

    public int FrameWidth => _firstFrame?.Width ?? 0;

    /// <summary>
    /// 质量提示：低置信度接缝 + 超长输出分段建议。
    /// </summary>
    public IReadOnlyList<QualityIssue> QualityIssues
    {
        get
        {
            var issues = new List<QualityIssue>();
            foreach (var segment in _stitchSegments)
            {
                if (segment.Confidence < LowConfidenceThreshold)
                {
                    issues.Add(new LowConfidenceIssue(segment.Id, segment.Confidence));
                }
            }

            if (_firstFrame is { } first)
            {
                var safeHeight = Math.Max(1, _maximumOutputPixels / Math.Max(1, first.Width));
                if (_accumulatedHeight > safeHeight)
                {
                    issues.Add(new UltraLongOutputIssue(
                        (int)Math.Ceiling((double)_accumulatedHeight / safeHeight)));
                }
            }

            return issues;
        }
    }

    /// <summary>
    /// 让复查界面就地微调接缝，无需重新截图。
    /// 负增量移除重复行；正增量恢复行。
    /// </summary>
    public bool AdjustSegment(Guid id, int pixelDelta)
    {
        var capturedIndex = _capturedSegments.FindIndex(s => s.Details.Id == id);
        var publicIndex = _stitchSegments.FindIndex(s => s.Id == id);
        if (capturedIndex < 0 || publicIndex < 0)
        {
            return false;
        }

        var captured = _capturedSegments[capturedIndex];
        var old = captured.Details;

        // 夹在 [1, 帧高] 内 —— 与 Mac 一致。
        var newHeight = Math.Clamp(old.NewPixelHeight + pixelDelta, 1, captured.Frame.Height);
        if (newHeight == old.NewPixelHeight)
        {
            return false;
        }

        var updated = old with { NewPixelHeight = newHeight };
        _capturedSegments[capturedIndex] = (captured.Frame, updated);
        _stitchSegments[publicIndex] = updated;
        _accumulatedHeight += newHeight - old.NewPixelHeight;
        return true;
    }

    public void Reset()
    {
        _firstFrame = null;
        _previousSample = null;
        _capturedSegments.Clear();
        _stitchSegments.Clear();
        _accumulatedHeight = 0;
        _viewportOffset = 0;
        _minimumViewportOffset = 0;
        _maximumViewportOffset = 0;
    }

    public FrameDisposition Append(
        IStitchFrame frame,
        ScrollConstraint constraint = ScrollConstraint.Any,
        int? preferredPixelShift = null)
    {
        RequireValidFrame(frame);
        var sample = _sampler(frame);

        if (_firstFrame is null || _previousSample is null)
        {
            _firstFrame = frame;
            _previousSample = sample;
            _accumulatedHeight = frame.Height;
            return FrameDisposition.FirstFrame;
        }

        if (frame.Width != _firstFrame.Width || frame.Height != _firstFrame.Height)
        {
            return FrameDisposition.Rejected;
        }

        var scale = (double)frame.Height / sample.Height;
        var preferredSampleShift = preferredPixelShift is { } shift
            ? (int)Math.Round((double)shift / scale)
            : (int?)null;

        var match = _matcher.Match(
            _previousSample, sample, constraint,
            preferredSampleShift);

        // 用 is not { } 模式取得非空值 —— match is null 的三元表达式不会收窄局部变量。
        if (match is not { } found)
        {
            return FrameDisposition.Rejected;
        }

        return AppendMatched(frame, sample, found, scale);
    }

    /// <summary>
    /// 提交滚动条到达最大后的最终稳定视口。
    ///
    /// 最后一次滚动通常比常规步长更短，而重复聊天行会让那道小接缝显得歧义。
    /// 在确认已到底时，以低置信度接受最佳的向下重叠更安全 —— 这样文档尾部
    /// 仍会保留下来供接缝复查。
    /// </summary>
    public FrameDisposition AppendTerminalFrame(IStitchFrame frame, int? preferredPixelShift = null)
    {
        RequireValidFrame(frame);
        var sample = _sampler(frame);

        if (_firstFrame is null || _previousSample is null)
        {
            _firstFrame = frame;
            _previousSample = sample;
            _accumulatedHeight = frame.Height;
            return FrameDisposition.FirstFrame;
        }

        if (frame.Width != _firstFrame.Width || frame.Height != _firstFrame.Height)
        {
            return FrameDisposition.Rejected;
        }

        var scale = (double)frame.Height / sample.Height;
        var preferredSampleShift = preferredPixelShift is { } shift
            ? (int)Math.Round((double)shift / scale)
            : (int?)null;

        var match = _matcher.Match(
            _previousSample,
            sample,
            ScrollConstraint.DownwardOnly,
            preferredSampleShift,
            allowsAmbiguousMatch: true,
            minimumAcceptedConfidence: TerminalMinimumConfidence);

        if (match is null)
        {
            return FrameDisposition.Rejected;
        }

        // 置信度上限 0.35 —— 末帧是刻意放宽接受的，不应标成高置信。
        var terminal = match.Value with { Confidence = Math.Min(match.Value.Confidence, TerminalConfidenceCap) };
        return AppendMatched(frame, sample, terminal, scale);
    }

    /// <summary>
    /// 视口确实移动了、但视觉匹配器找不到唯一接缝时推进锚点。
    ///
    /// 聊天时间线常有重复卡片、空白区、动画光标与固定输入框，这些条件会让
    /// 正确的接缝显得歧义 —— 尽管滚动本身成功了。保留这个低置信度帧比
    /// 阻塞后续捕获或静默丢掉整个视口更安全。
    /// </summary>
    public FrameDisposition AppendFallback(IStitchFrame frame, int requestedSignedPixelShift)
    {
        RequireValidFrame(frame);
        var sample = _sampler(frame);

        if (_firstFrame is null || _previousSample is null)
        {
            _firstFrame = frame;
            _previousSample = sample;
            _accumulatedHeight = frame.Height;
            return FrameDisposition.FirstFrame;
        }

        if (frame.Width != _firstFrame.Width || frame.Height != _firstFrame.Height)
        {
            return FrameDisposition.Rejected;
        }

        // 对应 Mac: Int((Double(image.height) * 0.55).rounded(.down))
        var maximumSafeShift = Math.Max(
            FallbackMinimumShift,
            (int)Math.Floor(frame.Height * FallbackShiftFraction));
        var magnitude = Math.Min(maximumSafeShift, Math.Max(FallbackMinimumShift, Math.Abs(requestedSignedPixelShift)));
        var signedPixelShift = requestedSignedPixelShift < 0 ? -magnitude : magnitude;

        _viewportOffset += signedPixelShift;

        if (TryAdvanceViewport(frame, sample, signedPixelShift, confidence: 0, minimumHeight: FallbackMinimumShift)
            is { } disposition)
        {
            return disposition;
        }

        _previousSample = sample;
        return FrameDisposition.Duplicate;
    }

    private FrameDisposition AppendMatched(
        IStitchFrame frame,
        GrayscaleFrame sample,
        ScrollMatch match,
        double scale)
    {
        if (_firstFrame is null)
        {
            return FrameDisposition.Rejected;
        }

        if (match.IsDuplicate)
        {
            return FrameDisposition.Duplicate;
        }

        var signedPixelShift = (int)Math.Round(match.SignedShift * scale);
        _viewportOffset += signedPixelShift;

        if (TryAdvanceViewport(frame, sample, signedPixelShift, match.Confidence, MinimumAppendedHeight)
            is { } disposition)
        {
            return disposition;
        }

        _previousSample = sample;
        return FrameDisposition.Duplicate;
    }

    /// <summary>
    /// 视口记账的核心。返回 null 表示该帧未产生新增内容（重复）。
    ///
    /// 对应 Mac :267-299。三个分支：
    ///   · 视口越过下界 → 新增高度，更新下界
    ///   · 视口越过上界 → 新增高度，更新上界
    ///   · 视口退回到已覆盖区域 → 只更新匹配基准，不计入高度
    /// </summary>
    private FrameDisposition? TryAdvanceViewport(
        IStitchFrame frame,
        GrayscaleFrame sample,
        int signedPixelShift,
        double confidence,
        int minimumHeight)
    {
        if (_firstFrame is null)
        {
            return null;
        }

        int newPixelHeight;
        if (_viewportOffset < _minimumViewportOffset)
        {
            newPixelHeight = _minimumViewportOffset - _viewportOffset;
            _minimumViewportOffset = _viewportOffset;
        }
        else if (_viewportOffset > _maximumViewportOffset)
        {
            newPixelHeight = _viewportOffset - _maximumViewportOffset;
            _maximumViewportOffset = _viewportOffset;
        }
        else
        {
            // 用户退回到了已表示的区域。保留该帧作为下一个匹配基准，但不重复计入。
            return null;
        }

        var appendedHeight = Math.Min(frame.Height, Math.Max(1, newPixelHeight));
        if (appendedHeight < minimumHeight)
        {
            return null;
        }

        if (_previousSample is { } previousSample)
        {
            var stable = _matcher.StableEdges(previousSample, sample);
            var scale = (double)frame.Height / sample.Height;

            var details = new StitchSegment
            {
                Id = Guid.NewGuid(),
                Direction = signedPixelShift < 0 ? ScrollDirection.Up : ScrollDirection.Down,
                NewPixelHeight = appendedHeight,
                Confidence = confidence,
                // 稳定高度也夹到帧高的 1/3 —— 与 Mac 一致。
                StableTopHeight = Math.Min(
                    frame.Height / 3,
                    (int)Math.Round(stable.TopRows * scale)),
                StableBottomHeight = Math.Min(
                    frame.Height / 3,
                    (int)Math.Round(stable.BottomRows * scale)),
            };

            _capturedSegments.Add((frame, details));
            _stitchSegments.Add(details);
        }

        _accumulatedHeight = _firstFrame.Height + _maximumViewportOffset - _minimumViewportOffset;
        return FrameDisposition.Appended;
    }

    /// <summary>
    /// 条带组装，输出顺序即拼接顺序。对应 Mac :386-433。
    /// </summary>
    public IReadOnlyList<ImageStrip> MakeStrips()
    {
        if (_firstFrame is not { } first)
        {
            return [];
        }

        var upward = _capturedSegments.Where(s => s.Details.Direction == ScrollDirection.Up).ToList();
        var downward = _capturedSegments.Where(s => s.Details.Direction == ScrollDirection.Down).ToList();

        // 固定区域必须在各帧间一致出现。取**下中位数**而非最大值：
        // 取最大值会让一帧空白/重复内容从每条接缝都裁掉那么多行，
        // 在有大型输入框的聊天应用里破坏性尤其大。
        var stableTop = ConservativeStableHeight(upward.Select(s => s.Details.StableTopHeight));
        var stableBottom = ConservativeStableHeight(downward.Select(s => s.Details.StableBottomHeight));
        var safeTop = Math.Min(stableTop, first.Height / 3);
        var safeBottom = Math.Min(stableBottom, first.Height / 3);

        var strips = new List<ImageStrip>();

        if (upward.Count > 0 && safeTop > 0)
        {
            var earliest = upward[^1];
            strips.Add(new ImageStrip(earliest.Frame, 0, safeTop));
        }

        for (var i = upward.Count - 1; i >= 0; i--)
        {
            var segment = upward[i];
            var sourceY = Math.Min(safeTop, Math.Max(0, segment.Frame.Height - segment.Details.NewPixelHeight));
            var height = Math.Min(segment.Details.NewPixelHeight, segment.Frame.Height - sourceY);
            if (height > 0)
            {
                strips.Add(new ImageStrip(segment.Frame, sourceY, height));
            }
        }

        var baseTop = upward.Count == 0 ? 0 : safeTop;
        var baseBottom = downward.Count == 0 ? 0 : safeBottom;
        var baseHeight = Math.Max(0, first.Height - baseTop - baseBottom);
        if (baseHeight > 0)
        {
            strips.Add(new ImageStrip(first, baseTop, baseHeight));
        }

        foreach (var segment in downward)
        {
            var height = Math.Min(segment.Details.NewPixelHeight, segment.Frame.Height - safeBottom);
            var sourceY = Math.Max(0, segment.Frame.Height - safeBottom - height);
            if (height > 0)
            {
                strips.Add(new ImageStrip(segment.Frame, sourceY, height));
            }
        }

        if (downward.Count > 0 && safeBottom > 0)
        {
            var latest = downward[^1];
            strips.Add(new ImageStrip(latest.Frame, latest.Frame.Height - safeBottom, safeBottom));
        }

        return strips;
    }

    /// <summary>
    /// 下中位数：一次异常大的检测永远无法主导两帧的捕获，
    /// 而跨帧都看到的稳定区域能够保留下来。对应 Mac :435-441。
    /// </summary>
    private static int ConservativeStableHeight(IEnumerable<int> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted.Length == 0 ? 0 : sorted[(sorted.Length - 1) / 2];
    }

    /// <summary>
    /// 计算分段方案：把超长输出切成有序的、便于保存/复制到剪贴板的分片。
    /// 纯逻辑，不含像素操作。对应 Mac :371-384。
    /// </summary>
    public IReadOnlyList<(int StartY, int Height)> PlanParts(int maximumPixelHeight = DefaultMaximumPartPixelHeight)
    {
        if (_firstFrame is null)
        {
            throw new StitchException(StitchError.NoFrames);
        }

        var pixelSafeHeight = Math.Max(1, _maximumOutputPixels / Math.Max(1, _firstFrame.Width));
        var partHeight = Math.Max(1, Math.Min(maximumPixelHeight, pixelSafeHeight));

        var parts = new List<(int, int)>();
        var startY = 0;
        while (startY < _accumulatedHeight)
        {
            var height = Math.Min(partHeight, _accumulatedHeight - startY);
            parts.Add((startY, height));
            startY += height;
        }

        return parts;
    }

    /// <summary>
    /// 是否超出像素上限。对应 Mac makeImage 的前置检查。
    /// </summary>
    public bool ExceedsOutputLimit()
    {
        if (_firstFrame is null)
        {
            throw new StitchException(StitchError.NoFrames);
        }

        return (long)_firstFrame.Width * _accumulatedHeight > _maximumOutputPixels;
    }

    private static void RequireValidFrame(IStitchFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            throw new StitchException(StitchError.InvalidImage);
        }
    }
}
