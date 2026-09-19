using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Ta.Core.Imaging;
using Ta.Pinning.Interop;

namespace Ta.Pinning;

/// <summary>
/// 钉图窗口宿主 —— 所有钉图窗口共用的 STA 线程与消息循环。
///
/// 结构照抄已跑通的 <c>Ta.Platform/Overlay/SelectionOverlay</c>：
/// 独立 STA 线程 + 自己的消息循环，调用方线程不被阻塞，也不依赖调用方有没有消息泵。
/// 与覆盖层的两点差异：
///   1. 覆盖层是单实例、一次性；钉图要**同时存在多个**并各自响应菜单命令，
///      因此用 <see cref="_windows"/> 按句柄反查，而非 ThreadStatic 单实例。
///   2. 额外维护一个唤醒事件，让跨线程的创建/操作请求**立即**被处理，
///      而不是等最多 100ms 的消息循环空转。
///
/// ⚠️ 本类与 <see cref="PinnedImageWindow"/> 的全部成员都只在宿主线程上被调用，
/// 唯一例外是 <see cref="Post"/> / <see cref="InvokeAsync"/>（线程安全入队）。
/// </summary>
internal sealed class PinWindowHost : IDisposable
{
    private const string HostClassName = "TaPinningHostWindow";

    private static readonly object ClassLock = new();
    private static PinInterop.WndProcDelegate? _hostWndProc;
    private static PinInterop.WndProcDelegate? _pinWndProc;
    private static bool _classesRegistered;

    /// <summary>窗口过程运行在宿主线程，用 ThreadStatic 反查当前宿主。</summary>
    [ThreadStatic]
    private static PinWindowHost? _current;

    private readonly PinnedImageOptions _options;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new(false);
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly ConcurrentDictionary<IntPtr, PinnedImageWindow> _windows = new();
    private readonly IntPtr _wakeEvent;
    private readonly IntPtr[] _waitHandles;

    private IntPtr _hostWindow;
    private IntPtr _accelerator;
    private bool _stopping;
    private bool _disposed;
    private Exception? _startupError;

    internal PinWindowHost(PinnedImageOptions options)
    {
        _options = options;
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "Ta.Pinning", };
        _thread.SetApartmentState(ApartmentState.STA);

