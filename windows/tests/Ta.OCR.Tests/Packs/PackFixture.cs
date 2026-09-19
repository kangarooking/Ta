using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Ta.OCR.Models;
using Ta.OCR.Packs;
using Xunit;

namespace Ta.OCR.Tests.Packs;

/// <summary>
/// 假 PaddleOCR 适配器（<c>.cmd</c> + PowerShell 脚本）。
///
/// 实现 <c>docs/ocr-enhancement-pack-spec.md</c> 定义的三种子命令：
/// <c>--health-check</c> / <c>--input … --output json</c> / <c>--worker</c>。
/// 用 PowerShell 是因为它自带 stdin 行读，且 Windows 10/11 必然存在；
/// 载荷刻意全用 ASCII，绕开 PowerShell 重定向时的代码页问题。
/// </summary>
internal sealed class FakeAdapter
{
    public const string Engine = "paddleOCR";
    public const string Version = "1.0.0";

    public string ExecutableRelativePath => "bin/adapter.cmd";

    public string Text { get; set; } = "recognized text";

    public double Confidence { get; set; } = 0.88;

    public bool SupportsWorker { get; set; } = true;

    public int HealthCheckCalls { get; private set; }

    public int OneShotCalls { get; private set; }

    public int WorkerRequests { get; private set; }

    /// <summary>把适配器写进包目录。</summary>
    public void Write(string packDirectory)
    {
        var bin = Path.Combine(packDirectory, "bin");
        Directory.CreateDirectory(bin);

        File.WriteAllText(
            Path.Combine(bin, "adapter.cmd"),
            "@echo off\r\n"
            + "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"%~dp0adapter.ps1\" %*\r\n",
            Encoding.ASCII);

        File.WriteAllText(Path.Combine(bin, "adapter.ps1"), BuildScript(), Encoding.ASCII);
    }

    public string ManifestJson(string? engine = null, string? architecture = "x86_64", string? version = null)
    {
        // 用列表 join 而不是手写逗号：无 workerArguments 时会留下尾随逗号，JSON 直接非法。
        var fields = new List<string>
        {
            $"\"engine\":\"{engine ?? Engine}\"",
            $"\"version\":\"{version ?? Version}\"",
            $"\"executable\":\"{ExecutableRelativePath}\"",
        };

        if (architecture is not null)
        {
            fields.Add($"\"architecture\":\"{architecture}\"");
        }

        fields.Add("\"minimumWindows\":\"10.0.17763\"");
        fields.Add("\"healthCheckArguments\":[\"--health-check\"]");

        if (SupportsWorker)
        {
            fields.Add("\"workerArguments\":[\"--worker\"]");
        }

        return "{" + string.Join(",", fields) + "}";
    }

    private string BuildScript()
    {
        // 计数器写在同一目录下的计数文件里，测试据此断言协议被走了几次。
        var counter = "$dir = Split-Path -Parent $MyInvocation.MyCommand.Path; "
            + "$counterFile = Join-Path $dir 'calls.txt'; "
            + "function Bump($name) { "
            + "  if (Test-Path $counterFile) { $c = Get-Content $counterFile -Raw } else { $c = '' }; "
            + "  $c = $c + $name + \"`n\"; "
            + "  Set-Content -Path $counterFile -Value $c -NoNewline -Encoding ASCII }";

        return "$ErrorActionPreference = 'Stop'\n"
            + counter + "\n"
            + "$Engine = '" + Engine + "'\n"
            + "$Version = '" + Version + "'\n"
            + "$Text = '" + Text + "'\n"
            + "$Confidence = " + Confidence.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "\n"
            + "function Emit($obj) { $obj | ConvertTo-Json -Compress }\n"
            + "if ($args -contains '--worker') {\n"
            + "  Bump 'start'\n"
            + "  Emit @{ event = 'ready'; ok = $true; engine = $Engine; packVersion = $Version }\n"
            + "  while (($line = [Console]::In.ReadLine()) -ne $null) {\n"
            + "    if ([string]::IsNullOrWhiteSpace($line)) { continue }\n"
            + "    $request = $line | ConvertFrom-Json\n"
            + "    Bump 'worker'\n"
            + "    if ($request.command -eq 'ping') { Emit @{ id = $request.id; ok = $true; event = 'pong' }; continue }\n"
            + "    Emit @{ id = $request.id; ok = $true; text = $Text; confidence = $Confidence }\n"
            + "  }\n"
            + "  exit 0\n"
            + "}\n"
            + "if ($args -contains '--health-check') {\n"
            + "  Bump 'health'\n"
            + "  Emit @{ ok = $true; engine = $Engine; packVersion = $Version; architecture = 'x86_64'; offline = $true }\n"
            + "  exit 0\n"
            + "}\n"
            + "Bump 'oneshot'\n"
            + "Emit @{ text = $Text; confidence = $Confidence }\n"
            + "exit 0\n";
    }
}

