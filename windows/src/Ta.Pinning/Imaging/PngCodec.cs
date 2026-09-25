using System.Buffers.Binary;
using System.IO.Compression;
using Ta.Core.Imaging;

namespace Ta.Pinning.Imaging;

/// <summary>
/// 纯托管的 PNG 编解码（无 WIC、无 System.Drawing、无第三方库）。
///
/// 存在的理由：钉图有两处必须读写 PNG ——
///   1. 「复制图片」写入剪贴板。Mac 写 <c>public.png</c>
///      （PinnedImageWindowController.swift:168-171），Windows 上等价物是
///      注册格式 "PNG"（浏览器/Office 都认）与 CF_DIB。
///   2. 从剪贴板读图：浏览器复制图片时放的是 "PNG" 格式；资源管理器复制文件
///      时给的是 CF_HDROP 文件路径，文件本身通常是 PNG。
///
/// 覆盖范围：8/16 位、灰度 / 真彩 / 索引 / 灰+Alpha / RGBA，**非隔行**。
/// 隔行（Adam7）返回 null —— 截图与网络图片几乎不用隔行，宁缺勿错。
///
/// 压缩走 <c>System.IO.Compression.DeflateStream</c>：.NET 的 DeflateStream
/// 读写的是**裸 deflate** 流，因此 PNG 的 zlib 包头（0x78 0x01）与
/// Adler-32  trailer 需要自行拼/剥。
/// </summary>
public static class PngCodec
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    // ── 编码 ────────────────────────────────────────────────────────

    /// <summary>把 RGBA 位图编码为 8 位 RGBA 非隔行 PNG。</summary>
    public static byte[] Encode(RgbaBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        var stride = bitmap.Width * 4;
        var raw = new byte[(stride + 1) * bitmap.Height];
        for (var y = 0; y < bitmap.Height; y++)
        {
            var destination = y * (stride + 1);
            raw[destination] = 0;   // filter type: None
            Buffer.BlockCopy(bitmap.Pixels, y * bitmap.Stride, raw, destination + 1, stride);
        }

        var zlib = ZlibWrap(Deflate(raw), raw);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0), (uint)bitmap.Width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)bitmap.Height);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 6;    // color type: RGBA
        ihdr[10] = 0;   // compression: deflate
        ihdr[11] = 0;   // filter method: adaptive
        ihdr[12] = 0;   // interlace: none

        using var output = new MemoryStream();
        output.Write(Signature);
        WriteChunk(output, "IHDR", ihdr);
        WriteChunk(output, "IDAT", zlib);
        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    private static byte[] Deflate(byte[] raw)
    {
        using var buffer = new MemoryStream();
        // leaveOpen: 不关闭底层 buffer，否则 ToArray 前就被释放。
        using (var deflate = new DeflateStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw, 0, raw.Length);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// 拼上 zlib 头与 Adler-32 尾。Mac/CGImage 侧走 ImageIO，此处自行保证格式合法。
    /// </summary>
    /// <param name="deflated">裸 deflate 流。</param>
    /// <param name="raw">压缩前的原始数据，Adler-32 对它计算。</param>
    private static byte[] ZlibWrap(byte[] deflated, byte[] raw)
    {
        var result = new byte[deflated.Length + 6];
        result[0] = 0x78;   // CM = 8 (deflate), CINFO = 7 (32K window)
        result[1] = 0x01;   // FCHECK 使 (0x7801) % 31 == 0；FLEVEL = 0 (fastest)
        Buffer.BlockCopy(deflated, 0, result, 2, deflated.Length);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(deflated.Length + 2), Adler32(raw));
        return result;
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)data.Length);
        output.Write(header[..4]);

        Span<byte> typeBytes = stackalloc byte[4];
        for (var i = 0; i < 4; i++)
        {
            typeBytes[i] = (byte)type[i];
        }

        output.Write(typeBytes);
        output.Write(data);

        Span<byte> crcInput = stackalloc byte[4 + data.Length];
        typeBytes.CopyTo(crcInput);
        data.CopyTo(crcInput[4..]);
        BinaryPrimitives.WriteUInt32BigEndian(header[..4], Crc32(crcInput));
        output.Write(header[..4]);
    }

    // ── 解码 ────────────────────────────────────────────────────────

    /// <summary>解码 PNG；不支持或数据损坏时返回 null。</summary>
    public static RgbaBitmap? Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < Signature.Length || !data[..Signature.Length].SequenceEqual(Signature))
        {
            return null;
        }

        var position = Signature.Length;
        int? width = null, height = null, bitDepth = null, colorType = null, interlace = null;
        byte[]? palette = null;
        byte[]? paletteAlpha = null;
        var idat = new MemoryStream();

        while (position + 8 <= data.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(position, 4));
            var type = System.Text.Encoding.ASCII.GetString(data.Slice(position + 4, 4));
            var bodyStart = position + 8;
            if (length > int.MaxValue || bodyStart + length + 4 > data.Length)
            {
                return null;   // 长度越界 —— 数据损坏
            }

            var body = data.Slice(bodyStart, (int)length);

            switch (type)
            {
                case "IHDR" when body.Length >= 13:
                    width = (int)BinaryPrimitives.ReadUInt32BigEndian(body[..4]);
                    height = (int)BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4, 4));
                    bitDepth = body[8];
                    colorType = body[9];
                    interlace = body[12];
                    break;

                case "PLTE":
                    palette = body.ToArray();
                    break;

                case "tRNS":
                    paletteAlpha = body.ToArray();
                    break;

                case "IDAT":
                    idat.Write(body);
                    break;

                case "IEND":
                    position = data.Length;
                    break;
            }

            position = bodyStart + (int)length + 4;
        }

        if (width is not { } w || height is not { } h || w <= 0 || h <= 0
            || bitDepth is not { } depth || colorType is not { } channels || interlace is not 0)
        {
            return null;
        }

        if (depth is not (8 or 16) || channels is < 0 or > 6 || channels == 1 || channels == 5)
        {
            return null;
        }

        var samples = channels switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            _ => 4,
        };

        var bytesPerSample = depth / 8;
        var stride = w * samples * bytesPerSample;
        var inflated = Inflate(idat.ToArray(), (long)stride * h);
        if (inflated is null || inflated.Length < (long)stride * h)
        {
            return null;
        }

        var recon = new byte[(long)stride * h];
        if (!Unfilter(inflated, recon, h, stride, Math.Max(1, samples * bytesPerSample)))
        {
            return null;
        }

        return ToRgba(recon, w, h, depth, channels, samples, bytesPerSample, palette, paletteAlpha);
    }

    private static byte[]? Inflate(byte[] zlib, long expected)
    {
        var offset = 0;
        // 剥掉 2 字节 zlib 头（.NET DeflateStream 只吃裸 deflate）。
        if (zlib.Length >= 2 && (zlib[0] & 0x0F) == 8 && (((zlib[0] << 8) | zlib[1]) % 31) == 0)
        {
            offset = 2;
        }

        try
        {
            using var input = new MemoryStream(zlib, offset, zlib.Length - offset, writable: false);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream((int)Math.Min(expected, 4 * 1024 * 1024));
            deflate.CopyTo(output);
            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static bool Unfilter(byte[] source, byte[] recon, int height, int stride, int bpp)
    {
        var position = 0;
        for (var y = 0; y < height; y++)
        {
            if (position + 1 + stride > source.Length)
            {
                return false;
            }

            var filter = source[position++];
            var rowStart = y * stride;

            for (var x = 0; x < stride; x++)
            {
                var raw = source[position + x];
                var left = x >= bpp ? recon[rowStart + x - bpp] : (byte)0;
                var up = y > 0 ? recon[rowStart - stride + x] : (byte)0;
                var upLeft = x >= bpp && y > 0 ? recon[rowStart - stride + x - bpp] : (byte)0;

                recon[rowStart + x] = filter switch
                {
                    0 => raw,
                    1 => (byte)(raw + left),
                    2 => (byte)(raw + up),
                    3 => (byte)(raw + ((left + up) / 2)),
                    4 => (byte)(raw + Paeth(left, up, upLeft)),
                    _ => (byte)0,
                };
            }

            position += stride;
        }

        return true;
    }

    private static byte Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
    }

    private static RgbaBitmap ToRgba(
        byte[] recon, int width, int height, int depth, int colorType, int samples,
        int bytesPerSample, byte[]? palette, byte[]? paletteAlpha)
    {
        var bitmap = new RgbaBitmap(width, height);
        var stride = width * samples * bytesPerSample;

        for (var y = 0; y < height; y++)
        {
            var rowStart = y * stride;
            var destination = y * bitmap.Stride;

            for (var x = 0; x < width; x++)
            {
                var source = rowStart + (x * samples * bytesPerSample);
                var target = destination + (x * RgbaBitmap.BytesPerPixel);

                // 16 位取高字节。
                byte Sample(int index) => bytesPerSample == 2
                    ? recon[source + (index * 2)]
                    : recon[source + index];

                switch (colorType)
                {
                    case 0:
                        bitmap.Pixels[target] = bitmap.Pixels[target + 1] = bitmap.Pixels[target + 2] = Sample(0);
                        bitmap.Pixels[target + 3] = 255;
                        break;

                    case 2:
                        bitmap.Pixels[target] = Sample(0);
                        bitmap.Pixels[target + 1] = Sample(1);
                        bitmap.Pixels[target + 2] = Sample(2);
                        bitmap.Pixels[target + 3] = 255;
                        break;

                    case 3 when palette is not null:
                        var index = Sample(0) * 3;
                        bitmap.Pixels[target] = index < palette.Length ? palette[index] : (byte)0;
                        bitmap.Pixels[target + 1] = index + 1 < palette.Length ? palette[index + 1] : (byte)0;
                        bitmap.Pixels[target + 2] = index + 2 < palette.Length ? palette[index + 2] : (byte)0;
                        bitmap.Pixels[target + 3] = paletteAlpha is not null && Sample(0) < paletteAlpha.Length
                            ? paletteAlpha[Sample(0)]
                            : (byte)255;
                        break;

                    case 4:
                        bitmap.Pixels[target] = bitmap.Pixels[target + 1] = bitmap.Pixels[target + 2] = Sample(0);
                        bitmap.Pixels[target + 3] = Sample(1);
                        break;

                    default:
                        bitmap.Pixels[target] = Sample(0);
                        bitmap.Pixels[target + 1] = Sample(1);
                        bitmap.Pixels[target + 2] = Sample(2);
                        bitmap.Pixels[target + 3] = samples >= 4 ? Sample(3) : (byte)255;
                        break;
                }
            }
        }

        return bitmap;
    }

    // ── 校验和 ──────────────────────────────────────────────────────

    /// <summary>Adler-32。编码时用作 zlib trailer；对 1MB 以上数据用 NMAX 分块避免频繁取模。</summary>
    public static uint Adler32(ReadOnlySpan<byte> data)
    {
        const uint modulo = 65521;
        const int blockSize = 5552;   // NMAX：保证累加不溢出前取模

        uint a = 1, b = 0;
        var index = 0;
        while (index < data.Length)
        {
            var count = Math.Min(blockSize, data.Length - index);
            for (var i = 0; i < count; i++)
            {
                a += data[index + i];
                b += a;
            }

            a %= modulo;
            b %= modulo;
            index += count;
        }

        return (b << 16) | a;
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
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    /// <summary>CRC-32（PNG 用的多项式 0xEDB88320）。</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