        _wakeEvent = PinInterop.CreateEvent(IntPtr.Zero, true, false, null);
        _waitHandles = new[] { _wakeEvent };
    }

    /// <summary>窗口被销毁（用户关闭或主动 Close）时触发，供控制器记账。</summary>
    internal event Action<PinnedImageWindow>? WindowDestroyed;

    /// <summary>当前存活的钉图窗口数。</summary>
    internal int WindowCount => _windows.Count;

    /// <summary>
    /// 宿主窗口句柄。不显示，但必须存在 —— <c>OpenClipboard</c> 需要
    /// 「拥有消息循环的窗口」作属主（见 RunMessageLoop 内注释）。
    /// </summary>
    internal IntPtr HostWindow => _hostWindow;

    // ── 线程编排 ────────────────────────────────────────────────────

    internal void Start()
    {
        _thread.Start();
        _started.Wait();

        if (_startupError is not null)
        {
            throw new InvalidOperationException("钉图宿主线程启动失败。", _startupError);
        }
    }

    /// <summary>投递一个动作到宿主线程执行，不等待结果。</summary>
    internal void Post(Action action)
    {
        _queue.Enqueue(action);
        PinInterop.SetEvent(_wakeEvent);
    }

    /// <summary>在宿主线程上执行并异步等待结果。</summary>
    internal Task<T> InvokeAsync<T>(Func<T> function)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                completion.TrySetResult(function());
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        });

        return completion.Task;
    }

    internal Task InvokeAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                action();
                completion.TrySetResult();
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        });

        return completion.Task;
    }

    private void ThreadMain()
    {
        _current = this;

        try
        {
            if (_options.EnablePerMonitorDpi)
            {
                // per-monitor-v2：多屏不同缩放时坐标才不会被系统虚拟化。
                try
                {
                    PinInterop.SetProcessDpiAwarenessContext(
                        PinInterop.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
                }
                catch (DllNotFoundException)
                {
                    // Win10 1607 之前没有这个 API。
                }
                catch (EntryPointNotFoundException)
                {
                    // 同上。
                }
            }

            RunMessageLoop();
        }
        catch (Exception error)
        {
            _startupError = error;
        }
        finally
        {
            _current = null;
            _started.Set();
        }
    }

    private void RunMessageLoop()
    {
        EnsureClassesRegistered();

        // 宿主窗口不显示，只为两件事存在：
        //   1. OpenClipboard 需要「拥有消息循环的窗口」作属主
        //   2. 作为未激活窗口上加速键/消息的落脚点
        _hostWindow = PinInterop.CreateWindowEx(
            PinInterop.WS_EX_TOOLWINDOW,
            HostClassName,
            "Ta Pinning Host",
            PinInterop.WS_POPUP,
            0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, PinInterop.GetModuleHandle(null), IntPtr.Zero);

        if (_hostWindow == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"宿主窗口创建失败，Win32 错误 {Marshal.GetLastWin32Error()}");
        }

        _accelerator = CreateAcceleratorTable();
        _started.Set();

        while (!_stopping)
        {
            DrainQueue();

            // 有新消息或队列有新项就醒；100ms 兜底防止漏信号。dwFlags 传 0（无 ALERTABLE）。
            PinInterop.MsgWaitForMultipleObjectsEx(
                1, _waitHandles, bWaitAll: false, 100, PinInterop.QS_ALLINPUT, dwFlags: 0);

            PinInterop.ResetEvent(_wakeEvent);
            DrainQueue();

            while (PinInterop.PeekMessage(out var message, IntPtr.Zero, 0, 0, PinInterop.PM_REMOVE))
            {
                if (message.message == PinInterop.WM_QUIT)
                {
                    _stopping = true;
                    break;
                }

                // 加速键优先：键等价（c/[/]/0/w）只有在窗口处于前台时才可能触发，
                // 与 Mac 的 canBecomeKey = false 表现一致（不可用时静默无效）。
                if (_accelerator == IntPtr.Zero
                    || PinInterop.TranslateAccelerator(message.hwnd, _accelerator, in message) == 0)
                {
                    PinInterop.TranslateMessage(in message);
                    PinInterop.DispatchMessage(in message);
                }

                DrainQueue();
            }
        }

        foreach (var window in _windows.Values.ToArray())
        {
            window.Dispose();
        }

        _windows.Clear();

        if (_accelerator != IntPtr.Zero)
        {
            PinInterop.DestroyAcceleratorTable(_accelerator);
            _accelerator = IntPtr.Zero;
        }

        if (_hostWindow != IntPtr.Zero)
        {
            PinInterop.DestroyWindow(_hostWindow);
            _hostWindow = IntPtr.Zero;
        }

        if (_wakeEvent != IntPtr.Zero)
        {
            PinInterop.CloseHandle(_wakeEvent);
        }
    }

    private void DrainQueue()
    {
        while (_queue.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch
            {
                // 单个动作失败不该炸掉整条消息循环（其余钉图还要继续工作）。
            }
        }
    }

    private static void EnsureClassesRegistered()
    {
        lock (ClassLock)
        {
            if (_classesRegistered)
            {
                return;
            }

            _hostWndProc = HostWndProc;
            _pinWndProc = PinWndProc;

            var hostClass = new PinInterop.WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<PinInterop.WNDCLASSEXW>(),
                style = 0,
                lpfnWndProc = _hostWndProc,
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = PinInterop.GetModuleHandle(null),
                hIcon = IntPtr.Zero,
                hCursor = PinInterop.LoadCursor(IntPtr.Zero, PinInterop.IDC_ARROW),
                hbrBackground = IntPtr.Zero,
                lpszMenuName = null,
                lpszClassName = HostClassName,
                hIconSm = IntPtr.Zero,
            };

            var pinClass = new PinInterop.WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<PinInterop.WNDCLASSEXW>(),
                // CS_DBLCLKS 是双击判定的前提：没有它 Windows 只会发两次 WM_LBUTTONDOWN。
                style = PinInterop.CS_HREDRAW | PinInterop.CS_VREDRAW | PinInterop.CS_DBLCLKS,
                lpfnWndProc = _pinWndProc,
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = PinInterop.GetModuleHandle(null),
                hIcon = IntPtr.Zero,
                hCursor = PinInterop.LoadCursor(IntPtr.Zero, PinInterop.IDC_ARROW),
                hbrBackground = IntPtr.Zero,
                lpszMenuName = null,
                lpszClassName = PinnedImageWindow.ClassName,
                hIconSm = IntPtr.Zero,
            };

            if (PinInterop.RegisterClassEx(in hostClass) == 0
                && Marshal.GetLastWin32Error() != 1410 /*ERROR_CLASS_ALREADY_EXISTS*/)
            {
                throw new InvalidOperationException(
                    $"RegisterClassEx(host) 失败，Win32 错误 {Marshal.GetLastWin32Error()}");
            }

            if (PinInterop.RegisterClassEx(in pinClass) == 0
                && Marshal.GetLastWin32Error() != 1410)
            {
                throw new InvalidOperationException(
                    $"RegisterClassEx(pin) 失败，Win32 错误 {Marshal.GetLastWin32Error()}");
            }

            _classesRegistered = true;
        }
    }

    private static IntPtr HostWndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam) =>
        _current?.HostProc(hwnd, message, wParam, lParam) ?? PinInterop.DefWindowProc(hwnd, message, wParam, lParam);

    private static IntPtr PinWndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam) =>
        _current?.PinProc(hwnd, message, wParam, lParam) ?? PinInterop.DefWindowProc(hwnd, message, wParam, lParam);

    private IntPtr HostProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam) =>
        PinInterop.DefWindowProc(hwnd, message, wParam, lParam);

    private IntPtr PinProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (_windows.TryGetValue(hwnd, out var window))
        {
            return window.WndProc(hwnd, message, wParam, lParam);
        }

        return PinInterop.DefWindowProc(hwnd, message, wParam, lParam);
    }

    /// <summary>窗口过程里 WM_DESTROY 时由窗口调用。</summary>
    internal void OnWindowDestroyed(PinnedImageWindow window)
    {
        if (window.Handle != IntPtr.Zero)
        {
            _windows.TryRemove(window.Handle, out _);
        }

        WindowDestroyed?.Invoke(window);
    }

    // ── 钉图窗口的创建与批量操作 ────────────────────────────────────

    internal Task<PinnedImageWindow> CreateWindowAsync(RgbaBitmap image, PinSizeD size, PinPointD origin) =>
        InvokeAsync(() =>
        {
            var state = new PinViewState(new RgbaBitmapSource(image.Width, image.Height), size, origin);
            var window = new PinnedImageWindow(image, state, _options, this);
            window.Create();
            _windows[window.Handle] = window;
            return window;
        });

    internal Task HideAllAsync() => InvokeAsync(() =>
    {
        // 对应 Mac: hideAll（:96-98）
        foreach (var window in _windows.Values)
        {
            window.Hide();
        }
    });

    internal Task ShowAllAsync() => InvokeAsync(() =>
    {
        // 对应 Mac: showAll（:100-102）—— orderFrontRegardless
        foreach (var window in _windows.Values)
        {
            window.Show();
        }
    });

    internal Task EnableInteractionForAllAsync() => InvokeAsync(() =>
    {
        // 对应 Mac: enableInteractionForAll（:104-106）
        foreach (var window in _windows.Values)
        {
            window.SetClickThrough(false);
        }
    });

    internal Task CloseAllAsync() => InvokeAsync(() =>
    {
        foreach (var window in _windows.Values.ToArray())
        {
            window.Close();
        }
    });

    // ── 菜单命令里的「组」操作 ──────────────────────────────────────

    /// <summary>把当前所有可见钉图编成一组。对应 Mac: groupVisiblePins(containing:)（:191-196）。</summary>
    internal void GroupVisiblePins(PinnedImageWindow source)
    {
        var groupId = Guid.NewGuid();
        foreach (var window in _windows.Values)
        {
            if (window.IsVisible)
            {
                window.GroupId = groupId;
            }
        }

        _ = source;
    }

    /// <summary>隐藏与指定钉图同组的全部钉图。对应 Mac: hideGroup(containing:)（:198-201）。</summary>
    internal void HideGroup(PinnedImageWindow source)
    {
        if (source.GroupId is not { } groupId)
        {
            return;
        }

        foreach (var window in _windows.Values)
        {
            if (window.GroupId == groupId)
            {
                window.Hide();
            }
        }
    }

    /// <summary>
    /// 把钉图当前（裁剪后）的图像写进剪贴板。对应 Mac 的 onCopy（:166-172）。
    /// 必须在宿主线程调用 —— OpenClipboard 的属主窗口在这里。
    /// </summary>
    internal void CopyImageToClipboard(PinnedImageWindow window)
    {
        var rect = window.State.EffectiveImageRect;
        var image = window.Source;

        RgbaBitmap payload;
        if (rect.Left == 0 && rect.Top == 0 && rect.Width == image.Width && rect.Height == image.Height)
        {
            payload = image;
        }
        else
        {
            payload = image.Crop(rect.Left, rect.Top, rect.Width, rect.Height);
        }

        try
        {
            // Mac 写 public.png；Windows 上写 "PNG" + CF_DIB。
            PinClipboardWriter.WriteImage(_hostWindow, payload);
        }
        finally
        {
            if (!ReferenceEquals(payload, image))
            {
                payload.Dispose();
            }
        }
    }

    // ── 诊断入口（供自动回归使用） ──────────────────────────────────

    /// <summary>
    /// 在宿主线程上为指定钉图建一次右键菜单并**立即销毁**。
    /// 用于自动回归验证菜单真的建对了（项数 / 标题 / 勾选态），
    /// 而不必把 UI 线程阻塞在 TrackPopupMenuEx 上。
    /// </summary>
    internal Task<IntPtr> BuildMenuAsync(Guid id) => InvokeAsync(() =>
    {
        var window = _windows.Values.FirstOrDefault(candidate => candidate.Id == id);
        return window?.BuildMenu() ?? IntPtr.Zero;
    });

    // ── 加速键 ──────────────────────────────────────────────────────

    /// <summary>
    /// 把 Mac 的 NSMenuItem.keyEquivalent 翻成 Win32 加速键。
    /// Mac 是**裸字母**（不带修饰键），这里保持一致；窗口不在前台时加速键自然无效。
    /// </summary>
    private IntPtr CreateAcceleratorTable()
    {
        var entries = new List<PinInterop.ACCEL>();
        foreach (var item in PinMenuSpec.Items)
        {
            var key = KeyEquivalentToVirtualKey(item.KeyEquivalent);
            if (key == 0)
            {
                continue;
            }

            entries.Add(new PinInterop.ACCEL(
                PinInterop.FVIRTKEY | PinInterop.FNOINVERT,
                key,
                (ushort)(PinMenuSpec.CommandIdBase + (int)item.Command)));
        }

        return entries.Count == 0
            ? IntPtr.Zero
            : PinInterop.CreateAcceleratorTable(entries.ToArray(), entries.Count);
    }

    private static ushort KeyEquivalentToVirtualKey(string keyEquivalent) => keyEquivalent switch
    {
        "c" => PinInterop.VK_C,
        "w" => PinInterop.VK_W,
        "0" => PinInterop.VK_0,
        "[" => PinInterop.VK_OEM_4,
        "]" => PinInterop.VK_OEM_6,
        _ => 0,
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_hostWindow != IntPtr.Zero)
        {
            PinInterop.PostMessage(_hostWindow, PinInterop.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        _started.Dispose();
    }
}
