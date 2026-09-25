using System.Runtime.InteropServices;
using Ta.Core.Capture;
using Ta.Core.Imaging;
using Ta.Pinning.Interop;

namespace Ta.Pinning;

/// <summary>
/// 一个钉图的对外句柄。
///
/// 只暴露**跨线程安全**的操作（内部都经 <see cref="PinWindowHost"/> 投递到宿主线程），
/// 不暴露窗口过程或渲染细节 —— 对应 Mac 版 App 层只拿 <c>PinRecord</c> 的 id 与 panel。
/// </summary>
public sealed class PinnedImage
{
    private readonly PinnedImageWindow _window;
    private readonly PinnedImageController _controller;

    internal PinnedImage(PinnedImageWindow window, PinnedImageController controller)
    {
        _window = window;
        _controller = controller;
    }

    /// <summary>钉图标识。对应 Mac 的 PinRecord.id（:32-37）。</summary>
    public Guid Id => _window.Id;

    /// <summary>
    /// 窗口句柄。仅用于诊断与自动化验证（真机测试靠它断言窗口真的出现了）。
    /// </summary>
    public IntPtr WindowHandle => _window.Handle;

    /// <summary>是否可见。对应 Mac: panel.isVisible（:193）。</summary>
    public bool IsVisible => _window.IsVisible;

    /// <summary>当前内容尺寸（逻辑 pt）。</summary>
    public PinSizeD ContentSize => _window.State.ContentSize;

    /// <summary>当前原点（逻辑 pt）。</summary>
    public PinPointD Origin => _window.State.Origin;

    /// <summary>内部窗口实例。仅供同一程序集的诊断与自动回归使用。</summary>
    internal PinnedImageWindow Window => _window;

    /// <summary>关闭钉图。对应 Mac 的 closePin（:642-647）。</summary>
    public void Close() => _controller.Close(this);

    /// <summary>隐藏钉图。对应 Mac: panel.orderOut(nil)（:179）。</summary>
    public void Hide() => _controller.Hide(this);

    /// <summary>显示钉图。对应 Mac: orderFrontRegardless()（:101）。</summary>
    public void Show() => _controller.Show(this);
}

/// <summary>已关闭、仍可恢复的钉图。对应 Mac 的 <c>ClosedPin</c>（:39-42）。</summary>
internal sealed record ClosedPin(RgbaBitmap Image, PinSizeD Size, PinPointD Origin);

/// <summary>
/// 钉图控制器 —— Mac 版 <c>PinnedImageWindowController</c>
/// （Sources/AIScreenshotApp/UI/PinnedImageWindowController.swift:30-243）的 Windows 移植。
///
/// 职责与 Mac 一一对应：
///   · <see cref="Pin"/>            → <c>pin(_:near:)</c>（:47-54）
///   · <see cref="PinFromClipboard"/> → <c>pinFromPasteboard()</c>（:56-94）
///   · <see cref="HideAll"/> / <see cref="ShowAll"/> / <see cref="EnableInteractionForAll"/>
///                                → :96-106
///   · <see cref="RestoreLastClosed"/> → <c>restoreLastClosed()</c>（:108-118，最多留 3 个）
///   · 分组 / 隐藏本组              → :191-201（实现见 <see cref="PinWindowHost"/>）
///
/// 线程模型：本类是**调用方线程**上的门面；所有窗口操作都被投递到宿主 STA 线程。
/// ⚠️ 因此不要在钉图宿主线程上调用本类（会自等死）。
/// </summary>
public sealed class PinnedImageController : IDisposable
{
    /// <summary>Mac: <c>if closedPins.count &gt; 3 { closedPins.removeFirst() }</c>（:178）。</summary>
    public const int MaxClosedPins = 3;

    private readonly PinnedImageOptions _options;
    private readonly object _gate = new();
    private PinWindowHost? _host;
    private readonly List<PinnedImageWindow> _records = new();
    private readonly List<ClosedPin> _closed = new();
    private bool _hostEventsHooked;
    private bool _disposed;

    public PinnedImageController(PinnedImageOptions? options = null)
    {
        _options = options ?? new PinnedImageOptions();
    }

    /// <summary>当前存活的钉图数量。</summary>
    public int PinCount
    {
        get
        {
            lock (_gate)
            {
                return _records.Count;
            }
        }
    }

    /// <summary>当前可见的钉图数量。对应 Mac 的 records.filter { panel.isVisible }（:193）。</summary>
    public int VisiblePinCount
    {
        get
        {
            lock (_gate)
            {
                return _records.Count(record => record.IsVisible);
            }
        }
    }