/// <summary>测试夹具：搭好应用数据根、包目录、catalog 与压缩包。</summary>
internal sealed class PackFixture : IDisposable
{
    public PackFixture(bool supportsWorker = true)
    {
        Root = Path.Combine(Path.GetTempPath(), "ta-ocr-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        // 模拟真实布局：应用数据根 = <AppSupport>/AI Screenshot（生产方
        // ConfiguredOCRService.CreateDefaultPacks 传入的就是带应用名的目录）。
        ApplicationSupportRoot = Path.Combine(Root, "appdata", "AI Screenshot");
        BundleRoot = Path.Combine(Root, "bundle");
        Directory.CreateDirectory(ApplicationSupportRoot);
        Directory.CreateDirectory(BundleRoot);

        Adapter = new FakeAdapter { SupportsWorker = supportsWorker };
    }

    public string Root { get; }

    public string ApplicationSupportRoot { get; }

    public string BundleRoot { get; }

    public FakeAdapter Adapter { get; }

    /// <summary>安装目录（对应 Mac 的 <c>&lt;AppSupport&gt;/AI Screenshot/OCRPacks/&lt;engine&gt;</c>）。</summary>
    public string PackDirectory => Path.Combine(ApplicationSupportRoot, "OCRPacks", FakeAdapter.Engine);

    public string CatalogPath => Path.Combine(BundleRoot, "ocr-packs", "catalog.json");

    /// <summary>
    /// 压缩包的默认位置：与 catalog 同目录。
    /// catalog 的 <c>downloadURL</c> 是相对 catalog 所在目录解析的（`:389</c>），
    /// 所以发行产物必须放在一起 —— 这也是真实发布布局。
    /// </summary>
    public string ArchiveDirectory => Path.GetDirectoryName(CatalogPath)!;

    public OptionalOCRPackManager CreateManager(
        string? catalogUrl = null,
        PersistentOCRWorker? worker = null)
        => new(
            new OCRPackManagerOptions
            {
                ApplicationSupportRoot = ApplicationSupportRoot,
                BundleRoot = BundleRoot,
                ConfiguredCatalogUrl = catalogUrl,
            },
            worker: worker);

    /// <summary>造一个已解压的包目录（含 manifest 与适配器）。</summary>
    public string MakePackDirectory(string? engine = null, string? architecture = "x86_64", string? version = null)
    {
        var pack = Path.Combine(Root, "pack-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pack);

        Adapter.Write(pack);
        File.WriteAllText(Path.Combine(pack, "manifest.json"), Adapter.ManifestJson(engine, architecture, version));
        return pack;
    }

    /// <summary>把已解压的包打成 zip（顶层一个目录，模拟真实发行产物）。</summary>
    public string MakeArchive(string packDirectory, string archiveName = "pack.zip")
    {
        Directory.CreateDirectory(ArchiveDirectory);
        var archive = Path.Combine(ArchiveDirectory, archiveName);
        if (File.Exists(archive))
        {
            File.Delete(archive);
        }

        System.IO.Compression.ZipFile.CreateFromDirectory(
            packDirectory,
            archive,
            CompressionLevel.Optimal,
            includeBaseDirectory: true);

        return archive;
    }

    public string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>写 catalog.json。</summary>
    public void WriteCatalog(string archivePath, string? engine = null, string? architecture = "x86_64", string? version = null, string? relativeDownload = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath)!);

        var download = relativeDownload ?? Path.GetFileName(archivePath);
        File.WriteAllText(
            CatalogPath,
            "{"
            + "\"schemaVersion\":1,"
            + "\"packages\":[{"
            + $"\"engine\":\"{engine ?? FakeAdapter.Engine}\","
            + $"\"version\":\"{version ?? FakeAdapter.Version}\","
            + $"\"architecture\":\"{architecture}\","
            + "\"minimumWindows\":\"10.0.17763\","
            + $"\"downloadURL\":\"{download}\","
            + $"\"archiveSHA256\":\"{Sha256(archivePath)}\","
            + "\"archiveSize\":123456789"
            + "}]}");
    }

    /// <summary>写 catalog.json，但强制指定校验和（用于「校验失败拒绝安装」用例）。</summary>
    public void WriteCatalogRaw(string archivePath, string archiveSha256, string? engine = null, string? architecture = "x86_64", string? version = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath)!);

        File.WriteAllText(
            CatalogPath,
            "{"
            + "\"schemaVersion\":1,"
            + "\"packages\":[{"
            + $"\"engine\":\"{engine ?? FakeAdapter.Engine}\","
            + $"\"version\":\"{version ?? FakeAdapter.Version}\","
            + $"\"architecture\":\"{architecture}\","
            + "\"minimumWindows\":\"10.0.17763\","
            + $"\"downloadURL\":\"{Path.GetFileName(archivePath)}\","
            + $"\"archiveSHA256\":\"{archiveSha256}\","
            + "\"archiveSize\":123456789"
            + "}]}");
    }

    /// <summary>读出适配器记录的调用序列（用于断言协议路径）。</summary>
    public IReadOnlyList<string> ReadCalls()
    {
        var file = Path.Combine(PackDirectory, "bin", "calls.txt");
        if (!File.Exists(file))
        {
            return [];
        }

        return File.ReadAllText(file)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
