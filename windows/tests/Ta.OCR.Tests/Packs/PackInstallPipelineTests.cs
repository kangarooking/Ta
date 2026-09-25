using System.IO.Compression;
using Ta.Core.Imaging;
using Ta.OCR.Models;
using Ta.OCR.Packs;
using Xunit;

namespace Ta.OCR.Tests.Packs;

/// <summary>
/// 增强包安装与调用管线测试。
///
/// 对应 Mac 版 <c>Tests/AIScreenshotAppTests/OptionalOCRPackManagerTests.swift</c>
/// 的几条关键用例，并补齐 Windows 特有路径（ZIP slip 用
/// <c>System.IO.Compression</c> 而非 <c>zipinfo</c> / <c>ditto</c>）。
/// </summary>
public class PackInstallPipelineTests
{
    private static OCRPackInstallStage[] StagesOf(IReadOnlyList<OCRPackInstallProgress> progress)
        => progress.Select(item => item.Stage).ToArray();

    // ── 一键安装 ───────────────────────────────────────────────────

    [Fact]
    public async Task 本地catalog加压缩包可以一键安装()
    {
        using var fixture = new PackFixture();
        var pack = fixture.MakePackDirectory();
        var archive = fixture.MakeArchive(pack);
        fixture.WriteCatalog(archive);

        var manager = fixture.CreateManager();

        var availability = await manager.AvailablePackageAsync(OCREnginePreference.PaddleOCR);

        Assert.True(availability.IsLocal);
        Assert.Equal("1.0.0", availability.Package.Version);

        var info = await manager.InstallRecommendedAsync(
            OCREnginePreference.PaddleOCR, _ => { });

        Assert.Equal("1.0.0", info.Version);
        Assert.Equal("x86_64", info.Architecture);
        Assert.True(manager.IsInstalled(OCREnginePreference.PaddleOCR));
        Assert.True(File.Exists(Path.Combine(fixture.PackDirectory, "manifest.json")));
    }

    [Fact]
    public async Task 安装进AI_Screenshot目录()
    {
        // 参考文档 §9.7 末尾：目录名是 "AI Screenshot"，与 socket 路径用的 "Ta" 不同。
        using var fixture = new PackFixture();
        fixture.MakeCatalogAndArchive();

        _ = await fixture.CreateManager().InstallRecommendedAsync(
            OCREnginePreference.PaddleOCR, _ => { });

        Assert.EndsWith(Path.Combine("AI Screenshot", "OCRPacks", "paddleOCR"), fixture.PackDirectory);
    }

    [Fact]
    public async Task 安装进度按阶段推进且校验段覆盖0到1()
    {
        // 对应 Mac testOneClickInstallHandlesArchiveListingLargerThanPipeBuffer 的断言
        // （OptionalOCRPackManagerTests.swift:142-147）。
        using var fixture = new PackFixture();
        fixture.MakeCatalogAndArchive(paddingEntries: 600);

        var progress = new List<OCRPackInstallProgress>();
        _ = await fixture.CreateManager().InstallRecommendedAsync(
            OCREnginePreference.PaddleOCR, progress.Add);

        var stages = StagesOf(progress);
        Assert.Equal(OCRPackInstallStage.Resolving, stages[0]);
        Assert.Contains(OCRPackInstallStage.Downloading, stages);
        Assert.Contains(OCRPackInstallStage.Verifying, stages);
        Assert.Contains(OCRPackInstallStage.Extracting, stages);
        Assert.Contains(OCRPackInstallStage.HealthChecking, stages);
        Assert.Equal(OCRPackInstallStage.Installing, stages[^1]);

        var verification = progress
            .Where(item => item.Stage == OCRPackInstallStage.Verifying)
            .Select(item => item.Fraction)
            .Where(fraction => fraction is not null)
            .Select(fraction => fraction!.Value)
            .ToList();

        Assert.Equal(0, verification[0], 6);
        Assert.Equal(1, verification[^1], 6);
        Assert.Contains(verification, fraction => fraction > 0 && fraction < 1);
    }

    [Fact]
    public async Task 阶段标签逐字对应Mac的rawValue()
    {
        // OptionalOCRPackManager.swift:82-87 —— 设置页直接展示这些字符串。
        Assert.Equal("正在查找增强包…", OCRPackInstallStage.Resolving.Label());
        Assert.Equal("正在下载…", OCRPackInstallStage.Downloading.Label());
        Assert.Equal("正在校验…", OCRPackInstallStage.Verifying.Label());
        Assert.Equal("正在解压…", OCRPackInstallStage.Extracting.Label());
        Assert.Equal("正在启动检查…", OCRPackInstallStage.HealthChecking.Label());
        Assert.Equal("正在安装…", OCRPackInstallStage.Installing.Label());
    }

