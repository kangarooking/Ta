using System.IO;
using System.Runtime.InteropServices;
using Ta.Core.Imaging;
using WinRT;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Ta.Capture.WinRt;

/// <summary>捕获目标：整屏（HMONITOR）或单窗（HWND）。</summary>
internal readonly record struct CaptureTarget(IntPtr Handle, bool IsWindow);

/// <summary>
/// Windows.Graphics.Capture 一次性抓帧器。
///
/// ⚠️ **这是本移植里最「不自然」的一处**。Mac 版有
/// <c>SCScreenshotManager.captureImage</c>（ScreenCaptureService.swift:127）这种一次性静态截图 API；
/// Windows.Graphics.Capture **没有**，它只提供流式帧池。因此「冻结整屏」必须按
/// 参考文档 §14 风险 #1 描述的近似方案实现：
/// <code>
/// 起帧池 → StartCapture → 等一帧 → 拷到 staging texture → Map 读回 → 全部拆除
/// </code>
/// 代价是 1–2 帧（约 16–33 ms）延迟，「快捷键时刻冻结帧」这个不变量只能**近似**复现。
/// 已在多处注释标明这是已知取舍，不是实现缺陷。
///
/// ── 实现路线（2026-09 定案）─────────────────────────────────────────
/// WinRT 侧调用**全部走官方托管投影**（Microsoft.Windows.SDK.NET.Ref，CsWinRT 生成）：
/// GraphicsCaptureItem、Direct3D11CaptureFramePool.CreateFreeThreaded、CreateCaptureSession、
/// IsCursorCaptureEnabled / IsBorderRequired、StartCapture、TryGetNextFrame、Close。
/// 此前手写 vtable 直调在 CreateCaptureSession 处返回垃圾指针（进程 AccessViolation），
/// 按 winmd 修正 slot 后仍连环出错 —— WinRT ABI 的复杂度不该由本工程承担。
///
/// 只保留三座**已验证工作**的原生 COM 桥（本文件内）：
/// 1. D3D11CreateDevice + CreateDirect3D11DeviceFromDXGIDevice（设备创建）；
/// 2. IGraphicsCaptureItemInterop::CreateForMonitor / CreateForWindow（句柄 → 捕获项）；
/// 3. IDirect3DDxgiInterfaceAccess::GetInterface（WinRT Surface → ID3D11Texture2D）。
/// 三者槽位都是 IUnknown/固定 ABI（0-4），无 IInspectable 演化风险。
///
/// 关键取舍说明：
/// - 用 <c>CreateFreeThreaded</c> 而非 <c>Create</c>：后者的 FrameArrived 绑到 DispatcherQueue，
///   而一次性截图没有消息循环，等不到帧。
/// - 不订阅 FrameArrived 事件，改用轮询 TryGetNextFrame：少一个事件反注册的竞态，
///   且不需要为一次性用途维护事件源。
/// - 请求 B8G8R8A8 格式：WGC 原生输出就是 BGRA8，与 DXGI_FORMAT_B8G8R8A8_UNORM 数值相同，
///   staging texture 可同格式直接 CopyResource，全程无格式转换。
/// </summary>
internal sealed class GraphicsCaptureGrabber : IDisposable
{
    private IntPtr _device;              // ID3D11Device（原生）
    private IntPtr _context;             // ID3D11DeviceContext（原生）
    private IntPtr _direct3DDeviceAbi;   // WinRT IDirect3DDevice 的 ABI 指针（进程生命周期）
    private IDirect3DDevice? _d3dDevice; // 托管包装（交给投影 API）
    private bool _disposed;

    public GraphicsCaptureGrabber()
    {
        ComInterop.EnsureApartmentInitialized();
    }

    /// <summary>D3D11 设备是否已就绪。供真机探针查询。</summary>
    public bool IsDeviceReady => _device != IntPtr.Zero && _d3dDevice is not null;

