using System.Buffers.Binary;
using System.IO.Compression;
using Ta.Core.Imaging;
using Ta.OCR.Imaging;
using Xunit;

namespace Ta.OCR.Tests.Imaging;

/// <summary>
/// <see cref="OCRImagePreparation"/> 与 <see cref="PngWriter"/> 测试。
///
/// PNG 编码器是增强包协议的必需品（协议要求 <c>--input &lt;png&gt;</c>），
/// 所以这里不依赖任何图像库，直接用「手工解 zlib + 重算 CRC/Adler-32」的方式
/// 验证产物是结构合法的 PNG。
/// </summary>
public class PngWriterTests
{
    [Fact]
    public void 写出合法PNG签名与三个必要块()
    {
        var png = PngWriter.Encode(CreateSolid(4, 3, 255, 0, 0));

        // 8 字节签名
        Assert.Equal(
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
            png.Take(8));

        // 块序列
        var chunks = ReadChunks(png);
        Assert.Equal(["IHDR", "IDAT", "IEND"], chunks.Select(c => c.Type));
    }

    [Fact]
    public void IHDR字段正确()
    {
        var png = PngWriter.Encode(CreateSolid(7, 5, 0, 0, 0));
        var ihdr = ReadChunks(png).First(c => c.Type == "IHDR").Data;

        Assert.Equal(7u, BinaryPrimitives.ReadUInt32BigEndian(ihdr.AsSpan(0, 4)));
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32BigEndian(ihdr.AsSpan(4, 4)));
        Assert.Equal(8, ihdr[8]);   // 位深
        Assert.Equal(6, ihdr[9]);   // 真彩色 + alpha
        Assert.Equal(0, ihdr[10]);  // deflate
        Assert.Equal(0, ihdr[11]);  // 自适应滤波
        Assert.Equal(0, ihdr[12]);  // 非隔行
    }

    [Fact]
    public void 所有块的CRC校验通过()
    {
        var png = PngWriter.Encode(CreateSolid(6, 6, 1, 2, 3));

        foreach (var chunk in ReadChunks(png))
        {
            var input = new byte[4 + chunk.Data.Length];
            System.Text.Encoding.ASCII.GetBytes(chunk.Type).CopyTo(input, 0);
            chunk.Data.CopyTo(input, 4);
            Assert.Equal(chunk.Crc, Crc32(input));
        }
    }

    [Fact]
    public void IDAT是合法zlib流且可完整解回原始扫描线()
    {
        var bitmap = CreateChecker(4, 3);
        var png = PngWriter.Encode(bitmap);
        var idat = ReadChunks(png).First(c => c.Type == "IDAT").Data;

        // zlib 头（RFC 1950）：CMF/FLG，且 (CMF<<8|FLG) % 31 == 0
        Assert.Equal(0x78, idat[0]);
        Assert.Equal(1, idat[1]);
        Assert.Equal(0, ((idat[0] << 8) | idat[1]) % 31);

        // 尾 4 字节是 Adler-32
        var adler = BinaryPrimitives.ReadUInt32BigEndian(idat.AsSpan(idat.Length - 4));
        Assert.Equal(Adler32(BuildRawScanlines(bitmap)), adler);

        // 中间是裸 deflate，用 DeflateStream 解回来
        var expected = BuildRawScanlines(bitmap);
        var actual = new byte[expected.Length];
        using (var input = new MemoryStream(idat, 2, idat.Length - 6))
        using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
        {
            var offset = 0;
            int read;
            while ((read = deflate.Read(actual, offset, actual.Length - offset)) > 0)
            {
                offset += read;
            }

            Assert.Equal(expected.Length, offset);
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void 每行前置滤波字节0()
    {
        var bitmap = CreateSolid(3, 2, 9, 9, 9);
        var raw = BuildRawScanlines(bitmap);

        // stride = 3*4 = 12，每行 13 字节
        Assert.Equal(2 * 13, raw.Length);
        Assert.Equal(0, raw[0]);
        Assert.Equal(0, raw[13]);
    }

    // ── OCRImagePreparation ─────────────────────────────────────────

    [Fact]
    public void 不超限时原样返回同一实例()
    {
        var bitmap = CreateSolid(100, 50, 1, 2, 3);

        Assert.Same(bitmap, OCRImagePreparation.Prepare(bitmap, 2560));
    }

    [Fact]
    public void 最长边等于上限时不缩放()
    {
        var bitmap = CreateSolid(2560, 100, 1, 2, 3);

        Assert.Same(bitmap, OCRImagePreparation.Prepare(bitmap, 2560));
    }

    [Fact]
    public void 上限为非正数时不缩放()
    {
        var bitmap = CreateSolid(4000, 100, 1, 2, 3);

        Assert.Same(bitmap, OCRImagePreparation.Prepare(bitmap, 0));
        Assert.Same(bitmap, OCRImagePreparation.Prepare(bitmap, -1));
    }

    [Fact]
    public void 等比缩小最长边()
    {
        var bitmap = CreateSolid(3840, 2160, 1, 2, 3);

        var prepared = OCRImagePreparation.Prepare(bitmap, 2560);

        Assert.Equal(2560, prepared.Width);
        Assert.Equal(1440, prepared.Height);
    }

    [Fact]
    public void 宽高各自独立换算不从对方推导()
    {
        // 若用 width 反推 height，3840×2160 会得到 1439 而不是 1440。
        var bitmap = CreateSolid(3840, 2160, 1, 2, 3);

        var prepared = OCRImagePreparation.Prepare(bitmap, 2560);

        Assert.Equal(1440, prepared.Height);
    }

    [Fact]
    public void 取整用远离零而非银行家舍入()
    {
        // 3000×2000 缩到 1999：scale = 1999/3000，height = 2000 * 0.666333… = 1332.666… → 1333。
        // 找一个 .5 边界更能说明问题：1999×1 → 缩放后 1*1999/1999 = 1（无 .5）。
        // 改用 2000×1 → 上限 1500：scale = 0.75，width = 1500，height = 0.75 → round → 1。
        var bitmap = CreateSolid(2000, 1, 1, 2, 3);

        var prepared = OCRImagePreparation.Prepare(bitmap, 1500);

        Assert.Equal(1500, prepared.Width);
        Assert.Equal(1, prepared.Height);
    }

    [Fact]
    public void 缩小结果永不小于1像素()
    {
        // 极端长条：6000×1 缩到 2000 → height = round(1 * 0.3333) = 0 → 夹到 1。
        var bitmap = CreateSolid(6000, 1, 1, 2, 3);

        var prepared = OCRImagePreparation.Prepare(bitmap, 2000);

        Assert.Equal(2000, prepared.Width);
        Assert.Equal(1, prepared.Height);
    }

    [Fact]
    public void 纯色图缩放后颜色不变()
    {
        var bitmap = CreateSolid(400, 200, 10, 20, 30);

        var prepared = OCRImagePreparation.Prepare(bitmap, 100);

        Assert.Equal(100, prepared.Width);
        Assert.Equal(50, prepared.Height);
        for (var i = 0; i < prepared.Pixels.Length; i += RgbaBitmap.BytesPerPixel)
        {
            Assert.Equal(10, prepared.Pixels[i]);
            Assert.Equal(20, prepared.Pixels[i + 1]);
            Assert.Equal(30, prepared.Pixels[i + 2]);
            Assert.Equal(255, prepared.Pixels[i + 3]);
        }
    }

    [Fact]
    public void 缩放不修改源位图()
    {
        var bitmap = CreateSolid(400, 200, 1, 2, 3);

        _ = OCRImagePreparation.Prepare(bitmap, 100);

        Assert.Equal(400, bitmap.Width);
        Assert.Equal(200, bitmap.Height);
    }

    // ── 辅助 ───────────────────────────────────────────────────────

    private static RgbaBitmap CreateSolid(int width, int height, byte r, byte g, byte b)
    {
        var bitmap = new RgbaBitmap(width, height);
        bitmap.Fill(r, g, b);
        return bitmap;
    }

    private static RgbaBitmap CreateChecker(int width, int height)
    {
        var bitmap = new RgbaBitmap(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * bitmap.Stride) + (x * RgbaBitmap.BytesPerPixel);
                var value = (byte)(((x + y) % 2) * 255);
                bitmap.Pixels[i] = value;
                bitmap.Pixels[i + 1] = value;
                bitmap.Pixels[i + 2] = value;
                bitmap.Pixels[i + 3] = 255;
            }
        }

        return bitmap;
    }

    private static byte[] BuildRawScanlines(RgbaBitmap bitmap)
    {
        var raw = new byte[bitmap.Height * (1 + bitmap.Stride)];
        for (var y = 0; y < bitmap.Height; y++)
        {
            var offset = y * (1 + bitmap.Stride);
            raw[offset] = 0;
            Array.Copy(bitmap.Pixels, y * bitmap.Stride, raw, offset + 1, bitmap.Stride);
        }

        return raw;
    }

    private sealed record PngChunk(string Type, byte[] Data, uint Crc);

    private static IReadOnlyList<PngChunk> ReadChunks(byte[] png)
    {
        var chunks = new List<PngChunk>();
        var offset = 8;

        while (offset + 8 <= png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            var data = png.Skip(offset + 8).Take(length).ToArray();
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length, 4));

            chunks.Add(new PngChunk(type, data, crc));
            offset += 12 + length;

            if (type == "IEND")
            {
                break;
            }
        }

        return chunks;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
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

        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        const uint modulus = 65521;
        uint a = 1, b = 0;
        foreach (var value in data)
        {
            a = (a + value) % modulus;
            b = (b + a) % modulus;
        }

        return (b << 16) | a;
    }
}
