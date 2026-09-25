using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Ta.Core.Imaging;
using Ta.Translate;
using Ta.Translate.Rendering;

namespace Ta.Translate.Tests;

/// <summary>
/// ⚠️ 临时诊断用例：渲染已知内容的图并存成 PNG，供人工目视检查排版/字形质量。
/// 仅当环境变量 TA_TRANSLATE_DUMP 指向目录时执行。不属于常规回归集。
/// （自带极简 PNG 编码器，避免依赖 Ta.Encoding 的 WIC COM 初始化。）
/// </summary>
public class VisualDumpTests
{
    [Fact]
    public void Dump渲染结果()
    {
        var dir = Environment.GetEnvironmentVariable("TA_TRANSLATE_DUMP");
        if (string.IsNullOrWhiteSpace(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        var renderer = new TranslatedImageRenderer();

        // 1) bilingual：上方是带文字的「原图」，下方是双语面板。
        var source = MakePhotoLikeSource(720, 300);
        var bilingualLines = new[]
        {
            TestBitmaps.TranslatedLine(
                "Screenshot translation tool",
                "截图翻译工具",
                0.05, 0.15, 0.9, 0.12),
            TestBitmaps.TranslatedLine(
                "Translate visible text in place, keeping the original layout intact.",
                "在原位翻译可见文字，保持原有排版不变。",
                0.05, 0.4, 0.9, 0.12),
            TestBitmaps.TranslatedLine(
                "Bilingual panel: source text above, translation below.",
                "双语面板：源文在上，译文在下。",
                0.05, 0.65, 0.9, 0.12),
        };
        var bilingual = renderer.Render(source, bilingualLines, ScreenshotTranslationMode.BilingualImage, "简体中文");
        File.WriteAllBytes(Path.Combine(dir, "bilingual.png"), EncodePng(bilingual));

        // 2) fullImage：就地替换文字。
        var fullLines = new[]
        {
            TestBitmaps.TranslatedLine("Hello World", "你好，世界", 0.08, 0.30, 0.35, 0.16),
            TestBitmaps.TranslatedLine("Settings", "设置", 0.55, 0.30, 0.30, 0.16),
            TestBitmaps.TranslatedLine("A longer English sentence to test word wrapping in the fitted box",
                "这是一句更长的中文，用来测试字号拟合与自动换行", 0.08, 0.55, 0.84, 0.30),
        };
        var full = renderer.Render(source, fullLines, ScreenshotTranslationMode.FullImage, "简体中文");
        File.WriteAllBytes(Path.Combine(dir, "fullimage.png"), EncodePng(full));
    }

    // ---------- 极简 PNG 编码（仅诊断用）：RGBA → 无损 PNG，zlib 用 .NET 自带 DeflateStream ----------

    private static byte[] EncodePng(RgbaBitmap bitmap)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var ihdr = new byte[13];
        WriteUInt32BE(ihdr, 0, (uint)bitmap.Width);
        WriteUInt32BE(ihdr, 4, (uint)bitmap.Height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // RGBA
        WriteChunk(ms, "IHDR", ihdr);

        // 原始扫描线：每行前加 filter 字节 0。
        var raw = new byte[(bitmap.Width * 4) + 1];
        using (var idat = new MemoryStream())
        {
            for (var y = 0; y < bitmap.Height; y++)
            {
                Buffer.BlockCopy(bitmap.Pixels, y * bitmap.Stride, raw, 1, bitmap.Width * 4);
                idat.Write(raw, 0, raw.Length);
            }

            idat.Position = 0;
            using var compressed = new MemoryStream();
            using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                idat.CopyTo(deflate);
            }

            WriteChunk(ms, "IDAT", compressed.ToArray());
        }

        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void WriteChunk(MemoryStream ms, string type, byte[] data)
    {
        var length = new byte[4];
        WriteUInt32BE(length, 0, (uint)data.Length);
        ms.Write(length, 0, 4);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        ms.Write(typeBytes, 0, 4);
        ms.Write(data, 0, data.Length);

        var crcInput = new byte[4 + data.Length];
        Buffer.BlockCopy(typeBytes, 0, crcInput, 0, 4);
        Buffer.BlockCopy(data, 0, crcInput, 4, data.Length);

        var crc = new byte[4];
        WriteUInt32BE(crc, 0, Crc32(crcInput));
        ms.Write(crc, 0, 4);
    }

    private static void WriteUInt32BE(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
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

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static RgbaBitmap MakePhotoLikeSource(int width, int height)
    {
        var bitmap = new RgbaBitmap(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * bitmap.Stride) + (x * 4);

                // 渐变底 + 两条浅色「文字带」，模拟截图的明暗背景。
                var gradient = (byte)(40 + (180 * y / height));
                bitmap.Pixels[i] = gradient;
                bitmap.Pixels[i + 1] = (byte)(gradient + 20);
                bitmap.Pixels[i + 2] = (byte)(255 - gradient);

                var bandA = y > 60 && y < 105;
                var bandB = y > 170 && y < 215;
                if (bandA || bandB)
                {
                    bitmap.Pixels[i] = 245;
                    bitmap.Pixels[i + 1] = 245;
                    bitmap.Pixels[i + 2] = 245;
                }

                bitmap.Pixels[i + 3] = 255;
            }
        }

        return bitmap;
    }
}
