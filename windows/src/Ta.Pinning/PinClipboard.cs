using System.Runtime.InteropServices;
using Ta.Core.Imaging;
using Ta.Pinning.Imaging;
using Ta.Pinning.Interop;

namespace Ta.Pinning;

/// <summary>
/// 剪贴板读到的内容。
/// </summary>
/// <param name="Bitmap">位图（物理像素）。</param>
/// <param name="PreferredSizePixels">
/// 首选尺寸（**物理像素**）—— 与 <see cref="PinnedImageController.Pin(RgbaBitmap, CaptureSelection, PinSizeD?)"/>
/// 的入参单位一致。文字卡片带这个值，保证「卡片就是 520pt 宽」在任何 DPI 下都成立；
/// 图片内容为 null（走 Mac 的 640×480 那一支）。
/// </param>
internal sealed record PinClipboardContent(RgbaBitmap Bitmap, PinSizeD? PreferredSizePixels);

/// <summary>
/// 从剪贴板生成钉图内容。
///
/// 对应 Mac 版 <c>pinFromPasteboard</c>（PinnedImageWindowController.swift:56-94），
/// 按 Mac 的**尝试顺序**逐项落地到 Windows：
///
/// | Mac | Windows 实现 | 状态 |
/// |---|---|---|
/// | <c>.png</c> 数据（:58） | 注册格式 "PNG"（浏览器/Office 都会放） | ✅ |
/// | <c>.tiff</c> 数据（:58） | CF_DIB / CF_DIBV5 / CF_BITMAP | ✅（TIFF 本身 Windows 剪贴板几乎不出现） |
/// | <c>NSURL</c> 文件列表（:64-73） | CF_HDROP，取第一个文件解码 | ✅ 仅 PNG；JPEG 等无解码器 |
/// | <c>.html</c>（:74-83） | —— | ❌ **未实现**，见 <see cref="Read"/> 内注释 |
/// | <c>.string</c> 纯文本（:84-92） | CF_UNICODETEXT → 文字卡片 | ✅ |
///
/// ⚠️ 所有剪贴板调用**必须在有窗口的消息循环线程上**进行（OpenClipboard 的属主窗口），
/// 因此本类只应由 <see cref="PinWindowHost"/> 在宿主线程上调用。
/// </summary>
internal static class PinClipboardReader
{
    private const uint PngFormatRetry = 8;
    private const int OpenRetryCount = 12;
    private const int OpenRetryDelayMs = 12;

    /// <summary>CF_HDROP 的文件索引常量（-1 表示查询文件数）。</summary>
    private const uint DragQueryFileCount = 0xFFFFFFFF;

    public static PinClipboardContent? Read(IntPtr ownerWindow)
    {
        if (!OpenWithRetry(ownerWindow))
        {
            return null;
        }

        try
        {
            // ① 图片数据：PNG → DIB → HBITMAP
            if (TryImageData(out var imageData))
            {
                return new PinClipboardContent(imageData!, null);
            }

            // ② 文件列表（资源管理器复制文件）
            if (TryFileDrop(out var dropped, out var droppedSize))
            {
                return new PinClipboardContent(dropped!, droppedSize);
            }

            // ③ HTML（Mac: .html → NSAttributedString）
            //    ❌ Windows 无直接等价物：CF_HTML 是带片段标记的纯文本，需要 HTML 解析 +
            //    富文本排版引擎才能还原成 Mac 的 NSAttributedString 效果。
            //    如实未实现 —— 不假装支持，直接落到下一步纯文本。

            // ④ 纯文本 → 文字卡片
            if (TryText(out var text))
            {
                var scale = PinScreen.ScaleAt(PinScreen.CursorPosition());
                var (bitmap, logicalSize) = PinTextCard.RenderTextCard(text, scale);
                // 折算回物理像素：Pin 的入参单位是像素（见该方法的单位约定）。
                return new PinClipboardContent(
                    bitmap, new PinSizeD(logicalSize.Width * scale, logicalSize.Height * scale));
            }

            return null;
        }
        finally
        {
            PinInterop.CloseClipboard();
        }
    }

