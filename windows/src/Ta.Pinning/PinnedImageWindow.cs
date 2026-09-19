using System.Runtime.InteropServices;
using Ta.Core.Capture;
using Ta.Core.Imaging;
using Ta.Pinning.Imaging;
using Ta.Pinning.Interop;

namespace Ta.Pinning;

/// <summary>钉图窗口的可调行为。默认值对齐 Mac 版的取舍。</summary>
public sealed class PinnedImageOptions
{
    /// <summary>
    /// 点击钉图时是否**不抢前台**。对应 macOS 的 <c>[.nonactivatingPanel]</c>
    /// （PinnedImageWindowController.swift:154）与 <c>canBecomeKey/Main = false</c>（:246-247）。
    ///
    /// ⚠️ Windows 上这是一个**真实的功能取舍**（参考文档 §14 风险 #4：没有中间态）：
    /// <list type="bullet">
    ///   <item><c>true</c>：钉图永不被激活，与 Mac 一致；但右键菜单来自未激活窗口，
    ///     在窗口外点击时可能不消失（已知 Windows 行为），且键等价（c/[/]/0/w）不可能生效
    ///     —— 与 Mac 的 <c>canBecomeKey = false</c> 表现一致。</item>
    ///   <item><c>false</c>：钉图可被激活，右键菜单与键等价完全可靠，代价是点击时会短暂抢焦点。</item>
    /// </list>
    /// 默认 false：右键菜单 20 项里有 5 项带键等价，功能完整性优先。
    /// </summary>
    public bool DoesNotStealFocus { get; init; }

    /// <summary>是否把本进程声明为 per-monitor-v2 DPI 感知。多屏不同缩放下必须为 true。</summary>
    public bool EnablePerMonitorDpi { get; init; } = true;
}

/// <summary>
/// 一个钉图窗口。
///
/// 对应 Mac 版的 <c>PinnedImagePanel</c> + <c>PinnedImageView</c>
/// （Sources/AIScreenshotApp/UI/PinnedImageWindowController.swift:245-648）：
///   · 窗口属性 → <c>WS_POPUP</c> + <c>WS_EX_TOPMOST</c> + <c>WS_EX_TOOLWINDOW</c> + 自绘阴影
///   · <c>constrainFrameRect</c> 原样返回 → <c>SetWindowPos</c>/<c>UpdateLayeredWindow</c> 不做任何夹取
///   · 交互 → WM_LBUTTONDOWN/MOVE/UP/DBLCLK + WM_MOUSEWHEEL + WM_RBUTTONUP
///   · 绘制 → <see cref="PinnedImageRenderer"/> 合成后 <c>UpdateLayeredWindow</c>
///
/// ⚠️ 本类**只能在宿主 UI 线程上使用**（见 <see cref="PinWindowHost"/>）。
/// </summary>
internal sealed class PinnedImageWindow : IDisposable
{
    internal const string ClassName = "TaPinnedImageWindow";

    private readonly RgbaBitmap _source;
    private readonly PinViewState _state;
    private readonly PinnedImageOptions _options;
    private readonly PinWindowHost _host;

    private double _scale = 1;
    private IntPtr _memoryDc;
    private IntPtr _section;
    private IntPtr _bits;
    private int _surfaceWidth;
    private int _surfaceHeight;
    private bool _dragging;
    private PinPointD _dragCursorStartPx;
    private PinPointD _dragOriginStartPt;
    private bool _disposed;

    /// <summary>已成功推送到屏幕的帧数。真机验证用它确认窗口**真的画出来了**。</summary>
    internal int RenderCount { get; private set; }

    /// <summary>最近一次 <c>UpdateLayeredWindow</c> 的 Win32 错误码（0 = 成功）。</summary>
    internal int LastRenderError { get; private set; }

    /// <summary>是否正处于拖拽中（已按下并捕获鼠标）。真机验证靠它避免竞态。</summary>
    internal bool IsDragging => _dragging;

    internal PinnedImageWindow(
        RgbaBitmap source,
        PinViewState state,
        PinnedImageOptions options,
        PinWindowHost host)
    {
        _source = source;
        _state = state;
        _options = options;
        _host = host;
    }

    /// <summary>窗口句柄。</summary>
    internal IntPtr Handle { get; private set; }

    /// <summary>钉图标识。对应 Mac 的 PinRecord.id（:32-37）。</summary>
    internal Guid Id { get; } = Guid.NewGuid();

    /// <summary>所属分组；null 表示未编组。对应 Mac 的 PinRecord.groupID（:35）。</summary>
    internal Guid? GroupId { get; set; }

