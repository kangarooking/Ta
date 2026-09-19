using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

// 允许测试工程引用 WIC COM 声明，以便独立地「用 WIC 解回来」验证编码结果。
// 编码器自身的代码路径不能用来验证编码正确性（会自我循环）。
[assembly: InternalsVisibleTo("Ta.Encoding.Tests")]

namespace Ta.Encoding;

/// <summary>
/// WIC（Windows Imaging Component）手写 COM 互操作声明。
///
/// 为什么不用 CsWin32 / ClangSharp / 引用 Windows SDK 的 winmd：
/// 本仓库的环境下 CsWin32 源码生成器反复不产出代码（详见任务说明），
/// 因此改为手写。所有 <c>[Guid]</c> 与**方法顺序**都从
/// <c>Windows.Win32.winmd</c>（microsoft.windows.sdk.win32metadata 71.0.14-preview）
/// 的元数据中逐一提取核对过。
///
/// ## 手写 COM 互操作的四个必错点，全部在此规避
///
/// 1. <see cref="InterfaceType"/> 必须显式为 <see cref="ComInterfaceType.InterfaceIsIUnknown"/>。
///    不写的话默认是 <c>InterfaceIsDual</c>，会在 vtable 前部插入 IDispatch 的 4 个方法，
///    之后**每一个**方法槽位都错位，症状是莫名其妙的 HRESULT 或直接崩溃。
/// 2. 方法声明顺序必须与 wincodec.idl 完全一致，包括本实现不调用的方法 ——
///    它们同样占 vtable 槽位。
/// 3. HRESULT 一律不 <c>PreserveSig</c>，让 CLR 在失败时自动抛 <see cref="COMException"/>，
///    避免「静默返回错误码、然后拿空数据继续走」的假成功。
/// 4. ⚠️ **<c>object</c> 参数会被封送成 VARIANT，不是指针。**
///    这是本项目实际踩到并导致 <c>AccessViolationException</c> 的坑：
///    把一个 COM 对象的 RCW 传进 <c>object</c> 形参，CLR 生成的是 16 字节的
///    VARIANT（<c>VT_UNKNOWN</c>，真实指针在偏移 8 处），而原生签名要的是
///    <c>IPropertyBag2*</c> 这样的裸接口指针 —— 于是原生代码把 VARIANT 头当前指针解引用。
///    同理 <c>null</c> 传进 <c>object</c> 形参得到的是 <c>VT_EMPTY</c> 的 VARIANT，
///    **不是** NULL 指针。
///    因此：**凡是原生签名是指针的位置，形参就必须是接口类型或 <see cref="IntPtr"/>，
///    绝不能用 <c>object</c>。**
///    本文件里未被调用的占位方法统一用 <see cref="IntPtr"/> 承载接口指针 ——
///    裸指针在 ABI 上是对的，且永远不会被解引用（这些方法不会被调用）。
/// </summary>
internal static class WicConstants
{
    /// <summary>CLSID_WICImagingFactory —— WIC 工厂 coclass。</summary>
    public static readonly Guid ClsidImagingFactory = new("cacaf262-9370-4615-a13b-9f5539da4c0a");

    /// <summary>
    /// GUID_VendorMicrosoftBuiltIn。显式指定内置编解码器，
    /// 等价于传 NULL，但写出来更明确。
    /// </summary>
    public static readonly Guid VendorMicrosoftBuiltIn = new("257a30fd-06b6-462b-aea4-63f70b86e533");

    /// <summary>GUID_ContainerFormatPng。</summary>
    public static readonly Guid ContainerFormatPng = new("1b7cfaf4-713f-473c-bbcd-6137425faeaf");

    /// <summary>GUID_ContainerFormatJpeg。</summary>
    public static readonly Guid ContainerFormatJpeg = new("19e4a5aa-5662-4fc5-a0c0-1758028e1057");

