using System.IO.Compression;
using Ta.OCR.Packs;
using Xunit;

namespace Ta.OCR.Tests.Packs;

/// <summary>
/// ZIP slip 防护测试（验收项 3）。
///
/// 对应 Mac 版 <c>validateArchiveEntries</c>
/// （<c>OptionalOCRPackManager.swift:444-460</c>）：那里调
/// <c>/usr/bin/zipinfo -1</c> 列条目后拒绝「前导 <c>/</c>、含 <c>\</c>、含 <c>..</c> 组件」。
/// Windows 侧用 <c>System.IO.Compression</c> 自己枚举，规则 = Mac 三条 + Windows 特有的四条。
///
/// 这里既测**纯函数** <see cref="OcrPackArchiveGuard"/>，也测**真实 zip**：
/// 构造含 <c>../</c> 条目的压缩包，断言安装管线整体拒绝、且磁盘上没有任何文件落盘。
/// </summary>
public class OcrPackArchiveGuardTests
{
    // ── Mac 原有三条规则 ────────────────────────────────────────────

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("/absolute/path/file.txt")]
    [InlineData("/")]
    public void 前导斜杠被拒(string entry)
    {
        Assert.False(OcrPackArchiveGuard.IsEntryNameSafe(entry));
        Assert.NotNull(OcrPackArchiveGuard.ValidateEntryName(entry));
    }

    [Theory]
    [InlineData("..\\..\\windows\\system32\\evil.dll")]
    [InlineData("bin\\adapter.exe")]
    [InlineData("a\\b\\c.txt")]
    public void 含反斜杠被拒(string entry)
        => Assert.False(OcrPackArchiveGuard.IsEntryNameSafe(entry));

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("bin/../../evil.txt")]
    [InlineData("bin/../evil.txt")]
    [InlineData("..")]
    [InlineData("a/../../b")]
    public void 含上级目录组件被拒(string entry)
        => Assert.False(OcrPackArchiveGuard.IsEntryNameSafe(entry));

    // ── Windows 特有的补充规则 ──────────────────────────────────────

    [Theory]
    [InlineData("C:\\Windows\\System32\\evil.dll")]
    [InlineData("c:/temp/evil.txt")]
    [InlineData("C:evil.txt")]
    public void 盘符路径被拒(string entry)
        => Assert.False(OcrPackArchiveGuard.IsEntryNameSafe(entry));

    [Theory]
    [InlineData("//server/share/evil.txt")]
    [InlineData("\\\\server\\share\\evil.txt")]
    public void UNC路径被拒(string entry)
        => Assert.False(OcrPackArchiveGuard.IsEntryNameSafe(entry));

    [Theory]
    [InlineData("\\evil.txt")]
    [InlineData("\\Windows\\evil.txt")]
    public void 设备路径被拒(string entry)
        => Assert.False(OcrPackArchiveGuard.IsEntryNameSafe(entry));

    [Theory]
    [InlineData("?/GlobalRoot/evil.txt")]
    public void NT设备前缀被拒(string entry)
        => Assert.False(OcrPackArchiveGuard.IsEntryNameSafe(entry));

    [Fact]
    public void 空条目名被拒()
        => Assert.False(OcrPackArchiveGuard.IsEntryNameSafe(null));

    // ── 合法条目必须放行 ────────────────────────────────────────────

    [Theory]
    [InlineData("manifest.json")]
    [InlineData("bin/paddleocr-adapter")]
    [InlineData("models/PP-OCRv5_mobile_det_onnx/inference.onnx")]
    [InlineData("a/b/c/d/e/f.txt")]
    [InlineData("bin/")]                       // 目录条目（结尾斜杠）
    [InlineData("..hidden")]                   // 只是**以**点开头，不是 ".." 组件
    [InlineData("a..b")]                       // 点出现在中间，不是组件
    [InlineData("中文/文件.txt")]
    [InlineData("a//b")]                       // 连续斜杠：Mac 用 omittingEmptySubsequences: false 切分，仍不含 ".."
    [InlineData("file..txt")]
    public void 合法条目放行(string entry)
    {
        Assert.True(OcrPackArchiveGuard.IsEntryNameSafe(entry));
        Assert.Null(OcrPackArchiveGuard.ValidateEntryName(entry));
    }

    // ── 落盘路径夹取（纵深防御） ────────────────────────────────────

    [Fact]
    public void 合法条目解析到目标目录内()
    {
        var root = Path.Combine(Path.GetTempPath(), "ocr-guard-root");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

        Assert.True(OcrPackArchiveGuard.TryResolveDestination(root, "bin/adapter.exe", out var fullPath));
        Assert.Equal(Path.Combine(root, "bin", "adapter.exe"), fullPath);
        Assert.StartsWith(root, fullPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 嵌套条目解析到目标目录内()
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ocr-guard-root")));

        Assert.True(OcrPackArchiveGuard.TryResolveDestination(root, "a/b/c.txt", out var fullPath));
        Assert.Equal(Path.Combine(root, "a", "b", "c.txt"), fullPath);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("..\\escape.txt")]
    [InlineData("C:\\escape.txt")]
    public void 不安全条目不产出落盘路径(string entry)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ocr-guard-root")));

        Assert.False(OcrPackArchiveGuard.TryResolveDestination(root, entry, out var fullPath));
        Assert.Equal(string.Empty, fullPath);
    }

    [Fact]
    public void 目录条目解析到目录本身()
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ocr-guard-root")));

        Assert.True(OcrPackArchiveGuard.TryResolveDestination(root, "bin/", out var fullPath));
        // Path.GetFullPath 会给目录补上尾部分隔符，所以期望值也要带。
        Assert.Equal(Path.Combine(root, "bin") + Path.DirectorySeparatorChar, fullPath);
    }

    [Fact]
    public void 同名前缀目录不能冒充父目录()
    {
        // "/root/pack" 与 "/root/pack-evil"：前缀比较必须带分隔符，
        // 否则后者会被当成前者内部路径放行。
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ocr-guard-pack")));

        Assert.False(OcrPackArchiveGuard.TryResolveDestination(root, "../pack-evil/x.txt", out _));
    }

    // ── 真实 zip：构造恶意压缩包并验证被拒 ───────────────────────────

    [Fact]
    public void 含上级目录条目的真实压缩包被拒绝且不落盘()
    {
        using var sandbox = new TempDirectory();
        var archive = Path.Combine(sandbox.Path, "evil.zip");

        // 恶意条目 "bin/../../escape.txt" + 一个正常条目
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("bin/../../escape.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("pwned");
        }

        // 目标目录在 sandbox 内，escape.txt 若成功落盘会出现在 sandbox 外面（父目录）
        var destination = Path.Combine(sandbox.Path, "extracted");
        Directory.CreateDirectory(destination);

        var escaped = Path.GetFullPath(Path.Combine(sandbox.Path, "..", "escape.txt"));
        if (File.Exists(escaped))
        {
            File.Delete(escaped);
        }

        using var opened = ZipFile.OpenRead(archive);
        var unsafeEntries = opened.Entries
            .Where(item => !OcrPackArchiveGuard.IsEntryNameSafe(item.FullName))
            .ToList();

        // 断言 1：恶意条目被识别出来
        var entryName = Assert.Single(unsafeEntries).FullName;
        Assert.Contains("..", entryName);

        // 断言 2：不允许解压
        Assert.False(OcrPackArchiveGuard.TryResolveDestination(destination, entryName, out _));

        // 断言 3：磁盘上确实什么都没写出去
        Assert.False(File.Exists(escaped));
        Assert.Empty(Directory.GetFiles(destination));
    }

    [Fact]
    public void 含反斜杠条目的真实压缩包被拒绝()
    {
        using var sandbox = new TempDirectory();
        var archive = Path.Combine(sandbox.Path, "backslash.zip");

        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("..\\..\\escape.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("pwned");
        }

        using var opened = ZipFile.OpenRead(archive);
        var fullName = Assert.Single(opened.Entries).FullName;

        Assert.False(OcrPackArchiveGuard.IsEntryNameSafe(fullName));
    }

    [Fact]
    public void 恶意条目与合法条目混在一起时整体拒绝()
    {
        using var sandbox = new TempDirectory();
        var archive = Path.Combine(sandbox.Path, "mixed.zip");

        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
            {
                writer.Write("{}");
            }

            using (var writer = new StreamWriter(zip.CreateEntry("../evil.txt").Open()))
            {
                writer.Write("pwned");
            }
        }

        using var opened = ZipFile.OpenRead(archive);
        var anyUnsafe = opened.Entries.Any(item => !OcrPackArchiveGuard.IsEntryNameSafe(item.FullName));

        // 一个坏条目就整体拒绝（Mac: :456-458 的 for 循环 + throw）
        Assert.True(anyUnsafe);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ta-ocr-guard-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // 测试收尾失败不影响断言
            }
        }
    }
}