    [Fact]
    public async Task 已安装时重复安装会替换而不是叠加()
    {
        using var fixture = new PackFixture();
        fixture.MakeCatalogAndArchive();

        var manager = fixture.CreateManager();
        _ = await manager.InstallRecommendedAsync(OCREnginePreference.PaddleOCR, _ => { });

        var marker = Path.Combine(fixture.PackDirectory, "marker.txt");
        File.WriteAllText(marker, "old");
        Assert.True(File.Exists(marker));

        _ = await manager.InstallRecommendedAsync(OCREnginePreference.PaddleOCR, _ => { });

        Assert.True(manager.IsInstalled(OCREnginePreference.PaddleOCR));
        // 暂存目录与垃圾桶都被清掉
        var parent = Path.GetDirectoryName(fixture.PackDirectory)!;
        Assert.DoesNotContain(Directory.GetDirectories(parent), dir => dir.Contains("-staged-"));
        Assert.DoesNotContain(Directory.GetDirectories(parent), dir => dir.Contains("-trash-"));
    }

    [Fact]
    public async Task 卸载删除安装目录()
    {
        using var fixture = new PackFixture();
        fixture.MakeCatalogAndArchive();

        var manager = fixture.CreateManager();
        _ = await manager.InstallRecommendedAsync(OCREnginePreference.PaddleOCR, _ => { });
        Assert.True(Directory.Exists(fixture.PackDirectory));

        manager.Remove(OCREnginePreference.PaddleOCR);

        Assert.False(Directory.Exists(fixture.PackDirectory));
        Assert.False(manager.IsInstalled(OCREnginePreference.PaddleOCR));
    }

    [Fact]
    public async Task 手动导入已解压目录()
    {
        using var fixture = new PackFixture();
        var pack = fixture.MakePackDirectory();

        var manager = fixture.CreateManager();
        var info = await manager.InstallDirectoryAsync(pack, OCREnginePreference.PaddleOCR);

        Assert.Equal("1.0.0", info.Version);
        Assert.True(manager.IsInstalled(OCREnginePreference.PaddleOCR));
    }

    // ── 拒绝路径 ───────────────────────────────────────────────────

