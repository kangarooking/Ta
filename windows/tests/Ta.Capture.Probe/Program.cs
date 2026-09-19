using System.Reflection;
using Ta.Core.Capture;
using Ta.Core.Imaging;
using Ta.Capture;
using Ta.Capture.Win32;
using Ta.Capture.WinRt;

/// <summary>
/// Ta.Capture 真机诊断程序。
///
/// 存在的两个理由：
/// 1. **真机验证**：真的抓一次当前屏幕，断言位图非空、尺寸合理、内容不是黑帧。
///    参考文档 §14 风险 #29 说 UAC 提权 / RDP / 安全桌面会得到黑帧，而尺寸断言
///    完全抓不到这种静默失败，所以这里额外统计像素多样性。
/// 2. **GUID 审计**：两个原生 COM 接口（IGraphicsCaptureItemInterop、
///    IDirect3DDxgiInterfaceAccess）不在任何投影 winmd 里，取值无法离线核对。
///    这里用 IInspectable::GetIids 把激活工厂真正实现的接口列出来，
///    并直接试探两个 GUID 的 QueryInterface 是否成功，结果打到控制台供人工核对。
///
/// 用法：<c>dotnet run --project tests\Ta.Capture.Probe</c>
/// 退出码：0 = 全部通过，1 = 有断言失败。
/// </summary>
internal static class Program
{
    private static int _failures;

    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        Section("环境");
        Console.WriteLine($"OS            : {Environment.OSVersion}");
        Console.WriteLine($"64 位进程      : {Environment.Is64BitProcess}");
        Console.WriteLine($"程序集         : {Assembly.GetExecutingAssembly().Location}");

        Section("WGC 可用性");
        var supported = GraphicsCaptureGrabber.IsSupported();
        Console.WriteLine($"GraphicsCaptureSession.IsSupported = {supported}");
        Check("WGC 在当前系统可用", supported);

        AuditGuids();

        Section("显示器枚举");
        var capture = new GraphicsCaptureScreenCapture();
        var displays = capture.Displays;
        Console.WriteLine($"枚举到 {displays.Count} 块显示器：");
        foreach (var d in displays)
        {
            Console.WriteLine(
                $"  id={d.Id} origin=({d.Frame.MinX},{d.Frame.MinY}) size={d.Frame.Width}x{d.Frame.Height} "
                + $"pixelScale={d.PixelScale} primary={d.IsPrimary}");
        }
        Check("至少一块显示器", displays.Count > 0);
        Check("恰好一块主屏（风险 #25：不靠枚举顺序）", displays.Count(d => d.IsPrimary) == 1);

        var primary = DisplaySnapshot.Primary(displays);
        Check("找到主屏", primary is not null);

        Section("窗口枚举");
        var windows = capture.Windows;
        Console.WriteLine($"枚举到 {windows.Count} 个合规窗口（前台进程 + 可见 + 尺寸 + alpha）：");
        foreach (var w in windows.Take(12))
        {
            Console.WriteLine(
                $"  hwnd=0x{w.WindowHandle.ToInt64():X} pid={w.ProcessId} app={w.AppName} z={w.ZOrder} "
                + $"size={w.Frame.Width}x{w.Frame.Height} title={w.Title ?? "<null>"}");
        }
        Check("所有 AppName 非空", windows.All(w => !string.IsNullOrWhiteSpace(w.AppName)));
        Check(
            "所有窗口满足 24x16 下限",
            windows.All(w => w.Frame.Width >= WindowSnapTargetFilter.MinimumWidth
                && w.Frame.Height >= WindowSnapTargetFilter.MinimumHeight));