    /// <summary>
    /// OpenClipboard 在别的进程持有剪贴板时会失败（参考文档 §14 风险 #15：
    /// Windows 没有 macOS 那种「随时可读」的语义），因此带重试与退避。
    /// </summary>
    private static bool OpenWithRetry(IntPtr ownerWindow)
    {
        for (var attempt = 0; attempt < OpenRetryCount; attempt++)
        {
            if (PinInterop.OpenClipboard(ownerWindow))
            {
                return true;
            }

            Thread.Sleep(OpenRetryDelayMs);
        }

        return false;
    }

    private static bool TryImageData(out RgbaBitmap? bitmap)
    {
        bitmap = null;

        var pngFormat = PinInterop.RegisterClipboardFormat("PNG");
        if (pngFormat != 0 && PinInterop.IsClipboardFormatAvailable(pngFormat))
        {
            bitmap = FromGlobalBytes(
                PinInterop.GetClipboardData(pngFormat), data => PngCodec.Decode(data));
            if (bitmap is not null)
            {
                return true;
            }
        }

        foreach (var format in new uint[] { PinInterop.CF_DIB, PinInterop.CF_DIBV5 })
        {
            if (PinInterop.IsClipboardFormatAvailable(format))
            {
                bitmap = FromDib(PinInterop.GetClipboardData(format));
                if (bitmap is not null)
                {
                    return true;
                }
            }
        }

        if (PinInterop.IsClipboardFormatAvailable(PinInterop.CF_BITMAP))
        {
            bitmap = FromBitmap(PinInterop.GetClipboardData(PinInterop.CF_BITMAP));
            if (bitmap is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// CF_HDROP：取第一个文件。能解成图片就返回图片（逻辑尺寸 null），
    /// 否则按 Mac 的 else 分支渲染「文件名 + 路径」文字卡片（带逻辑尺寸）。
    /// 对应 Mac: :64-73。
    /// </summary>
    private static bool TryFileDrop(out RgbaBitmap? bitmap, out PinSizeD? logicalSize)
    {
        bitmap = null;
        logicalSize = null;

        if (!PinInterop.IsClipboardFormatAvailable(PinInterop.CF_HDROP))
        {
            return false;
        }

        var drop = PinInterop.GetClipboardData(PinInterop.CF_HDROP);
        if (drop == IntPtr.Zero)
        {
            return false;
        }

        var count = PinInterop.DragQueryFile(drop, DragQueryFileCount, null, 0);
        if (count == 0)
        {
            return false;
        }

        // Mac 取 urls.first（:65）
        var builder = new System.Text.StringBuilder(1024);
        if (PinInterop.DragQueryFile(drop, 0, builder, (uint)builder.Capacity) == 0)
        {
            return false;
        }

        var path = builder.ToString();
        if (path.Length == 0 || !File.Exists(path))
        {
            return false;
        }

        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        // Mac 用 NSImage(contentsOf:)（:66-67），任何 ImageIO 支持的格式都能读。
        // 这里只有纯托管的 PNG 解码器；读不出图时按 Mac 的 else 分支
        // （:69-71）渲染一张「文件名 + 路径」的文字卡片。
        bitmap = PngCodec.Decode(data);
        if (bitmap is not null)
        {
            return true;
        }

        var scale = PinScreen.ScaleAt(PinScreen.CursorPosition());
        // 用不同名字承接解构结果 —— 若直接写 var (card, logicalSize)，
        // 会在方法内再声明一个同名局部变量，遮蔽 out 参数并触发 CS0136/CS0841。
        // 与 TryText 分支（:75-78）一致：逻辑尺寸乘 scale 得物理像素。
        var (card, size) = PinTextCard.RenderFileCard(Path.GetFileName(path), path, scale);
        bitmap = card;
        logicalSize = new PinSizeD(size.Width * scale, size.Height * scale);
        return true;
    }

    private static bool TryText(out string text)
    {
        text = string.Empty;

        if (!PinInterop.IsClipboardFormatAvailable(PinInterop.CF_UNICODETEXT))
        {
            return false;
        }

        var handle = PinInterop.GetClipboardData(PinInterop.CF_UNICODETEXT);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var pointer = PinInterop.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var value = Marshal.PtrToStringUni(pointer) ?? string.Empty;
            if (value.Length == 0)
            {
                return false;
            }

            text = value;
            return true;
        }
        finally
        {
            PinInterop.GlobalUnlock(handle);
        }
    }

    private static RgbaBitmap? FromGlobalBytes(IntPtr handle, Func<byte[], RgbaBitmap?> decode)
    {
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var pointer = PinInterop.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var size = (int)(ulong)PinInterop.GlobalSize(handle);
            if (size <= 0)
            {
                return null;
            }

            var data = new byte[size];
            Marshal.Copy(pointer, data, 0, size);
            return decode(data);
        }
        finally
        {
            PinInterop.GlobalUnlock(handle);
        }
    }

    /// <summary>CF_DIB / CF_DIBV5 → 位图。只支持 BI_RGB 的 24/32bpp（剪贴板上 99% 是这两种）。</summary>
    private static RgbaBitmap? FromDib(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var pointer = PinInterop.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var header = Marshal.PtrToStructure<PinInterop.BITMAPINFOHEADER>(pointer);
            if (header.biCompression != PinInterop.BI_RGB
                || header.biBitCount is not (24 or 32)
                || header.biWidth <= 0 || header.biHeight == 0)
            {
                return null;
            }

            var width = header.biWidth;
            var height = Math.Abs(header.biHeight);
            var topDown = header.biHeight < 0;
            var bytesPerPixel = header.biBitCount / 8;

            // DIB 行按 4 字节对齐；调色板仅见于 ≤8bpp，这里已排除。
            var stride = (((width * bytesPerPixel) + 3) / 4) * 4;
            var pixels = (int)(ulong)PinInterop.GlobalSize(handle) - (int)header.biSize;
            if (pixels < stride * height)
            {
                return null;
            }

            return FromBottomUpBgr(pointer + (int)header.biSize, width, height, stride, bytesPerPixel, topDown);
        }
        finally
        {
            PinInterop.GlobalUnlock(handle);
        }
    }

