using System.Windows.Automation;
using Ta.Core.Capture;

namespace Ta.LongSession;

/// <summary>滚动驱动策略。实测结论决定 <see cref="ScrollStrategy.Auto"/> 的默认顺序。</summary>
public enum ScrollStrategy
{
    /// <summary>自动：按实测可靠性依次尝试 UIA ScrollPattern → 合成滚轮 → 消息投递 → 标准滚动条。</summary>
    Auto,

    /// <summary>UIA ScrollPattern（Chromium/Electron 上最可靠）。</summary>
    UiaScrollPattern,

    /// <summary>SendInput / mouse_event 合成滚轮（真实硬件级事件，会短暂移动光标）。</summary>
    SyntheticWheelInput,

    /// <summary>PostMessage(WM_MOUSEWHEEL) 到命中点所在的最内层子窗口（标准 Win32 控件有效）。</summary>
    PostMessageWheel,

    /// <summary>SetScrollPos + WM_VSCROLL（标准滚动条窗口的最后手段）。</summary>
    Win32ScrollBar,
}

/// <summary>
/// 滚动目标。对应 Mac 版 AccessibilityScrollTarget（AccessibilityAutoScrollService.swift:20-37）。
///
/// Windows 侧用 UI Automation 元素替代 AXUIElement：
/// · <see cref="ScrollContainer"/> 对应 kAXScrollAreaRole 所在元素
/// · <see cref="VerticalScrollBar"/> 对应 kAXVerticalScrollBarAttribute
/// · 进度读取优先 RangeValuePattern（对应 Mac 读滚动条 value），
///   回退 ScrollPattern.VerticalScrollPercent（Chromium 上更可靠）。
/// </summary>
public sealed class ScrollTarget
{
    public ScrollTarget(
        int? sourceProcessId,
        System.Drawing.Point eventLocation,
        AutomationElement? scrollContainer = null,
        AutomationElement? verticalScrollBar = null)
    {
        SourceProcessId = sourceProcessId;
        EventLocation = eventLocation;
        ScrollContainer = scrollContainer;
        VerticalScrollBar = verticalScrollBar;
    }

    public int? SourceProcessId { get; }

    /// <summary>滚轮事件应投递到的屏幕点（选区垂直中心 × 水平中心）。</summary>
    public System.Drawing.Point EventLocation { get; }

    /// <summary>命中的可滚动容器（对应 Mac 的 scrollContainer）。</summary>
    public AutomationElement? ScrollContainer { get; }

    /// <summary>找到的垂直滚动条元素（可能为 null → 回退模式）。</summary>
    public AutomationElement? VerticalScrollBar { get; }

    /// <summary>有可读进度才宣称 tracked —— 与 Mac 的 mode 注释一致（:32-36）。</summary>
    public ScrollTargetMode Mode =>
        ReadProgress(this) is null ? ScrollTargetMode.EventFallback : ScrollTargetMode.AccessibilityTracked;

    /// <summary>快捷读取：当前目标是否支持 UIA ScrollPattern。</summary>
    public bool SupportsScrollPattern => ScrollContainer is { } c && SupportsPattern(c, ScrollPattern.Pattern);