    /// <summary>
    /// 当前环境是否支持 Windows.Graphics.Capture。
    /// 对应 Mac 的 <c>CGPreflightScreenCaptureAccess</c>（参考文档 §13 对照表）——
    /// 语义不完全等同：Mac 问的是 TCC 权限，Windows 问的是 WGC 可用性
    /// （Win10 1903+ 且有可用的图形栈）。任何异常一律视为「不支持」。
    /// </summary>
    public static bool IsSupported()
    {
        try
        {
            ComInterop.EnsureApartmentInitialized();
            return GraphicsCaptureSession.IsSupported();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 建立可复用的 D3D11 设备 + WinRT IDirect3DDevice。
    ///
    /// 设备创建一次长期复用 —— 每次截图都建设备会显著拖慢首次捕获，
    /// 且 D3D11 设备是进程级重量对象（长截图会连续抓几十帧）。
    /// </summary>
    public void EnsureDevice()
    {
        if (IsDeviceReady)
        {
            return;
        }

        // D3D11CreateDevice 用 featureLevels = null 表示「给最高可用」。
        var hr = ComInterop.D3D11CreateDevice(
            adapter: IntPtr.Zero,
            driverType: WgcConstants.D3DDriverTypeHardware,
            softwareRasterizerModule: IntPtr.Zero,
            flags: WgcConstants.D3D11CreateDeviceBgraSupport,
            featureLevels: IntPtr.Zero,
            featureLevelsCount: 0,
            sdkVersion: WgcConstants.D3D11SdkVersion,
            device: out _device,
            featureLevel: out _,
            immediateContext: out _context);

        if (hr < 0 || _device == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"D3D11CreateDevice 失败，HRESULT=0x{hr:X8}。WGC 需要可用的 D3D11 设备。");
        }

        // 正向桥：IDXGIDevice → WinRT IDirect3DDevice。
        // CreateDirect3D11DeviceFromDXGIDevice 返回的 ABI 指针引用由本类持有
        //（进程生命周期）；FromAbi 只做托管包装，不转移所有权、不重复 AddRef。
        var dxgiDevice = ComInterop.QueryInterface(_device, WgcGuids.DxgiDevice);
        try
        {
            hr = ComInterop.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var direct3DDevice);
            if (hr < 0 || direct3DDevice == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"CreateDirect3D11DeviceFromDXGIDevice 失败，HRESULT=0x{hr:X8}。");
            }

            _direct3DDeviceAbi = direct3DDevice;
            _d3dDevice = MarshalInterface<IDirect3DDevice>.FromAbi(direct3DDevice);
        }
        finally
        {
            ComInterop.Release(dxgiDevice);
        }
    }

    /// <summary>
    /// 抓一帧，返回指定像素尺寸的 RGBA 位图。
    ///
    /// <paramref name="pixelWidth"/>/<paramref name="pixelHeight"/> 就是**输出位图的精确尺寸**。
    /// 帧池按这个尺寸创建，因此输出不会小于请求值，也不会被 WGC 偷偷重采样成别的尺寸 ——
    /// 对应 Mac 的 <c>scalesToFit = false</c>（ScreenCaptureService.swift:171）。
    /// 若请求尺寸与内容原生尺寸不同，WGC 自身必须缩放，这是 WGC 模型的内在行为，
    /// 我们能保证的是「输出尺寸精确」而非「输出绝不缩放」。
    /// </summary>
    /// <summary>
    /// 抓取入口。整条 WGC 流程必须在 MTA 公寓上执行（见 <see cref="ComInterop.RunOnMtaThread"/>），
    /// 调用方线程（UI / testhost）可能是 STA，因此统一丢到专属 MTA 线程上跑。
    /// </summary>
    public RgbaBitmap Grab(CaptureTarget target, int pixelWidth, int pixelHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RgbaBitmap result = null!;
        ComInterop.RunOnMtaThread(() => result = GrabCore(target, pixelWidth, pixelHeight));
        return result;
    }

    internal static void Diag(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ta", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "ta-capture-diag.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // 诊断日志绝不影响主流程。
        }
    }

    private RgbaBitmap GrabCore(CaptureTarget target, int pixelWidth, int pixelHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureDevice();

        // 句柄 → GraphicsCaptureItem：原生 interop 桥（已验证）+ 托管包装。
        var itemAbi = CreateCaptureItemAbi(target);
        var item = MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemAbi);

        var size = new global::Windows.Graphics.SizeInt32
        {
            Width = pixelWidth,
            Height = pixelHeight,
        };

        Direct3D11CaptureFramePool? pool = null;
        GraphicsCaptureSession? session = null;
        try
        {
            // ── WinRT 主链：全部走官方托管投影，ABI 由微软的 CsWinRT 保证 ──
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _d3dDevice!,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                WgcConstants.FramePoolBufferCount,
                size);

            session = pool.CreateCaptureSession(item);

            // 关光标（对应 Mac 恒传 showsCursor = false）。
            // 老系统没有 Session2 时投影会抛 —— 降级继续。
            try { session.IsCursorCaptureEnabled = false; } catch { /* 老系统无 Session2 */ }

            session.StartCapture();

            var frame = WaitForFrame(pool);
            if (frame is null)
            {
                throw new InvalidOperationException(
                    $"等待捕获帧超时（{WgcConstants.FrameWaitTimeoutMs} ms）。");
            }

            using (frame)
            {
                // 反向桥：托管 IDirect3DSurface → ABI 指针 → ID3D11Texture2D → CPU 位图。
                var surfaceAbi = MarshalInterface<IDirect3DSurface>.FromManaged(frame.Surface);
                try
                {
                    var texture = GetFrameTexture(surfaceAbi);
                    try
                    {
                        return ReadTexture(texture, pixelWidth, pixelHeight);
                    }
                    finally
                    {
                        ReleaseTexture(texture);
                    }
                }
                finally
                {
                    Marshal.Release(surfaceAbi);
                }
            }
        }
        finally
        {
            // 拆除顺序：会话 → 帧池。CsWinRT 把 IClosable::Close 投影为 Dispose
            //（Dispose 即同步停流断开缓冲队列），所以只调 Dispose。
            // free-threaded 帧池在会话停止后仍可能被 OS 捕获管线填充缓冲；
            // 只靠 Release 引用计数拆除，若系统侧还持有引用，帧到达回调就撞上
            // 已释放的池 —— 这正是真机测试间歇性收尾崩溃的最可疑来源。
            try { session?.Dispose(); } catch { /* 已释放 */ }

            try { pool?.Dispose(); } catch { /* 已释放 */ }

            // 停流是同步调用，但 OS 捕获管线（win32k 侧的合成进程）停止向池
            // 投递帧是异步完成的。留一拍缓冲给管线时间完成停流。
            Thread.Sleep(2);
            GC.KeepAlive(item);
            GC.KeepAlive(_d3dDevice);
        }
    }

    /// <summary>
    /// 轮询等第一帧。
    /// <para>
    /// TryGetNextFrame 在还没有帧时返回 null，所以必须轮询而非只调一次。
    /// 另外它会**消费**帧（返回最新一帧并释放更早的），轮询本身不会漏帧。
    /// </para>
    /// </summary>
    private static Direct3D11CaptureFrame? WaitForFrame(Direct3D11CaptureFramePool pool)
    {
        var deadline = Environment.TickCount64 + WgcConstants.FrameWaitTimeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            var frame = pool.TryGetNextFrame();
            if (frame is not null)
            {
                return frame;
            }

            Thread.Sleep(WgcConstants.FramePollIntervalMs);
        }

        return null;
    }

    /// <summary>
    /// 用 interop 工厂创建 GraphicsCaptureItem（返回 ABI 指针，引用归调用方）。
    /// 对应 Mac 的 <c>SCContentFilter(display:)</c> / <c>SCContentFilter(desktopIndependentWindow:)</c>
    /// （ScreenCaptureService.swift:114、:145）。
    /// </summary>
    private static IntPtr CreateCaptureItemAbi(CaptureTarget target)
    {
        // ⚠️ 参照官方样例（Windows.Graphics.Capture 示例的 simplecapture.cpp）：
        // 把 IGraphicsCaptureItemInterop 的 IID **直接**传给 RoGetActivationFactory。
        // 拿 IActivationFactory 再 QueryInterface 会得到 E_NOINTERFACE ——
        // interop 接口只挂在 RoGetActivationFactory 直取的对象上。
        var interop = ComInterop.GetActivationFactory(
            "Windows.Graphics.Capture.GraphicsCaptureItem", WgcGuids.GraphicsCaptureItemInterop);

        try
        {
            // ⚠️ IGraphicsCaptureItemInterop 继承自 IUnknown（非 IInspectable），
            // 槽位是 0-2 IUnknown + 3 CreateForWindow + 4 CreateForMonitor。
            // 原实现按 IInspectable 布局取 6/7，读到了 vtable 之外的垃圾指针。
            var iid = WgcGuids.GraphicsCaptureItem;
            var hr = target.IsWindow
                ? ComInterop.Method<ComInterop.CreateForWindowFn>(interop, 3)(interop, target.Handle, ref iid, out var item)
                : ComInterop.Method<ComInterop.CreateForMonitorFn>(interop, 4)(interop, target.Handle, ref iid, out item);

            if (hr < 0 || item == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"CreateFor{(target.IsWindow ? "Window" : "Monitor")} 失败，HRESULT=0x{hr:X8}。"
                    + "常见原因是目标窗口已销毁，或目标属于更高完整性级别的进程（UAC 提权）。");
            }

            return item;
        }
        finally
        {
            ComInterop.Release(interop);
        }
    }

    /// <summary>
    /// 反向桥：WinRT IDirect3DSurface（ABI 指针）→ ID3D11Texture2D。
    /// 对应 Mac 侧 <c>CGImage.cropping(to:)</c> 之前必须先拿到像素缓冲
    /// （ScreenCaptureService.swift:62）—— 两个平台都得先把 GPU 纹理变成可读的东西。
    /// </summary>
    private static IntPtr GetFrameTexture(IntPtr surfaceAbi)
    {
        var access = ComInterop.QueryInterface(surfaceAbi, WgcGuids.DxgiInterfaceAccess);
        try
        {
            var getInterface = ComInterop.Method<ComInterop.GetInterfaceFn>(access, 3);
            var iid = WgcGuids.D3D11Texture2D;
            var hr = getInterface(access, ref iid, out var texture);

            if (hr < 0 || texture == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"IDirect3DDxgiInterfaceAccess.GetInterface(ID3D11Texture2D) 失败，HRESULT=0x{hr:X8}。");
            }

            return texture;
        }
        finally
        {
            ComInterop.Release(access);
        }
    }

    /// <summary>
    /// 把 GPU 纹理读回 CPU 并转成 RGBA。
    ///
    /// 三步：建 staging texture（CPU 可读）→ CopyResource → Map 后逐行拷贝。
    /// 必须用 staging texture，因为 WGC 的帧纹理是 DEFAULT usage，GPU 端 Map 不被允许。
    ///
    /// ⚠️ 行距对齐：D3D11 的 <c>RowPitch</c> 通常大于 width×4（按 256 字节对齐），
    /// 所以逐行拷贝时源步长必须用 RowPitch，目标步长用 RgbaBitmap.Stride。
    /// </summary>
    private unsafe RgbaBitmap ReadTexture(IntPtr texture, int pixelWidth, int pixelHeight)
    {
        var desc = new D3D11Texture2DDesc
        {
            Width = (uint)pixelWidth,
            Height = (uint)pixelHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = WgcConstants.DxgiFormatB8G8R8A8Unorm,
            Count = 1,
            Quality = 0,
            Usage = WgcConstants.D3D11UsageStaging,
            BindFlags = 0,
            CPUAccessFlags = WgcConstants.D3D11CpuAccessRead,
            MiscFlags = 0,
        };

        // ID3D11Device::CreateTexture2D 的 vtable 槽位是 5
        //（winmd 方法序 idx 2 + IUnknown 3；0-2 IUnknown、3 CreateBuffer、4 CreateTexture1D）。
        var createTexture = ComInterop.Method<ComInterop.CreateTexture2DFn>(_device, 5);
        var hr = createTexture(_device, in desc, IntPtr.Zero, out var staging);
        if (hr < 0 || staging == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"CreateTexture2D(staging) 失败，HRESULT=0x{hr:X8}。");
        }

        try
        {
            // winmd 定案（ID3D11DeviceContext idx + IUnknown 3 + DeviceChild 4）：
            // CopyResource idx 40 → slot 47。曾误写 43。
            var copyResource = ComInterop.Method<ComInterop.CopyResourceFn>(_context, 47);
            ComInterop.Ensure(copyResource(_context, staging, texture), "CopyResource");

            // winmd 定案：Map idx 7 → slot 14；Unmap idx 8 → slot 15。曾误写 10/11。
            var map = ComInterop.Method<ComInterop.MapFn>(_context, 14);
            hr = map(_context, staging, 0, WgcConstants.D3D11MapRead, 0, out var mapped);
            if (hr < 0 || mapped.Data == IntPtr.Zero)
            {
                throw new InvalidOperationException($"Map(staging) 失败，HRESULT=0x{hr:X8}。");
            }

            try
            {
                var bitmap = new RgbaBitmap(pixelWidth, pixelHeight);
                CopyBgraToRgba(bitmap, mapped.Data, (int)mapped.RowPitch);
                return bitmap;
            }
            finally
            {
                var unmap = ComInterop.Method<ComInterop.UnmapFn>(_context, 15);
                unmap(_context, staging, 0);
            }
        }
        finally
        {
            ReleaseTexture(staging);
        }
    }

    private static void ReleaseTexture(IntPtr texture)
    {
        if (texture != IntPtr.Zero)
        {
            ComInterop.Release(texture);
        }
    }

    /// <summary>
    /// BGRA → RGBA 逐行翻转通道。
    /// <para>
    /// RgbaBitmap 的字节序契约是 R,G,B,A 且**未预乘**（见 RgbaBitmap 类型注释），
    /// 与 Mac 版 CGImage 的 premultipliedLast 一致。WGC 输出 B8G8R8A8_UNORM 是
    /// 直通 alpha（截屏内容不透明，alpha 恒为 255），因此只需交换 R/B，无需反预乘。
    /// </para>
    /// </summary>
    private static unsafe void CopyBgraToRgba(RgbaBitmap bitmap, IntPtr source, int sourceRowPitch)
    {
        var destinationStride = bitmap.Stride;
        var destination = bitmap.Pixels;

        for (var y = 0; y < bitmap.Height; y++)
        {
            var sourceRow = (byte*)source + (y * sourceRowPitch);
            var destinationRow = y * destinationStride;

            for (var x = 0; x < bitmap.Width; x++)
            {
                var s = x * 4;
                var d = destinationRow + s;
                destination[d] = sourceRow[s + 2];      // R ← B
                destination[d + 1] = sourceRow[s + 1];  // G ← G
                destination[d + 2] = sourceRow[s];      // B ← R
                destination[d + 3] = 255;               // A 恒 255
                // ⚠️ 不抄 WGC 的 alpha：DWM 合成输出的 alpha 是内部细节
                //（实测部分区域 0、部分 200+，与可见性无关）。截图的语义是
                // 不透明图像 —— 与 Mac 版 CGImage 截屏行为一致。
            }
        }
    }

    private static void Release(ref IntPtr instance)
    {
        if (instance != IntPtr.Zero)
        {
            ComInterop.Release(instance);
            instance = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _d3dDevice = null;
        Release(ref _direct3DDeviceAbi);
        Release(ref _context);
        Release(ref _device);
    }
}
