namespace Ta.Core.LongCapture;

/// <summary>
/// 垂直滚动匹配器。
///
/// 逐行对应 Mac 版 VerticalScrollMatcher.swift（392 行）。这是长截图里
/// 算法最密集的部分，所有阈值都有测试锁定，改动任何一个都会改变拼接结果。
///
/// 核心思路：把内容宽度切成 5 个独立竖直带，**只在边缘像素上**累加差异
/// （类 Sobel 梯度 >= 10）。这样文字/代码边缘主导打分，而空白与固定侧栏
/// 不会主导全局。再取带分的**下中位数**，使少数动态带无法否决多数已对齐带。
/// </summary>
public sealed class VerticalScrollMatcher
{
    // ── 默认阈值（对应 Mac :86-92）─────────────────────────────────
    // 用只读属性而非 init 属性：构造时需要夹取越界值，而 init 属性无法在构造函数里重新赋值。
    public double MaximumShiftFraction { get; }
    public double IgnoredTopFraction { get; }
    public double IgnoredBottomFraction { get; }
    public double IgnoredSideFraction { get; }
    public double MaximumMeanAbsoluteDifference { get; }
    public double MinimumConfidence { get; }
    public int SampleStride { get; }

    /// <summary>参与投票的竖直带数量。</summary>
    public const int BandCount = 5;

    /// <summary>边缘判定的梯度下限。</summary>
    public const int EdgeGradientThreshold = 10;

    /// <summary>可用纵向范围的最小高度。</summary>
    public const int MinimumUsableHeight = 12;

    /// <summary>帧的最小尺寸。</summary>
    public const int MinimumFrameWidth = 8;
    public const int MinimumFrameHeight = 24;

    /// <summary>重叠校验的上限倍数。对应 Mac: max(36, max * 2.5)。</summary>
    public const double OverlapCeilingFloor = 36;
    public const double OverlapCeilingMultiplier = 2.5;

    /// <summary>反向方向的容差。对应 Mac: oppositeBest + 0.5 &lt; bestDifference。</summary>
    public const double OppositeDirectionTolerance = 0.5;

    /// <summary>远歧义的位移距离下限。对应 Mac: max(8, height / 18)。</summary>
    public const double FarDistanceDivisor = 18;
    public const int FarDistanceFloor = 8;

    /// <summary>远歧义的差异容差。</summary>
    public const double FarAmbiguityTolerance = 0.15;

    /// <summary>向下安全偏移：最多多保留 2 行，避免吞掉接缝处的文字。</summary>
    public const int DownwardSafetyBiasRows = 2;
    public const double DownwardSafetyBiasTolerance = 0.35;

    /// <summary>分离度放大系数。</summary>
    public const double SeparationMultiplier = 6;

    /// <summary>置信度权重。</summary>
    public const double QualityWeight = 0.8;
    public const double SeparationWeight = 0.2;

    /// <summary>稳定边缘检测的默认参数。</summary>
    public const double StableEdgeMaximumFraction = 0.25;
    public const double StableEdgeMaximumRowDifference = 3.0;
    public const int StableEdgeMinimumRows = 3;

    public VerticalScrollMatcher(
        double maximumShiftFraction = 0.82,
        double ignoredTopFraction = 0.10,
        double ignoredBottomFraction = 0.22,
        double ignoredSideFraction = 0.28,
        double maximumMeanAbsoluteDifference = 18,
        double minimumConfidence = 0.56,
        int sampleStride = 2)
    {
        MaximumShiftFraction = maximumShiftFraction;
        IgnoredTopFraction = ignoredTopFraction;
        IgnoredBottomFraction = ignoredBottomFraction;
        IgnoredSideFraction = ignoredSideFraction;
        MaximumMeanAbsoluteDifference = maximumMeanAbsoluteDifference;
        // 对应 Mac: minimumConfidence = min(1, max(0, minimumConfidence))
        MinimumConfidence = Math.Clamp(minimumConfidence, 0, 1);
        // 对应 Mac: sampleStride = max(1, sampleStride)
        SampleStride = Math.Max(1, sampleStride);
    }

