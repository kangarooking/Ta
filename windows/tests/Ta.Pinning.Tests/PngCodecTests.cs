using Ta.Core.Imaging;
using Ta.Pinning.Imaging;

namespace Ta.Pinning.Tests;

/// <summary>
/// 纯托管 PNG 编解码测试。
/// 存在理由见 <see cref="PngCodec"/>：钉图的「复制图片」写 PNG，
/// 从剪贴板/文件读图也要解 PNG。
/// </summary>
public class PngCodecTests
{
    private static RgbaBitmap CreateGradient(int width, int height)
    {
        var bitmap = new RgbaBitmap(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = (y * bitmap.Stride) + (x * RgbaBitmap.BytesPerPixel);
                bitmap.Pixels[index] = (byte)((x * 7 + y * 13) % 256);
                bitmap.Pixels[index + 1] = (byte)((x * 29 + y * 3) % 256);
                bitmap.Pixels[index + 2] = (byte)((x * 5 + y * 41) % 256);
                bitmap.Pixels[index + 3] = (byte)((x + y) % 2 == 0 ? 255 : 128);
            }
        }

        return bitmap;
    }

    [Fact]
    public void 编码再解码逐像素一致()
    {
        var source = CreateGradient(37, 23);
        var encoded = PngCodec.Encode(source);
        var decoded = PngCodec.Decode(encoded);

        Assert.NotNull(decoded);
        Assert.Equal(source.Width, decoded!.Width);
        Assert.Equal(source.Height, decoded.Height);
        Assert.Equal(source.Pixels, decoded.Pixels);
    }

    [Fact]
    public void 编码结果带标准PNG签名与IHDR()
    {
        var source = CreateGradient(4, 4);
        var encoded = PngCodec.Encode(source);

        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, encoded.Take(8).ToArray());
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(encoded.Skip(12).Take(4).ToArray()));
        // 签名 8 + IHDR chunk(4+4+13+4 = 25) = 33 → IDAT 的 type 在 8+25+4 = 37
        Assert.Equal("IDAT", System.Text.Encoding.ASCII.GetString(encoded.Skip(37).Take(4).ToArray()));
    }

    [Fact]
    public void 单像素图也能往返()
    {
        var source = new RgbaBitmap(1, 1);
        source.Pixels[0] = 12;
        source.Pixels[1] = 34;
        source.Pixels[2] = 56;
        source.Pixels[3] = 78;

        var decoded = PngCodec.Decode(PngCodec.Encode(source));

        Assert.NotNull(decoded);
        Assert.Equal(source.Pixels, decoded!.Pixels);
    }

    [Fact]
    public void 长条图往返后高度不丢()
    {
        // 长截图动辄几万像素高，行数与 stride 不能算错。
        var source = CreateGradient(3, 5000);
        var decoded = PngCodec.Decode(PngCodec.Encode(source));

        Assert.NotNull(decoded);
        Assert.Equal(5000, decoded!.Height);
        Assert.Equal(source.Pixels, decoded.Pixels);
    }

    [Fact]
    public void 非PNG数据返回null()
    {
        Assert.Null(PngCodec.Decode(new byte[] { 1, 2, 3, 4 }));
        Assert.Null(PngCodec.Decode(Array.Empty<byte>()));
    }

    [Fact]
    public void 截断的PNG返回null而不是抛异常()
    {
        var encoded = PngCodec.Encode(CreateGradient(8, 8));
        Assert.Null(PngCodec.Decode(encoded.Take(encoded.Length / 2).ToArray()));
    }

    [Fact]
    public void 解码手写的非隔行真彩PNG()
    {
        // 2×1 真彩（colorType 2）：1 行 = filter(1) + 3 字节 = 4 字节
        var raw = new byte[] { 0, 255, 0, 0, 0, 0, 255 };
        var idat = ZlibWrap(raw);

        var png = new List<byte> { 137, 80, 78, 71, 13, 10, 26, 10 };
        png.AddRange(Chunk("IHDR", new byte[]
        {
            0, 0, 0, 2,      // width
            0, 0, 0, 1,      // height
            8,               // bit depth
            2,               // color type: truecolor
            0, 0, 0,
        }));
        png.AddRange(Chunk("IDAT", idat));
        png.AddRange(Chunk("IEND", Array.Empty<byte>()));

        var decoded = PngCodec.Decode(png.ToArray());

        Assert.NotNull(decoded);
        Assert.Equal(2, decoded!.Width);
        Assert.Equal(1, decoded.Height);
        Assert.Equal(255, decoded.Pixels[0]);   // 左=红
        Assert.Equal(0, decoded.Pixels[1]);
        Assert.Equal(0, decoded.Pixels[2]);
        Assert.Equal(255, decoded.Pixels[3]);
        Assert.Equal(0, decoded.Pixels[4]);     // 右=蓝
        Assert.Equal(255, decoded.Pixels[6]);
    }

    [Fact]
    public void 带Sub滤镜的PNG也能解()
    {
        // 两行 2px 灰度（colorType 0），第二行用 Sub 滤镜（filter type 1）。
        var source = new RgbaBitmap(2, 2);
        source.Fill(10, 20, 30, 255);

        // 手工构造 2×2 灰度（colorType 0），stride = 2：
        // 第 0 行 filter=0：10, 10
        // 第 1 行 filter=1（Sub，参照**左侧**像素）：差 10, 0 → 10, 10
        var raw = new byte[] { 0, 10, 10, 1, 10, 0 };
        var idat = ZlibWrap(raw);

        var png = new List<byte> { 137, 80, 78, 71, 13, 10, 26, 10 };
        png.AddRange(Chunk("IHDR", new byte[]
        {
            0, 0, 0, 2, 0, 0, 0, 2, 8, 0, 0, 0, 0,
        }));
        png.AddRange(Chunk("IDAT", idat));
        png.AddRange(Chunk("IEND", Array.Empty<byte>()));

        var decoded = PngCodec.Decode(png.ToArray());

        Assert.NotNull(decoded);
        // 灰度源：r = g = b = gray
        Assert.Equal(10, decoded!.Pixels[0]);
        Assert.Equal(10, decoded.Pixels[1]);
        Assert.Equal(10, decoded.Pixels[2]);
        Assert.Equal(255, decoded.Pixels[3]);
        // 第 1 行应与第 0 行相同（Sub 差值为 0）
        Assert.Equal(10, decoded.Pixels[8]);
        Assert.Equal(10, decoded.Pixels[9]);
        Assert.Equal(10, decoded.Pixels[10]);
        _ = source;
    }

    [Fact]
    public void Adler32与CRC32与已知值一致()
    {
        // "Wikipedia" 的 Adler-32 是 0x11E60398
        var data = System.Text.Encoding.ASCII.GetBytes("Wikipedia");
        Assert.Equal(0x11E60398u, PngCodec.Adler32(data));

        // CRC-32("123456789") = 0xCBF43926
        var digits = System.Text.Encoding.ASCII.GetBytes("123456789");
        Assert.Equal(0xCBF43926u, PngCodec.Crc32(digits));
    }

    private static byte[] ZlibWrap(byte[] raw)
    {
        using var buffer = new MemoryStream();
        using (var deflate = new System.IO.Compression.DeflateStream(buffer, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw, 0, raw.Length);
        }

        var deflated = buffer.ToArray();
        var result = new byte[deflated.Length + 6];
        result[0] = 0x78;
        result[1] = 0x01;
        Buffer.BlockCopy(deflated, 0, result, 2, deflated.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
            result.AsSpan(deflated.Length + 2), PngCodec.Adler32(raw));
        return result;
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        var result = new byte[12 + data.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0, 4), (uint)data.Length);
        for (var i = 0; i < 4; i++)
        {
            result[4 + i] = (byte)type[i];
        }

        Buffer.BlockCopy(data, 0, result, 8, data.Length);

        var crcInput = new byte[4 + data.Length];
        Buffer.BlockCopy(result, 4, crcInput, 0, 4);
        Buffer.BlockCopy(data, 0, crcInput, 4, data.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
            result.AsSpan(8 + data.Length, 4), PngCodec.Crc32(crcInput));
        return result;
    }
}
