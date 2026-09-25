using System.Runtime.InteropServices;
using System.Drawing;
using Ta.Core.Capture;

namespace Ta.Platform.Overlay;

/// <summary>
/// 单个覆盖层窗口的运行状态。
///
/// 对应 Mac 版 SelectionOverlayView 的那些可变属性
/// （dragMode / selectionRect / snapTargets / isTransitioning 等）。
/// </summary>
internal sealed class OverlayState
{
    public required SelectionOverlay Owner { get; init; }
    public required IntPtr Hwnd { get; init; }
    public required OverlayOptions Options { get; init; }

    /// <summary>覆盖层左上角在屏幕坐标中的位置。</summary>
    public int ScreenOriginX { get; init; }
    public int ScreenOriginY { get; init; }
    public int ScreenWidth { get; init; }
    public int ScreenHeight { get; init; }

    public Native.POINT Cursor { get; set; }
    public bool IsDragging { get; set; }
    public Native.POINT DragStart { get; set; }
    public Native.POINT DragCurrent { get; set; }
    public RectD? Preset { get; set; }

    public bool IsFinished { get; set; }

    // ── 操作栏（拖选完成后浮出的动作按钮排）──

    /// <summary>工具栏模式：拖选已完成，正在等待用户点选动作。</summary>
    public bool ToolbarVisible { get; set; }

    /// <summary>拖选完成时的选区（客户区坐标）。</summary>
    public Native.RECT FinishedSelection { get; set; }

    /// <summary>工具栏整体边界（客户区坐标）。</summary>
    public Native.RECT ToolbarBounds { get; set; }

    /// <summary>工具栏按钮（客户区坐标 + 标签 + 动作索引）。</summary>
    public List<(Native.RECT Rect, string Label, int Action)> ToolbarButtons { get; } = new();

    /// <summary>悬停按钮索引；-1 = 无。</summary>
    public int ToolbarHover { get; set; } = -1;

    /// <summary>
    /// 当前有效选区（屏幕像素坐标）。无选区或选区过小时返回 null。
    /// 最小尺寸对应 Mac 的 4×4 pt。
    /// </summary>
    public RectD? CurrentSelection()
    {
        if (Preset is { } preset)
        {
            return preset;
        }

        // POINT 未重载 ==，逐字段比较。
        if (!IsDragging
            && DragStart.x == 0 && DragStart.y == 0
            && DragCurrent.x == 0 && DragCurrent.y == 0)
        {
            return null;
        }

        return NormalizedSelectionAsRect();
    }

    /// <summary>把客户区坐标的拖拽矩形换算为屏幕像素坐标。</summary>
    public Native.RECT NormalizedSelection()
    {
        if (Preset is { } preset)
        {
            return new Native.RECT(
                ScreenOriginX + (int)preset.X,
                ScreenOriginY + (int)preset.Y,
                ScreenOriginX + (int)(preset.X + preset.Width),
                ScreenOriginY + (int)(preset.Y + preset.Height));
        }

        var left = Math.Min(DragStart.x, DragCurrent.x);
        var top = Math.Min(DragStart.y, DragCurrent.y);
        var right = Math.Max(DragStart.x, DragCurrent.x);
        var bottom = Math.Max(DragStart.y, DragCurrent.y);

        return new Native.RECT(
            ScreenOriginX + left,
            ScreenOriginY + top,
            ScreenOriginX + right,
            ScreenOriginY + bottom);
    }

    /// <summary>以 <see cref="RectD"/> 形式返回当前选区。</summary>
    public RectD NormalizedSelectionAsRect()
    {
        var rect = NormalizedSelection();

        // 最小尺寸对应 Mac 的 4×4 pt；不足则视为无选区。
        return rect.Width < Native.MinSelectionSize || rect.Height < Native.MinSelectionSize
            ? default
            : new RectD(rect.left, rect.top, rect.Width, rect.Height);
    }

    /// <summary>
    /// 光标选择。对应 Mac 版 SelectionOverlayView.cursor(at:)：
    /// 命中手柄 → 手柄光标；选区内 → openHand；其他 → crosshair。
    /// </summary>
    public IntPtr CursorForCurrentPoint() => Native.LoadCursor(IntPtr.Zero, Native.IDC_CROSS);
}