    /// <summary>
    /// 匹配两帧之间的垂直位移。无可行匹配时返回 null。
    /// </summary>
    public ScrollMatch? Match(
        GrayscaleFrame previous,
        GrayscaleFrame current,
        ScrollConstraint constraint = ScrollConstraint.Any,
        int? preferredSignedShift = null,
        bool allowsAmbiguousMatch = false,
        double? minimumAcceptedConfidence = null)
    {
        if (previous.Width != current.Width
            || previous.Height != current.Height
            || previous.Width < MinimumFrameWidth
            || previous.Height < MinimumFrameHeight)
        {
            return null;
        }

        var height = previous.Height;

        // 固定边缘会加宽边距。对应 Mac :122-124（此处用 0.28 而非默认的 0.25）。
        var fixedEdges = StableEdges(previous, current, 0.28, 3.0);

        var topMargin = Math.Max(
            Math.Max(1, (int)(height * IgnoredTopFraction)),
            fixedEdges.TopRows);
        var bottomMargin = Math.Max(
            Math.Max(1, (int)(height * IgnoredBottomFraction)),
            fixedEdges.BottomRows);
        var sideMargin = Math.Min(
            Math.Max(1, (int)(previous.Width * IgnoredSideFraction)),
            Math.Max(1, previous.Width / 3));

        var startX = sideMargin;
        var endX = previous.Width - sideMargin;
        if (endX - startX < MinimumFrameWidth)
        {
            return null;
        }

        var maximumShift = Math.Min(
            height - topMargin - bottomMargin - MinimumUsableHeight,
            (int)(height * MaximumShiftFraction));
        if (maximumShift < 0)
        {
            return null;
        }

        var shiftRange = constraint switch
        {
            ScrollConstraint.Any => (-maximumShift, maximumShift),
            ScrollConstraint.DownwardOnly => (0, maximumShift),
            ScrollConstraint.UpwardOnly => (-maximumShift, 0),
            _ => (-maximumShift, maximumShift),
        };

        var candidateShifts = new List<int>(shiftRange.Item2 - shiftRange.Item1 + 1);
        for (var s = shiftRange.Item1; s <= shiftRange.Item2; s++)
        {
            candidateShifts.Add(s);
        }

        if (preferredSignedShift is { } preferred)
        {
            // 稳定排序：先按与期望值的距离，距离相同时按绝对位移。
            candidateShifts = candidateShifts
                .OrderBy(s => Math.Abs(s - preferred))
                .ThenBy(Math.Abs)
                .ToList();
        }

        var bestShift = 0;
        var bestDifference = double.MaxValue;
        var secondBestDifference = double.MaxValue;
        var scoredCandidates = new List<(int Shift, double Difference)>();

        foreach (var signedShift in candidateShifts)
        {
            if (Difference(previous, current, signedShift, topMargin, bottomMargin, startX, endX)
                is not { } candidateDifference)            {
                continue;
            }

            scoredCandidates.Add((signedShift, candidateDifference));

            if (candidateDifference < bestDifference)
            {
                secondBestDifference = bestDifference;
                bestDifference = candidateDifference;
                bestShift = signedShift;
            }
            else if (candidateDifference < secondBestDifference)
            {
                secondBestDifference = candidateDifference;
            }
        }

        if (bestDifference > MaximumMeanAbsoluteDifference)
        {
            return null;
        }

        // 多带投票定位位移后，再做一次更宽的重叠校验，
        // 排除"只有几条窄带恰好对齐"的巧合。宽松上限仍能容忍动画面板。
        if (UnweightedDifference(previous, current, bestShift, topMargin, bottomMargin, startX, endX)
            is not { } overlapVerification
            || overlapVerification > Math.Max(OverlapCeilingFloor, MaximumMeanAbsoluteDifference * OverlapCeilingMultiplier))
        {
            return null;
        }

        if (constraint != ScrollConstraint.Any)
        {
            // 方向约束在重复聊天卡片上不够：向上的帧仍可能有看似合理的正偏移。
            // 因此交叉检查被禁止的方向，若它明显更能解释当前帧则拒绝。
            var (oppositeFrom, oppositeTo) = constraint switch
            {
                ScrollConstraint.DownwardOnly => (-maximumShift, -2),
                ScrollConstraint.UpwardOnly => (2, maximumShift),
                _ => (0, 0),
            };

            var oppositeBest = double.MaxValue;
            for (var s = oppositeFrom; s <= oppositeTo; s++)
            {
                if (Difference(previous, current, s, topMargin, bottomMargin, startX, endX) is { } d && d < oppositeBest)
                {
                    oppositeBest = d;
                }
            }

            if (oppositeBest < double.MaxValue && oppositeBest + OppositeDirectionTolerance < bestDifference)
            {
                return null;
            }

            // 重复聊天卡片可能产生两个同样可信的偏移；接受任意一个就是
            // 自动向下截图跳到更早对话的原因。除非某一处明显更优，否则拒绝。
            var farDistance = Math.Max(FarDistanceFloor, height / FarDistanceDivisor);
            var hasFarAmbiguity = scoredCandidates.Any(c =>
                Math.Abs(c.Shift - bestShift) >= farDistance
                && c.Difference <= bestDifference + FarAmbiguityTolerance);

            if (!allowsAmbiguousMatch && hasFarAmbiguity)
            {
                return null;
            }

            // 轻微低估会永久丢失像素，轻微高估只留下一段可复查的重复。
            // 因此在测量不确定范围内，向下最多偏 2 个采样行，确保文字不被接缝吞掉。
            if (constraint == ScrollConstraint.DownwardOnly)
            {
                // 用可空元组而非 default 比较 —— (0, 0.0) 是合法的候选值，
                // 拿 default 判存在与否会把真实的零位移候选误判为"未找到"。
                (int Shift, double Difference)? safer = null;
                foreach (var candidate in scoredCandidates)
                {
                    if (candidate.Shift < bestShift || candidate.Shift > bestShift + DownwardSafetyBiasRows)
                    {
                        continue;
                    }

                    if (candidate.Difference > bestDifference + DownwardSafetyBiasTolerance)
                    {
                        continue;
                    }

                    if (safer is null || candidate.Shift > safer.Value.Shift)
                    {
                        safer = candidate;
                    }
                }

                if (safer is { } chosen)
                {
                    bestShift = chosen.Shift;
                    bestDifference = chosen.Difference;
                }
            }
        }

        var quality = Math.Max(0, 1 - (bestDifference / MaximumMeanAbsoluteDifference));

        double separation;
        if (double.IsFinite(secondBestDifference) && secondBestDifference > 0)
        {
            separation = Math.Clamp(
                ((secondBestDifference - bestDifference) / secondBestDifference) * SeparationMultiplier,
                0, 1);
        }
        else
        {
            separation = 1;
        }

        var confidence = Math.Min(1, (quality * QualityWeight) + (separation * SeparationWeight));
        if (confidence < (minimumAcceptedConfidence ?? MinimumConfidence))
        {
            return null;
        }

        return new ScrollMatch(bestShift, bestDifference, confidence);
    }