    private static bool SupportsPattern(AutomationElement element, AutomationPattern pattern)
    {
        try
        {
            return element.GetCurrentPattern(pattern) is not null;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal static bool ElementSupportsScrollPattern(AutomationElement? element) =>
        element is not null && SupportsPattern(element, ScrollPattern.Pattern);

    /// <summary>读取滚动进度。RangeValuePattern 优先，ScrollPattern 回退。</summary>
    internal static ScrollProgress? ReadProgress(ScrollTarget target)
    {
        // 1) 滚动条 RangeValuePattern —— 对应 Mac 读 kAXValueAttribute（:135-144）
        if (target.VerticalScrollBar is { } bar)
        {
            try
            {
                if (bar.GetCurrentPattern(RangeValuePattern.Pattern) is RangeValuePattern range)
                {
                    var current = range.Current;
                    if (current.Maximum > current.Minimum)
                    {
                        return new ScrollProgress(current.Value, current.Minimum, current.Maximum);
                    }
                }
            }
            catch (ElementNotAvailableException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        // 2) 容器 ScrollPattern.VerticalScrollPercent（0..100，-1 表示未知）
        //    Chromium 上这是最可靠的进度来源。
        if (target.ScrollContainer is { } container)
        {
            try
            {
                if (container.GetCurrentPattern(ScrollPattern.Pattern) is ScrollPattern scroll)
                {
                    var percent = scroll.Current.VerticalScrollPercent;
                    if (percent >= 0)
                    {
                        return new ScrollProgress(percent, 0, 100);
                    }
                }
            }
            catch (ElementNotAvailableException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        return null;
    }
}

/// <summary>
/// 自动滚动驱动服务。对应 Mac 版 AccessibilityAutoScrollService.swift。
///
/// ⚠️ 与 Mac 版最大的差异（参考文档 §14 风险 #2）：
/// macOS 用 CGEvent 投递**逐像素**滚轮手势；Windows 滚轮是**档位式**
/// （WHEEL_DELTA=120/档，走系统光标），无逐像素等价物。Mac 的
/// 32px 分块 + 14ms 间隔方案在 Windows 上只能近似保留
/// 「大手势拆成多个柔和事件」这一动画特征，每块的滚轮单位按
/// <see cref="WheelUnitsPerMacPixel"/> 换算（见该常量注释）。
///
/// 进度**读取**仍走无障碍：UIA ScrollPattern / RangeValuePattern，
/// 与 Mac 的「AX 只读不写」设计一致（Mac 注释 :160-166）。
/// </summary>
public sealed class AccessibilityAutoScrollService
{
    /// <summary>每个滚轮事件的最大 Mac 像素增量。对应 Mac: :40。</summary>
    public const int MaximumPixelDeltaPerEvent = 32;

    /// <summary>滚轮事件间隔（毫秒）。对应 Mac: :41。</summary>
    public const int ScrollEventIntervalMilliseconds = 14;

    /// <summary>沿父级查找滚动条的最大层数。对应 Mac: :93。</summary>
    public const int MaximumParentLevels = 32;

    /// <summary>在滚动区域内搜索滚动条的最大深度。对应 Mac: :111。</summary>
    public const int ScrollBarSearchDepth = 2;

    /// <summary>
    /// Mac 像素 → Windows 滚轮单位的换算系数。
    ///
    /// Mac 逐像素手势 1pt ≈ Windows 需要 ~2.2 个滚轮单位才滚动相近的内容量
    /// （Chromium 默认一档 120 单位 ≈ 54px 内容）。保留比例而不是每块固定
    /// 一档，是为了让 <c>recommendedScrollDistance</c> 的语义大体不变：
    /// 130pt 手势 ≈ 290 单位 ≈ 2.4 档，而不是 5 档的 4 倍过冲。
    /// </summary>
    public const double WheelUnitsPerMacPixel = 2.2;

    /// <summary>进度读取失败/无权限时的行为与 Mac 一致：始终可用（Windows 无 TCC 权限门）。</summary>
    public bool IsGranted => true;

    /// <summary>Windows 没有 macOS 式的辅助功能授权弹窗，请求即视为已授权。</summary>
    public bool Request() => true;

    /// <summary>
    /// 解析滚动目标。对应 Mac: resolveTarget(for:)（:51-133）。
    ///
    /// 流程：命中点 → 系统级命中测试（UIA FromPoint，最接近
    /// AXUIElementCopyElementAtPosition）→ PID 必须匹配源应用 →
    /// 沿父级最多 32 层找垂直滚动条，或滚动区域 → 深度 2 搜索 →
    /// 接受第一个有可读进度的候选；否则回退 eventFallback。
    /// </summary>
    public ScrollTarget ResolveTarget(CaptureSelection selection, int? sourceProcessId = null)
    {
        var eventLocation = ScrollTargetLocation(selection);

        // 权限门槛（Windows 上无等价物，保留分支以对齐 Mac 的结构）。
        if (!IsGranted)
        {
            return new ScrollTarget(sourceProcessId, eventLocation);
        }

        AutomationElement? hit;
        try
        {
            hit = AutomationElement.FromPoint(
                new System.Windows.Point(eventLocation.X, eventLocation.Y));
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or ArgumentException)
        {
            return new ScrollTarget(sourceProcessId, eventLocation);
        }

        if (hit is null)
        {
            return new ScrollTarget(sourceProcessId, eventLocation);
        }

        // PID 校验：命中元素必须属于源应用（对应 Mac 的 AXUIElementGetPid 比较 :79-89）。
        try
        {
            var hitPid = hit.Current.ProcessId;
            if (sourceProcessId is { } expectedPid && expectedPid != hitPid)
            {
                return new ScrollTarget(sourceProcessId, eventLocation);
            }
        }
        catch (ElementNotAvailableException)
        {
            return new ScrollTarget(sourceProcessId, eventLocation);
        }

        // 沿父级最多 32 层（对应 Mac: :91-125）。
        var current = hit;
        var visited = 0;
        while (current is not null && visited < MaximumParentLevels)
        {
            if (FindVerticalScrollBar(current, ScrollBarSearchDepth) is { } scrollBar)
            {
                var candidate = new ScrollTarget(sourceProcessId, eventLocation, current, scrollBar);
                if (ScrollTarget.ReadProgress(candidate) is not null)
                {
                    return candidate;
                }
            }

            // 滚动区域自身（对应 kAXScrollAreaRole 分支）。
            if (ScrollTarget.ElementSupportsScrollPattern(current))
            {
                if (FindVerticalScrollBar(current, ScrollBarSearchDepth) is { } areaScrollBar)
                {
                    var candidate = new ScrollTarget(sourceProcessId, eventLocation, current, areaScrollBar);
                    if (ScrollTarget.ReadProgress(candidate) is not null)
                    {
                        return candidate;
                    }
                }

                // Chromium 的文档元素常带 ScrollPattern 但没有独立滚动条元素 ——
                // 容器自身就是进度来源，直接接受。
                var containerOnly = new ScrollTarget(sourceProcessId, eventLocation, current, null);
                if (ScrollTarget.ReadProgress(containerOnly) is not null)
                {
                    return containerOnly;
                }
            }

            try
            {
                current = TreeWalker.ControlViewWalker.GetParent(current);
            }
            catch (ElementNotAvailableException)
            {
                break;
            }

            visited++;
        }

        return new ScrollTarget(sourceProcessId, eventLocation, hit, null);
    }

    /// <summary>读取滚动进度。</summary>
    public ScrollProgress? Progress(ScrollTarget target) => ScrollTarget.ReadProgress(target);

    /// <summary>
    /// 发送向下滚动手势。对应 Mac: postDownwardScroll / postVerticalScroll（:146-185）。
    ///
    /// ⚠️ 滚动**不由无障碍驱动**：Mac 注释（:160-166）说明 AX 值常被归一化到
    /// 0..1，加 300px 步长在 Chromium/Electron 会直接跳到最大值。Windows 上
    /// 同理 —— 只有「读取」用 UIA，「驱动」用真实事件。
    /// </summary>
    public Task<bool> PostDownwardScrollAsync(
        CaptureSelection selection,
        ScrollTarget? target = null,
        int pixels = 260,
        ScrollStrategy strategy = ScrollStrategy.Auto,
        CancellationToken cancellationToken = default)
    {
        if (!IsGranted)
        {
            return Task.FromResult(false);
        }

        var location = target?.EventLocation ?? ScrollTargetLocation(selection);
        var deltas = AutoScrollSessionLogic.PixelDeltas(Math.Max(1, pixels));
        return PostChunkedAsync(location, deltas, target, strategy, cancellationToken);
    }

    /// <summary>按 14ms 间隔逐块投递（对应 Mac: :169-183 的循环）。</summary>
    private static async Task<bool> PostChunkedAsync(
        System.Drawing.Point location,
        IReadOnlyList<int> macPixelChunks,
        ScrollTarget? target,
        ScrollStrategy strategy,
        CancellationToken cancellationToken)
    {
        var ordered = OrderStrategies(strategy, target);

        for (var index = 0; index < macPixelChunks.Count; index++)
        {
            var chunkPixels = macPixelChunks[index];
            var wheelUnits = (int)Math.Max(1, Math.Round(Math.Abs(chunkPixels) * WheelUnitsPerMacPixel));
            var posted = false;

            foreach (var attempt in ordered)
            {
                if (attempt(ScrollContext.For(location, target, wheelUnits)))
                {
                    posted = true;
                    break;
                }
            }

            if (!posted)
            {
                return false;
            }

            if (index < macPixelChunks.Count - 1)
            {
                try
                {
                    await Task.Delay(ScrollEventIntervalMilliseconds, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// 策略排序。Auto 模式下按实测可靠性：
    /// 1. UIA ScrollPattern（Chromium 上唯一被证明可靠的驱动）
    /// 2. SendInput 合成滚轮（硬件级，标准应用有效；Chromium 亦响应真实滚轮）
    /// 3. PostMessage WM_MOUSEWHEEL（标准 Win32 控件有效；Chromium 常忽略）
    /// 4. SetScrollPos + WM_VSCROLL（标准滚动条窗口兜底）
    /// </summary>
    private static List<Func<ScrollContext, bool>> OrderStrategies(ScrollStrategy strategy, ScrollTarget? target)
    {
        var hasUia = target is { ScrollContainer: not null } && ScrollTarget.ElementSupportsScrollPattern(target.ScrollContainer);

        Func<ScrollContext, bool> uia = ctx =>
        {
            if (ctx.Target?.ScrollContainer is not { } container)
            {
                return false;
            }

            try
            {
                if (container.GetCurrentPattern(ScrollPattern.Pattern) is ScrollPattern scroll)
                {
                    // 大增量步进：Scroll 需要同时给水平/垂直量，这里只驱动垂直。
                    scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
                    return true;
                }
            }
            catch (ElementNotAvailableException)
            {
            }
            catch (InvalidOperationException)
            {
            }

            return false;
        };

        Func<ScrollContext, bool> sendInput = ctx => PostSyntheticWheelInput(ctx.Location, ctx.WheelUnits);

        Func<ScrollContext, bool> postMessage = ctx => PostMessageWheel(ctx.Location, ctx.WheelUnits, ctx.Target);

        Func<ScrollContext, bool> scrollBar = ctx => DriveStandardScrollBar(ctx.Location, ctx.WheelUnits);

        return strategy switch
        {
            ScrollStrategy.UiaScrollPattern => new() { uia },
            ScrollStrategy.SyntheticWheelInput => new() { sendInput },
            ScrollStrategy.PostMessageWheel => new() { postMessage },
            ScrollStrategy.Win32ScrollBar => new() { scrollBar },
            _ => new()
            {
                hasUia ? uia : sendInput,
                sendInput,
                postMessage,
                scrollBar,
            },
        };
    }

    /// <summary>
    /// SendInput 合成滚轮：真实硬件级事件，Chromium 与标准应用都响应。
    /// 代价：需要把光标移到命中点（投递完移回原位）。
    /// </summary>
    private static bool PostSyntheticWheelInput(System.Drawing.Point location, int wheelUnits)
    {
        if (!Win32.Native.GetCursorPos(out var original))
        {
            original = new Win32.Native.POINT(location.X, location.Y);
        }

        if (!Win32.Native.SetCursorPos(location.X, location.Y))
        {
            return false;
        }

        try
        {
            var inputs = new[]
            {
                new Win32.Native.INPUT
                {
                    type = Win32.Native.INPUT_MOUSE,
                    mi = new Win32.Native.MOUSEINPUT
                    {
                        dx = 0,
                        dy = 0,
                        mouseData = (uint)wheelUnits,
                        dwFlags = Win32.Native.MOUSEEVENTF_WHEEL,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero,
                    },
                },
            };

            var sent = Win32.Native.SendInput(1, inputs, System.Runtime.InteropServices.Marshal.SizeOf<Win32.Native.INPUT>());
            if (sent == 0)
            {
                return false;
            }
        }
        finally
        {
            // 光标只是投递的载体，用完立刻还回去 —— 用户不应看见光标跳动。
            Win32.Native.SetCursorPos(original.x, original.y);
        }

        return true;
    }

    /// <summary>
    /// PostMessage(WM_MOUSEWHEEL) 到命中点所在的最内层子窗口。
    /// 标准 Win32 控件（EDIT、listbox、标准滚动窗口）有效；
    /// Chromium/Electron 常忽略 —— 实测结论见项目报告。
    /// </summary>
    private static bool PostMessageWheel(System.Drawing.Point location, int wheelUnits, ScrollTarget? target)
    {
        var hwnd = target is { SourceProcessId: { } pid }
            ? FindWindowUnderPoint(location, pid)
            : Win32.Native.WindowFromPoint(new Win32.Native.POINT(location.X, location.Y));

        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        // 先落到最内层可见子窗口（对应 Chromium 的 Chrome_RenderWidgetHostHWND）。
        var child = InnermostChild(hwnd);
        if (child != IntPtr.Zero)
        {
            hwnd = child;
        }

        var client = new Win32.Native.POINT(location.X, location.Y);
        Win32.Native.ScreenToClient(hwnd, ref client);

        Win32.Native.PostWheel(hwnd, wheelUnits, client.x, client.y);
        return true;
    }

    /// <summary>
    /// 标准滚动条兜底：SetScrollPos + WM_VSCROLL。
    /// 对「有标准垂直滚动条」的窗口（记事本、listview、标准滚动窗口）有效。
    /// </summary>
    private static bool DriveStandardScrollBar(System.Drawing.Point location, int wheelUnits)
    {
        var hwnd = Win32.Native.WindowFromPoint(new Win32.Native.POINT(location.X, location.Y));
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var info = Win32.Native.SCROLLINFO.Create();
        if (!Win32.Native.GetScrollInfo(hwnd, Win32.Native.SB_VERT, ref info))
        {
            return false;
        }

        if (info.nMax <= info.nMin || info.nPage >= (uint)(info.nMax - info.nMin))
        {
            return false;   // 无垂直滚动条
        }

        // 每 120 单位约当一页的 1/8 —— 保持柔和步进。
        var page = (int)Math.Max(1, info.nPage / 8);
        var notches = Math.Max(1, wheelUnits / Win32.Native.WHEEL_DELTA);
        var newPos = Math.Clamp(info.nPos + page * notches, info.nMin, info.nMax - (int)info.nPage);
        if (newPos == info.nPos)
        {
            return true;   // 已到底 —— 消息本身无需再发
        }

        Win32.Native.SetScrollPos(hwnd, Win32.Native.SB_VERT, newPos, true);
        var wParam = (IntPtr)((Win32.Native.SB_THUMBPOSITION & 0xFFFF) | ((newPos & 0xFFFF) << 16));
        Win32.Native.SendMessage(hwnd, Win32.Native.WM_VSCROLL, wParam, IntPtr.Zero);
        return true;
    }

    /// <summary>命中点所在窗口（按 PID 过滤，对应 Mac 的源应用 PID 校验）。</summary>
    private static IntPtr FindWindowUnderPoint(System.Drawing.Point point, int processId)
    {
        var hwnd = Win32.Native.WindowFromPoint(new Win32.Native.POINT(point.X, point.Y));
        while (hwnd != IntPtr.Zero)
        {
            Win32.Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == (uint)processId)
            {
                return hwnd;
            }

            hwnd = Win32.Native.GetAncestor(hwnd, Win32.Native.GA_PARENT);
        }

        return Win32.Native.WindowFromPoint(new Win32.Native.POINT(point.X, point.Y));
    }

    /// <summary>逐层下钻到最内层可见子窗口。</summary>
    private static IntPtr InnermostChild(IntPtr hwnd)
    {
        var current = hwnd;
        for (var depth = 0; depth < 8; depth++)
        {
            var next = IntPtr.Zero;
            bool Found(IntPtr child, IntPtr _)
            {
                if (Win32.Native.IsWindowVisible(child))
                {
                    next = child;
                    return false;   // 只要第一个可见子窗口
                }

                return true;
            }

            Win32.Native.EnumChildWindows(current, Found, IntPtr.Zero);
            if (next == IntPtr.Zero)
            {
                break;
            }

            current = next;
        }

        return current;
    }

    /// <summary>
    /// 滚轮目标点 = 选区水平中心 × 垂直中心。
    /// Windows 屏幕坐标已是全局左上原点，无需 Mac 的 Quartz 翻转（:208-214）。
    /// </summary>
    public static System.Drawing.Point ScrollTargetLocation(CaptureSelection selection) =>
        new(
            (int)Math.Round(selection.GlobalRect.MinX + selection.GlobalRect.Width / 2),
            (int)Math.Round(selection.GlobalRect.MinY + selection.GlobalRect.Height / 2));

    /// <summary>
    /// 在元素子树内深度优先找垂直滚动条。对应 Mac: findVerticalScrollBar(depth:)（:216-238）。
    /// </summary>
    private static AutomationElement? FindVerticalScrollBar(AutomationElement element, int maximumDepth)
    {
        if (maximumDepth < 0)
        {
            return null;
        }

        try
        {
            var controlType = element.Current.ControlType;
            if (controlType == ControlType.ScrollBar)
            {
                var orientation = element.GetCurrentPropertyValue(AutomationElement.OrientationProperty);
                if (orientation is not OrientationType orientationType
                    || orientationType == OrientationType.Vertical)
                {
                    return element;
                }
            }

            if (maximumDepth == 0)
            {
                return null;
            }

            var children = element.FindAll(TreeScope.Children, Condition.TrueCondition);
            foreach (AutomationElement child in children)
            {
                if (FindVerticalScrollBar(child, maximumDepth - 1) is { } found)
                {
                    return found;
                }
            }
        }
        catch (ElementNotAvailableException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        return null;
    }

    private readonly record struct ScrollContext(
        System.Drawing.Point Location,
        ScrollTarget? Target,
        int WheelUnits)
    {
        public static ScrollContext For(System.Drawing.Point location, ScrollTarget? target, int wheelUnits) =>
            new(location, target, wheelUnits);
    }
}
