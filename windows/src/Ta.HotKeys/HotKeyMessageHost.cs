namespace Ta.HotKeys;

/// <summary>
/// 收 <c>WM_HOTKEY</c> 的消息宿主窗口 + 消息泵。
///
/// 对应 macOS 侧 <c>InstallEventHandler(GetEventDispatcherTarget(), …, kEventClassKeyboard,
/// kEventHotKeyPressed)</c>（GlobalHotKeyManager.swift:53-91）—— Windows 没有全局事件处理器，
/// 必须自建一个属于注册线程的窗口来收消息。
///
/// 窗口创建为 <c>HWND_MESSAGE</c> 子窗口（消息专属窗口）：不可见、不进任务栏、不参与 Alt+Tab，
/// 与「只想要热键、不想要界面」的诉求匹配。
/// </summary>
public sealed class HotKeyMessageHost : IDisposable
{
    // 窗口过程委托必须长期持有，否则 GC 回收后 native 侧函数指针悬空。
    private static readonly HotKeyInterop.WndProcDelegate StaticWndProc = HandleMessage;

    private static readonly object ClassLock = new();
    private static bool _classRegistered;

    private IntPtr _windowHandle;

    /// <summary>宿主线程 id（诊断用）。</summary>
    public uint HostThreadId { get; }

    /// <summary>窗口句柄；未创建时为 <see cref="IntPtr.Zero"/>。</summary>
    public IntPtr WindowHandle => _windowHandle;

    /// <summary>
    /// 收到 <c>WM_HOTKEY</c> 时触发，参数是 <c>wParam</c>（= <see cref="GlobalHotKeyAction.HotKeyId"/>）。
    /// 在宿主线程上同步触发。
    /// </summary>
    public event EventHandler<int>? HotKeyPressed;

    public HotKeyMessageHost()
    {
        HostThreadId = HotKeyInterop.GetCurrentThreadId();
        _windowHandle = CreateWindow();
        HotKeyPressedDispatcher.Attach(this);
    }

    /// <summary>窗口是否仍在（未 Dispose、未收到 WM_DESTROY）。</summary>
    public bool IsAlive => _windowHandle != IntPtr.Zero;

    /// <summary>
    /// 非阻塞地抽干消息队列，最多等 <paramref name="timeoutMilliseconds"/>。
    /// 期间收到 <c>WM_HOTKEY</c> 立即返回 true。
    /// </summary>
    public bool PumpPending(int timeoutMilliseconds = 100)
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return false;
        }

        var deadline = Environment.TickCount64 + Math.Max(0, timeoutMilliseconds);
        var sawHotKey = false;

        while (Environment.TickCount64 <= deadline)
        {
            // 先等一小段，避免空转烧 CPU；有任意输入立即返回。
            HotKeyInterop.MsgWaitForMultipleObjects(0, null, false, 25, HotKeyInterop.QS_ALLINPUT);

            while (HotKeyInterop.PeekMessage(out var message, IntPtr.Zero, 0, 0, HotKeyInterop.PM_REMOVE))
            {
                if (message.message == HotKeyInterop.WM_QUIT)
                {
                    return sawHotKey;
                }

                HotKeyInterop.TranslateMessage(in message);
                HotKeyInterop.DispatchMessage(in message);

                if (message.message == HotKeyInterop.WM_HOTKEY)
                {
                    sawHotKey = true;
                }
            }

            if (sawHotKey)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 阻塞式消息循环，直到 <paramref name="cancellationToken"/> 被取消。
    /// 生产路径使用；测试请用 <see cref="PumpPending"/>。
    /// </summary>
    public void Run(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _windowHandle != IntPtr.Zero)
        {
            var result = HotKeyInterop.GetMessage(out var message, IntPtr.Zero, 0, 0);
            if (result <= 0)
            {
                // 0 = WM_QUIT，-1 = 错误
                return;
            }

            HotKeyInterop.TranslateMessage(in message);
            HotKeyInterop.DispatchMessage(in message);
        }
    }

    /// <summary>请求结束 <see cref="Run"/> 的消息循环。</summary>
    public void RequestQuit()
    {
        HotKeyInterop.PostThreadMessage(HostThreadId, HotKeyInterop.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _windowHandle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            HotKeyPressedDispatcher.Detach(this);
            HotKeyInterop.DestroyWindow(handle);
        }

        GC.SuppressFinalize(this);
    }

    private static IntPtr CreateWindow()
    {
        lock (ClassLock)
        {
            var module = HotKeyInterop.GetModuleHandle(null);
            if (!_classRegistered)
            {
                var windowClass = new HotKeyInterop.WNDCLASSEXW
                {
                    cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<HotKeyInterop.WNDCLASSEXW>(),
                    style = 0,
                    lpfnWndProc = StaticWndProc,
                    cbClsExtra = 0,
                    cbWndExtra = 0,
                    hInstance = module,
                    hIcon = IntPtr.Zero,
                    hCursor = IntPtr.Zero,
                    hbrBackground = IntPtr.Zero,
                    lpszMenuName = null,
                    lpszClassName = "TaGlobalHotKeyMessageWindow",
                    hIconSm = IntPtr.Zero,
                };

                HotKeyInterop.RegisterClassEx(in windowClass);
                // 类已存在（本进程第二次创建宿主）也算成功，因此不检查返回值。
                _classRegistered = true;
            }

            // dwStyle 传 0：消息专属窗口不需要任何样式位。
            var handle = HotKeyInterop.CreateWindowEx(
                0,
                "TaGlobalHotKeyMessageWindow",
                "Ta HotKeys",
                0,
                0, 0, 0, 0,
                HotKeyInterop.HWND_MESSAGE,
                IntPtr.Zero,
                module,
                IntPtr.Zero);

            return handle;
        }
    }

    private static IntPtr HandleMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == HotKeyInterop.WM_HOTKEY)
        {
            var id = (int)wParam.ToInt64();
            // 事件同步在窗口线程触发：注册线程就是消息线程，语义与 Mac 的
            // Task { @MainActor in manager.invoke(id:) } 一致。
            HotKeyPressedDispatcher.Dispatch(hWnd, id);
            return IntPtr.Zero;
        }

        if (msg == HotKeyInterop.WM_DESTROY)
        {
            return IntPtr.Zero;
        }

        return HotKeyInterop.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// 静态窗口过程拿不到 this，用一张句柄 → 宿主的表转发事件。
    /// 键用 <see cref="GCHandle"/> 避免窗口句柄被复用后打到错的实例上。
    /// </summary>
    internal static class HotKeyPressedDispatcher
    {
        private static readonly Dictionary<IntPtr, WeakReference<HotKeyMessageHost>> Hosts = new();

        internal static void Attach(HotKeyMessageHost host)
        {
            lock (Hosts)
            {
                if (host.WindowHandle != IntPtr.Zero)
                {
                    Hosts[host.WindowHandle] = new WeakReference<HotKeyMessageHost>(host);
                }
            }
        }

        internal static void Detach(HotKeyMessageHost host)
        {
            lock (Hosts)
            {
                if (host.WindowHandle != IntPtr.Zero)
                {
                    Hosts.Remove(host.WindowHandle);
                }
            }
        }

        internal static void Dispatch(IntPtr hWnd, int id)
        {
            HotKeyMessageHost? host = null;
            lock (Hosts)
            {
                if (Hosts.TryGetValue(hWnd, out var weak) && weak.TryGetTarget(out var target))
                {
                    host = target;
                }
            }

            host?.RaiseHotKeyPressed(id);
        }
    }

    private void RaiseHotKeyPressed(int id) => HotKeyPressed?.Invoke(this, id);
}