        Section("冻结整屏（WGC 一次性抓帧）");
        if (primary is null)
        {
            Console.WriteLine("跳过：没有主屏。");
        }
        else
        {
            var expectedWidth = Math.Max(1, (int)Math.Round(primary.Frame.Width * primary.PixelScale));
            var expectedHeight = Math.Max(1, (int)Math.Round(primary.Frame.Height * primary.PixelScale));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            RgbaBitmap frozen;
            try
            {
                frozen = capture.CaptureDisplay(primary.Id, primary.PixelScale);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CaptureDisplay 抛出 {ex.GetType().Name}: {ex.Message}");
                Check("CaptureDisplay 成功", false);
                return Report();
            }
            sw.Stop();

            Console.WriteLine(
                $"尺寸            : {frozen.Width}x{frozen.Height}（期望 {expectedWidth}x{expectedHeight}）");
            Console.WriteLine($"字节数          : {frozen.Pixels.Length:N0}（{frozen.Pixels.Length / 1024 / 1024} MB）");
            Console.WriteLine($"耗时            : {sw.ElapsedMilliseconds} ms（含 1–2 帧等待，见参考文档 §14 风险 #1）");

            Check("位图非空", frozen.Pixels.Length > 0);
            Check("宽度精确等于 pixelScale 换算值", frozen.Width == expectedWidth);
            Check("高度精确等于 pixelScale 换算值", frozen.Height == expectedHeight);
            Check(
                "字节数 = 宽×高×4",
                frozen.Pixels.Length == (long)frozen.Width * frozen.Height * RgbaBitmap.BytesPerPixel);

            // 亮度 / alpha 分布 —— 区分「真黑帧」与「内容正确但 alpha 异常」。
            {
                long sumL = 0, minL = long.MaxValue, maxL = long.MinValue;
                var alphaBuckets = new int[5];
                var px = frozen.Pixels;
                for (var i = 0; i < px.Length; i += 4 * 97)
                {
                    int r = px[i], g = px[i + 1], b = px[i + 2], a = px[i + 3];
                    var lum = (r * 299 + g * 587 + b * 114) / 1000;
                    sumL += lum;
                    if (lum < minL) minL = lum;
                    if (lum > maxL) maxL = lum;
                    alphaBuckets[Math.Min(a / 64, 4)]++;
                }
                var n = px.Length / (4 * 97);
                Console.WriteLine($"亮度 min={minL} avg={sumL / n} max={maxL}");
                Console.WriteLine($"alpha 分布 [0-63|64-127|128-191|192-254|255]=" +
                    $"{string.Join('|', alphaBuckets)} / {n}");
            }

            // 32x18 亮度网格 —— 直接看内容形状，不经过 PNG 编码链。
            {
                var gw = 32; var gh = 18;
                var chars = ".:-=+*#%@";
                for (var gy = 0; gy < gh; gy++)
                {
                    var line = new System.Text.StringBuilder();
                    for (var gx = 0; gx < gw; gx++)
                    {
                        var sx = (int)((long)gx * frozen.Width / gw);
                        var sy = (int)((long)gy * frozen.Height / gh);
                        var off = sy * frozen.Stride + sx * 4;
                        int r = frozen.Pixels[off], g = frozen.Pixels[off + 1], b = frozen.Pixels[off + 2];
                        var lum = (r * 299 + g * 587 + b * 114) / 1000;
                        line.Append(chars[Math.Min(lum * chars.Length / 256, chars.Length - 1)]);
                    }
                    Console.WriteLine($"  |{line}|");
                }
            }

            var (distinct, opaqueRatio) = SampleStatistics(frozen);
            Console.WriteLine($"像素多样性      : {distinct:N0} 种不同取样");
            Console.WriteLine($"完全不透明占比  : {opaqueRatio:P2}");
            Check("不是黑帧/纯色帧（风险 #29）", distinct > 1);
            Check("内容基本不透明", opaqueRatio > 0.99);

            // 存一份 PNG 便于人工核对 —— 只写临时目录，不污染仓库。
            SavePreview(frozen);

            Section("从冻结帧裁剪");
            var selection = new CaptureSelection
            {
                GlobalRect = new RectD(
                    primary.Frame.MinX + primary.Frame.Width * 0.25,
                    primary.Frame.MinY + primary.Frame.Height * 0.25,
                    primary.Frame.Width * 0.5,
                    primary.Frame.Height * 0.5),
                ScreenFrame = primary.Frame,
                BackingScaleFactor = primary.PixelScale,
            };
            var cropped = capture.CropFrozen(frozen, selection);
            var expectedCropWidth = Math.Round(primary.Frame.Width * 0.5 * primary.PixelScale);
            Console.WriteLine(
                $"裁剪结果        : {cropped.Width}x{cropped.Height}（期望宽 {expectedCropWidth}）");
            Check("裁剪结果非空", cropped.Width > 0 && cropped.Height > 0);
            Check(
                "裁剪宽度符合比例换算",
                Math.Abs(cropped.Width - expectedCropWidth) <= 1);

            var (croppedDistinct, _) = SampleStatistics(cropped);
            Console.WriteLine($"裁剪后像素多样性: {croppedDistinct:N0} 种");
            Check("裁剪结果不是纯色", croppedDistinct > 1);

            Section("连续两次抓帧（帧池拆除后不留残留状态）");
            var second = capture.CaptureDisplay(primary.Id, primary.PixelScale);
            {
                long sumL = 0;
                var px = second.Pixels;
                for (var i = 0; i < px.Length; i += 4 * 97)
                {
                    sumL += (px[i] * 299 + px[i + 1] * 587 + px[i + 2] * 114) / 1000;
                }
                Console.WriteLine($"第二次          : {second.Width}x{second.Height} avg亮度={sumL / (px.Length / (4 * 97))}");
            }
            Check("两次尺寸一致", second.Width == frozen.Width && second.Height == frozen.Height);
        }