    /// <summary>CF_BITMAP（HBITMAP）→ 位图，走 GetDIBits 取 32bpp BGRA。</summary>
    private static RgbaBitmap? FromBitmap(IntPtr bitmap)
    {
        if (bitmap == IntPtr.Zero)
        {
            return null;
        }

        if (PinInterop.GetObject(bitmap, Marshal.SizeOf<PinInterop.BITMAP>(), out var info) == 0
            || info.bmWidth <= 0 || info.bmHeight <= 0)
        {
            return null;
        }

        var width = info.bmWidth;
        var height = info.bmHeight;
        var buffer = Marshal.AllocHGlobal(width * height * 4);
        try
        {
            var header = new PinInterop.BITMAPINFO();
            header.bmiHeader.biSize = (uint)Marshal.SizeOf<PinInterop.BITMAPINFOHEADER>();
            header.bmiHeader.biWidth = width;
            header.bmiHeader.biHeight = height;   // 自下而上
            header.bmiHeader.biPlanes = 1;
            header.bmiHeader.biBitCount = 32;
            header.bmiHeader.biCompression = PinInterop.BI_RGB;

            var screenDc = PinInterop.GetDC(IntPtr.Zero);
            try
            {
                var copied = PinInterop.GetDIBits(
                    screenDc, bitmap, 0, (uint)height, buffer, ref header, PinInterop.DIB_RGB_COLORS);
                if (copied == 0)
                {
                    return null;
                }
            }
            finally
            {
                PinInterop.ReleaseDC(IntPtr.Zero, screenDc);
            }

            return FromBottomUpBgr(buffer, width, height, width * 4, 4, topDown: false);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>BGR(A) 缓冲 → <see cref="RgbaBitmap"/>（左上原点）。</summary>
    private static RgbaBitmap FromBottomUpBgr(IntPtr source, int width, int height, int stride, int bytesPerPixel, bool topDown)
    {
        var bitmap = new RgbaBitmap(width, height);
        var row = new byte[stride];

        for (var y = 0; y < height; y++)
        {
            // topDown 为假时源是自下而上，需反向取行。
            Marshal.Copy(source + (int)((long)(topDown ? y : height - 1 - y) * stride), row, 0, stride);

            var destination = y * bitmap.Stride;
            for (var x = 0; x < width; x++)
            {
                var from = x * bytesPerPixel;
                var to = destination + (x * RgbaBitmap.BytesPerPixel);
                bitmap.Pixels[to] = row[from + 2];
                bitmap.Pixels[to + 1] = row[from + 1];
                bitmap.Pixels[to + 2] = row[from];
                // 剪贴板 DIB 的 alpha 字节常为 0（未预乘约定），一律按不透明处理。
                bitmap.Pixels[to + 3] = 255;
            }
        }

        return bitmap;
    }
}

/// <summary>
/// 把图像写入剪贴板。对应 Mac 的 <c>onCopy</c>（:166-172）—— 那里只写 <c>public.png</c>。
/// Windows 上同时写注册格式 "PNG" 与 CF_DIB，覆盖浏览器/Office/聊天窗口各类消费方。
/// </summary>
internal static class PinClipboardWriter
{
    private const uint OpenRetryCount = 12;
    private const int OpenRetryDelayMs = 12;

    public static bool WriteImage(IntPtr ownerWindow, RgbaBitmap bitmap)
    {
        for (var attempt = 0; attempt < OpenRetryCount; attempt++)
        {
            if (!PinInterop.OpenClipboard(ownerWindow))
            {
                Thread.Sleep(OpenRetryDelayMs);
                continue;
            }

            try
            {
                if (!PinInterop.EmptyClipboard())
                {
                    return false;
                }

                var png = PngCodec.Encode(bitmap);
                var pngFormat = PinInterop.RegisterClipboardFormat("PNG");
                var written = false;

                if (pngFormat != 0)
                {
                    var handle = CopyToGlobal(png);
                    if (handle != IntPtr.Zero && PinInterop.SetClipboardData(pngFormat, handle) == IntPtr.Zero)
                    {
                        PinInterop.GlobalFree(handle);
                    }
                    else
                    {
                        written = true;
                    }
                }

                var dib = BuildDib(bitmap);
                if (dib is not null)
                {
                    var handle = CopyToGlobal(dib);
                    if (handle != IntPtr.Zero && PinInterop.SetClipboardData(PinInterop.CF_DIB, handle) == IntPtr.Zero)
                    {
                        PinInterop.GlobalFree(handle);
                    }
                    else
                    {
                        written = true;
                    }
                }

                return written;
            }
            finally
            {
                PinInterop.CloseClipboard();
            }
        }

        return false;
    }

    /// <summary>RGBA → CF_DIB（32bpp BI_RGB、自下而上）。</summary>
    private static byte[]? BuildDib(RgbaBitmap bitmap)
    {
        var headerSize = Marshal.SizeOf<PinInterop.BITMAPINFOHEADER>();
        var stride = bitmap.Width * 4;
        var result = new byte[headerSize + (stride * bitmap.Height)];

        var header = result.AsSpan(0, headerSize);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header[..4], (uint)headerSize);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header.Slice(4, 4), bitmap.Width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header.Slice(8, 4), bitmap.Height);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(12, 2), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(14, 2), 32);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(16, 4), PinInterop.BI_RGB);

        for (var y = 0; y < bitmap.Height; y++)
        {
            var sourceRow = y * bitmap.Stride;
            var destinationRow = headerSize + ((bitmap.Height - 1 - y) * stride);
            for (var x = 0; x < bitmap.Width; x++)
            {
                var from = sourceRow + (x * RgbaBitmap.BytesPerPixel);
                var to = destinationRow + (x * 4);
                result[to] = bitmap.Pixels[from + 2];
                result[to + 1] = bitmap.Pixels[from + 1];
                result[to + 2] = bitmap.Pixels[from];
                result[to + 3] = 255;
            }
        }

        return result;
    }

    private static IntPtr CopyToGlobal(byte[] data)
    {
        var handle = PinInterop.GlobalAlloc(
            PinInterop.GMEM_MOVEABLE | PinInterop.GMEM_ZEROINIT, (UIntPtr)(ulong)data.Length);
        if (handle == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var pointer = PinInterop.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            PinInterop.GlobalFree(handle);
            return IntPtr.Zero;
        }

        Marshal.Copy(data, 0, pointer, data.Length);
        PinInterop.GlobalUnlock(handle);
        return handle;
    }
}