    /// <summary>可恢复的已关闭钉图数量（上限 <see cref="MaxClosedPins"/>）。</summary>
    public int RestorablePinCount
    {
        get
        {
            lock (_gate)
            {
                return _closed.Count;
            }
        }
    }

    // ── 创建钉图 ────────────────────────────────────────────────────

    /// <summary>
    /// 钉一张图。
    ///
    /// ⚠️ **单位约定（与 Mac 的差异）**：Windows 侧的 <c>CaptureSelection.GlobalRect</c> 与
    /// 截图位图都是**物理像素**（见 SelectionOverlay / FrozenDisplayCropper），
    /// 而 Mac 的 <c>selection.globalRect</c> 是 pt。因此本方法按锚点所在屏的 DPI
    /// 把传入的像素值折算成 pt 后再做布局 —— 与 Mac「选区多大就钉多大」的观感一致。
    /// </summary>
    /// <param name="image">源图像（RGBA，物理像素）。控制器不复制它，调用方请勿在钉图存活期间改写。</param>
    /// <param name="anchor">锚点：通常是截图选区（<c>CaptureSelection.GlobalRect</c>，物理像素）。</param>
    /// <param name="preferredLogicalSize">首选尺寸，**物理像素**。截图钉图传选区尺寸，剪贴板钉图传 null。</param>
    public PinnedImage Pin(RgbaBitmap image, CaptureSelection anchor, PinSizeD? preferredLogicalSize = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(image);

        var anchorPoint = new PinInterop.POINT(
            (int)Math.Round(anchor.GlobalRect.X),
            (int)Math.Round(anchor.GlobalRect.Y));

        // Mac 用 selection.globalRect 所在屏的 visibleFrame（:137-139）；
        // Windows 上等价物是显示器**工作区**，且必须折算成 pt 才能与布局算术同单位。
        var scale = PinScreen.ScaleAt(anchorPoint);
        var visibleFrame = PinScreen.WorkAreaInPoints(anchorPoint);

        var anchorLogical = new PinPointD(anchor.GlobalRect.X / scale, anchor.GlobalRect.Y / scale);
        var preferred = preferredLogicalSize is { } pixels
            ? new PinSizeD(pixels.Width / scale, pixels.Height / scale)
            : (PinSizeD?)null;

        var (size, origin) = PinnedImageLayout.InitialFrame(
            (image.Width, image.Height), preferred, visibleFrame, anchorLogical);

        return CreateWindow(image, size, origin);
    }

    /// <summary>
    /// 在光标处钉一张图（截图钉图的常用入口，锚点为光标）。
    /// </summary>
    /// <param name="preferredLogicalSize">首选尺寸，**物理像素**；null 则按 640×480 上限走剪贴板那一支。</param>
    public PinnedImage PinAtCursor(RgbaBitmap image, PinSizeD? preferredLogicalSize = null)
    {
        var cursor = PinScreen.CursorPosition();
        return Pin(image, new CaptureSelection
        {
            GlobalRect = new RectD(cursor.x, cursor.y, 1, 1),
            ScreenFrame = PinScreen.WorkAreaInPixels(cursor),
        }, preferredLogicalSize);
    }

    /// <summary>
    /// 从剪贴板生成钉图。对应 Mac: <c>pinFromPasteboard()</c>（:56-94）。
    /// 返回 null 表示剪贴板里没有可钉的内容。
    /// </summary>
    public PinnedImage? TryPinFromClipboard()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var host = EnsureHost();
        var content = host.InvokeAsync(() => PinClipboardReader.Read(host.HostWindow)).GetAwaiter().GetResult();
        if (content is null)
        {
            return null;
        }

        var cursor = PinScreen.CursorPosition();
        var anchor = new CaptureSelection
        {
            GlobalRect = new RectD(cursor.x, cursor.y, 1, 1),
            ScreenFrame = PinScreen.WorkAreaInPixels(cursor),
        };