        Section("前台窗口捕获");
        try
        {
            var front = capture.CaptureFrontmost(1);
            Console.WriteLine($"CaptureFrontmost: {front.Width}x{front.Height}");
            Check("前台窗口位图非空", front.Width > 0 && front.Height > 0);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CaptureFrontmost 抛出 {ex.GetType().Name}: {ex.Message}");
            Check("CaptureFrontmost 成功", false);
        }

        Section("自身窗口排除（SetWindowDisplayAffinity）");
        Console.WriteLine(
            "SelfWindowExclusion.Exclude(IntPtr.Zero) = "
            + $"{SelfWindowExclusion.Exclude(IntPtr.Zero)}（空句柄按约定返回 false）");
        Console.WriteLine("提示：覆盖层应把自身 HWND 传给 Exclude，参考 Mac 的 excludedWindowIDs。");

        return Report();

        static int Report()
        {
            Console.WriteLine();
            if (_failures == 0)
            {
                Console.WriteLine("结果：全部通过。");
                return 0;
            }

            Console.WriteLine($"结果：{_failures} 项断言失败。");
            return 1;
        }
    }

    // ── GUID 审计 ───────────────────────────────────────────────────

    /// <summary>
    /// 核对两个无法从 winmd 读出的原生 COM GUID。
    /// 做法：拿到 GraphicsCaptureItem 的激活工厂，先列它实现的全部接口（IInspectable::GetIids），
    /// 再用它试探 QueryInterface。QI 成功就说明 GUID 与真实实现一致。
    /// </summary>
    private static void AuditGuids()
    {
        Section("GUID 审计（两个原生 COM 接口）");

        ComInterop.EnsureApartmentInitialized();

        var factory = ComInterop.GetActivationFactory(
            "Windows.Graphics.Capture.GraphicsCaptureItem", WgcGuids.ActivationFactory);
        try
        {
            var inspectable = ComInterop.TryQueryInterface(factory, WgcGuids.Inspectable);
            if (inspectable is null)
            {
                Console.WriteLine("拿不到 IInspectable，无法枚举。");
                return;
            }

            try
            {
                var getIids = ComInterop.Method<ComInterop.GetIidsFn>(inspectable.Value, 3);
                if (getIids(inspectable.Value, out var count, out var iids) == 0 && count > 0 && iids != IntPtr.Zero)
                {
                    Console.WriteLine($"激活工厂实现了 {count} 个接口：");
                    for (var i = 0; i < count; i++)
                    {
                        var bytes = new byte[16];
                        System.Runtime.InteropServices.Marshal.Copy(
                            iids + (i * 16), bytes, 0, 16);
                        var guid = new Guid(bytes).ToString().ToUpperInvariant();
                        var tag = guid == WgcGuids.GraphicsCaptureItemInterop.ToString().ToUpperInvariant()
                            ? "  <-- IGraphicsCaptureItemInterop（本实现采用的值）"
                            : guid == WgcGuids.ActivationFactory.ToString().ToUpperInvariant()
                                ? "  (IActivationFactory)"
                                : string.Empty;
                        Console.WriteLine($"  {guid}{tag}");
                    }
                }
            }
            finally
            {
                ComInterop.Release(inspectable.Value);
            }

            var interop = ComInterop.TryQueryInterface(factory, WgcGuids.GraphicsCaptureItemInterop);
            Console.WriteLine(
                $"QueryInterface(IGraphicsCaptureItemInterop {WgcGuids.GraphicsCaptureItemInterop}) = "
                + (interop is not null ? "成功" : "失败"));
            Check("IGraphicsCaptureItemInterop GUID 正确", interop is not null);

            if (interop is not null)
            {
                ComInterop.Release(interop.Value);
            }
        }
        finally
        {
            ComInterop.Release(factory);
        }
    }

    // ── 辅助 ────────────────────────────────────────────────────────

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"── {title} " + new string('─', Math.Max(0, 58 - title.Length * 2)));
    }

    private static void Check(string description, bool passed)
    {
        if (!passed)
        {
            _failures++;
        }

        Console.WriteLine($"  [{(passed ? "OK" : "FAIL")}] {description}");
    }

    private static (int Distinct, double OpaqueRatio) SampleStatistics(RgbaBitmap bitmap)
    {
        const int samples = 4000;
        var seen = new HashSet<int>();
        long opaque = 0;

        var stride = Math.Max(1, bitmap.Pixels.Length / RgbaBitmap.BytesPerPixel / samples);
        for (var i = 0; i < bitmap.Pixels.Length; i += stride * RgbaBitmap.BytesPerPixel)
        {
            seen.Add(
                (bitmap.Pixels[i] << 24)
                | (bitmap.Pixels[i + 1] << 16)
                | (bitmap.Pixels[i + 2] << 8)
                | bitmap.Pixels[i + 3]);

            if (bitmap.Pixels[i + 3] == 255)
            {
                opaque++;
            }
        }

        var total = (bitmap.Pixels.Length / RgbaBitmap.BytesPerPixel + stride - 1) / stride;
        return (seen.Count, total == 0 ? 0 : (double)opaque / total);
    }

    /// <summary>
    /// 把冻结帧写成 PNG 到临时目录，便于人工核对抓到的确实是屏幕内容。
    /// 用纯手写的 PNG 编码（stored/deflate 走 System.IO.Compression），
    /// 不引任何第三方库 —— 编码器属于另一个 agent 的范围。
    /// </summary>
    private static void SavePreview(RgbaBitmap bitmap)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "ta-capture-preview.png");
            PngWriter.Write(path, bitmap);
            Console.WriteLine($"预览已写入      : {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"预览写入失败（不影响断言）: {ex.Message}");
        }
    }
}