    /// <summary>是否可见。对应 Mac 的 panel.isVisible（:193）。</summary>
    internal bool IsVisible { get; private set; }

    /// <summary>源图（供「恢复显示最近关闭」复用，:177）。</summary>
    internal RgbaBitmap Source => _source;

    internal PinViewState State => _state;

    // ── 生命周期 ────────────────────────────────────────────────────

    internal void Create()
    {
        var exStyle = PinInterop.WS_EX_TOOLWINDOW
            | PinInterop.WS_EX_TOPMOST
            | PinInterop.WS_EX_LAYERED
            | (_options.DoesNotStealFocus ? PinInterop.WS_EX_NOACTIVATE : 0);

        var (width, height) = PinnedImageRenderer.WindowSize(_state.ContentSize, _scale, _state.Decoration.ShowsShadow);

        Handle = PinInterop.CreateWindowEx(
            exStyle,
            ClassName,
            "Ta 钉图",
            PinInterop.WS_POPUP,
            0, 0, width, height,
            IntPtr.Zero, IntPtr.Zero, PinInterop.GetModuleHandle(null), IntPtr.Zero);

        if (Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"CreateWindowEx 失败，Win32 错误 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        }

        var dpi = PinInterop.GetDpiForWindow(Handle);
        _scale = dpi == 0 ? 1 : dpi / 96.0;

        // SW_SHOWNOACTIVATE —— 显示但不抢焦点（:186 orderFrontRegardless + canBecomeMain = false）。
        PinInterop.ShowWindow(Handle, PinInterop.SW_SHOWNOACTIVATE);
        IsVisible = true;

        Render();
    }

    internal void Render(bool positionOnly = false)
    {
        if (Handle == IntPtr.Zero || _disposed)
        {
            return;
        }

        var (windowWidth, windowHeight) = PinnedImageRenderer.WindowSize(
            _state.ContentSize, _scale, _state.Decoration.ShowsShadow);

        if (!positionOnly)
        {
            var margin = PinnedImageRenderer.ShadowMargin(_state.Decoration.ShowsShadow, _scale);
            var radius = Math.Max(1, (int)Math.Round(PinnedImageRenderer.CornerRadiusPoints * _scale));
            var borderWidth = Math.Max(1, (int)Math.Round(PinnedImageRenderer.BorderWidthPoints * _scale));
            var viewWidth = Math.Max(1, windowWidth - (margin * 2));
            var viewHeight = Math.Max(1, windowHeight - (margin * 2));

            var request = PinDrawRequest.FromState(_source, _state, viewWidth, viewHeight);
            var buffer = PinnedImageRenderer.Compose(request, _scale, radius, borderWidth, margin);

            EnsureSurface(windowWidth, windowHeight);
            System.Runtime.InteropServices.Marshal.Copy(buffer, 0, _bits, buffer.Length);
        }

        if (_memoryDc == IntPtr.Zero)
        {
            return;
        }

        // UpdateLayeredWindow 同时更新**位置、尺寸与内容** —— 一次调用、无闪烁。
        var destination = new PinInterop.POINT(
            (int)Math.Round(_state.Origin.X * _scale),
            (int)Math.Round(_state.Origin.Y * _scale));
        var size = new PinInterop.SIZE(windowWidth, windowHeight);
        var source = new PinInterop.POINT(0, 0);
        var blend = new PinInterop.BLENDFUNCTION
        {
            BlendOp = PinInterop.AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = PinInterop.AC_SRC_ALPHA,
        };

        var screenDc = PinInterop.GetDC(IntPtr.Zero);
        try
        {
            var ok = PinInterop.UpdateLayeredWindow(
                Handle, screenDc, ref destination, ref size, _memoryDc, ref source, 0, ref blend,
                PinInterop.ULW_ALPHA);
            LastRenderError = ok ? 0 : Marshal.GetLastWin32Error();
            if (ok)
            {
                RenderCount++;
            }
        }
        finally
        {
            PinInterop.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    internal void Hide()
    {
        if (Handle == IntPtr.Zero)
        {
            return;
        }

        IsVisible = false;
        // 对应 Mac: panel.orderOut(nil)（:97, :179, :200）
        PinInterop.ShowWindow(Handle, 0);
    }

    internal void Show()
    {
        if (Handle == IntPtr.Zero)
        {
            return;
        }

        IsVisible = true;
        PinInterop.ShowWindow(Handle, PinInterop.SW_SHOWNOACTIVATE);
        Render();
    }

    internal void Close()
    {
        if (Handle == IntPtr.Zero || !_state.TryBeginClose())
        {
            return;
        }

        PinInterop.DestroyWindow(Handle);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseSurface();

        if (Handle != IntPtr.Zero)
        {
            PinInterop.DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }
    }

    private void EnsureSurface(int width, int height)
    {
        if (_memoryDc != IntPtr.Zero && _surfaceWidth == width && _surfaceHeight == height)
        {
            return;
        }

        ReleaseSurface();

        var screenDc = PinInterop.GetDC(IntPtr.Zero);
        try
        {
            _memoryDc = PinInterop.CreateCompatibleDC(screenDc);
            if (_memoryDc == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateCompatibleDC 失败。");
            }

            var info = new PinInterop.BITMAPINFO();
            info.bmiHeader.biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<PinInterop.BITMAPINFOHEADER>();
            info.bmiHeader.biWidth = width;
            // 负高度 = 自上而下，与合成器的行序一致。
            info.bmiHeader.biHeight = -height;
            info.bmiHeader.biPlanes = 1;
            info.bmiHeader.biBitCount = 32;
            info.bmiHeader.biCompression = PinInterop.BI_RGB;

            _section = PinInterop.CreateDIBSection(
                screenDc, ref info, PinInterop.DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
            if (_section == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateDIBSection 失败。");
            }

            PinInterop.SelectObject(_memoryDc, _section);
        }
        finally
        {
            PinInterop.ReleaseDC(IntPtr.Zero, screenDc);
        }

        _surfaceWidth = width;
        _surfaceHeight = height;
    }

    private void ReleaseSurface()
    {
        if (_memoryDc != IntPtr.Zero)
        {
            PinInterop.DeleteDC(_memoryDc);
            _memoryDc = IntPtr.Zero;
        }

        if (_section != IntPtr.Zero)
        {
            PinInterop.DeleteObject(_section);
            _section = IntPtr.Zero;
        }

        _bits = IntPtr.Zero;
        _surfaceWidth = 0;
        _surfaceHeight = 0;
    }

    // ── 窗口过程 ────────────────────────────────────────────────────

    internal IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case PinInterop.WM_LBUTTONDBLCLK:
                // 对应 Mac: closeForDoubleClickIfNeeded（:634-640）—— clickCount >= 2 即关闭。
                // 注意：Windows 只在窗口类带 CS_DBLCLKS 时才发这条消息（见 PinWindowHost 注册处）。
                Close();
                return IntPtr.Zero;

            case PinInterop.WM_LBUTTONDOWN:
                if (_state.IsCropping)
                {
                    // 对应 Mac: mouseDown 的裁剪分支（:401-406）
                    var cropPoint = ViewPoint(lParam);
                    _state.UpdateCropDrag(cropPoint, cropPoint);
                    PinInterop.SetCapture(hwnd);
                }
                else
                {
                    BeginDrag();
                }

                return IntPtr.Zero;

            case PinInterop.WM_MOUSEMOVE:
                if (_state.IsCropping)
                {
                    // 对应 Mac: mouseDragged 的裁剪分支（:413-417）
                    if (_state.CropDragStart is not null)
                    {
                        _state.UpdateCropDrag(_state.CropDragStart.Value, ViewPoint(lParam));
                        Render();
                    }
                }
                else if (_dragging)
                {
                    UpdateDrag();
                }

                return IntPtr.Zero;

            case PinInterop.WM_LBUTTONUP:
                if (_state.IsCropping)
                {
                    // 对应 Mac: mouseUp 的裁剪提交（:426-448）
                    _state.CommitCrop(ViewBounds());
                    PinInterop.ReleaseCapture();
                    Render();
                }
                else if (_dragging)
                {
                    EndDrag();
                }

                return IntPtr.Zero;

            case PinInterop.WM_RBUTTONUP:
                // 对应 Mac: NSView.menu（:320-347）—— 右键弹出。
                ShowContextMenu();
                return IntPtr.Zero;

            case PinInterop.WM_MOUSEWHEEL:
                // 对应 Mac: scrollWheel（:450-465）。Windows 的 ⌘ 对应 Ctrl。
                var ctrlDown = (PinInterop.GetKeyState(0x11) & 0x8000) != 0;
                if (_state.Scroll(PinInterop.GetWheelDelta(wParam) / (double)PinInterop.WHEEL_DELTA, ctrlDown))
                {
                    Render();
                }

                return IntPtr.Zero;

            case PinInterop.WM_DPICHANGED:
                // 移到另一块不同缩放的显示器。尺寸/原点都是逻辑 pt，只换缩放系数即可。
                var newDpi = (uint)(wParam.ToInt64() & 0xFFFF);
                _scale = newDpi == 0 ? _scale : newDpi / 96.0;
                Render();
                return IntPtr.Zero;

            case PinInterop.WM_COMMAND:
                ExecuteCommand((int)(wParam.ToInt64() & 0xFFFF));
                return IntPtr.Zero;

            case PinInterop.WM_DESTROY:
                IsVisible = false;
                _host.OnWindowDestroyed(this);
                return IntPtr.Zero;
        }

        return PinInterop.DefWindowProc(hwnd, message, wParam, lParam);
    }

    /// <summary>把客户区坐标换算成视图坐标（pt，已扣除阴影外扩）。</summary>
    private PinPointD ViewPoint(IntPtr lParam)
    {
        var client = PinInterop.LParamToPoint(lParam);
        var margin = PinnedImageRenderer.ShadowMargin(_state.Decoration.ShowsShadow, _scale);
        return new PinPointD((client.x - margin) / _scale, (client.y - margin) / _scale);
    }

    private RectD ViewBounds() => new(0, 0, _state.ContentSize.Width, _state.ContentSize.Height);

    // ── 拖拽移动 ────────────────────────────────────────────────────

    /// <summary>
    /// 开始拖动。对应 Mac 的 mouseDown（:408-409）：记下**全局**光标位置与窗口原点。
    /// Windows 上取全局坐标用 GetCursorPos，移动用 UpdateLayeredWindow 的新位置。
    /// </summary>
    private void BeginDrag()
    {
        _dragging = true;
        PinInterop.GetCursorPos(out var cursor);
        _dragCursorStartPx = new PinPointD(cursor.x, cursor.y);
        _dragOriginStartPt = _state.Origin;
        PinInterop.SetCapture(Handle);
    }

    /// <summary>拖动中。对应 Mac: mouseDragged（:412-424）。</summary>
    private void UpdateDrag()
    {
        PinInterop.GetCursorPos(out var cursor);

        // 光标位移是物理像素，状态里的原点是逻辑 pt，按本窗缩放折算。
        var deltaX = (cursor.x - _dragCursorStartPx.X) / _scale;
        var deltaY = (cursor.y - _dragCursorStartPx.Y) / _scale;
        _state.MoveTo(_dragOriginStartPt.X + deltaX, _dragOriginStartPt.Y + deltaY);

        // 只挪位置不重新合成 —— 拖拽要跟手。
        Render(positionOnly: true);
    }

    private void EndDrag()
    {
        _dragging = false;
        PinInterop.ReleaseCapture();
    }

    // ── 右键菜单与命令 ──────────────────────────────────────────────

    private void ShowContextMenu()
    {
        var menu = BuildMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        PinInterop.GetCursorPos(out var cursor);

        // 未激活窗口上弹出的菜单在「窗口外点击」时不会自动消失，
        // 因此允许激活的配置下先把窗口提到前台（经典 TrackPopupMenu 收尾手法）。
        if (!_options.DoesNotStealFocus)
        {
            PinInterop.SetForegroundWindow(Handle);
        }

        // TPM_RETURNCMD：同步返回选中项，避免依赖 WM_COMMAND 回投。
        var selected = PinInterop.TrackPopupMenuEx(
            menu,
            PinInterop.TPM_RETURNCMD | PinInterop.TPM_RIGHTBUTTON | PinInterop.TPM_LEFTALIGN | PinInterop.TPM_TOPALIGN,
            cursor.x, cursor.y, Handle, IntPtr.Zero);

        PinInterop.DestroyMenu(menu);

        if (selected != 0 && PinMenuSpec.CommandFromId(selected) is { } chosen)
        {
            ExecuteCommand(PinMenuSpec.CommandIdBase + (int)chosen);
        }
    }

    /// <summary>
    /// 按 <see cref="PinMenuSpec"/> 建出 Win32 弹出菜单。
    /// 抽成独立方法是为了让自动回归能在**不阻塞 UI 线程**的前提下
    /// 检查菜单真的建对了（项数、标题、键等价、勾选态）。
    /// 返回的 HMENU 由调用方负责 <c>DestroyMenu</c>。
    /// </summary>
    internal IntPtr BuildMenu()
    {
        var menu = PinInterop.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        PinCommand? previous = null;

        foreach (var item in PinMenuSpec.Items)
        {
            if (previous is { } last && PinMenuSpec.SeparatorAfter.Contains(last))
            {
                PinInterop.AppendMenu(menu, PinInterop.MF_SEPARATOR, UIntPtr.Zero, null);
            }

            var id = PinMenuSpec.CommandIdBase + (int)item.Command;

            // 键等价显示在 Tab 之后 —— 与 Mac 的 NSMenuItem 表现一致。
            var label = string.IsNullOrEmpty(item.KeyEquivalent)
                ? item.Title
                : $"{item.Title}\t{item.KeyEquivalent}";

            PinInterop.AppendMenu(menu, PinInterop.MF_STRING, (UIntPtr)id, label);
            PinInterop.CheckMenuItem(menu, (uint)id, PinInterop.MF_BYCOMMAND
                | (IsCommandChecked(item.Command) ? PinInterop.MF_CHECKED : PinInterop.MF_UNCHECKED));

            previous = item.Command;
        }

        return menu;
    }

    /// <summary>菜单勾选态。灰度/反色互斥天然成立：同一时刻只有一个能勾上。</summary>
    private bool IsCommandChecked(PinCommand command) => command switch
    {
        PinCommand.ToggleGrayscale => _state.FilterMode == PinFilterMode.Grayscale,
        PinCommand.ToggleInversion => _state.FilterMode == PinFilterMode.Inverted,
        PinCommand.ToggleBorder => _state.Decoration.ShowsBorder,
        PinCommand.ToggleShadow => _state.Decoration.ShowsShadow,
        PinCommand.ToggleTopmost => _state.IsTopmost,
        PinCommand.ToggleThumbnail => _state.IsThumbnail,
        _ => false,
    };

    private void ExecuteCommand(int id)
    {
        var command = PinMenuSpec.CommandFromId(id);
        if (command is null)
        {
            return;
        }

        switch (command.Value)
        {
            case PinCommand.CopyImage:
                // 对应 Mac: onCopy（:166-172）—— 把当前（裁剪后）图像写进剪贴板。
                _host.CopyImageToClipboard(this);
                return;

            case PinCommand.BeginCrop:
                _state.BeginCrop();
                break;

            case PinCommand.ResetCrop:
                _state.ResetCrop();
                break;

            case PinCommand.RotateLeft:
                _state.RotateLeft();
                break;

            case PinCommand.RotateRight:
                _state.RotateRight();
                break;

            case PinCommand.MirrorHorizontally:
                _state.ToggleMirrorHorizontally();
                break;

            case PinCommand.MirrorVertically:
                _state.ToggleMirrorVertically();
                break;

            case PinCommand.ToggleGrayscale:
            case PinCommand.ToggleInversion:
                _state.ToggleFilter(command.Value);
                break;

            case PinCommand.ToggleBorder:
                _state.ToggleBorder();
                break;

            case PinCommand.ToggleShadow:
                _state.ToggleShadow();
                break;

            case PinCommand.ToggleTopmost:
                _state.ToggleTopmost();
                SetZOrder();
                break;

            case PinCommand.ResetAppearance:
                _state.ResetAppearance();
                break;

            case PinCommand.ToggleThumbnail:
                _state.ToggleThumbnail();
                break;

            case PinCommand.EnableClickThrough:
                _state.ToggleClickThrough();
                SetClickThroughStyle();
                break;

            case PinCommand.GroupVisiblePins:
                _host.GroupVisiblePins(this);
                return;

            case PinCommand.HideGroup:
                _host.HideGroup(this);
                return;

            case PinCommand.ClosePin:
                Close();
                return;
        }

        Render();
    }

    private void SetZOrder()
    {
        if (Handle == IntPtr.Zero)
        {
            return;
        }

        PinInterop.SetWindowPos(
            Handle,
            _state.IsTopmost ? PinInterop.HWND_TOPMOST : PinInterop.HWND_NOTOPMOST,
            0, 0, 0, 0,
            PinInterop.SWP_NOMOVE | PinInterop.SWP_NOSIZE | PinInterop.SWP_NOACTIVATE);
    }

    /// <summary>
    /// 设置鼠标穿透。对应 Mac: <c>panel.ignoresMouseEvents</c>（:182, :591）。
    /// 供「恢复全部交互」之类的批量操作使用（:104-106）。
    /// </summary>
    internal void SetClickThrough(bool enabled)
    {
        if (_state.ClickThrough == enabled)
        {
            return;
        }

        _state.ToggleClickThrough();
        SetClickThroughStyle();
    }

    private void SetClickThroughStyle()
    {
        if (Handle == IntPtr.Zero)
        {
            return;
        }

        var current = PinInterop.GetWindowLongPtr(Handle, PinInterop.GWL_EXSTYLE).ToInt64();
        var updated = _state.ClickThrough
            ? current | PinInterop.WS_EX_TRANSPARENT
            : current & ~PinInterop.WS_EX_TRANSPARENT;

        PinInterop.SetWindowLongPtr(Handle, PinInterop.GWL_EXSTYLE, new IntPtr(updated));
    }
}
