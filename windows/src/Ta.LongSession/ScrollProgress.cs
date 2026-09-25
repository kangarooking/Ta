namespace Ta.LongSession;

/// <summary>
/// 滚动进度。对应 Mac 版 AccessibilityScrollProgress（AccessibilityAutoScrollService.swift:5-18）。
///
/// 纯数据，不依赖 UIA，便于状态机单测。
/// </summary>
/// <param name="Value">滚动条当前值。</param>
/// <param name="Minimum">滚动条最小值，默认 0。</param>
/// <param name="Maximum">滚动条最大值，默认 1。</param>
public sealed record ScrollProgress(double Value, double Minimum = 0, double Maximum = 1)
{
    /// <summary>归一化到 0..1。</summary>
    public double NormalizedValue
    {
        get
        {
            if (Maximum <= Minimum)
            {
                return 0;
            }

            return Math.Clamp((Value - Minimum) / (Maximum - Minimum), 0, 1);
        }
    }

    /// <summary>归一化值 ≥ 0.9995 视为到底。对应 Mac: :17。</summary>
    public bool IsAtEnd => NormalizedValue >= IsAtEndThreshold;

    /// <summary>到底判定阈值。对应 Mac 常量 0.9995。</summary>
    public const double IsAtEndThreshold = 0.9995;

    /// <summary>进度前进判定容差。对应 Mac 常量 0.00005。</summary>
    public const double ProgressAdvancedTolerance = 0.00005;
}

/// <summary>滚动目标模式。对应 Mac: AccessibilityScrollTarget.Mode。</summary>
public enum ScrollTargetMode
{
    /// <summary>有可读进度（UIA ScrollPattern / RangeValuePattern）的无障碍跟踪目标。</summary>
    AccessibilityTracked,

    /// <summary>纯事件回退模式：无法读取进度，只能靠像素稳定性判断到底。</summary>
    EventFallback,
}

/// <summary>
/// 自动滚动尝试的结算结果。对应 Mac: AutoScrollAttemptResolution。
/// </summary>
public enum AutoScrollAttemptResolution
{
    Waiting,
    Progress,
    NoMovement,
}
