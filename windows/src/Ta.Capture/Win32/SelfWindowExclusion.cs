namespace Ta.Capture.Win32;

/// <summary>
/// 自身窗口的「不进捕获」标记。
///
/// 对应 Mac 版 <c>CaptureSelection.excludedWindowIDs</c>（ScreenCaptureService.swift:93、:112-113）：
/// 冻结整屏时必须把覆盖层面板自己排除掉，否则冻结帧里会带一块半透明遮罩，
/// 用户就从「被污染的帧」上框选了 —— 直接破坏参考文档 §3.1 的行为契约。
///
/// Windows 上的实现比 Mac 更干净：一行 <c>SetWindowDisplayAffinity</c> 即可，
/// 该窗口从此不出现在**任何**捕获结果里（WGC、BitBlt、PrintWindow 都生效），
/// 不需要像 Mac 那样在每次调用时传排除清单。
/// </summary>
public static class SelfWindowExclusion
{
    /// <summary>
    /// 把窗口标记为不进任何屏幕捕获。
    /// </summary>
    /// <returns>设置成功与否。失败多为窗口已销毁或系统不支持。</returns>
    public static bool Exclude(IntPtr windowHandle) =>
        windowHandle != IntPtr.Zero
        && NativeMethods.SetWindowDisplayAffinity(windowHandle, NativeMethods.WDA_EXCLUDEFROMCAPTURE);

    /// <summary>恢复默认（该窗口重新出现在捕获里）。用于覆盖层销毁前的清理。</summary>
    public static bool Include(IntPtr windowHandle) =>
        windowHandle != IntPtr.Zero
        && NativeMethods.SetWindowDisplayAffinity(windowHandle, 0);
}