    /// <summary>
    /// GUID_WICPixelFormat32bppRGBA —— **未预乘** RGBA，每像素 4 字节，序 R,G,B,A。
    /// 这就是 <see cref="Ta.Core.Imaging.RgbaBitmap.Pixels"/> 的字节序。
    /// </summary>
    public static readonly Guid PixelFormat32bppRGBA = new("f5c7ad2d-6a8d-43dd-a7a8-a29935261ae9");

    /// <summary>
    /// GUID_WICPixelFormat32bppBGRA —— **未预乘** BGRA。PNG/JPEG 编码器最通用的 32 位目标格式。
    /// 从 RGBA 转 BGRA 只是通道重排，不会改亮度。
    /// </summary>
    public static readonly Guid PixelFormat32bppBGRA = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");

    /// <summary>
    /// ⚠️ GUID_WICPixelFormat32bppPBGRA —— **预乘** alpha。
    /// <b>不要</b>把它当编码目标：把未预乘数据交给 PBGRA 会让
    /// <c>RGB *= A/255</c>，半透明像素直接变暗。
    /// </summary>
    public static readonly Guid PixelFormat32bppPBGRA = new("6fddc324-4e03-4bfe-b185-3d77768dc910");

    /// <summary>⚠️ GUID_WICPixelFormat32bppPRGBA —— 同样是**预乘**变体，同样不能用。</summary>
    public static readonly Guid PixelFormat32bppPRGBA = new("3cc4a650-a527-4d37-a916-3142c7ebedba");

    /// <summary>
    /// GUID_WICPixelFormat24bppBGR —— JPEG 编码器实际选定的格式。
    /// JPEG 没有 alpha 通道，所以 WIC 的 JPEG 编码器会把我们请求的 32bppBGRA
    /// 改写成 24bppBGR。它同样**未预乘**，所以是安全的。
    /// </summary>
    public static readonly Guid PixelFormat24bppBGR = new("6fddc324-4e03-4bfe-b185-3d77768dc90c");
}

/// <summary>WICBitmapEncoderCacheOption。</summary>
internal enum WicBitmapEncoderCacheOption
{
    /// <summary>编码器把整帧缓存在内存里（我们不这么做：长截图可达数百 MB）。</summary>
    InMemory = 0,

    /// <summary>编码器用临时文件缓存。</summary>
    TempFile = 1,

    /// <summary>不缓存，流式写出。长截图分段导出选这个。</summary>
    NoCache = 2,
}

/// <summary>WICDecodeOptions。</summary>
internal enum WicDecodeOptions
{
    MetadataCacheOnDemand = 0,
    MetadataCacheOnLoad = 1,
}

/// <summary>WICBitmapCreateCacheOption。</summary>
internal enum WicBitmapCreateCacheOption
{
    NoCache = 0,
    CacheOnDemand = 1,
    CacheOnLoad = 2,
}

/// <summary>WICBitmapDitherType。我们做通道重排，恒为 None。</summary>
internal enum WicBitmapDitherType
{
    None = 0,
}

/// <summary>WICBitmapPaletteType。32bppBGRA 不需要调色板，传 Custom(=0)。</summary>
internal enum WicBitmapPaletteType
{
    Custom = 0,
}

/// <summary>PROPBAG2.dwType —— 请求「默认类型」即可（等价于原生代码里零初始化结构体）。</summary>
internal enum PropBag2Type
{
    Default = 0,
    UnderlyingType = 1,
}

/// <summary>
/// PROPBAG2（objidl.h）。用于给 JPEG 编码器写 <c>ImageQuality</c>。
/// 字段顺序必须是 DWORD / VARTYPE / 指针 / 指针，CLR 会自行补齐到自然对齐（x64 下 24 字节）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropBag2
{
    public PropBag2Type dwType;

    /// <summary>VT_EMPTY(0) = 不指定类型，由属性包自行决定。</summary>
    public VarEnum vt;

    public IntPtr pclipdata;

    [MarshalAs(UnmanagedType.LPWStr)]
    public string? pstrName;
}

