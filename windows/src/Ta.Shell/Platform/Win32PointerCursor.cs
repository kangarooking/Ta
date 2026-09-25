using System.Runtime.InteropServices;

namespace Ta.Shell.Platform;

/// <summary>
/// Win32 鼠标指针控制。
///
/// 对应 Mac 版 <c>NSCursor.crosshair.set()</c> / <c>NSCursor.arrow.set()</c>
/// （CaptureCoordinator.swift:165、:203-206）。
///
/// ## 为什么需要专门一个类
///
/// Mac 版的 <c>NSCursor.set()</c> 是<b>全局</b>设置 —— 光标形态会持续到下一次显式改变，
/// 即使鼠标移出 Ta 的窗口也保持十字。
///
/// Win32 的 <c>SetCursor</c> 只在<b>当前</b> WM_SETCURSOR 生效，鼠标一动就被系统重置回
/// 窗口类的 hCursor。要拿到「全局十字光标」的效果，正确做法是改系统光标本身：
///   <c>SystemParametersInfo(SPI_SETCURSORS, ...)</c> 重建设所有系统光标，
///   再把默认箭头换成十字（CopyIcon + SetSystemCursor(IDC_ARROW)）。
/// 这样光标在任何窗口上都是十字，直到显式还原。
///
/// ⚠️ 这是<b>进程级</b>改动：还原必须成对，否则用户的光标会一直是十字。
/// 因此用引用计数保护，且还原失败时也要尽力恢复箭头。
/// </summary>
public sealed class Win32PointerCursor : Contracts.IPointerCursor
{
    private const int IDC_CROSS = 32515;
    private const int IDC_ARROW = 32512;
    private const int OCR_NORMAL = 32512;
    private const uint SPI_SETCURSORS = 0x0057;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;
    private const uint LR_SHARED = 0x00008000;

    private IntPtr _originalArrow;
    private IntPtr _crossIcon;
    private int _crosshairDepth;

    /// <summary>当前是否处于十字光标状态。</summary>
    public bool IsCrosshair => _crosshairDepth > 0;

    /// <summary>
    /// 设十字光标。
    ///
    /// 必须在覆盖层出现<b>之前</b>调用 —— 这是参考文档 §3.1 的行为契约：
    /// 用户按下快捷键的瞬间光标就该是十字，而不是等整屏冻结完才变。
    /// </summary>
    public void ShowCrosshair()
    {
        _crosshairDepth++;

        if (_crosshairDepth > 1)
        {
            // 已经是十字了（嵌套调用，比如长截图先设一次、主链路又设一次）。
            return;
        }

        // 1. 立即切一次 —— 覆盖 SetSystemCursor 生效前的空隙。
        var immediate = LoadCursorW(IntPtr.Zero, IDC_CROSS);
        if (immediate != IntPtr.Zero)
        {
            SetCursor(immediate);
        }

        // 2. 改系统箭头本身，拿到「全局十字」的效果。
        var cross = CopyIcon(LoadCursorW(IntPtr.Zero, IDC_CROSS));
        if (cross == IntPtr.Zero)
        {
            return;
        }

        _crossIcon = cross;

        // 先备份原箭头，再替换 —— 还原时要换回来。
        _originalArrow = CopyIcon(LoadCursorW(IntPtr.Zero, IDC_ARROW));
        SetSystemCursor(cross, OCR_NORMAL);

        // 3. 广播一次，让已缓存的窗口刷新光标。
        SystemParametersInfoW(SPI_SETCURSORS, 0, IntPtr.Zero, SPIF_SENDCHANGE);
    }

    /// <summary>恢复默认箭头。</summary>
    public void RestoreArrow()
    {
        if (_crosshairDepth == 0)
        {
            return;
        }

        _crosshairDepth--;

        if (_crosshairDepth > 0)
        {
            // 还有嵌套的调用方在用十字。
            return;
        }

        if (_originalArrow != IntPtr.Zero)
        {
            // SetSystemCursor 会接管句柄所有权，因此换回去的就是原箭头本身。
            SetSystemCursor(_originalArrow, OCR_NORMAL);
            _originalArrow = IntPtr.Zero;
        }
        else if (_crossIcon != IntPtr.Zero)
        {
            // 备份失败时的兜底：重建一个标准箭头盖回去。
            var arrow = CopyIcon(LoadCursorW(IntPtr.Zero, IDC_ARROW));
            if (arrow != IntPtr.Zero)
            {
                SetSystemCursor(arrow, OCR_NORMAL);
            }
        }

        _crossIcon = IntPtr.Zero;

        var immediate = LoadCursorW(IntPtr.Zero, IDC_ARROW);
        if (immediate != IntPtr.Zero)
        {
            SetCursor(immediate);
        }

        SystemParametersInfoW(SPI_SETCURSORS, 0, IntPtr.Zero, SPIF_SENDCHANGE | SPIF_UPDATEINIFILE);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadCursorW(IntPtr hInstance, int lpCursorName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CopyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetSystemCursor(IntPtr hcur, uint id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);
}
