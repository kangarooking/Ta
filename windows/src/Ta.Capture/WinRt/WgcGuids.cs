namespace Ta.Capture.WinRt;

/// <summary>
/// WinRT / D3D11 互操作所需的全部 GUID。
///
/// ⚠️ 这些值的来源：全部从 <c>Microsoft.Windows.SDK.NET.Ref</c> 10.0.26100.57 的
/// <c>Windows.Foundation.UniversalApiContract.winmd</c> 里用 <c>System.Reflection.Metadata</c>
/// 逐条读出（winmd 把 GUID 存在独立的 #Guid 堆，GuidAttribute 的 blob 是
/// <c>01 00</c> prolog + 16 字节原始 GUID）。**没有一个是凭记忆写的** ——
/// GUID 或 vtable 顺序写错只会得到 E_NOINTERFACE 或静默崩溃，极难调试。
///
/// 例外见 <see cref="GraphicsCaptureItemInterop"/> 与 <see cref="DxgiInterfaceAccess"/>：
/// 这两个是原生 COM 接口（声明在 d3d11.h / windows.graphics.capture.interop.h），
/// 不在任何投影 winmd 里，其取值需在真机验证时确认（见各成员注释）。
/// </summary>
internal static class WgcGuids
{
    // ── Windows.Graphics.Capture（已从 winmd 读出并核对）──────────────────

    /// <summary>IGraphicsCaptureItem。方法序：get_DisplayName / get_Size / add_Closed / remove_Closed。</summary>
    public static readonly Guid GraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    /// <summary>IGraphicsCaptureSession。方法序：StartCapture。</summary>
    public static readonly Guid GraphicsCaptureSession = new("814E42A9-F70F-4AD7-939B-FDDCC6EB880D");

    /// <summary>IGraphicsCaptureSession2。方法序：get_IsCursorCaptureEnabled / put_IsCursorCaptureEnabled。</summary>
    /// <remarks>对应 Mac 的 SCStreamConfiguration.showsCursor（参考文档 §13）。</remarks>
    public static readonly Guid GraphicsCaptureSession2 = new("2C39AE40-7D2E-5044-804E-8B6799D4CF9E");

    /// <summary>IGraphicsCaptureSession3。方法序：get_IsBorderRequired / put_IsBorderRequired。</summary>
    /// <remarks>
    /// Windows 的黄色选中边框。Mac 没有对应物（<c>ignoreWindowShadowsSingleWindow</c> 管的是阴影），
    /// 但边框会进入像素，必须关掉，否则单窗截图会带一圈黄边。
    /// </remarks>
    public static readonly Guid GraphicsCaptureSession3 = new("F2CDD966-22AE-5EA1-9596-3A289344C3BE");

    /// <summary>IGraphicsCaptureSessionStatics。方法序：IsSupported。</summary>
    public static readonly Guid GraphicsCaptureSessionStatics = new("2224A540-5974-49AA-B232-0882536F4CB5");

    /// <summary>IDirect3D11CaptureFramePool。方法序：Recreate / TryGetNextFrame / add+remove_FrameArrived / CreateCaptureSession / get_DispatcherQueue。</summary>
    public static readonly Guid CaptureFramePool = new("24EB6D22-1975-422E-82E7-780DBD8DDF24");

    /// <summary>
    /// IDirect3D11CaptureFramePoolStatics（静态接口，挂在激活工厂上）。
    /// 方法序：Create(device, item, sizeFormat, numberOfBuffers, size)。
    /// </summary>
    public static readonly Guid CaptureFramePoolStatics = new("7784056A-67AA-4D53-AE54-1088D5A8CA21");

    /// <summary>
    /// IDirect3D11CaptureFramePoolStatics2。方法序：CreateFreeThreaded(device, item, sizeFormat, numberOfBuffers, size)。
    /// <para>
    /// 一次性截图必须用这个：普通 Create 出来的帧池把 FrameArrived 绑到 DispatcherQueue，
    /// 而一次性截图没有消息循环，等不到帧。CreateFreeThreaded 的帧池可在任意线程
    /// 直接 TryGetNextFrame，正是参考文档 §14 风险 #1 描述的「起帧池→等一帧→拆除」方案所需。
    /// </para>
    /// </summary>
    public static readonly Guid CaptureFramePoolStatics2 = new("589B103F-6BBC-5DF5-A991-02E28B3B66D5");

    /// <summary>IDirect3D11CaptureFrame。方法序：get_Surface / get_SystemRelativeTime / get_ContentSize。</summary>
    public static readonly Guid CaptureFrame = new("FA50C623-38DA-4B32-ACF3-FA9734AD800E");

    /// <summary>IDirect3DDevice（WinRT）。方法序：Trim。</summary>
    public static readonly Guid Direct3DDevice = new("A37624AB-8D5F-4650-9D3E-9EAE3D9BC670");

    /// <summary>IDirect3DSurface（WinRT）。方法序：get_Description。</summary>
    public static readonly Guid Direct3DSurface = new("0BF4A146-13C1-4694-BEE3-7ABF15EAF586");

    /// <summary>IClosable。方法序：Close。用来显式停掉捕获会话，而不是靠 GC 回收。</summary>
    public static readonly Guid Closable = new("30D5A829-7FA4-4026-83BB-D75BAE4EA99E");