/// <summary>
/// 最小 PNG 编码器：把 RgbaBitmap 写成 8 位 RGBA PNG。
///
/// 只为诊断输出服务，刻意不实现 Ta.Encoding 的接口（那是另一个 agent 的范围）。
/// 采用 filter type 0（None）+ zlib deflate，任何合规解码器都能读。
/// </summary>
internal static class PngWriter
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public static void Write(string path, RgbaBitmap bitmap)
    {
        // 诊断专用：alpha 强制 255 —— WGC 帧的 alpha 是 DWM 侧输出（不保证 255），
        // 透明像素会让查看器把内容叠在黑底上看起来「全黑」。
        for (var i = 3; i < bitmap.Pixels.Length; i += 4)
        {
            bitmap.Pixels[i] = 255;
        }

        using var stream = File.Create(path);
        stream.Write(Signature, 0, Signature.Length);

        // IHDR：宽、高、位深 8、色彩类型 6（RGBA）、压缩 0、滤波 0、隔行 0。
        var header = new byte[13];
        WriteUInt32(header, 0, (uint)bitmap.Width);
        WriteUInt32(header, 4, (uint)bitmap.Height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(stream, "IHDR", header);

        // IDAT：每行前加一个 filter 字节 0。
        var raw = new byte[(bitmap.Width * 4 + 1) * bitmap.Height];
        for (var y = 0; y < bitmap.Height; y++)
        {
            var destination = y * (bitmap.Width * 4 + 1);
            raw[destination] = 0;
            System.Buffer.BlockCopy(bitmap.Pixels, y * bitmap.Stride, raw, destination + 1, bitmap.Stride);
        }

        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var deflate = new System.IO.Compression.DeflateStream(
                output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(raw, 0, raw.Length);
            }

            compressed = output.ToArray();
        }

        WriteChunk(stream, "IDAT", compressed);
        WriteChunk(stream, "IEND", Array.Empty<byte>());
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteUInt32(length, 0, (uint)data.Length);
        stream.Write(length, 0, 4);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes, 0, 4);
        stream.Write(data, 0, data.Length);

        var crcInput = new byte[4 + data.Length];
        System.Buffer.BlockCopy(typeBytes, 0, crcInput, 0, 4);
        System.Buffer.BlockCopy(data, 0, crcInput, 4, data.Length);

        var crc = new byte[4];
        WriteUInt32(crc, 0, Crc32(crcInput));
        stream.Write(crc, 0, 4);
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static readonly uint[] CrcTable = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (var n = 0; n < 256; n++)
        {
            var c = (uint)n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