    /// <summary>
    /// 边缘加权打分。对应 Mac :191-252 的 difference(for:)。
    /// </summary>
    private double? Difference(
        GrayscaleFrame previous,
        GrayscaleFrame current,
        int signedShift,
        int topMargin,
        int bottomMargin,
        int startX,
        int endX)
    {
        var shift = Math.Abs(signedShift);
        var height = previous.Height;
        var startY = topMargin;
        var endY = height - shift - bottomMargin;
        if (endY - startY < MinimumUsableHeight)
        {
            return null;
        }

        var contentWidth = endX - startX;
        var bandWidth = Math.Max(2, contentWidth / BandCount);
        var bandScores = new List<double>();

        for (var band = 0; band < BandCount; band++)
        {
            var bandStart = startX + (band * bandWidth);
            var bandEnd = band == BandCount - 1
                ? endX
                : Math.Min(endX, bandStart + bandWidth);

            if (bandEnd - bandStart < 2)
            {
                continue;
            }

            long edgeDifference = 0;
            long edgeSamples = 0;

            // 从 1 开始，因为要访问 [x-1, y] 与 [x, y-1]。
            for (var y = Math.Max(startY, 1); y < endY; y += SampleStride)
            {
                for (var x = Math.Max(bandStart, 1); x < bandEnd; x += SampleStride)
                {
                    var previousY = signedShift >= 0 ? y + shift : y;
                    var currentY = signedShift >= 0 ? y : y + shift;

                    var previousValue = previous[x, previousY];
                    var currentValue = current[x, currentY];

                    var previousGradient = Math.Abs(previousValue - previous[x, previousY - 1])
                        + Math.Abs(previousValue - previous[x - 1, previousY]);
                    var currentGradient = Math.Abs(currentValue - current[x, currentY - 1])
                        + Math.Abs(currentValue - current[x - 1, currentY]);

                    if (Math.Max(previousGradient, currentGradient) >= EdgeGradientThreshold)
                    {
                        edgeDifference += Math.Abs(previousValue - currentValue);
                        edgeSamples++;
                    }
                }
            }

            var minimumEvidence = Math.Max(8, (endY - startY) / 14);
            if (edgeSamples >= minimumEvidence)
            {
                bandScores.Add((double)edgeDifference / edgeSamples);
            }
        }

        if (bandScores.Count >= 2)
        {
            bandScores.Sort();
            if (bandScores.Count >= 4)
            {
                // 去掉最差的带（通常是侧栏、动画或光标），保守聚合其余。
                bandScores.RemoveAt(bandScores.Count - 1);
            }

            // 取**下**中位数，使少数两条动态/固定带无法否决三条已独立对齐的内容带。
            return bandScores[(bandScores.Count - 1) / 2];
        }

        return UnweightedDifference(previous, current, signedShift, topMargin, bottomMargin, startX, endX);
    }