/// <summary>
/// IWICImagingFactory —— 创建所有 WIC 对象的工厂。
///
/// 未被本实现调用的方法：出参统一用 <see cref="IntPtr"/>（裸 <c>void**</c>）。
/// 这样即使将来有人误调，拿到的也是指针而不是被封送坏的 VARIANT。
/// </summary>
[ComImport]
[Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWicImagingFactory
{
    void CreateDecoderFromFilename(
        [MarshalAs(UnmanagedType.LPWStr)] string wzFilename,
        ref Guid pguidVendor,
        uint dwDesiredAccess,
        WicDecodeOptions metadataOptions,
        out IntPtr ppIDecoder);

    void CreateDecoderFromStream(
        IStream pIStream,
        ref Guid pguidVendor,
        WicDecodeOptions metadataOptions,
        out IWicBitmapDecoder ppIDecoder);

    void CreateDecoderFromFileHandle(
        UIntPtr hFile,
        ref Guid pguidVendor,
        WicDecodeOptions metadataOptions,
        out IntPtr ppIDecoder);

    void CreateComponentInfo(ref Guid clsidComponent, out IntPtr ppIInfo);

    void CreateDecoder(
        ref Guid guidContainerFormat,
        ref Guid pguidVendor,
        out IntPtr ppIDecoder);

    void CreateEncoder(
        ref Guid guidContainerFormat,
        ref Guid pguidVendor,
        out IWicBitmapEncoder ppIEncoder);

    void CreatePalette(out IntPtr ppIPalette);

    void CreateFormatConverter(out IWicFormatConverter ppIFormatConverter);

    void CreateBitmapScaler(out IntPtr ppIBitmapScaler);

    void CreateBitmapClipper(out IntPtr ppIBitmapClipper);

    void CreateBitmapFlipRotator(out IntPtr ppIBitmapFlipRotator);

    void CreateStream(out IWicStream ppIStream);

    void CreateColorContext(out IntPtr ppIColorContext);

    void CreateColorTransformer(out IntPtr ppIColorTransform);

    void CreateBitmap(
        uint uiWidth,
        uint uiHeight,
        ref Guid pixelFormat,
        WicBitmapCreateCacheOption option,
        out IntPtr ppIBitmap);

    void CreateBitmapFromSource(
        IWicBitmapSource pIBitmapSource,
        WicBitmapCreateCacheOption option,
        out IntPtr ppIBitmap);

    void CreateBitmapFromSourceRect(
        IWicBitmapSource pIBitmapSource,
        uint x,
        uint y,
        uint width,
        uint height,
        out IntPtr ppIBitmap);

    void CreateBitmapFromMemory(
        uint uiWidth,
        uint uiHeight,
        ref Guid pixelFormat,
        uint cbStride,
        uint cbBufferSize,
        [In] byte[] pbBuffer,
        out IWicBitmap ppIBitmap);

    void CreateBitmapFromHBITMAP(
        IntPtr hBitmap,
        IntPtr hPalette,
        uint options,
        out IntPtr ppIBitmap);

    void CreateBitmapFromHICON(IntPtr hIcon, out IntPtr ppIBitmap);

    void CreateComponentEnumerator(uint componentTypes, uint options, out IntPtr ppIEnumUnknown);

    void CreateFastMetadataEncoderFromDecoder(IntPtr pIDecoder, out IntPtr ppIEncoder);

    void CreateFastMetadataEncoderFromFrameDecode(IntPtr pIFrameDecoder, out IntPtr ppIEncoder);

    void CreateQueryWriter(
        ref Guid guidMetadataFormat,
        ref Guid pguidVendor,
        out IntPtr ppIMetadataQueryWriter);

    void CreateQueryWriterFromReader(
        IntPtr pIQueryReader,
        ref Guid pguidVendor,
        out IntPtr ppIMetadataQueryWriter);
}

/// <summary>IWICBitmapSource —— 所有位图来源的基接口。</summary>
[ComImport]
[Guid("00000120-a8f2-4877-ba0a-fd2b6645fb94")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWicBitmapSource
{
    void GetSize(out uint puiWidth, out uint puiHeight);

    void GetPixelFormat(out Guid pPixelFormat);

    void GetResolution(out double pDpiX, out double pDpiY);

    /// <summary>
    /// prc 用 <see cref="IntPtr"/> 而非 <c>WICRect*</c>：本实现只取整图，
    /// 需要传 NULL（IntPtr.Zero）；C# 里没有「可空的 ref 结构体」这种写法。
    /// </summary>
    void CopyPixels(IntPtr prc, uint cbStride, uint cbBufferSize, [Out] byte[] pbBuffer);
}

// ⚠️ IWicBitmapSource 必须**恰好**这 4 个方法，且顺序不可调。
//
// 这里曾多声明了一个 `CopyPalette`（那是 IWICPalette 的方法，不属于本接口），
// 导致 vtable 从 CopyPixels 起**整体错位一格**。后果：
//  · IWicBitmapSource.CopyPixels 实际调到了错误槽位
//  · IWicFormatConverter.Initialize（排在其后）落到 CopyPixels 上，
//    以 Initialize 的参数表去调 CopyPixels → WINCODEC_ERR_WRONGSTATE (0x88982F04)
//
// 之所以编码器没暴露它：编码路径走 IWICBitmapFrameEncode.SetPixelFormat /
// WritePixels，没有踩到位移后的槽位；只有「读回内存中的图」这条路径会炸。
// 也就是说，若生产代码要解码自己编出的图，会拿到静默错误 —— 这类 bug
// 编译器不会报，只有往返测试能抓到。

/// <summary>IWICBitmap —— 可锁定的内存位图。</summary>
[ComImport]
[Guid("00000121-a8f2-4877-ba0a-fd2b6645fb94")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWicBitmap : IWicBitmapSource
{
    void Lock(IntPtr prcLock, uint flags, out IntPtr ppILock);

    void SetPalette(IntPtr pIPalette);

    void SetResolution(double dpiX, double dpiY);
}

/// <summary>IWICBitmapLock。</summary>
[ComImport]
[Guid("00000123-a8f2-4877-ba0a-fd2b6645fb94")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWicBitmapLock
{
    void GetSize(out uint puiWidth, out uint puiHeight);

    void GetStride(out uint pcbStride);

    void GetDataPointer(out uint pcbBufferSize, out IntPtr pbBuffer);

    void GetPixelFormat(out Guid pPixelFormat);
}

/// <summary>IWICPalette —— 只声明 GUID，本实现走 32 位 BGRA，用不到它。</summary>
[ComImport]
[Guid("00000040-a8f2-4877-ba0a-fd2b6645fb94")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWicPalette
{
}

/// <summary>IWICFormatConverter —— 像素格式转换（本实现只做 RGBA→BGRA 通道重排）。</summary>
[ComImport]
[Guid("00000301-a8f2-4877-ba0a-fd2b6645fb94")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWicFormatConverter : IWicBitmapSource
{
    /// <summary>
    /// pIPalette 传 <see cref="IntPtr.Zero"/>（NULL）。
    /// ⚠️ 不能用 <c>object</c> 形参 —— 那会被封送成 VARIANT，见本文件头部第 4 条。
    /// </summary>
    void Initialize(
        IWicBitmapSource pISource,
        ref Guid dstFormat,
        WicBitmapDitherType dither,
        IntPtr pIPalette,
        double alphaThresholdPercent,
        WicBitmapPaletteType paletteTranslate);

    void CanConvert(ref Guid srcPixelFormat, ref Guid dstPixelFormat, out int pfCanConvert);

    // ⚠️ Convert 必须声明出来。真实 IWICFormatConverter 的 vtable 是
    // Initialize → CanConvert → Convert；漏掉 Convert 会让任何继承
    // IWicFormatConverter 的接口（或按 vtable 索引取方法的代码）从这一位起错位。
    void Convert(
        IWicBitmapSource pISource,
        IntPtr pIPaletteTranslate,
        WicBitmapDitherType dither,
        WicBitmapPaletteType paletteTranslate);
}

/// <summary>
/// IWICStream —— 既能当编码输出目标，也能当解码输入源。
///
/// ⚠️ **本实现刻意不使用它**，原因已实测确认（详见 <see cref="WicMemoryStream"/>）：
/// 在本环境（Windows 11 + .NET 8.0.425）里，<see cref="IWicStream"/> 的四个
/// InitializeFrom* 方法**一律**返回 <c>WINCODEC_ERR_NOTINITIALIZED</c>（0x88982F0C，
/// 中文消息「组件未初始化」），且与 COM 初始化状态无关：
///  · MTA / STA 线程都一样
///  · <c>CoInitializeEx</c> 由我们自己调用（拿到 S_OK）也一样
///  · 传真实缓冲区 / 传 NULL / 传文件名 / 传 IStream，都一样
/// 同时可以确认**不是**本文件其它声明的问题：
///  · <c>IWICImagingFactory::CreateStream</c> 本身成功，且 QI 到本接口成功
///    （否则会拿到 E_NOINTERFACE，而不是一个干净的 HRESULT）
///  · <c>CreateBitmapFromMemory</c> 成功且回读的尺寸与像素格式完全正确
///  · 同一台机器上用 WPF 的 <c>PngBitmapEncoder</c> / <c>JpegBitmapEncoder</c>
///    （底层就是 WIC）能正常编出合法 PNG/JPEG —— 说明 WIC 组件本身是好的
/// 声明保留在此：接口与 GUID 是从 <c>Windows.Win32.winmd</c> 逐字核对的，
/// 若将来换了环境或 SDK 版本，可以直接改回用它，只需换掉
/// <see cref="WicMemoryStream"/> 这一处。
/// </summary>
[ComImport]
[Guid("135ff860-22b7-4ddf-b0f6-218f4f299a43")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWicStream : IStream
{
    void InitializeFromIStream(IStream pIStream);

    void InitializeFromFilename(
        [MarshalAs(UnmanagedType.LPWStr)] string wzFileName,
        uint dwDesiredAccess);

    void InitializeFromMemory([In] byte[]? pbBuffer, uint cbBufferSize);

    void InitializeFromIStreamRegion(IStream pIStream, ulong ulOffset, ulong ulSize);
}

/// <summary>IWICBitmapEncoder —— 容器级编码器（PNG / JPEG 各一个）。</summary>
[ComImport]
[Guid("00000103-a8f2-4877-ba0a-fd2b6645fb94")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWicBitmapEncoder
{
    void Initialize(IStream pIStream, WicBitmapEncoderCacheOption cacheOption);

    void GetContainerFormat(out Guid pguidContainerFormat);

    void GetEncoderInfo(out IntPtr ppIEncoderInfo);

    void SetColorContexts(uint cCount, IntPtr ppIColorContext);

    void SetPalette(IntPtr pIPalette);

    void SetThumbnail(IWicBitmapSource pIThumbnail);

    void SetPreview(IWicBitmapSource pIPreview);

    void CreateNewFrame(
        out IWicBitmapFrameEncode ppIFrameEncode,
        out IPropertyBag2 ppIEncoderOptions);

    void Commit();

    void GetMetadataQueryWriter(out IntPtr ppIMetadataQueryWriter);
}

/// <summary>
/// IPropertyBag2 —— 给编码器写选项（这里是 JPEG 的 <c>ImageQuality</c>）。
/// </summary>
[ComImport]
[Guid("22f55882-280b-11d0-a8a9-00a0c90c2004")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyBag2
{
    void Read(uint cProperties, ref PropBag2 pPropBag, IntPtr pErrLog, out IntPtr pvarValue, out int phrError);

    /// <summary>
    /// 写单个属性。pvarValue 用 <c>object</c> 承载 VARIANT：传 boxed <see cref="float"/>
    /// 会被封送成 <c>VT_R4</c>，正是 <c>ImageQuality</c> 需要的类型。
    /// 这里用 <c>object</c> 是**正确**的 —— 原生签名本来就是 <c>VARIANT*</c>。
    /// </summary>
    void Write(uint cProperties, [In] ref PropBag2 pPropBag, [In] ref object pvarValue);

    void CountProperties(out uint pcProperties);

    void GetPropertyInfo(uint iProperty, uint cProperties, out PropBag2 pPropBag, out uint pcProperties);

    void LoadObject(
        [MarshalAs(UnmanagedType.LPWStr)] string pstrName,
        uint dwHint,
        IntPtr pUnkObject,
        IntPtr pErrLog);
}

/// <summary>IWICBitmapFrameEncode —— 单帧编码。</summary>
[ComImport]
[Guid("00000105-a8f2-4877-ba0a-fd2b6645fb94")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWicBitmapFrameEncode
{
    /// <summary>
    /// ⚠️ 形参必须是接口类型。写成 <c>object</c> 会封送成 VARIANT，
    /// 原生侧把它当 <c>IPropertyBag2*</c> 解引用 → <c>AccessViolationException</c>。
    /// </summary>
    void Initialize(IPropertyBag2 pEncoderOptions);

    void SetSize(uint uiWidth, uint uiHeight);

    void SetResolution(double dpiX, double dpiY);

    /// <summary>进出参数：编码器会把它改写成「实际支持的最接近格式」，必须读回来核对。</summary>
    void SetPixelFormat(ref Guid pPixelFormat);

    void SetColorContexts(uint cCount, IntPtr ppIColorContext);

    void SetPalette(IntPtr pIPalette);

    void SetThumbnail(IWicBitmapSource pIThumbnail);

    void WritePixels(uint lineCount, uint cbStride, uint cbBufferSize, [In] byte[] pbBuffer);

    /// <summary>prc 传 IntPtr.Zero = 整个来源。用 WriteSource 而非 WritePixels，让 WIC 自己做格式转换。</summary>
    void WriteSource(IWicBitmapSource pIBitmapSource, IntPtr prc);

    void Commit();

    void GetMetadataQueryWriter(out IntPtr ppIMetadataQueryWriter);
}

/// <summary>IWICBitmapDecoder —— 容器级解码器。测试用它把编码结果解回来。</summary>
[ComImport]
[Guid("9edde9e7-8dee-47ea-99df-e6faf2ed44bf")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWicBitmapDecoder
{
    void QueryCapability(IStream pIStream, out uint pdwCapability);

    void Initialize(IStream pIStream, WicDecodeOptions cacheOptions);

    void GetContainerFormat(out Guid pguidContainerFormat);

    void GetDecoderInfo(out IntPtr ppIDecoderInfo);

    void CopyPalette(IntPtr pIPalette);

    void GetMetadataQueryReader(out IntPtr ppIMetadataQueryReader);

    void GetPreview(out IWicBitmapSource ppIPreview);

    void GetColorContexts(uint cCount, IntPtr ppIColorContext, out uint pcActualCount);

    void GetThumbnail(out IWicBitmapSource ppIThumbnail);

    void GetFrameCount(out uint pCount);

    /// <summary>
    /// 出参用 <see cref="IWicBitmapSource"/> 而不是 <see cref="IWicBitmapFrameDecode"/>：
    /// 前者 QI 稳定成功，且我们只需要位图来源那三个方法。原因见
    /// <see cref="IWicBitmapFrameDecode"/> 的备注。
    /// </summary>
    void GetFrame(uint index, out IWicBitmapSource ppIBitmapFrame);
}

/// <summary>IWICBitmapFrameDecode —— 单帧解码。</summary>
/// <remarks>
/// ⚠️ 本实现不直接用它：用 <see cref="IWicBitmapSource"/> 接收
/// <see cref="IWicBitmapDecoder.GetFrame"/> 的返回值即可 ——
/// <c>IWICBitmapFrameDecode</c> 继承 <c>IWICBitmapSource</c>，而我们只需要
/// GetSize / GetPixelFormat / CopyPixels 这三个。
/// 实测在 <c>Windows.Win32.winmd</c> 里读到的本接口 GUID
/// （<c>3b16811b-6a43-4ec9-b813-3d930c13b940</c>）在本机上 QI 得到 E_NOINTERFACE，
/// 因此刻意绕开，只声明在此备查。
/// </remarks>
[ComImport]
[Guid("3b16811b-6a43-4ec9-b813-3d930c13b940")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWicBitmapFrameDecode : IWicBitmapSource
{
    void GetMetadataQueryReader(out IntPtr ppIMetadataQueryReader);

    void GetColorContexts(uint cCount, IntPtr ppIColorContext, out uint pcActualCount);

    void GetThumbnail(out IWicBitmapSource ppIThumbnail);
}

/// <summary>
/// 保证调用线程已初始化 COM 的作用域。
///
/// 为什么需要：.NET（Core/5+）在纯 COM 互操作路径上**不会**保证调用过
/// <c>CoInitializeEx</c>，而 WIC 的多数对象在未初始化 COM 的线程上会直接失败。
/// 这一点在 .NET Framework 时代是自动的，移植时容易被想当然。
///
/// 选 MTA 而不是 STA：WIC 的 coclass 都是 ThreadingModel=Both，MTA 一定可行；
/// 而若宿主线程已经是 STA（WinUI/WPF 就是），请求 MTA 会得到
/// <c>RPC_E_CHANGED_MODE</c> —— 那种情况下 COM 本来就已经初始化好了，
/// 什么都不用做，也**绝不能**调 <c>CoUninitialize</c>。
/// </summary>
internal readonly struct ComScope : IDisposable
{
    /// <summary>COINIT_MULTITHREADED。</summary>
    private const int CoinitMultithreaded = 0;

    /// <summary>S_OK。</summary>
    private const int SOk = 0;

    private readonly bool _ownedByThisScope;

    private ComScope(bool ownedByThisScope)
    {
        _ownedByThisScope = ownedByThisScope;
    }

    public static ComScope Enter()
    {
        var result = CoInitializeEx(IntPtr.Zero, CoinitMultithreaded);

        // 只有拿到 S_OK 才说明这次初始化是我们做的，退出时才有资格 CoUninitialize。
        // S_FALSE(1) = 本线程此前已初始化过，由那个调用方负责收尾；
        // RPC_E_CHANGED_MODE(0x80010106) = 线程已是 STA，COM 本就绪。
        return new ComScope(result == SOk);
    }

    public void Dispose()
    {
        if (_ownedByThisScope)
        {
            CoUninitialize();
        }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr pvReserved, int dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}

/// <summary>
/// COM 对象释放辅助。
///
/// 长截图一次导出可达数百 MB 数据，编码器/解码器内部持有非托管缓冲；
/// 漏一个引用计数就会把窗口化存储或文件句柄拖到进程退出。
/// 参考文档 §14 #36 提到 Windows 侧必须自己处理这类资源生命周期。
/// </summary>
internal static class WicCom
{
    /// <summary>安全释放。已归零或非 COM 对象都不报错 —— 释放失败不该掩盖真正的编码异常。</summary>
    public static void Release(object? comObject)
    {
        if (comObject is null)
        {
            return;
        }

        try
        {
            if (Marshal.IsComObject(comObject))
            {
                Marshal.ReleaseComObject(comObject);
            }
        }
        catch (ArgumentException)
        {
            // 引用计数已经归零，无需处理。
        }
    }

    /// <summary>按「后创建先释放」的逆序释放一组对象。</summary>
    public static void ReleaseAll(params object?[] comObjects)
    {
        for (var i = comObjects.Length - 1; i >= 0; i--)
        {
            Release(comObjects[i]);
        }
    }
}
