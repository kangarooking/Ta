namespace Ta.Core.LongCapture;

/// <summary>
/// 灰度帧：长截图算法的基本输入。
///
/// 对应 Mac 版 VerticalScrollMatcher.swift:3-19 的 GrayscaleFrame。
///
/// ⚠️ 采样高度**独立于采样宽度**，这是刻意的（Mac 注释见 :495-497）：
/// 若从宽度推导高度，2× 宽的 Retina 选区会因采样过矮而察觉不到小幅滚动。
/// 不要把它"修正"成单一宽高比。
/// </summary>
public sealed class GrayscaleFrame
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public GrayscaleFrame(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if (pixels.Length != width * height)
        {
            throw new ArgumentException("像素数必须与尺寸匹配。", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public byte this[int x, int y] => Pixels[(y * Width) + x];
}

/// <summary>滚动方向。</summary>
public enum ScrollDirection
{
    Stationary,
    Down,
    Up,
}

/// <summary>
/// 位移约束。限制允许的匹配方向。
/// </summary>
public enum ScrollConstraint
{
    Any,
    DownwardOnly,
    UpwardOnly,
}

/// <summary>稳定边缘区域（固定标题栏 / 底栏 / 输入框）。</summary>
public readonly record struct StableEdgeRegions(int TopRows, int BottomRows);

/// <summary>
/// 一次匹配结果。
/// signedShift 为正表示视口下移，为负表示上移。
/// </summary>
public readonly record struct ScrollMatch(int SignedShift, double MeanAbsoluteDifference, double Confidence)
{
    public int Shift => Math.Abs(SignedShift);

    public ScrollDirection Direction => SignedShift switch
    {
        > 0 => ScrollDirection.Down,
        < 0 => ScrollDirection.Up,
        _ => ScrollDirection.Stationary,
    };

    /// <summary>位移不超过 1 且平均差不超过 2.5 视为重复帧。</summary>
    public bool IsDuplicate => Shift <= 1 && MeanAbsoluteDifference <= 2.5;
}

/// <summary>
/// 视口运动检测。
///
/// 对应 Mac 版 ViewportMotionDetector.swift。
///
/// 与接缝匹配**刻意分离**：一时匹配不上的帧，不等于已经到底。
/// 这个区分是长截图能否正确结束的关键。
/// </summary>
public sealed class ViewportMotionDetector
{
    public const int MinimumSampleWidth = 32;
    public const int MinimumSampleHeight = 120;

    // 暴露出来便于诊断与测试 —— 采样尺寸直接决定能否察觉小幅滚动。
    public int SampleWidth => _sampleWidth;
    public int SampleHeight => _sampleHeight;

    private readonly int _sampleWidth;
    private readonly int _sampleHeight;
    private readonly double _ignoredTopFraction;
    private readonly double _ignoredBottomFraction;
    private readonly double _ignoredSideFraction;
    private readonly double _stationaryMeanDifference;
    private readonly double _stationaryChangedFraction;
    private readonly int _changedPixelDifference;

    private GrayscaleFrame? _reference;

    public ViewportMotionDetector(
        int sampleWidth = 72,
        int sampleHeight = 360,
        double ignoredTopFraction = 0.12,
        double ignoredBottomFraction = 0.12,
        double ignoredSideFraction = 0.06,
        double stationaryMeanDifference = 2.8,
        double stationaryChangedFraction = 0.025,
        int changedPixelDifference = 12)
    {
        _sampleWidth = Math.Max(MinimumSampleWidth, sampleWidth);
        _sampleHeight = Math.Max(MinimumSampleHeight, sampleHeight);
        _ignoredTopFraction = Math.Clamp(ignoredTopFraction, 0, 0.3);
        _ignoredBottomFraction = Math.Clamp(ignoredBottomFraction, 0, 0.3);
        _ignoredSideFraction = Math.Clamp(ignoredSideFraction, 0, 0.2);
        _stationaryMeanDifference = Math.Max(0, stationaryMeanDifference);
        _stationaryChangedFraction = Math.Clamp(stationaryChangedFraction, 0, 1);
        _changedPixelDifference = Math.Max(1, changedPixelDifference);
    }

    public void Reset() => _reference = null;

    /// <summary>记录基准帧。</summary>
    public void Commit(GrayscaleFrame frame) => _reference = frame;

    /// <summary>与基准帧比较，判断视口是否静止。</summary>
    public ViewportMotionMeasurement Compare(GrayscaleFrame current)
    {
        if (_reference is not { } reference
            || reference.Width != current.Width
            || reference.Height != current.Height)
        {
            return new ViewportMotionMeasurement(false, 0, 0, false);
        }

        var top = Math.Min(reference.Height - 1, (int)(reference.Height * _ignoredTopFraction));
        var bottom = Math.Max(top + 1, reference.Height - (int)(reference.Height * _ignoredBottomFraction));
        var left = Math.Min(reference.Width - 1, (int)(reference.Width * _ignoredSideFraction));
        var right = Math.Max(left + 1, reference.Width - (int)(reference.Width * _ignoredSideFraction));

        long totalDifference = 0;
        long changedPixels = 0;
        long count = 0;

        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var difference = Math.Abs(reference[x, y] - current[x, y]);
                totalDifference += difference;
                if (difference >= _changedPixelDifference)
                {
                    changedPixels++;
                }

                count++;
            }
        }

        if (count == 0)
        {
            return new ViewportMotionMeasurement(true, 0, 0, true);
        }

        var meanDifference = (double)totalDifference / count;
        var changedFraction = (double)changedPixels / count;

        // ⚠️ 稀疏聊天/文档页面滚动时往往只有窄窄一条像素带变化。
        // 必须**两个**信号都安静才判定静止；若用 OR，这些页面的文字明明动了
        // 却会被误判为已到底。这是 Mac 版特意修正过的 bug（注释见 :107-110）。
        var stationary = meanDifference <= _stationaryMeanDifference
            && changedFraction <= _stationaryChangedFraction;

        return new ViewportMotionMeasurement(true, meanDifference, changedFraction, stationary);
    }
}

public readonly record struct ViewportMotionMeasurement(
    bool HasReference,
    double MeanAbsoluteDifference,
    double ChangedPixelFraction,
    bool IsStationary);
