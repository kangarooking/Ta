using System.Buffers.Binary;
using System.IO.Compression;
using Ta.Core.Imaging;

namespace Ta.OCR.Imaging;

/// <summary>
/// 送进 OCR 前的图片预处理。
///
/// 对应 Mac 版 <c>prepareOCRImage</c>
/// （<c>AIScreenshotApp/Recognition/OptionalOCRPackManager.swift:716-736</c>）。
///
/// 规则（逐字）：最长边超过 <paramref name="maximumDimension"/> 时**等比**缩小；
/// 目标边长 = <c>max(1, (原边长 × scale).rounded())</c>；否则原样返回。
///
/// ⚠️ 两个易错点，Mac 版就是这么写的：
/// · <c>.rounded()</c> 在 Swift 里的默认规则是「**四舍六入五取远离零**」，
///   .NET 的 <c>Math.Round(double)</c> 默认是**银行家舍入**（五取偶），
///   必须显式 <see cref="MidpointRounding.AwayFromZero"/>，否则 .5 边界会差 1 像素。
/// · 宽高**各自**从原边长换算，不互相推导 —— 否则 3840×2160 缩到 2560 会得到 1439 而不是 1440。
/// </summary>
public static class OCRImagePreparation
{
    /// <summary>宿主喂给增强包的最长边上限（参考文档 §9.7）。</summary>
    public const int DefaultMaximumDimension = 2560;

    public static RgbaBitmap Prepare(RgbaBitmap source, int maximumDimension = DefaultMaximumDimension)
    {
        ArgumentNullException.ThrowIfNull(source);

        // :718 —— maximumDimension <= 0 视为「不限制」。
        if (maximumDimension <= 0)
        {
            return source;
        }

        var largestDimension = Math.Max(source.Width, source.Height);
        if (largestDimension <= maximumDimension)
        {
            // :718 —— 不超限时原样返回，Mac 是返回同一张 CGImage（不复制）。
            return source;
        }

        var scale = (double)maximumDimension / largestDimension;

        // :720-721
        var width = Math.Max(1, (int)Math.Round(source.Width * scale, MidpointRounding.AwayFromZero));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale, MidpointRounding.AwayFromZero));

        return RgbaDownscaler.Downscale(source, width, height);
    }
}

/// <summary>
/// 最小 PNG 编码器。
///
/// 增强包协议要求以 <c>--input &lt;png&gt;</c> 传图（<c>adapter.py:216</c>、
/// 参考文档 §9.7），所以必须能产出 PNG 字节。
///
/// 为什么不用 <c>Ta.Core.Imaging.IImageEncoder</c>：那个接口的实现放在平台层（WIC），
/// 而本程序集不依赖任何图像编解码库，也不该为了喂一个子进程引入 WIC。
/// 这里的实现刻意取最小集：8 位 RGBA、非隔行、滤波方式 None。
/// zlib 流由 <c>DeflateStream</c>（裸 deflate）+ 手工 zlib 头尾拼出 ——
/// <c>ZlibStream</c> 在本 TFM 下不可用。
/// </summary>
public static class PngWriter
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[] Encode(RgbaBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        using var stream = new MemoryStream();
        stream.Write(Signature, 0, Signature.Length);

        // IHDR（:13 bytes + 类型/长度）
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)bitmap.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)bitmap.Height);
        header[8] = 8;   // 位深
        header[9] = 6;   // 颜色类型 6 = 真彩色 + alpha
        header[10] = 0;  // 压缩方式
        header[11] = 0;  // 滤波方法
        header[12] = 0;  // 隔行
        WriteChunk(stream, "IHDR", header);

        WriteChunk(stream, "IDAT", Compress(bitmap));
        WriteChunk(stream, "IEND", []);

        return stream.ToArray();
    }

    /// <summary>逐行前置滤波字节 0（None），再整体 zlib 压缩。</summary>
    private static byte[] Compress(RgbaBitmap bitmap)
    {
        var raw = new byte[bitmap.Height * (1 + bitmap.Stride)];

        for (var y = 0; y < bitmap.Height; y++)
        {
            var offset = y * (1 + bitmap.Stride);
            raw[offset] = 0; // filter type: None
            Array.Copy(bitmap.Pixels, y * bitmap.Stride, raw, offset + 1, bitmap.Stride);
        }

        using var deflated = new MemoryStream();

        // DeflateStream 产出的是**裸 deflate**（无 zlib 头、无 Adler-32），
        // 而 PNG 的 IDAT 负载是完整 zlib 流（RFC 1950），所以要自己补头尾。
        using (var deflate = new DeflateStream(deflated, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw, 0, raw.Length);
        }

        var deflateData = deflated.ToArray();

        var result = new byte[2 + deflateData.Length + 4];

        // CMF = 0x78（32K 窗口、deflate 压缩方法）
        // FLG = 0x01（FLEVEL=0、无预置字典、FCHECK=1）
        // FCHECK 校验：(0x78 << 8 | 0x01) % 31 == 0 → 30721 % 31 == 0 ✓
        result[0] = 0x78;
        result[1] = 0x01;

        deflateData.CopyTo(result, 2);

        BinaryPrimitives.WriteUInt32BigEndian(
            result.AsSpan(result.Length - 4, 4),
            Adler32(raw));

        return result;
    }

    /// <summary>
    /// Adler-32（RFC 1950）。zlib 流的尾部校验和。
    ///
    /// 之所以要自己算：本程序集的 TFM（<c>net8.0-windows10.0.19041.0</c>）下
    /// <c>System.IO.Compression.ZlibStream</c> 不可用（已实测编译不过），
    /// 只有 <c>DeflateStream</c>（裸 deflate）可用。
    /// </summary>
    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        const uint Modulus = 65521;

        uint a = 1;
        uint b = 0;

        // 每 5552 字节取一次模，避免 a/b 在 uint 上溢出（5552 是 RFC 1950 推荐值）。
        var index = 0;
        while (index < data.Length)
        {
            var blockEnd = Math.Min(index + 5552, data.Length);
            for (; index < blockEnd; index++)
            {
                a += data[index];
                b += a;
            }

            a %= Modulus;
            b %= Modulus;
        }

        return (b << 16) | a;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes, 0, typeBytes.Length);
        stream.Write(data, 0, data.Length);

        // CRC 覆盖「类型 + 数据」，不含长度字段。
        var crcInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, typeBytes.Length);

        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(crcInput));
        stream.Write(crc);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
