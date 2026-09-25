namespace Ta.Capture.Win32;

/// <summary>
/// Win32 显示器枚举。产出 <see cref="MonitorRecord"/>，之后全部交给纯逻辑层处理。
///
/// 对应 Mac 版 <c>NSScreen.screens</c> + <c>screen.backingScaleFactor</c>
/// （TaAgentCaptureService.swift:264-273）。差别是 Mac 可以直接问 AppKit，
/// Windows 必须 EnumDisplayMonitors + GetDpiForMonitor 两个调用拼出来。
/// </summary>
public static class DisplayEnumerator
{
    /// <summary>
    /// 枚举当前所有显示器。返回顺序由 EnumDisplayMonitors 决定，**不保证**任何含义，
    /// 也不保证第一块是主屏 —— 主屏只看 <see cref="NativeMethods.MONITORINFOF_PRIMARY"/>。
    /// </summary>
    public static IReadOnlyList<MonitorRecord> Enumerate()
    {
        var results = new List<MonitorRecord>();

        // 委托必须活到 EnumDisplayMonitors 返回，否则 GC 回收后 native 侧函数指针失效。
        NativeMethods.MonitorEnumProc proc = (monitor, _, _, _) =>
        {
            var info = NativeMethods.MONITORINFOEXW.Create();
            if (NativeMethods.GetMonitorInfoW(monitor, ref info))
            {
                // 物理像素尺寸：rcMonitor 的单位取决于本进程的 DPI 感知模式，
                // 只有显示模式尺寸能给出「物理像素」这个绝对量（见 MonitorRecord.PixelScale）。
                var hasPhysical = TryGetPhysicalSize(info.szDevice, out var physicalWidth, out var physicalHeight);

                results.Add(new MonitorRecord(
                    Handle: monitor,
                    DeviceName: info.szDevice,
                    Bounds: ToRect(info.rcMonitor),
                    WorkArea: ToRect(info.rcWork),
                    IsPrimary: (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
                    EffectiveDpiX: TryGetDpi(monitor, out var dpiX, out _) ? dpiX : 96,
                    EffectiveDpiY: TryGetDpi(monitor, out _, out var dpiY) ? dpiY : 96,
                    PhysicalWidth: hasPhysical ? physicalWidth : 0,
                    PhysicalHeight: hasPhysical ? physicalHeight : 0));
            }

            return true;
        };

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);

        // 显式延长委托生命周期，防止 JIT 在 EnumDisplayMonitors 返回前提早回收。
        GC.KeepAlive(proc);

        return results;
    }

    /// <summary>
    /// 取该显示器的**真实物理像素**尺寸（当前显示模式，如 2560x1440）。
    ///
    /// 与 <see cref="NativeMethods.GetDpiForMonitor"/> 的区别：DPI 是「每英寸多少点」的
    /// 相对量，只有配合「rcMonitor 的单位」才能算出倍率，而 rcMonitor 的单位又取决于
    /// 调用进程的 DPI 感知模式 —— 拿 dpi/96 直接当倍率，在 DPI 感知进程里会把 2560
    /// 再乘成 3200（实测的全屏截图被放大 1.25 倍的根因）。显示模式尺寸是绝对量，不需要猜。
    ///
    /// 取不到时返回 false，调用方退回 DPI 倍率（老行为）。
    /// </summary>
    private static bool TryGetPhysicalSize(string deviceName, out uint width, out uint height)
    {
        width = 0;
        height = 0;

        var mode = new NativeMethods.DEVMODEW
        {
            dmSize = (ushort)System.Runtime.InteropServices.Marshal
                .SizeOf<NativeMethods.DEVMODEW>(),
        };

        if (!NativeMethods.EnumDisplaySettingsW(
                deviceName, NativeMethods.ENUM_CURRENT_SETTINGS, ref mode))
        {
            return false;
        }

        width = mode.dmPelsWidth;
        height = mode.dmPelsHeight;
        return width > 0 && height > 0;
    }

    /// <summary>
    /// GetDpiForMonitor 在极少数配置（某些远程桌面、无头环境）会失败。
    /// 失败时退回 96（100%），与 Mac 取不到 backingScaleFactor 时用 1 兜底同理。
    /// </summary>
    private static bool TryGetDpi(IntPtr monitor, out uint dpiX, out uint dpiY)
    {
        var hr = NativeMethods.GetDpiForMonitor(
            monitor, NativeMethods.MDT_EFFECTIVE_DPI, out dpiX, out dpiY);
        return hr == 0 && dpiX > 0;
    }

    private static Ta.Core.Capture.RectD ToRect(NativeMethods.RECT r) =>
        Ta.Core.Capture.RectD.FromLTRB(r.left, r.top, r.right, r.bottom);
}