    /// <summary>
    /// IGraphicsCaptureItemInterop（原生 COM，**不在**任何投影 winmd 中）。
    /// <para>
    /// 从 WinRT 世界拿到 HWND/HMONITOR 对应的 GraphicsCaptureItem 的唯一入口：
    /// 用 <c>RoGetActivationFactory(L"Windows.Graphics.Capture.GraphicsCaptureItem")</c>
    /// 拿到激活工厂，再 QueryInterface 出本接口，然后 CreateForMonitor / CreateForWindow。
    /// 方法序（IUnknown 之后）：CreateForMonitor(6) / CreateForWindow(7)。
    /// </para>
    /// <para>
    /// ⚠️ 本 GUID 无法从 winmd 读出，需真机验证。验证方式：把激活工厂的
    /// <c>IInspectable::GetIids</c> 结果打印出来比对（见 Ta.Capture.Tests 的探针用例）。
    /// </para>
    /// </summary>
    // ⚠️ 真实 GUID 是 {3628E81B-3CAC-4C60-B7F4-23CE0E0C3356}
    // （windows.graphics.capture.interop.h）。原值 79C3F95B-31A7-… 是从
    // IGraphicsCaptureItem 的 GUID 手抄改动出来的假值，QI 永远 E_NOINTERFACE。
    public static readonly Guid GraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

    /// <summary>
    /// IDirect3DDxgiInterfaceAccess（原生 COM，**不在**任何投影 winmd 中）。
    /// <para>
    /// 唯一的「反向」桥：把 WGC 交给我们的 WinRT <c>IDirect3DSurface</c> 变回
    /// <c>ID3D11Texture2D</c>，这样才能 CopyResource 进 staging texture 读回 CPU。
    /// 方法序（IUnknown 之后）：GetInterface(3)。
    /// </para>
    /// </summary>
    public static readonly Guid DxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    // ── 基础 WinRT / D3D11（固定值，跨版本不变）─────────────────────────

    /// <summary>IInspectable。方法序：GetIids(3) / GetRuntimeClassName(4) / GetTrustLevel(5)。</summary>
    public static readonly Guid Inspectable = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");

    /// <summary>IActivationFactory。方法序：ActivateInstance(3)。</summary>
    public static readonly Guid ActivationFactory = new("00000035-0000-0000-C000-000000000046");

    /// <summary>IDXGIDevice。</summary>
    public static readonly Guid DxgiDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    /// <summary>ID3D11Texture2D。</summary>
    public static readonly Guid D3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
}

/// <summary>
/// WGC / D3D11 互操作用到的常量。
/// </summary>
internal static class WgcConstants
{
    /// <summary>
    /// DirectXPixelFormat.B8G8R8A8UIntNormalized 的数值。
    /// 从 winmd 的枚举声明序读出（第 88 个成员，下标 87），不是猜的。
    /// <para>
    /// 选它的理由：WGC 原生输出就是 BGRA8，请求这个格式意味着**零转换**；
    /// 同时它的数值恰好等于 DXGI_FORMAT_B8G8R8A8_UNORM（也是 87），
    /// 所以帧池格式与 staging texture 格式能直接对上，不需要格式转换 blit。
    /// </para>
    /// </summary>
    public const int PixelFormatB8G8R8A8UIntNormalized = 87;

    /// <summary>DXGI_FORMAT_B8G8R8A8_UNORM。</summary>
    public const int DxgiFormatB8G8R8A8Unorm = 87;

    /// <summary>D3D11_USAGE_STAGING。</summary>
    public const int D3D11UsageStaging = 3;

    /// <summary>D3D11_CPU_ACCESS_READ。</summary>
    public const uint D3D11CpuAccessRead = 0x0002_0000;

    /// <summary>D3D11_MAP_READ。</summary>
    public const int D3D11MapRead = 1;

    /// <summary>RO_INIT_MULTITHREADED。WGC 帧池用自由线程模式，必须是 MTA。</summary>
    public const int RoInitMultithreaded = 1;

    /// <summary>帧池缓冲区数。2 足够：一次只有一个帧被取走，另一个供 WGC 继续写。</summary>
    public const int FramePoolBufferCount = 2;

    /// <summary>D3D_FEATURE_LEVEL_11_0。WGC 至少需要 11.0。</summary>
    public const int D3DFeatureLevel110 = 0xb000;

    /// <summary>D3D11_CREATE_DEVICE_BGRA_SUPPORT。WGC 要求设备支持 BGRA 交换链互操作。</summary>
    public const uint D3D11CreateDeviceBgraSupport = 0x0000_0020;

    /// <summary>D3D_DRIVER_TYPE_HARDWARE。</summary>
    public const int D3DDriverTypeHardware = 1;

    /// <summary>D3D11_SDK_VERSION。</summary>
    public const uint D3D11SdkVersion = 7;

    /// <summary>
    /// 等一帧的超时（毫秒）。WGC 起池后通常 1–2 帧（约 16–33 ms）内出帧，
    /// 留足余量应对合成器刚睡醒或显示器关闭的情况。对应参考文档 §14 风险 #1 的已知代价。
    /// </summary>
    public const int FrameWaitTimeoutMs = 2000;

    /// <summary>轮询间隔（毫秒）。TryGetNextFrame 返回 null 表示还没有帧。</summary>
    public const int FramePollIntervalMs = 2;
}
