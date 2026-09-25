namespace Ta.Capture.Win32;

/// <summary>
/// Win32 窗口枚举。产出 <see cref="RawWindowObservation"/>，之后全部交给纯逻辑层处理。
///
/// 对应 Mac 版 <c>CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], …)</c>
/// （WindowSnapService.swift:31）。差别是 CG 一次调用就返回全部属性，
/// Windows 要为每个窗口分别问可见性、扩展框架边界、遮蔽状态、透明度、标题、进程映像，
/// 所以这里的循环体就是那组 P/Invoke，没有任何判断逻辑。
/// </summary>
public static class WindowEnumerator
{
    /// <summary>
    /// 枚举所有顶层窗口，产出原始观测。
    /// <para>
    /// 顺序即 Z 序：EnumWindows 返回<b>前→后</b>（最上层在前），
    /// 与 CGWindowListCopyWindowInfo 的返回序一致，因此 EnumerationOrder 直接当 zOrder 用。
    /// </para>
    /// </summary>
    public static IReadOnlyList<RawWindowObservation> Enumerate()
    {
        var results = new List<RawWindowObservation>();

        // 委托必须活到 EnumWindows 返回，理由同 DisplayEnumerator。
        NativeMethods.EnumWindowsProc proc = (hwnd, _) =>
        {
            var index = results.Count;

            // EnumWindows 只返回顶层窗口，这里不再额外过滤 owner ——
            // Mac 版也不滤（前台 PID 条件已足够），保持一致。
            var extended = GetExtendedFrameBounds(hwnd);
            var processId = NativeMethods.GetProcessId(hwnd);
            var imagePath = NativeMethods.TryGetProcessImagePath(processId);

            results.Add(new RawWindowObservation(
                WindowHandle: hwnd,
                ProcessId: processId,
                ProcessImagePath: imagePath,
                Title: NativeMethods.GetWindowTitle(hwnd),
                Frame: extended,
                IsVisible: NativeMethods.IsWindowVisible(hwnd),
                IsToolWindow: IsToolWindow(hwnd),
                IsCloaked: IsCloaked(hwnd),
                Alpha: GetAlpha(hwnd),
                EnumerationOrder: index));

            return true;
        };

        NativeMethods.EnumWindows(proc, IntPtr.Zero);
        GC.KeepAlive(proc);

        return results;
    }

    /// <summary>当前前台窗口句柄。没有前台窗口时返回 IntPtr.Zero。</summary>
    public static IntPtr ForegroundWindow() => NativeMethods.GetForegroundWindow();

    /// <summary>
    /// 前台窗口所属进程。拿不到前台窗口时返回 null ——
    /// <see cref="WindowSnapSnapshotPolicy"/> 会因此放宽过滤（与 Mac 一致）。
    /// </summary>
    public static int? FrontmostProcessId()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return null;
        }

        var pid = NativeMethods.GetProcessId(foreground);
        return pid == 0 ? null : pid;
    }

    /// <summary>
    /// 取指定窗口的扩展框架边界（不含阴影）。窗口无效或已销毁时返回 null。
    ///
    /// 单独暴露是因为 <see cref="Ta.Core.Capture.IScreenCapture.CaptureWindow"/> 需要知道
    /// 目标尺寸，而被传入的句柄可能来自调用方而不是 <see cref="Enumerate"/> 的结果
    /// （例如 Agent 桥传进来的 windowId）。
    /// </summary>
    public static Ta.Core.Capture.RectD? TryGetWindowFrame(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        // 显式声明 out 变量类型：两个重载仅 out 参数类型不同，用 var 会二义。
        NativeMethods.RECT rect;
        var hr = NativeMethods.DwmGetWindowAttribute(
            hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out rect,
            System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RECT>());

        if (hr != 0 || rect.Width <= 0 || rect.Height <= 0)
        {
            return null;
        }

        return Ta.Core.Capture.RectD.FromLTRB(rect.left, rect.top, rect.right, rect.bottom);
    }

    /// <summary>
    /// DWMWA_EXTENDED_FRAME_BOUNDS。**不含**投影阴影，与 Mac 的 kCGWindowBounds 语义一致。
    /// 取不到（非桌面进程、已销毁）时退回空矩形，交由过滤层按尺寸剔除。
    /// </summary>
    private static Ta.Core.Capture.RectD GetExtendedFrameBounds(IntPtr hwnd) =>
        TryGetWindowFrame(hwnd) ?? default;

    private static bool IsToolWindow(IntPtr hwnd) =>
        (NativeMethods.GetWindowExStyle(hwnd) & NativeMethods.WS_EX_TOOLWINDOW) != 0;

    /// <summary>
    /// DWMWA_CLOAKED。被其他虚拟桌面或系统遮蔽的窗口对用户不可见，必须剔除 ——
    /// 对应 Mac 的 <c>layer &gt;= 0</c>（排除桌面元素）。
    /// </summary>
    private static bool IsCloaked(IntPtr hwnd)
    {
        // 显式声明 out 变量类型：两个重载仅 out 参数类型不同，用 var 会二义。
        int cloaked;
        var hr = NativeMethods.DwmGetWindowAttribute(
            hwnd, NativeMethods.DWMWA_CLOAKED, out cloaked, sizeof(int));
        return hr == 0 && cloaked != 0;
    }

    /// <summary>
    /// 逐窗口透明度，归一化到 [0,1]。未分层或未设 LWA_ALPHA 的窗口视为完全不透明 ——
    /// 对应 Mac 版 <c>(info[kCGWindowAlpha] as? NSNumber)?.doubleValue ?? 1</c> 的默认值 1。
    /// </summary>
    private static double GetAlpha(IntPtr hwnd)
    {
        if (!NativeMethods.GetLayeredWindowAttributes(hwnd, out _, out var alpha, out var flags))
        {
            return 1;
        }

        return (flags & NativeMethods.LWA_ALPHA) == 0 ? 1 : alpha / 255.0;
    }
}
