using System.Runtime.InteropServices;
using System.Threading;

namespace Ta.Capture.WinRt;

/// <summary>
/// COM vtable 直调基础设施。
///
/// 为什么手写而不引 Microsoft.Windows.SDK.NET / CsWinRT：
/// 1. 那两个包体积大（含整套 winmd 投影），且要求把 TFM 改成 <c>net8.0-windows10.0.xxxxx.0</c>；
/// 2. 捕获层只需要约 10 个接口的十几个方法，手写互操作的代码量可控且**零新增依赖** ——
///    整个解决方案因此不需要任何网络 restore，11 个并行 agent 互相不会因包还原失败而卡住；
/// 3. 接口 GUID 与 vtable 顺序已从官方 winmd 逐条读出（见 <see cref="WgcGuids"/>），
///    唯一靠互操作的「新鲜感」风险点已消除。
///
/// ⚠️ 关键实现约定：**所有 vtable 委托只返回 HRESULT，绝不在委托内部抛异常。**
/// 从 native 回调进托管帧时抛异常会跨非托管帧传播，行为未定义（可能直接终止进程）。
/// 委托返回 int，由托管侧 <see cref="Ensure"/> 统一抛 —— 这也是 CsWinRT 的做法。
/// </summary>
internal static class ComInterop
{
    // ── WinRT 运行时入口（combase.dll）───────────────────────────────

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoInitialize(int initType);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern void RoUninitialize();

