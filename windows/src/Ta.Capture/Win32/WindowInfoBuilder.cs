using Ta.Core.Capture;

namespace Ta.Capture.Win32;

/// <summary>
/// 把原始窗口观测换算为 <see cref="WindowInfo"/> 列表。纯函数，可单测。
///
/// 对应 Mac 版 TaAgentCaptureService.swift:289-302 的 windows 映射（产出
/// TaAgentWindowTarget），但**过滤条件采用 WindowSnapService.targets 的那一套**
/// （参考文档 §5.3 与本次移植要求）。两者在 Mac 版是两条独立路径：
/// <list type="bullet">
///   <item><description>Agent 桥要全量列表（只要 owningApplication 存在且尺寸 >= 1）</description></item>
///   <item><description>覆盖层吸附要过滤后的列表（前台进程 + 可见 + 尺寸 + alpha）</description></item>
/// </list>
/// 本实现统一到过滤后的那套，因为 <see cref="Ta.Core.Capture.IScreenCapture.Windows"/>
/// 在 Windows 侧同时服务两者，而过滤后的语义对两者都安全（少报不会让吸附命中幽灵窗口）。
/// </summary>
public static class WindowInfoBuilder
{
    /// <summary>
    /// 生成窗口信息列表。
    ///
    /// <paramref name="displays"/> 用于判定「这个窗口落在哪块屏上」：
    /// 取相交面积最大的屏。取不到任何相交屏的窗口不在用户视野内，直接剔除。
    /// <para>
    /// <b>Frame 不裁剪</b> —— 与 Mac 的 TaAgentWindowTarget.frame（未经 screen 求交）一致。
    /// 裁剪版只用于吸附目标 <see cref="WindowSnapTarget.Frame"/>。
    /// </para>
    /// </summary>
    public static IReadOnlyList<WindowInfo> Build(
        IReadOnlyList<DisplayInfo> displays,
        IReadOnlyList<RawWindowObservation> windows,
        int? frontmostProcessId)
    {
        ArgumentNullException.ThrowIfNull(displays);
        ArgumentNullException.ThrowIfNull(windows);

        var result = new List<WindowInfo>();

        foreach (var window in windows)
        {
            if (!WindowSnapTargetFilter.PassesFilters(window, frontmostProcessId))
            {
                continue;
            }

            // 窗口至少要有一部分落在某块屏上，否则它不在用户视野内。
            var host = HostDisplay(displays, window.Frame);
            if (host is null)
            {
                continue;
            }

            result.Add(new WindowInfo
            {
                WindowHandle = window.WindowHandle,
                ProcessId = window.ProcessId,
                AppName = AppName(window),
                BundleOrIdentity = window.ProcessImagePath,
                Title = window.Title,
                Frame = window.Frame,
                ZOrder = window.EnumerationOrder,
            });
        }

        return result;
    }

    /// <summary>与窗口相交面积最大的显示器。无交集返回 null。</summary>
    public static DisplayInfo? HostDisplay(IReadOnlyList<DisplayInfo> displays, RectD windowFrame)
    {
        DisplayInfo? best = null;
        var bestArea = 0d;

        foreach (var display in displays)
        {
            var area = ScreenGeometry.Area(ScreenGeometry.Intersect(display.Frame, windowFrame));
            if (area > bestArea)
            {
                best = display;
                bestArea = area;
            }
        }

        return best;
    }

    /// <summary>
    /// 应用名。对应 Mac 的 <c>owningApplication.applicationName</c>。
    /// Windows 上最接近的是**映像文件名去扩展名**（"chrome" 而不是 "C:\...\chrome.exe"），
    /// 与 Mac 的 "Google Chrome" 语义（进程的展示名）一致。
    /// 取不到映像路径时退回 PID 兜底 —— AppName 是非空契约，不能给 null。
    /// </summary>
    private static string AppName(RawWindowObservation window)
    {
        if (!string.IsNullOrWhiteSpace(window.ProcessImagePath))
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(window.ProcessImagePath);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }
            catch (ArgumentException)
            {
                // 路径含非法字符时落到 PID 兜底。
            }
        }

        return $"pid-{window.ProcessId}";
    }
}