    /// <summary>全宽平均绝对差。对应 Mac :162-189 的 unweightedDifference。</summary>
    private double? UnweightedDifference(
        GrayscaleFrame previous,
        GrayscaleFrame current,
        int signedShift,
        int topMargin,
        int bottomMargin,
        int fromX,
        int toX)
    {
        var shift = Math.Abs(signedShift);
        var startY = topMargin;
        var endY = previous.Height - shift - bottomMargin;
        if (endY - startY < MinimumUsableHeight)
        {
            return null;
        }

        long totalDifference = 0;
        long sampleCount = 0;

        for (var y = startY; y < endY; y += SampleStride)
        {
            for (var x = fromX; x < toX; x += SampleStride)
            {
                var previousY = signedShift >= 0 ? y + shift : y;
                var currentY = signedShift >= 0 ? y : y + shift;

                totalDifference += Math.Abs(previous[x, previousY] - current[x, currentY]);
                sampleCount++;
            }
        }

        return sampleCount == 0 ? null : (double)totalDifference / sampleCount;
    }

    /// <summary>
    /// 检测两帧之间保持不动的行（固定标题栏、输入框等）。
    /// 结果刻意保守 —— 调用方仍应把低置信度接缝交给人工复查。
    /// </summary>
    public StableEdgeRegions StableEdges(
        GrayscaleFrame previous,
        GrayscaleFrame current,
        double maximumFraction = StableEdgeMaximumFraction,
        double maximumRowDifference = StableEdgeMaximumRowDifference)
    {
        if (previous.Width != current.Width || previous.Height != current.Height)
        {
            return new StableEdgeRegions(0, 0);
        }

        var limit = Math.Max(0, Math.Min(previous.Height / 3, (int)(previous.Height * maximumFraction)));
        if (limit <= 0)
        {
            return new StableEdgeRegions(0, 0);
        }

        double RowDifference(int row)
        {
            var sideMargin = Math.Min(
                Math.Max(0, (int)(previous.Width * IgnoredSideFraction)),
                Math.Max(0, previous.Width / 3));

            long total = 0;
            long count = 0;
            for (var x = sideMargin; x < Math.Max(sideMargin + 1, previous.Width - sideMargin); x += SampleStride)
            {
                total += Math.Abs(previous[x, row] - current[x, row]);
                count++;
            }

            return count == 0 ? double.MaxValue : (double)total / count;
        }

        var top = 0;
        while (top < limit && RowDifference(top) <= maximumRowDifference)
        {
            top++;
        }

        var bottom = 0;
        while (bottom < limit && RowDifference(previous.Height - 1 - bottom) <= maximumRowDifference)
        {
            bottom++;
        }

        // 一两行偶然相等不构成稳定的应用区域。
        return new StableEdgeRegions(
            top >= StableEdgeMinimumRows ? top : 0,
            bottom >= StableEdgeMinimumRows ? bottom : 0);
    }
}
