using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

if (args.Length != 2)
{
    Console.Error.WriteLine("用法：Ta.IconBuilder <源PNG> <目标ICO>");
    return 1;
}

var sourcePath = Path.GetFullPath(args[0]);
var targetPath = Path.GetFullPath(args[1]);
if (!File.Exists(sourcePath))
{
    Console.Error.WriteLine($"图标源文件不存在：{sourcePath}");
    return 1;
}

Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
using var source = new Bitmap(sourcePath);
var sizes = new[] { 16, 32, 48, 256 };
var images = sizes.Select(size => RenderPng(source, size)).ToArray();
try
{
    using var output = File.Create(targetPath);
    using var writer = new BinaryWriter(output);
    writer.Write((ushort)0);
    writer.Write((ushort)1);
    writer.Write((ushort)images.Length);

    var offset = 6 + (16 * images.Length);
    for (var index = 0; index < images.Length; index++)
    {
        var size = sizes[index];
        writer.Write((byte)(size == 256 ? 0 : size));
        writer.Write((byte)(size == 256 ? 0 : size));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(checked((uint)images[index].Length));
        writer.Write(checked((uint)offset));
        offset = checked(offset + (int)images[index].Length);
    }

    foreach (var image in images)
    {
        writer.Write(image.ToArray());
    }
}
finally
{
    foreach (var image in images)
    {
        image.Dispose();
    }
}

Console.WriteLine($"Windows 图标已生成：{targetPath}");
return 0;

static MemoryStream RenderPng(Bitmap source, int size)
{
    using var resized = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using (var graphics = Graphics.FromImage(resized))
    {
        graphics.Clear(Color.Transparent);
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, 0, 0, size, size);
    }

    var stream = new MemoryStream();
    resized.Save(stream, ImageFormat.Png);
    stream.Position = 0;
    return stream;
}