    /// <summary>
    /// 参数顺序是 (sourceString, length, out string) —— 原实现写反成
    /// (length, sourceString)，导致 HSTRING 永远创建失败、整个 WinRT 路径不可用。
    /// </summary>
    [DllImport("combase.dll", EntryPoint = "WindowsCreateString", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString, uint length, out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    /// <summary>
    /// 确保当前线程处于 MTA 的 WinRT 公寓。
    /// <para>
    /// WGC 的自由线程帧池要求 MTA；STA 下起池会在帧回调上死锁或直接失败。
    /// RoInitialize 幂等，重复调用返回 S_FALSE(1)，按成功处理。
    /// </para>
    /// </summary>
    public static void EnsureApartmentInitialized()
    {
        var hr = RoInitialize(WgcConstants.RoInitMultithreaded);
        // S_OK = 0，S_FALSE = 1（已初始化），RPC_E_CHANGED_MODE = 0x80010106（已以别的模式初始化）。
        if (hr != 0 && hr != 1 && hr != unchecked((int)0x8001_0106))
        {
            throw Create(hr, "RoInitialize(MTA)");
        }
    }

    /// <summary>创建 HSTRING。用完必须 <see cref="DeleteHString"/>。</summary>
    public static IntPtr CreateHString(string value)
    {
        var hr = WindowsCreateString(value, (uint)value.Length, out var hstring);
        if (hr != 0)
        {
            throw Create(hr, "WindowsCreateString");
        }

        return hstring;
    }

    public static void DeleteHString(IntPtr hstring)
    {
        if (hstring != IntPtr.Zero)
        {
            WindowsDeleteString(hstring);
        }
    }

    /// <summary>
    /// 取 WinRT 运行时类的激活工厂。
    /// <para>
    /// 对于带 <c>[static]</c> 接口的类（如 Direct3D11CaptureFramePool），
    /// 拿到的工厂对象同时实现那些静态接口 —— 这就是
    /// <c>IDirect3D11CaptureFramePoolStatics</c> 的取得方式。
    /// </para>
    /// </summary>
    public static IntPtr GetActivationFactory(string runtimeClassName, Guid factoryInterface)
    {
        var hstring = CreateHString(runtimeClassName);
        try
        {
            var hr = RoGetActivationFactory(hstring, ref factoryInterface, out var factory);
            return hr == 0 ? factory : throw Create(hr, $"RoGetActivationFactory({runtimeClassName})");
        }
        finally
        {
            DeleteHString(hstring);
        }
    }

    /// <summary>
    /// 在**专属 MTA 线程**上执行委托并等待完成。
    ///
    /// RoGetActivationFactory / RoInitialize 对调用线程的公寓状态敏感：
    /// 若当前线程已被初始化为 STA（testhost、WPF UI 线程都很常见），
    /// RoInitialize(MTA) 会返回 RPC_E_CHANGED_MODE 且线程保持 STA，
    /// 此时取 interop 接口会得到 E_NOINTERFACE，帧池也会因非自由线程而不稳。
    /// 因此整条 WGC 流程统一放到专属 MTA 线程上跑。
    /// </summary>
    public static void RunOnMtaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    // ── vtable 直调 ─────────────────────────────────────────────────

    /// <summary>
    /// 取某 COM 对象第 <paramref name="slot"/> 个 vtable 槽位上的委托。
    /// slot 0/1/2 固定是 QueryInterface/AddRef/Release。
    ///
    /// ⚠️ 已知深水区：真机 WGC 测试在 testhost 收尾阶段会以 AccessViolation 崩溃
    /// （无托管栈；dump 定位到 IL_STUB_PInvoke 下的 vtable 直调帧）。A/B 验证与
    /// 帧池 Close/委托缓存均无关，指向某个 WGC interop 调用的签名/ABI 细节
    /// （嫌疑：SizeInt32 按值传参的 WinRT ABI 对齐）。留待后续以 vtable 逐调用
    /// 隔离的方式定位 —— 测试用例本身全部通过，仅进程退出阶段受影响。
    /// </summary>
    public static TDelegate Method<TDelegate>(IntPtr instance, int slot) where TDelegate : Delegate
    {
        if (instance == IntPtr.Zero)
        {
            throw new InvalidOperationException("COM 对象指针为空，可能已被提前释放。");
        }

        var vtable = Marshal.ReadIntPtr(instance, 0);
        var function = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        if (function == IntPtr.Zero)
        {
            throw new InvalidOperationException($"vtable 槽位 {slot} 为空。");
        }

        return Marshal.GetDelegateForFunctionPointer<TDelegate>(function);
    }

    /// <summary>QueryInterface。失败时返回 null（E_NOINTERFACE 是「不支持该接口」的正常信号）。</summary>
    public static IntPtr? TryQueryInterface(IntPtr instance, Guid iid)
    {
        var query = Method<QueryInterfaceFn>(instance, 0);
        var hr = query(instance, ref iid, out var result);
        return hr == 0 && result != IntPtr.Zero ? result : null;
    }

    /// <summary>QueryInterface，失败即抛。</summary>
    public static IntPtr QueryInterface(IntPtr instance, Guid iid)
    {
        var result = TryQueryInterface(instance, iid);
        if (result is null)
        {
            throw new InvalidOperationException($"QueryInterface({iid}) 失败。");
        }

        return result.Value;
    }

    public static int Release(IntPtr instance)
    {
        if (instance == IntPtr.Zero)
        {
            return 0;
        }

        var release = Method<ReleaseFn>(instance, 2);
        return (int)release(instance);
    }

    /// <summary>
    /// HRESULT 检查。**只在托管侧调用**，不要在 vtable 委托内部调用。
    /// </summary>
    public static void Ensure(int hr, string operation)
    {
        if (hr < 0)
        {
            throw Create(hr, operation);
        }
    }

    private static Exception Create(int hr, string operation) =>
        Marshal.GetExceptionForHR(hr) is { } ex
            ? new InvalidOperationException($"{operation} 失败，HRESULT=0x{hr:X8}。", ex)
            : new InvalidOperationException($"{operation} 失败，HRESULT=0x{hr:X8}。");

    // ── 委托签名 ────────────────────────────────────────────────────
    // 全部用 StdCall + 返回 int(HRESULT)，与 COM/WinRT ABI 一致。

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int QueryInterfaceFn(IntPtr instance, ref Guid iid, out IntPtr result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate uint ReleaseFn(IntPtr instance);

    /// <summary>IActivationFactory::ActivateInstance。slot 3。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int ActivateInstanceFn(IntPtr instance, out IntPtr inspectable);

    /// <summary>
    /// IGraphicsCaptureItemInterop::CreateForMonitor。slot 6。
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int CreateForMonitorFn(IntPtr instance, IntPtr monitor, ref Guid iid, out IntPtr item);

    /// <summary>IGraphicsCaptureItemInterop::CreateForWindow。slot 7。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int CreateForWindowFn(IntPtr instance, IntPtr window, ref Guid iid, out IntPtr item);

    /// <summary>IInspectable::GetIids。slot 3。用于真机探针：列出对象实现的所有接口。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int GetIidsFn(IntPtr instance, out int count, out IntPtr iids);

    /// <summary>
    /// IDirect3D11CaptureFramePoolStatics::Create / Statics2::CreateFreeThreaded。slot 6。
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    /// <summary>
    /// IDirect3D11CaptureFramePoolStatics(2)::Create/CreateFreeThreaded。
    /// ⚠️ 原生签名是 (device, format, buffers, size, out pool) —— **没有 item 参数**。
    /// 原声明多出的 item 使后续参数全部错位，调用即内存访问越界。
    /// </summary>
    public delegate int FramePoolCreateFn(
        IntPtr instance, IntPtr device, int sizeFormat, int numberOfBuffers,
        SizeInt32 size, out IntPtr pool);

    /// <summary>IDirect3D11CaptureFramePool::Recreate。slot 6。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int FramePoolRecreateFn(
        IntPtr instance, IntPtr device, int sizeFormat, int numberOfBuffers, SizeInt32 size);

    /// <summary>IDirect3D11CaptureFramePool::TryGetNextFrame。slot 7（winmd 方法序第 2 个）。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int TryGetNextFrameFn(IntPtr instance, out IntPtr frame);

    /// <summary>IDirect3D11CaptureFramePool::CreateCaptureSession。slot 10。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int CreateCaptureSessionFn(IntPtr instance, IntPtr item, out IntPtr session);

    /// <summary>IGraphicsCaptureSession::StartCapture。slot 6。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int StartCaptureFn(IntPtr instance);

    /// <summary>put_IsCursorCaptureEnabled / put_IsBorderRequired。均为 slot 7。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int PutBoolFn(IntPtr instance, [MarshalAs(UnmanagedType.U1)] bool value);

    /// <summary>IDirect3D11CaptureFrame::get_Surface。slot 6。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int GetSurfaceFn(IntPtr instance, out IntPtr surface);

    /// <summary>IDirect3D11CaptureFrame::get_ContentSize。slot 8。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int GetContentSizeFn(IntPtr instance, out SizeInt32 size);

    /// <summary>ID3D11Texture2D::GetDesc。slot 7（IUnknown 3 + DeviceChild 4）。C 签名返回 void。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public unsafe delegate void GetTextureDescFn(IntPtr instance, D3D11Texture2DDesc* desc);

    /// <summary>IDirect3DDxgiInterfaceAccess::GetInterface。slot 3。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int GetInterfaceFn(IntPtr instance, ref Guid iid, out IntPtr result);

    /// <summary>IClosable::Close。slot 6。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int CloseFn(IntPtr instance);

    /// <summary>IGraphicsCaptureSessionStatics::IsSupported。slot 6。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    /// <summary>
    /// WinRT 方法一律是 HRESULT + [out, retval] 参数：
    /// <c>HRESULT IsSupported(BOOL* value)</c>。原声明把返回值当 bool 直出、
    /// 缺少 out 指针，调用时 WinRT 会把 BOOL 写进未提供的寄存器垃圾地址 → AV 崩溃。
    /// </summary>
    public delegate int IsSupportedFn(IntPtr instance, out int supported);

    /// <summary>ID3D11Device::CreateTexture2D。slot 5。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    /// <summary>ID3D11Device::CreateTexture2D(desc, pInitData, out texture) —— 三个参数，缺 pInitData 会整体错位。</summary>
    public delegate int CreateTexture2DFn(IntPtr instance, in D3D11Texture2DDesc desc, IntPtr initialData, out IntPtr texture);

    /// <summary>ID3D11Device::GetImmediateContext。slot 40。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int GetImmediateContextFn(IntPtr instance, out IntPtr context);

    /// <summary>ID3D11DeviceContext::Map。slot 10。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int MapFn(
        IntPtr instance, IntPtr resource, uint subresource, int mapType, uint mapFlags,
        out D3D11MappedSubresource mapped);

    /// <summary>ID3D11DeviceContext::Unmap。slot 11。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int UnmapFn(IntPtr instance, IntPtr resource, uint subresource);

    /// <summary>ID3D11DeviceContext::CopyResource。slot 38。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int CopyResourceFn(IntPtr instance, IntPtr destination, IntPtr source);

    /// <summary>
    /// d3d11.dll 的导出 <c>CreateDirect3D11DeviceFromDXGIDevice</c>
    /// （已用 <c>grep -a</c> 在 C:\Windows\System32\d3d11.dll 的导出表里确认存在）。
    /// 这是「IDXGIDevice → WinRT IDirect3DDevice」的正向桥。
    /// </summary>
    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", PreserveSig = true)]
    public static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    /// <summary>ID3D11Device 创建入口（d3d11.dll）。</summary>
    [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice", PreserveSig = true)]
    public static extern int D3D11CreateDevice(
        IntPtr adapter, int driverType, IntPtr softwareRasterizerModule, uint flags,
        IntPtr featureLevels, uint featureLevelsCount, uint sdkVersion,
        out IntPtr device, out int featureLevel, out IntPtr immediateContext);
}

/// <summary>Windows.Foundation.Size 对应的 WinRT 值类型 <c>SizeInt32</c>。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SizeInt32
{
    public int Width;
    public int Height;

    public SizeInt32(int width, int height)
    {
        Width = width;
        Height = height;
    }
}

/// <summary>D3D11_TEXTURE2D_DESC 的读取所需字段。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Texture2DDesc
{
    public uint Width;
    public uint Height;
    public uint MipLevels;
    public uint ArraySize;
    public int Format;
    public uint Count;
    public uint Quality;
    public uint Usage;
    public uint BindFlags;
    public uint CPUAccessFlags;
    public uint MiscFlags;
}

/// <summary>D3D11_MAPPED_SUBRESOURCE。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct D3D11MappedSubresource
{
    public IntPtr Data;
    public uint RowPitch;
    public uint DepthPitch;
}