        // 剪贴板内容可自带首选尺寸（文字卡片：520 × min(1200, max(120, h+48))）。
        //
        // ⚠️ 单位是**物理像素**，不是逻辑尺寸 —— 尽管形参名叫 preferredLogicalSize，
        // 但实现层一致按像素处理（见本文件 :155 的 `is { } pixels`），
        // PinClipboardContent 的字段也因此叫 PreferredSizePixels。
        //
        // 因此原文注释里「卡片就是 520pt 宽、任何 DPI 下都成立」这条契约**不成立**：
        // 200% DPI 下 520px 只显示为 260pt。要做到 DPI 无关需改为传逻辑尺寸并
        // 在 InitialSize 里乘 scale，那会改变截图钉图那一支的行为，属于独立改动。
        return Pin(content.Bitmap, anchor, content.PreferredSizePixels);
    }

    /// <summary>
    /// 从剪贴板生成钉图，只返回是否成功。对应 Mac 的 <c>pinFromPasteboard() -> Bool</c>（:56）。
    /// </summary>
    public bool PinFromClipboard() => TryPinFromClipboard() is not null;

    /// <summary>
    /// 为指定钉图建一次右键菜单（不弹出，只供自动回归检查菜单内容）。
    /// 返回的 HMENU 由调用方负责 <c>DestroyMenu</c>。
    /// </summary>
    internal Task<IntPtr> BuildMenuFor(Guid id)
    {
        var host = Host;
        return host is null ? Task.FromResult(IntPtr.Zero) : host.BuildMenuAsync(id);
    }

    /// <summary>
    /// 恢复最近关闭的钉图。对应 Mac: <c>restoreLastClosed()</c>（:108-118）——
    /// 用关闭时保存的 frame 作为 preferredFrame，preferredLogicalSize 传 null。
    /// </summary>
    public bool RestoreLastClosed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ClosedPin closed;
        lock (_gate)
        {
            if (_closed.Count == 0)
            {
                return false;
            }

            closed = _closed[^1];
            _closed.RemoveAt(_closed.Count - 1);
        }

        CreateWindow(closed.Image, closed.Size, closed.Origin);
        return true;
    }

    private PinnedImage CreateWindow(RgbaBitmap image, PinSizeD size, PinPointD origin)
    {
        var host = EnsureHost();
        var window = host.CreateWindowAsync(image, size, origin).GetAwaiter().GetResult();

        lock (_gate)
        {
            _records.Add(window);
        }

        return new PinnedImage(window, this);
    }

    // ── 批量操作 ────────────────────────────────────────────────────

    /// <summary>隐藏全部钉图。对应 Mac: hideAll（:96-98）。</summary>
    public void HideAll()
    {
        var host = Host;
        host?.HideAllAsync().GetAwaiter().GetResult();
    }

    /// <summary>显示全部钉图。对应 Mac: showAll（:100-102）。</summary>
    public void ShowAll()
    {
        var host = Host;
        host?.ShowAllAsync().GetAwaiter().GetResult();
    }

    /// <summary>恢复全部钉图的鼠标交互。对应 Mac: enableInteractionForAll（:104-106）。</summary>
    public void EnableInteractionForAll()
    {
        var host = Host;
        host?.EnableInteractionForAllAsync().GetAwaiter().GetResult();
    }

    /// <summary>关闭全部钉图。</summary>
    public void CloseAll()
    {
        var host = Host;
        host?.CloseAllAsync().GetAwaiter().GetResult();
    }

    private PinWindowHost? Host
    {
        get
        {
            lock (_gate)
            {
                return _host;
            }
        }
    }

    private PinWindowHost EnsureHost()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_host is not null)
            {
                return _host;
            }

            var host = new PinWindowHost(_options);
            host.Start();
            if (!_hostEventsHooked)
            {
                host.WindowDestroyed += OnWindowDestroyed;
                _hostEventsHooked = true;
            }

            _host = host;
            return host;
        }
    }

    private void OnWindowDestroyed(PinnedImageWindow window)
    {
        lock (_gate)
        {
            _records.Remove(window);

            // Mac 的 onClose 把 sourceImage 与 frame 一起存进 closedPins（:174-181）。
            _closed.Add(new ClosedPin(window.Source, window.State.ContentSize, window.State.Origin));
            while (_closed.Count > MaxClosedPins)
            {
                _closed.RemoveAt(0);
            }
        }
    }

    internal void Close(PinnedImage pin)
    {
        var host = Host;
        host?.InvokeAsync(() => FindWindow(pin.Id)?.Close()).GetAwaiter().GetResult();
    }

    internal void Hide(PinnedImage pin)
    {
        var host = Host;
        host?.InvokeAsync(() => FindWindow(pin.Id)?.Hide()).GetAwaiter().GetResult();
    }

    internal void Show(PinnedImage pin)
    {
        var host = Host;
        host?.InvokeAsync(() => FindWindow(pin.Id)?.Show()).GetAwaiter().GetResult();
    }

    private PinnedImageWindow? FindWindow(Guid id)
    {
        lock (_gate)
        {
            return _records.FirstOrDefault(record => record.Id == id);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        PinWindowHost? host;
        lock (_gate)
        {
            host = _host;
            _host = null;
        }

        host?.Dispose();
    }
}