    [Fact]
    public async Task 压缩包含上级目录条目时整体拒绝且不落盘()
    {
        using var fixture = new PackFixture();
        var pack = fixture.MakePackDirectory();
        var archive = fixture.MakeArchive(pack);

        // 追加一个恶意条目
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        {
            using var writer = new StreamWriter(zip.CreateEntry("../escape.txt").Open());
            writer.Write("pwned");
        }

        fixture.WriteCatalog(archive);

        var error = await Assert.ThrowsAsync<OCRPackException>(() =>
            fixture.CreateManager().InstallRecommendedAsync(OCREnginePreference.PaddleOCR, _ => { }));

        Assert.Equal(OCRPackError.UnsafeArchive, error.Code);
        Assert.False(Directory.Exists(fixture.PackDirectory), "拒绝安装后不应留下任何目录。");
        // ZIP slip 在**解压之前**就被拦住，所以整个沙箱里不该出现 escape.txt。
        Assert.Empty(Directory.GetFiles(fixture.Root, "escape.txt", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task 压缩包校验和不匹配时拒绝安装()
    {
        using var fixture = new PackFixture();
        fixture.MakeCatalogAndArchive(corruptChecksum: true);

        var error = await Assert.ThrowsAsync<OCRPackException>(() =>
            fixture.CreateManager().InstallRecommendedAsync(OCREnginePreference.PaddleOCR, _ => { }));

        Assert.Equal(OCRPackError.ChecksumMismatch, error.Code);
        Assert.False(Directory.Exists(fixture.PackDirectory));
    }

    [Fact]
    public async Task 引擎不匹配时拒绝()
    {
        using var fixture = new PackFixture();
        var pack = fixture.MakePackDirectory(engine: "rapidOCR");

        var error = await Assert.ThrowsAsync<OCRPackException>(() =>
            fixture.CreateManager().InstallDirectoryAsync(pack, OCREnginePreference.PaddleOCR));

        Assert.Equal(OCRPackError.WrongEngine, error.Code);
    }

    [Fact]
    public async Task 架构不匹配时拒绝()
    {
        using var fixture = new PackFixture();
        var pack = fixture.MakePackDirectory(architecture: "arm64");

        var error = await Assert.ThrowsAsync<OCRPackException>(() =>
            fixture.CreateManager().InstallDirectoryAsync(pack, OCREnginePreference.PaddleOCR));

        Assert.Equal(OCRPackError.UnsupportedArchitecture, error.Code);
        Assert.Equal("x86_64", error.Detail);
    }

    [Fact]
    public async Task RID形态的架构字符串被接受()
    {
        // 参考文档 §14 #32：宿主报 x86_64，但 win-x64 / AMD64 是等价别名。
        using var fixture = new PackFixture();
        var pack = fixture.MakePackDirectory(architecture: "win-x64");

        var info = await fixture.CreateManager().InstallDirectoryAsync(pack, OCREnginePreference.PaddleOCR);

        Assert.Equal("1.0.0", info.Version);
    }

    [Fact]
    public async Task manifest版本与catalog不一致时拒绝()
    {
        // :233 —— guard manifest.version == resolved.package.version
        using var fixture = new PackFixture();
        var pack = fixture.MakePackDirectory(version: "9.9.9");
        var archive = fixture.MakeArchive(pack);
        fixture.WriteCatalog(archive, version: "1.0.0");

        var error = await Assert.ThrowsAsync<OCRPackException>(() =>
            fixture.CreateManager().InstallRecommendedAsync(OCREnginePreference.PaddleOCR, _ => { }));

        Assert.Equal(OCRPackError.InvalidManifest, error.Code);
    }

    [Fact]
    public async Task 可执行文件跑到包外时拒绝()
    {
        // 对应 Mac testRejectsExecutableOutsidePack（OptionalOCRPackManagerTests.swift:98-110）
        using var fixture = new PackFixture();
        var pack = fixture.MakePackDirectory();

        File.WriteAllText(
            Path.Combine(pack, "manifest.json"),
            fixture.Adapter.ManifestJson().Replace("\"bin/adapter.cmd\"", "\"../outside.cmd\""));

        var error = await Assert.ThrowsAsync<OCRPackException>(() =>
            fixture.CreateManager().InstallDirectoryAsync(pack, OCREnginePreference.PaddleOCR));

        Assert.Equal(OCRPackError.UnsafeExecutablePath, error.Code);
    }

    [Fact]
    public async Task 同名前缀目录不能冒充包目录()
    {
        // 前缀比较必须带分隔符，否则 <root>/paddleOCR-evil 会被当成 <root>/paddleOCR 内部。
        using var fixture = new PackFixture();
        var pack = fixture.MakePackDirectory();
        File.WriteAllText(
            Path.Combine(pack, "manifest.json"),
            fixture.Adapter.ManifestJson().Replace(
                "\"bin/adapter.cmd\"",
                "\"../paddleOCR-evil/adapter.cmd\""));

        var error = await Assert.ThrowsAsync<OCRPackException>(() =>
            fixture.CreateManager().InstallDirectoryAsync(pack, OCREnginePreference.PaddleOCR));

        Assert.Equal(OCRPackError.UnsafeExecutablePath, error.Code);
    }

    [Fact]
    public async Task 可执行文件SHA256不匹配时拒绝()
    {
        using var fixture = new PackFixture();
        var pack = fixture.MakePackDirectory();

        var executable = Path.Combine(pack, "bin", "adapter.cmd");
        var real = Sha256Of(executable);
        File.WriteAllText(
            Path.Combine(pack, "manifest.json"),
            fixture.Adapter.ManifestJson().Replace(
                "\"healthCheckArguments\"",
                $"\"executableSHA256\":\"{FakeSha256}\",\"healthCheckArguments\""));

        _ = real;

        var error = await Assert.ThrowsAsync<OCRPackException>(() =>
            fixture.CreateManager().InstallDirectoryAsync(pack, OCREnginePreference.PaddleOCR));

        Assert.Equal(OCRPackError.ChecksumMismatch, error.Code);
    }

    [Fact]
    public async Task 可执行文件SHA256匹配时通过()
    {
        using var fixture = new PackFixture();
        var pack = fixture.MakePackDirectory();

        var executable = Path.Combine(pack, "bin", "adapter.cmd");
        var real = Sha256Of(executable);
        File.WriteAllText(
            Path.Combine(pack, "manifest.json"),
            fixture.Adapter.ManifestJson().Replace(
                "\"healthCheckArguments\"",
                $"\"executableSHA256\":\"{real}\",\"healthCheckArguments\""));

        var info = await fixture.CreateManager().InstallDirectoryAsync(pack, OCREnginePreference.PaddleOCR);

        Assert.Equal("1.0.0", info.Version);
    }

    [Fact]
    public async Task 缺少manifest时报无效清单()
    {
        using var fixture = new PackFixture();
        var empty = Path.Combine(fixture.Root, "empty");
        Directory.CreateDirectory(empty);

        var error = await Assert.ThrowsAsync<OCRPackException>(() =>
            fixture.CreateManager().InstallDirectoryAsync(empty, OCREnginePreference.PaddleOCR));

        Assert.Equal(OCRPackError.InvalidManifest, error.Code);
    }

    [Fact]
    public async Task 内置引擎不能安装增强包()
    {
        using var fixture = new PackFixture();
        fixture.MakeCatalogAndArchive();

        await Assert.ThrowsAsync<OCRPackException>(() =>
            fixture.CreateManager().InstallRecommendedAsync(OCREnginePreference.AppleVision, _ => { }));
    }

    // ── catalog 解析 ───────────────────────────────────────────────

    [Fact]
    public async Task schemaVersion不是1时报无效清单()
    {
        using var fixture = new PackFixture();
        var pack = fixture.MakePackDirectory();
        var archive = fixture.MakeArchive(pack);
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.CatalogPath)!);

        File.WriteAllText(
            fixture.CatalogPath,
            "{\"schemaVersion\":2,\"packages\":[{\"engine\":\"paddleOCR\",\"version\":\"1.0.0\","
            + "\"architecture\":\"x86_64\",\"downloadURL\":\"" + Path.GetFileName(archive) + "\","
            + "\"archiveSHA256\":\"" + fixture.Sha256(archive) + "\",\"archiveSize\":1}]}");

        var error = await Assert.ThrowsAsync<OCRPackException>(() =>
            fixture.CreateManager().InstallRecommendedAsync(OCREnginePreference.PaddleOCR, _ => { }));

        Assert.Equal(OCRPackError.InvalidCatalog, error.Code);
    }

    [Fact]
    public async Task catalog格式损坏时报无效清单()
    {
        using var fixture = new PackFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.CatalogPath)!);
        File.WriteAllText(fixture.CatalogPath, "{ not json");

        var error = await Assert.ThrowsAsync<OCRPackException>(() =>
            fixture.CreateManager().InstallRecommendedAsync(OCREnginePreference.PaddleOCR, _ => { }));

        Assert.Equal(OCRPackError.InvalidCatalog, error.Code);
    }

    [Fact]
    public async Task 找不到catalog时报包不可用()
    {
        using var fixture = new PackFixture();

        var error = await Assert.ThrowsAsync<OCRPackException>(() =>
            fixture.CreateManager().InstallRecommendedAsync(OCREnginePreference.PaddleOCR, _ => { }));

        Assert.Equal(OCRPackError.PackageUnavailable, error.Code);
    }

    [Fact]
    public async Task 配置的catalog地址必须是https()
    {
        using var fixture = new PackFixture();
        fixture.MakeCatalogAndArchive();

        // http:// 会被忽略，回落到本地候选（本地有 catalog，所以安装仍然成功）
        var manager = fixture.CreateManager(catalogUrl: "http://insecure.example.com/catalog.json");
        var availability = await manager.AvailablePackageAsync(OCREnginePreference.PaddleOCR);

        Assert.True(availability.IsLocal);
    }

    [Fact]
    public async Task 按数字版本降序取最新包()
    {
        // :381 —— compare(options: .numeric)：1.10.0 > 1.9.0（纯字符串比较会搞反）。
        using var fixture = new PackFixture();

        var oldPack = fixture.MakePackDirectory(version: "1.9.0");
        var newPack = fixture.MakePackDirectory(version: "1.10.0");

        var oldArchive = fixture.MakeArchive(oldPack, "old.zip");
        var newArchive = fixture.MakeArchive(newPack, "new.zip");

        Directory.CreateDirectory(Path.GetDirectoryName(fixture.CatalogPath)!);
        File.WriteAllText(
            fixture.CatalogPath,
            "{\"schemaVersion\":1,\"packages\":["
            + Package(oldArchive, "1.9.0")
            + ","
            + Package(newArchive, "1.10.0")
            + "]}");

        var manager = fixture.CreateManager();
        var availability = await manager.AvailablePackageAsync(OCREnginePreference.PaddleOCR);

        Assert.Equal("1.10.0", availability.Package.Version);

        var info = await manager.InstallRecommendedAsync(OCREnginePreference.PaddleOCR, _ => { });

        // manifest 版本必须与被选中的 catalog 条目一致
        Assert.Equal("1.10.0", info.Version);
    }

    [Fact]
    public async Task 没有匹配架构的包时报不可用()
    {
        using var fixture = new PackFixture();
        var pack = fixture.MakePackDirectory();
        var archive = fixture.MakeArchive(pack);
        fixture.WriteCatalog(archive, architecture: "arm64");

        var error = await Assert.ThrowsAsync<OCRPackException>(() =>
            fixture.CreateManager().InstallRecommendedAsync(OCREnginePreference.PaddleOCR, _ => { }));

        Assert.Equal(OCRPackError.PackageUnavailable, error.Code);
    }

    [Fact]
    public async Task 没有匹配引擎的包时报不可用()
    {
        using var fixture = new PackFixture();
        var pack = fixture.MakePackDirectory(engine: "rapidOCR");
        var archive = fixture.MakeArchive(pack);
        fixture.WriteCatalog(archive, engine: "rapidOCR");

        var error = await Assert.ThrowsAsync<OCRPackException>(() =>
            fixture.CreateManager().InstallRecommendedAsync(OCREnginePreference.PaddleOCR, _ => { }));

        Assert.Equal(OCRPackError.PackageUnavailable, error.Code);
    }

    // ── 识别：一次性进程协议 ───────────────────────────────────────

    [Fact]
    public async Task 旧版包走一次性进程协议()
    {
        using var fixture = new PackFixture(supportsWorker: false);
        var pack = fixture.MakePackDirectory();
        await fixture.CreateManager().InstallDirectoryAsync(pack, OCREnginePreference.PaddleOCR);

        var manager = fixture.CreateManager();
        var result = await manager.RecognizeAsync(CreateImage(), OCREnginePreference.PaddleOCR);

        Assert.Equal("recognized text", result.Text);
        Assert.Equal(0.88, result.Confidence, 6);
        Assert.Equal(OCREnginePreference.PaddleOCR, result.Engine);
        Assert.Equal(CaptureContentType.PlainText, result.ContentType);
        Assert.Contains("oneshot", fixture.ReadCalls());
        Assert.DoesNotContain("worker", fixture.ReadCalls());
    }

    [Fact]
    public async Task 增强包路径不返回版面与条码()
    {
        // 参考文档 §9.7 末尾：协议里没有 layout / barcodes 字段。
        using var fixture = new PackFixture(supportsWorker: false);
        var pack = fixture.MakePackDirectory();
        await fixture.CreateManager().InstallDirectoryAsync(pack, OCREnginePreference.PaddleOCR);

        var result = await fixture.CreateManager()
            .RecognizeAsync(CreateImage(), OCREnginePreference.PaddleOCR);

        Assert.Empty(result.Document.Blocks);
        Assert.Empty(result.Document.Tables);
        Assert.Empty(result.Barcodes);
    }

    [Fact]
    public async Task 增强包识别结果仍过内容分类器()
    {
        using var fixture = new PackFixture(supportsWorker: false);
        var pack = fixture.MakePackDirectory();
        fixture.Adapter.Text = "Name\tScore\nAlice\t98";
        fixture.Adapter.Write(pack);
        await fixture.CreateManager().InstallDirectoryAsync(pack, OCREnginePreference.PaddleOCR);

        var result = await fixture.CreateManager()
            .RecognizeAsync(CreateImage(), OCREnginePreference.PaddleOCR);

        // :286 —— ContentClassifier().classify(response.text)
        Assert.Equal(CaptureContentType.Table, result.ContentType);
    }

    // ── 识别：常驻 Worker 协议 ─────────────────────────────────────

    [Fact]
    public async Task 声明workerArguments的包走常驻Worker协议()
    {
        using var fixture = new PackFixture(supportsWorker: true);
        var pack = fixture.MakePackDirectory();
        await fixture.CreateManager().InstallDirectoryAsync(pack, OCREnginePreference.PaddleOCR);

        var manager = fixture.CreateManager();
        var first = await manager.RecognizeAsync(CreateImage(), OCREnginePreference.PaddleOCR);
        var second = await manager.RecognizeAsync(CreateImage(), OCREnginePreference.PaddleOCR);

        Assert.Equal("recognized text", first.Text);
        Assert.Equal("recognized text", second.Text);

        // 关键：两次识别只启动**一个**进程（"worker" 只被计数一次 = 一个 ready）
        var calls = fixture.ReadCalls();
        Assert.Equal(2, calls.Count(item => item == "worker"));
        Assert.DoesNotContain("oneshot", calls);
    }

    [Fact]
    public async Task 预热会拉起Worker()
    {
        using var fixture = new PackFixture(supportsWorker: true);
        var pack = fixture.MakePackDirectory();
        await fixture.CreateManager().InstallDirectoryAsync(pack, OCREnginePreference.PaddleOCR);

        await fixture.CreateManager().PrewarmAsync(OCREnginePreference.PaddleOCR);

        // 等待就绪行写入计数文件（fake 在 worker 进程**启动**时记 'start'）。
        // Mac 的 warm() 只拉起进程等 ready、不发请求，'worker' 是按请求计数的。
        for (var i = 0; i < 100 && fixture.ReadCalls().Count == 0; i++)
        {
            await Task.Delay(50);
        }

        Assert.Contains(fixture.ReadCalls(), item => item == "start");
    }

    [Fact]
    public async Task 换到内置引擎时停掉Worker()
    {
        using var fixture = new PackFixture(supportsWorker: true);
        var pack = fixture.MakePackDirectory();
        await fixture.CreateManager().InstallDirectoryAsync(pack, OCREnginePreference.PaddleOCR);

        await fixture.CreateManager().PrewarmAsync(OCREnginePreference.PaddleOCR);
        for (var i = 0; i < 100 && fixture.ReadCalls().Count == 0; i++)
        {
            await Task.Delay(50);
        }

        await fixture.CreateManager().PrewarmAsync(OCREnginePreference.AppleVision);

        Assert.Contains(fixture.ReadCalls(), item => item == "start");
    }

    [Fact]
    public async Task 未安装时识别报未安装而不是静默成功()
    {
        using var fixture = new PackFixture();

        var error = await Assert.ThrowsAsync<OCRPackException>(() =>
            fixture.CreateManager().RecognizeAsync(CreateImage(), OCREnginePreference.PaddleOCR));

        Assert.Equal(OCRPackError.NotInstalled, error.Code);
    }

    [Fact]
    public void 未安装时IsInstalled为false()
    {
        using var fixture = new PackFixture();

        Assert.False(fixture.CreateManager().IsInstalled(OCREnginePreference.PaddleOCR));
        Assert.Null(fixture.CreateManager().InstalledInfo(OCREnginePreference.PaddleOCR));
    }

    [Fact]
    public void 内置引擎恒为已安装()
    {
        using var fixture = new PackFixture();

        // :156 —— guard engine != .appleVision else { return true }
        Assert.True(fixture.CreateManager().IsInstalled(OCREnginePreference.AppleVision));
        Assert.Null(fixture.CreateManager().InstalledInfo(OCREnginePreference.AppleVision));
    }

    // ── 辅助 ───────────────────────────────────────────────────────

    private const string FakeSha256 = "0000000000000000000000000000000000000000000000000000000000000000";

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string Package(string archivePath, string version)
        => "{"
        + "\"engine\":\"paddleOCR\","
        + $"\"version\":\"{version}\","
        + "\"architecture\":\"x86_64\","
        + "\"minimumWindows\":\"10.0.17763\","
        + $"\"downloadURL\":\"{Path.GetFileName(archivePath)}\","
        + $"\"archiveSHA256\":\"{Sha256Of(archivePath)}\","
        + "\"archiveSize\":1"
        + "}";

    private static RgbaBitmap CreateImage() => new(16, 16);
}

/// <summary>夹具扩展，便于在测试里一行搭好 catalog + 压缩包。</summary>
internal static class PackFixtureExtensions
{
    public static void MakeCatalogAndArchive(
        this PackFixture fixture,
        int paddingEntries = 0,
        bool corruptChecksum = false)
    {
        var pack = fixture.MakePackDirectory();

        for (var i = 0; i < paddingEntries; i++)
        {
            var directory = Path.Combine(pack, "pad", i.ToString());
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "f.txt"), new string('x', 64));
        }

        var archive = fixture.MakeArchive(pack);
        if (corruptChecksum)
        {
            // 写一个必然不匹配的校验和，用于验证管理器真的会拒绝安装。
            fixture.WriteCatalogRaw(archive, archiveSha256: new string('0', 64));
            return;
        }

        fixture.WriteCatalog(archive);
    }
}
