using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Ta.Core.Imaging;
using Ta.OCR.Imaging;
using Ta.OCR.Models;
using Ta.OCR.Text;

namespace Ta.OCR.Packs;

/// <summary>
/// 发行清单 / 压缩包的取回通道。
///
/// 抽出来是为了让安装管线可测：测试用 <see cref="LocalFileTransport"/> 指向临时目录，
/// 完全不碰网络。对应 Mac 版 <c>URLSession</c> 的那个注入点（`:139</c>、`:144-146</c>）。
/// </summary>
public interface IOCRPackTransport
{
    /// <summary>读取文本（发行清单）。<c>https://</c> 走网络，其余由调用方按本地文件处理。</summary>
    Task<string> ReadTextAsync(string url, CancellationToken cancellationToken);

    /// <summary>
    /// 流式下载到本地文件。<paramref name="progress"/> 传入 0..1，总长度未知时传 <c>null</c>
    /// —— 对应 Mac 的 <c>expectedContentLength</c>（`:424-437</c>）。
    /// </summary>
    Task DownloadAsync(
        string url,
        string destinationPath,
        Action<double?> progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// 本地文件传输实现。用于开发版 Alpha 分发（catalog 与压缩包都在磁盘上）。
/// </summary>
public sealed class LocalFileTransport : IOCRPackTransport
{
    public Task<string> ReadTextAsync(string url, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ToLocalPath(url);
        return File.ReadAllTextAsync(path, cancellationToken);
    }

    public async Task DownloadAsync(
        string url,
        string destinationPath,
        Action<double?> progress,
        CancellationToken cancellationToken)
    {
        var path = ToLocalPath(url);

        // 本地复制也要报进度，否则设置页的进度条会卡在「正在下载… 0%」。
        await using var source = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        await using var destination = new FileStream(
            destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);

        var total = source.Length;
        var buffer = new byte[64 * 1024];
        long received = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            progress(total > 0 ? Math.Min(1, (double)received / total) : null);
        }

        progress(1);
    }

    private static string ToLocalPath(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            return uri.LocalPath;
        }

        return url;
    }
}

/// <summary>
/// HTTPS 传输实现。64 KB 块、进度来自 <c>Content-Length</c>，
/// 对应 Mac 的 <c>session.bytes(from:)</c> 循环（`:413-442</c>）。
/// </summary>
public sealed class HttpOCRPackTransport : IOCRPackTransport
{
    private readonly HttpClient _client;

    public HttpOCRPackTransport(HttpClient? client = null)
    {
        _client = client ?? new HttpClient(new SocketsHttpHandler
        {
            // 增强包可达数百 MB，无限时由调用方的 CancellationToken 控制。
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        });
    }

    public async Task<string> ReadTextAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _client
            .GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DownloadAsync(
        string url,
        string destinationPath,
        Action<double?> progress,
        CancellationToken cancellationToken)
    {
        using var response = await _client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        // :419-421 —— 非 2xx 一律当 packageUnavailable。
        if (!response.IsSuccessStatusCode)
        {
            throw new OCRPackException(OCRPackError.PackageUnavailable);
        }

        var total = response.Content.Headers.ContentLength;

        await using var source = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var destination = new FileStream(
            destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);

        var buffer = new byte[64 * 1024];
        long received = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            progress(total is > 0 ? Math.Min(1, (double)received / total.Value) : null);
        }

        progress(1);
    }
}

/// <summary>增强包管理器的路径与配置。</summary>
public sealed record OCRPackManagerOptions
{
    /// <summary>
    /// 设置项 <c>ocrPackCatalogURL</c> 的值。对应 Mac 的
    /// <c>UserDefaults["ocrPackCatalogURL"]</c>（`:398-401</c>），**必须是 https**。
    /// </summary>
    public string? ConfiguredCatalogUrl { get; init; }

    /// <summary>应用数据根目录。包装在 <c>&lt;根&gt;/AI Screenshot/OCRPacks/&lt;engine&gt;/</c>。</summary>
    public required string ApplicationSupportRoot { get; init; }

    /// <summary>
    /// 应用所在目录，用于查找随包发布的 <c>ocr-packs/catalog.json</c>。
    /// 默认取当前程序集目录。
    /// </summary>
    public string BundleRoot { get; init; } = AppContext.BaseDirectory;
}

/// <summary>
/// 可选 OCR 增强包（PaddleOCR / RapidOCR）的安装与调用。
///
/// 逐字对应 Mac 版 <c>OptionalOCRPackManager</c>
/// （<c>AIScreenshotApp/Recognition/OptionalOCRPackManager.swift:1-736</c>）。
///
/// <b>安装管线</b>（<c>installRecommended</c>，`:188-237</c>），阶段顺序即上面的中文标签：
/// <list type="number">
///   <item>解析 catalog；校验架构 + 最低系统版本</item>
///   <item>下载（64 KB 块，进度来自 Content-Length）到临时目录 <c>pack.zip</c></item>
///   <item>流式 SHA-256（1 MiB 块，进度 ×0.9）比对 <c>archiveSHA256.lowercased()</c></item>
///   <item>ZIP slip 防护：枚举全部条目，拒绝不安全名字</item>
///   <item>解压（300 s 超时）</item>
///   <item>定位包根：<c>extracted/manifest.json</c>，否则要求**恰好一个**子目录含 <c>manifest.json</c></item>
///   <item>健康检查 + 版本必须与 catalog 一致</item>
///   <item>暂存安装 → 重新校验 → 原子替换就位 → 再校验可执行文件</item>
/// </list>
///
/// <b>Windows 差异</b>
/// <list type="bullet">
///   <item><c>/usr/bin/zipinfo</c> 与 <c>/usr/bin/ditto</c> 全部换成
///     <c>System.IO.Compression</c>：自己枚举条目做路径安全检查，自己逐条解压。</item>
///   <item>Mac 的 <c>replaceItemAt</c> 是原子的目录替换；Windows 没有等价的
///     <c>Directory.Replace</c>，实现为「旧目录改名到垃圾桶 → 暂存目录改名就位 → 删垃圾桶」，
///     并把失败路径写成回滚（见 <see cref="InstallValidatedDirectoryAsync"/>）。</item>
///   <item><c>isExecutableFile</c> 换成扩展名判断（Windows 没有可执行位）。</item>
///   <item><c>minimumMacOS</c> 在 Windows 上按「最低 Windows 版本」解释，
///     且版本号用 <c>RtlGetVersion</c> 读（<c>Environment.OSVersion</c> 受兼容清单影响）。</item>
/// </list>
/// </summary>
public sealed class OptionalOCRPackManager
{
    private readonly OCRPackManagerOptions _options;
    private readonly IOCRPackTransport _transport;
    private readonly PersistentOCRWorker _worker;

    public OptionalOCRPackManager(
        OCRPackManagerOptions options,
        IOCRPackTransport? transport = null,
        PersistentOCRWorker? worker = null)
    {
        _options = options;
        _transport = transport ?? new LocalFileTransport();
        _worker = worker ?? new PersistentOCRWorker();
    }

    /// <summary>内部使用的常驻 Worker，便于宿主在卸载/换引擎时统一收尾。</summary>
    public PersistentOCRWorker Worker => _worker;

    // ── 查询 ────────────────────────────────────────────────────────

    /// <summary>对应 <c>isInstalled(_:)</c>（`:155-158</c>）。</summary>
    public bool IsInstalled(OCREnginePreference engine)
    {
        // :156 —— 内置引擎恒为「已安装」。
        if (engine == OCREnginePreference.AppleVision)
        {
            return true;
        }

        return TryValidatedExecutable(engine, out _);
    }

    /// <summary>对应 <c>installedInfo(_:)</c>（`:160-168</c>）。</summary>
    public OCRPackInstalledInfo? InstalledInfo(OCREnginePreference engine)
    {
        if (engine == OCREnginePreference.AppleVision)
        {
            return null;
        }

        var directory = PackDirectory(engine);
        var manifest = TryReadManifest(directory);
        if (manifest is null)
        {
            return null;
        }

        return TryValidatedExecutable(engine, out _)
            ? new OCRPackInstalledInfo(manifest.Version, manifest.Architecture)
            : null;
    }

    /// <summary>对应 <c>availablePackage(for:)</c>（`:170-173</c>）。</summary>
    public async Task<OCRPackAvailability> AvailablePackageAsync(
        OCREnginePreference engine,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveRecommendedPackageAsync(engine, cancellationToken).ConfigureAwait(false);
        return new OCRPackAvailability(resolved.Package, resolved.IsLocal);
    }

    /// <summary>对应 <c>remove(_:)</c>（`:239-246</c>）。</summary>
    public void Remove(OCREnginePreference engine)
    {
        if (engine == OCREnginePreference.AppleVision)
        {
            return;
        }

        // :241 —— 先停 Worker，否则占用着文件删不掉（Windows 上尤其明显）。
        _worker.Stop();

        var directory = PackDirectory(engine);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ── 安装 ────────────────────────────────────────────────────────

    /// <summary>对应 <c>installRecommended(for:progress:)</c>（`:188-237</c>）。</summary>
    public async Task<OCRPackInstalledInfo> InstallRecommendedAsync(
        OCREnginePreference engine,
        Action<OCRPackInstallProgress> progress,
        CancellationToken cancellationToken = default)
    {
        if (engine == OCREnginePreference.AppleVision)
        {
            throw new OCRPackException(OCRPackError.WrongEngine);
        }

        progress(new OCRPackInstallProgress(OCRPackInstallStage.Resolving, null));

        var resolved = await ResolveRecommendedPackageAsync(engine, cancellationToken).ConfigureAwait(false);
        ValidateCompatibility(resolved.Package.Architecture, resolved.Package.MinimumSystemVersion);

        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            $"ai-screenshot-ocr-download-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);

        try
        {
            var archive = Path.Combine(temporaryRoot, "pack.zip");

            // :203-208 —— 本地包直接复制（进度直接给 1），远端走下载。
            if (resolved.IsLocal)
            {
                progress(new OCRPackInstallProgress(OCRPackInstallStage.Downloading, 1));
                await DownloadAsync(
                        resolved.ArchiveUrl,
                        archive,
                        _ => { },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await DownloadAsync(
                        resolved.ArchiveUrl,
                        archive,
                        fraction => progress(new OCRPackInstallProgress(OCRPackInstallStage.Downloading, fraction)),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // :211-219 —— SHA-256，进度 ×0.9，最后补一个 1。
            progress(new OCRPackInstallProgress(OCRPackInstallStage.Verifying, 0));
            var checksum = await Sha256Async(
                archive,
                fraction => progress(new OCRPackInstallProgress(
                    OCRPackInstallStage.Verifying, fraction * 0.9)),
                cancellationToken).ConfigureAwait(false);

            if (!string.Equals(checksum, resolved.Package.ArchiveSha256.Trim().ToLowerInvariant(), StringComparison.Ordinal))
            {
                throw new OCRPackException(OCRPackError.ChecksumMismatch);
            }

            // :218 —— 校验**全部**条目名（先于解压，避免任何字节落盘）。
            ValidateArchiveEntries(archive);

            progress(new OCRPackInstallProgress(OCRPackInstallStage.Verifying, 1));

            // :221-228 —— 解压。Mac 用 ditto（300 s 超时）；这里逐条落盘并再做一次路径夹取。
            progress(new OCRPackInstallProgress(OCRPackInstallStage.Extracting, null));
            var extracted = Path.Combine(temporaryRoot, "extracted");
            Directory.CreateDirectory(extracted);
            await ExtractSafelyAsync(archive, extracted, cancellationToken).ConfigureAwait(false);

            var source = LocatePackRoot(extracted);

            // :231-233 —— 健康检查 + 版本必须与 catalog 一致。
            progress(new OCRPackInstallProgress(OCRPackInstallStage.HealthChecking, null));
            var manifest = await ValidateDirectoryAsync(
                source, engine, performHealthCheck: true, cancellationToken).ConfigureAwait(false);

            if (!string.Equals(manifest.Version, resolved.Package.Version, StringComparison.Ordinal))
            {
                throw new OCRPackException(OCRPackError.InvalidManifest);
            }

            // :235-236
            progress(new OCRPackInstallProgress(OCRPackInstallStage.Installing, null));
            return await InstallValidatedDirectoryAsync(
                source, manifest, engine, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // :200 —— defer 清理临时目录。
            TryDeleteDirectory(temporaryRoot);
        }
    }

    /// <summary>
    /// 安装一个**已解压**的包目录。对应 <c>installDirectory(_:expectedEngine:performHealthCheck:)</c>
    /// （`:309-321</c>）—— 手动导入 / 离线分发入口。
    /// </summary>
    public async Task<OCRPackInstalledInfo> InstallDirectoryAsync(
        string source,
        OCREnginePreference expectedEngine,
        bool performHealthCheck = true,
        CancellationToken cancellationToken = default)
    {
        var manifest = await ValidateDirectoryAsync(
            source, expectedEngine, performHealthCheck, cancellationToken).ConfigureAwait(false);

        return await InstallValidatedDirectoryAsync(
            source, manifest, expectedEngine, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>对应 <c>validateDirectory</c>（`:323-334</c>）。</summary>
    private async Task<OCRPackManifest> ValidateDirectoryAsync(
        string source,
        OCREnginePreference expectedEngine,
        bool performHealthCheck,
        CancellationToken cancellationToken)
    {
        var manifest = TryReadManifest(source)
            ?? throw new OCRPackException(OCRPackError.InvalidManifest);

        // :329 —— 引擎必须一致。
        if (!string.Equals(manifest.Engine, expectedEngine.RawValue(), StringComparison.Ordinal))
        {
            throw new OCRPackException(OCRPackError.WrongEngine);
        }

        // :330 —— 架构 + 系统版本。
        ValidateCompatibility(manifest.Architecture, manifest.MinimumSystemVersion);

        var executable = ValidateExecutable(manifest, source);

        if (performHealthCheck)
        {
            await HealthCheckAsync(executable, manifest, cancellationToken).ConfigureAwait(false);
        }

        return manifest;
    }

    /// <summary>
    /// 对应 <c>installValidatedDirectory</c>（`:336-360</c>）。
    ///
    /// 顺序刻意与 Mac 一致：先停 Worker → 拷到 <c>.&lt;engine&gt;-staged-&lt;uuid&gt;</c>
    /// → **在暂存目录上重新完整校验** → 替换就位 → 再校验一次可执行文件。
    /// 「校验两遍」是关键：第一遍校验的是下载/导入的源目录，第二遍校验的是真正会被使用的目录。
    /// </summary>
    private async Task<OCRPackInstalledInfo> InstallValidatedDirectoryAsync(
        string source,
        OCRPackManifest manifest,
        OCREnginePreference expectedEngine,
        CancellationToken cancellationToken)
    {
        // :341 —— 不先停 Worker，Windows 上旧进程会锁着模型文件导致删除失败。
        _worker.Stop();

        var destination = PackDirectory(expectedEngine);
        var parent = Path.GetDirectoryName(destination)
            ?? throw new OCRPackException(OCRPackError.InvalidManifest, "无法确定增强包安装目录。");

        Directory.CreateDirectory(parent);

        var staged = Path.Combine(parent, $".{expectedEngine.RawValue()}-staged-{Guid.NewGuid():N}");

        CopyDirectory(source, staged);

        var replaced = false;
        try
        {
            // :348 —— 在暂存目录上重新校验（**不做**健康检查，Mac 这里传 false）。
            _ = await ValidateDirectoryAsync(
                staged, expectedEngine, performHealthCheck: false, cancellationToken).ConfigureAwait(false);

            var trash = Path.Combine(parent, $".{expectedEngine.RawValue()}-trash-{Guid.NewGuid():N}");

            if (Directory.Exists(destination))
            {
                // :349-350 —— Mac 的 replaceItemAt。Windows 没有原子目录替换，
                // 退化为「改名旧目录 → 改名新目录就位 → 删旧目录」。
                // 中间态存在（旧目录已改名、新目录未就位），但这两步都是同卷内的元数据操作，
                // 窗口在毫秒级；失败时回滚。
                Directory.Move(destination, trash);
                try
                {
                    Directory.Move(staged, destination);
                }
                catch
                {
                    // 回滚：把旧目录放回去。
                    if (!Directory.Exists(destination))
                    {
                        Directory.Move(trash, destination);
                    }

                    throw;
                }

                replaced = true;
                TryDeleteDirectory(trash);
            }
            else
            {
                // :352 —— 首次安装，直接改名。
                Directory.Move(staged, destination);
            }

            // :354 —— 就位后再校验一次可执行文件。
            if (!TryValidatedExecutable(expectedEngine, out _))
            {
                throw new OCRPackException(OCRPackError.UnsafeExecutablePath);
            }

            return new OCRPackInstalledInfo(manifest.Version, manifest.Architecture);
        }
        catch
        {
            // :356-358 —— 失败就清掉暂存目录，不留垃圾。
            TryDeleteDirectory(staged);
            throw;
        }
        finally
        {
            if (!replaced)
            {
                TryDeleteDirectory(staged);
            }
        }
    }

    // ── 识别 ────────────────────────────────────────────────────────

    /// <summary>
    /// 用增强包识别一张图。对应 <c>recognize(image:engine:)</c>（`:248-291</c>）。
    ///
    /// ⚠️ 这条路径**不返回 layout 与 barcodes** —— 协议里没有这两个字段。
    /// 后果：依赖 <c>document.blocks</c> 的图像翻译模式从增强包拿不到数据（参考文档 §9.7 末尾）。
    /// </summary>
    public async Task<OCRResult> RecognizeAsync(
        RgbaBitmap image,
        OCREnginePreference engine,
        CancellationToken cancellationToken = default)
    {
        var directory = PackDirectory(engine);
        var manifest = TryReadManifest(directory)
            ?? throw new OCRPackException(OCRPackError.NotInstalled);

        // :251 —— 引擎必须一致。
        if (!string.Equals(manifest.Engine, engine.RawValue(), StringComparison.Ordinal))
        {
            throw new OCRPackException(OCRPackError.WrongEngine);
        }

        ValidateCompatibility(manifest.Architecture, manifest.MinimumSystemVersion);

        var executable = ValidateExecutable(manifest, directory);

        // :255-263 —— 写临时 PNG。
        var temporary = Path.Combine(
            Path.GetTempPath(),
            $"ai-screenshot-ocr-{Guid.NewGuid():N}.png");

        try
        {
            var prepared = OCRImagePreparation.Prepare(image, OCRImagePreparation.DefaultMaximumDimension);
            await File.WriteAllBytesAsync(temporary, PngWriter.Encode(prepared), cancellationToken)
                .ConfigureAwait(false);

            OCRPackResponse response;

            if (manifest.SupportsWorker)
            {
                // :266-272 —— 1.1.0+ 的常驻 Worker 协议。
                response = await _worker.RecognizeAsync(
                    executable,
                    manifest.WorkerArguments!,
                    temporary,
                    OCRImagePreparation.DefaultMaximumDimension,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // :274-282 —— 旧版一次性进程协议。
                var result = await OCRProcessRunner.RunAsync(
                    executable,
                    ["--input", temporary, "--output", "json"],
                    OCRProcessRunner.RecognitionTimeout,
                    cancellationToken).ConfigureAwait(false);

                response = OCRPackResponse.TryParse(result.StandardOutput)
                    ?? throw new OCRPackException(OCRPackError.InvalidResponse);
            }

            // :284-289
            return new OCRResult(
                text: response.Text ?? string.Empty,
                contentType: new ContentClassifier().Classify(response.Text ?? string.Empty),
                confidence: response.Confidence ?? 0,
                engine: engine);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    /// <summary>对应 <c>prewarm(_:)</c>（`:293-307</c>）。</summary>
    public async Task PrewarmAsync(OCREnginePreference engine, CancellationToken cancellationToken = default)
    {
        if (engine == OCREnginePreference.AppleVision)
        {
            _worker.Stop();
            return;
        }

        var directory = PackDirectory(engine);
        var manifest = TryReadManifest(directory);

        if (manifest is null || !manifest.SupportsWorker)
        {
            _worker.Stop();
            return;
        }

        string executable;
        try
        {
            executable = ValidateExecutable(manifest, directory);
        }
        catch (OCRPackException)
        {
            _worker.Stop();
            return;
        }

        await _worker
            .WarmAsync(executable, manifest.WorkerArguments!, cancellationToken)
            .ConfigureAwait(false);
    }

    // ── catalog 解析 ────────────────────────────────────────────────

    private sealed record ResolvedCatalogPackage
    {
        public required OCRPackCatalogPackage Package { get; init; }

        public required string ArchiveUrl { get; init; }

        public required bool IsLocal { get; init; }
    }

    /// <summary>对应 <c>resolveRecommendedPackage(for:)</c>（`:362-395</c>）。</summary>
    private async Task<ResolvedCatalogPackage> ResolveRecommendedPackageAsync(
        OCREnginePreference engine,
        CancellationToken cancellationToken)
    {
        var catalogUrl = CatalogLocation();
        var isLocal = IsLocalUrl(catalogUrl);

        // :365-373
        var json = isLocal
            ? await File.ReadAllTextAsync(ToLocalPath(catalogUrl), cancellationToken).ConfigureAwait(false)
            : await _transport.ReadTextAsync(catalogUrl, cancellationToken).ConfigureAwait(false);

        // :374-377 —— schemaVersion 必须为 1。
        var catalog = OCRPackCatalog.TryParse(json);
        if (catalog is null || catalog.SchemaVersion != 1 || catalog.Packages is null)
        {
            throw new OCRPackException(OCRPackError.InvalidCatalog);
        }

        // :378-384 —— 按引擎 + 架构过滤，按数字版本降序取第一个。
        var package = catalog.Packages
            .Where(candidate =>
                string.Equals(candidate.Engine, engine.RawValue(), StringComparison.Ordinal)
                && OcrHostArchitecture.Matches(candidate.Architecture))
            .OrderByDescending(candidate => candidate, NumericVersionComparer.Instance)
            .FirstOrDefault();

        if (package is null)
        {
            throw new OCRPackException(OCRPackError.PackageUnavailable);
        }

        // :385-390 —— 相对 downloadURL 相对 catalog 所在目录解析。
        string archiveUrl;
        if (Uri.TryCreate(package.DownloadUrl, UriKind.Absolute, out var absolute) && absolute.Scheme.Length > 0)
        {
            archiveUrl = package.DownloadUrl;
        }
        else
        {
            var baseDirectory = isLocal
                ? Path.GetDirectoryName(ToLocalPath(catalogUrl)) ?? string.Empty
                : new Uri(catalogUrl).AbsoluteUri[..(new Uri(catalogUrl).AbsoluteUri.LastIndexOf('/') + 1)];

            archiveUrl = Path.GetFullPath(Path.Combine(baseDirectory, package.DownloadUrl));
        }

        // :391-393 —— 只允许 file:// 或 https。
        if (!IsLocalUrl(archiveUrl)
            && !archiveUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new OCRPackException(OCRPackError.InsecureDownloadUrl);
        }

        return new ResolvedCatalogPackage
        {
            Package = package,
            ArchiveUrl = archiveUrl,
            IsLocal = IsLocalUrl(archiveUrl),
        };
    }

    /// <summary>
    /// 对应 <c>catalogLocation()</c>（`:397-411</c>）。
    ///
    /// Windows 的候选位置（Mac 是「app 同级的 ocr-packs/catalog.json」和
    /// 「Contents/Resources/OCRPacks/catalog.json」两处）：
    /// ① 设置项 <c>ocrPackCatalogURL</c>（**必须 https**）
    /// ② 程序目录及其最多 5 层父目录下的 <c>ocr-packs/catalog.json</c>
    ///    （开发构建的 exe 在 <c>bin/Debug/&lt;tfm&gt;/</c>，比 Mac 的 <c>.app/Contents/MacOS</c> 深）
    /// ③ 程序目录下的 <c>OCRPacks/catalog.json</c>
    /// </summary>
    private string CatalogLocation()
    {
        // :398-401
        var configured = _options.ConfiguredCatalogUrl;
        if (!string.IsNullOrWhiteSpace(configured)
            && Uri.TryCreate(configured, UriKind.Absolute, out var configuredUri)
            && string.Equals(configuredUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return configured;
        }

        var bundleRoot = _options.BundleRoot;

        // :404-405 的 Windows 对应物。
        var candidates = new List<string>();

        var directory = new DirectoryInfo(bundleRoot);
        for (var depth = 0; depth <= 5 && directory is not null; depth++)
        {
            candidates.Add(Path.Combine(directory.FullName, "ocr-packs", "catalog.json"));
            candidates.Add(Path.Combine(directory.FullName, "OCRPacks", "catalog.json"));
            directory = directory.Parent;
        }

        var local = candidates.FirstOrDefault(File.Exists);
        if (local is not null)
        {
            return local;
        }

        // :410
        throw new OCRPackException(OCRPackError.PackageUnavailable);
    }

    // ── 校验 ────────────────────────────────────────────────────────

    /// <summary>
    /// 取回压缩包，把 IO 层异常翻译成 <see cref="OCRPackException"/>。
    ///
    /// 为什么需要这一层：catalog 里的 <c>downloadURL</c> 指向的文件可能不存在
    /// （本地 Alpha 分发时很常见 —— catalog 更新了但压缩包还没拷过来）。
    /// 不加这层会把 <see cref="FileNotFoundException"/> 直接抛给设置页，
    /// 用户看到的是 .NET 的原始堆栈而不是「没有找到适合这台机器的包」。
    /// </summary>
    private async Task DownloadAsync(
        string url,
        string archive,
        Action<double?> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            await _transport.DownloadAsync(url, archive, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OCRPackException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new OCRPackException(OCRPackError.PackageUnavailable, error.Message);
        }
    }

    /// <summary>对应 <c>validateCompatibility</c>（`:514-524</c>）。</summary>
    private static void ValidateCompatibility(string? architecture, string? minimumSystem)
    {
        // :515 —— architecture 未声明视为兼容（可选解包）。
        if (!OcrHostArchitecture.Matches(architecture))
        {
            throw new OCRPackException(OCRPackError.UnsupportedArchitecture, OcrHostArchitecture.Current);
        }

        if (!string.IsNullOrWhiteSpace(minimumSystem)
            && TryParseVersion(minimumSystem, out var required)
            && !WindowsVersionInfo.IsAtLeast(required))
        {
            throw new OCRPackException(OCRPackError.UnsupportedSystem, minimumSystem);
        }
    }

    /// <summary>对应 <c>validateExecutable</c>（`:498-512</c>）。</summary>
    private static string ValidateExecutable(OCRPackManifest manifest, string packDirectory)
    {
        var root = Path.GetFullPath(packDirectory);

        // Mac 用 resolvingSymlinksInPath() 解析符号链接后再比较。
        // Windows 上包内不放符号链接，但用 GetFullPath 做规范化仍然是必要的
        // （参考文档 §14 #35：standardizedFileURL ≠ Path.GetFullPath，必须先归一化）。
        string executable;
        try
        {
            executable = Path.GetFullPath(Path.Combine(root, manifest.Executable));
        }
        catch (Exception)
        {
            throw new OCRPackException(OCRPackError.UnsafeExecutablePath);
        }

        // :502 —— 必须落在包目录内。前缀比较必须带分隔符，
        // 否则 "/packs/paddleOCR" 会被 "/packs/paddleOCR-evil" 冒充通过。
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!executable.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new OCRPackException(OCRPackError.UnsafeExecutablePath);
        }

        // :503 —— Mac 的 isExecutableFile。Windows 没有可执行位，
        // 退化为「文件存在 + 扩展名可执行（或无扩展名的启动器）」。
        if (!IsExecutableFile(executable))
        {
            throw new OCRPackException(OCRPackError.UnsafeExecutablePath);
        }

        // :506-510 —— 可执行文件 SHA-256 必须匹配。
        if (!string.IsNullOrWhiteSpace(manifest.ExecutableSha256))
        {
            var actual = Sha256OfFile(executable);
            if (!string.Equals(actual, manifest.ExecutableSha256.Trim().ToLowerInvariant(), StringComparison.Ordinal))
            {
                throw new OCRPackException(OCRPackError.ChecksumMismatch);
            }
        }

        return executable;
    }

    /// <summary>
    /// 可执行文件校验的「只问结果」版本。对应 Mac 里 <c>(try? validatedExecutable(for:)) != nil</c>
    /// 这种用法（`:157</c>、`:164</c>、`:354</c>）。
    ///
    /// 必须是**实例**方法：包目录依赖注入进来的 <see cref="OCRPackManagerOptions.ApplicationSupportRoot"/>，
    /// 用静态版本会静默回落到 %APPDATA%，测试里就会出现「装在 A 处、查 B 处」的假阴性。
    /// </summary>
    private bool TryValidatedExecutable(OCREnginePreference engine, out string executable)
    {
        executable = string.Empty;
        if (engine == OCREnginePreference.AppleVision)
        {
            return false;
        }

        try
        {
            var manifest = TryReadManifest(PackDirectory(engine));
            if (manifest is null)
            {
                return false;
            }

            executable = ValidateExecutable(manifest, PackDirectory(engine));
            return true;
        }
        catch (OCRPackException)
        {
            return false;
        }
    }

    /// <summary>
    /// Windows 上「可执行文件」的判定：存在 + 扩展名在可执行集合内，
    /// 或没有扩展名（PaddleOCR 的原生启动器 <c>bin/paddleocr-adapter</c> 就是无扩展名）。
    /// </summary>
    private static bool IsExecutableFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return extension.Length == 0
            || extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>对应 <c>healthCheck</c>（`:526-548</c>）。</summary>
    private static async Task HealthCheckAsync(
        string executable,
        OCRPackManifest manifest,
        CancellationToken cancellationToken)
    {
        OCRProcessResult result;
        try
        {
            result = await OCRProcessRunner.RunAsync(
                executable,
                manifest.EffectiveHealthCheckArguments,
                OCRProcessRunner.HealthCheckTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OCRPackException error)
        {
            // :537-539 —— 把子进程错误包成 healthCheckFailed，保留原始文案。
            throw new OCRPackException(OCRPackError.HealthCheckFailed, error.Detail ?? error.Message);
        }

        var response = OCRPackHealthResponse.TryParse(result.StandardOutput);
        if (response is null || !response.Ok)
        {
            throw new OCRPackException(OCRPackError.HealthCheckFailed, "返回格式无效");
        }

        // :542 —— engine 必须与 manifest 一致。
        if (!string.Equals(response.Engine, manifest.Engine, StringComparison.Ordinal))
        {
            throw new OCRPackException(OCRPackError.HealthCheckFailed, "返回格式无效");
        }

        // :545-547 —— 若返回 architecture 必须与宿主一致。
        if (!OcrHostArchitecture.Matches(response.Architecture))
        {
            throw new OCRPackException(OCRPackError.UnsupportedArchitecture, OcrHostArchitecture.Current);
        }
    }

    /// <summary>对应 <c>readManifest(at:)</c>（`:489-496</c>）。</summary>
    private static OCRPackManifest? TryReadManifest(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "manifest.json");
            if (!File.Exists(path))
            {
                return null;
            }

            return OCRPackManifest.TryParse(File.ReadAllText(path));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ── 压缩包 ──────────────────────────────────────────────────────

    /// <summary>对应 <c>validateArchiveEntries</c>（`:444-460</c>）的 Windows 实现。</summary>
    private static void ValidateArchiveEntries(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);

        foreach (var entry in archive.Entries)
        {
            if (!OcrPackArchiveGuard.IsEntryNameSafe(entry.FullName))
            {
                // :456-458 —— 一个不安全条目就整体拒绝，不做部分解压。
                throw new OCRPackException(OCRPackError.UnsafeArchive);
            }
        }
    }

    /// <summary>
    /// 逐条解压，替代 <c>/usr/bin/ditto -x -k</c>（`:224-228</c>）。
    ///
    /// 两道防线：
    /// ① <see cref="ValidateArchiveEntries"/> 已把所有条目名过了一遍（本方法再复核一次，
    ///    因为理论上 <c>ZipFile.OpenRead</c> 两次打开的条目清单可以不同 —— Mac 的
    ///    zipinfo/ditto 正是分两次读，这里合并成一次）；
    /// ② 每个条目的落盘路径再经 <see cref="OcrPackArchiveGuard.TryResolveDestination"/>
    ///    夹回目标目录。
    /// </summary>
    private static Task ExtractSafelyAsync(
        string archivePath,
        string destinationRoot,
        CancellationToken cancellationToken)
        => Task.Run(
            () =>
            {
                using var archive = ZipFile.OpenRead(archivePath);
                Directory.CreateDirectory(destinationRoot);

                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!OcrPackArchiveGuard.TryResolveDestination(destinationRoot, entry.FullName, out var target))
                    {
                        throw new OCRPackException(OCRPackError.UnsafeArchive);
                    }

                    // 目录条目（FullName 以 "/" 结尾、Name 为空）只建目录。
                    if (entry.Name.Length == 0)
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }

                    var parent = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(parent))
                    {
                        Directory.CreateDirectory(parent);
                    }

                    entry.ExtractToFile(target, overwrite: true);
                }
            },
            cancellationToken);

    /// <summary>对应 <c>locatePackRoot(in:)</c>（`:462-478</c>）。</summary>
    private static string LocatePackRoot(string extracted)
    {
        if (File.Exists(Path.Combine(extracted, "manifest.json")))
        {
            return extracted;
        }

        // :466 —— skipsHiddenFiles：隐藏目录不算候选。
        var roots = Directory
            .GetDirectories(extracted)
            .Where(child => !IsHidden(child))
            .Where(child => File.Exists(Path.Combine(child, "manifest.json")))
            .ToList();

        // :474-475 —— 必须**恰好一个**。
        if (roots.Count != 1)
        {
            throw new OCRPackException(OCRPackError.InvalidManifest);
        }

        return roots[0];
    }

    private static bool IsHidden(string path)
    {
        var attributes = File.GetAttributes(path);
        return (attributes & FileAttributes.Hidden) == FileAttributes.Hidden;
    }

    // ── 路径 ────────────────────────────────────────────────────────

    private string PackDirectory(OCREnginePreference engine) =>
        PackDirectoryOf(engine, _options);

    private static string PackDirectoryOf(OCREnginePreference engine, OCRPackManagerOptions? options = null)
    {
        var root = options?.ApplicationSupportRoot
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AI Screenshot");

        // :571-575 —— ⚠️ 目录名是 "AI Screenshot"（与 socket 路径用的 "Ta" 不同，参考文档 §9.7 末尾）。
        return Path.Combine(root, "OCRPacks", engine.RawValue());
    }

    // ── 工具 ────────────────────────────────────────────────────────

    /// <summary>对应 <c>sha256(of:progress:)</c>（`:550-569</c>）：1 MiB 块。</summary>
    private static async Task<string> Sha256Async(
        string path,
        Action<double>? progress,
        CancellationToken cancellationToken)
    {
        var expectedBytes = new FileInfo(path).Length;

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[1024 * 1024];
        long processed = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
            processed += read;
            if (expectedBytes > 0)
            {
                progress?.Invoke(Math.Min(1, (double)processed / expectedBytes));
            }
        }

        progress?.Invoke(1);

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string Sha256OfFile(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: false);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool IsLocalUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return uri.IsFile;
        }

        // 裸路径（Windows 盘符或 UNC）视为本地。
        return true;
    }

    private static string ToLocalPath(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            return uri.LocalPath;
        }

        return url;
    }

    /// <summary>
    /// 数字版本比较。对应 Mac 的
    /// <c>$0.version.compare($1.version, options: .numeric)</c>（`:381</c>）。
    /// 逐段按整数比，非数字段按序数字符串比 —— 例如 1.10.0 &gt; 1.9.0（纯字符串比较会搞反）。
    /// </summary>
    private sealed class NumericVersionComparer : IComparer<OCRPackCatalogPackage>
    {
        public static readonly NumericVersionComparer Instance = new();

        public int Compare(OCRPackCatalogPackage? x, OCRPackCatalogPackage? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            return CompareVersions(x.Version, y.Version);
        }

        public static int CompareVersions(string left, string right)
        {
            var l = left.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var r = right.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            for (var i = 0; i < Math.Max(l.Length, r.Length); i++)
            {
                var ls = i < l.Length ? l[i] : "0";
                var rs = i < r.Length ? r[i] : "0";

                if (long.TryParse(ls, out var ln) && long.TryParse(rs, out var rn))
                {
                    if (ln != rn)
                    {
                        return ln.CompareTo(rn);
                    }

                    continue;
                }

                var comparison = string.CompareOrdinal(ls, rs);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return 0;
        }
    }

    internal static bool TryParseVersion(string value, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        var parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Length > 4)
        {
            return false;
        }

        var numbers = new int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out numbers[i]))
            {
                return false;
            }
        }

        version = new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(file));
            File.Copy(file, target, overwrite: true);
        }

        foreach (var child in Directory.GetDirectories(source))
        {
            CopyDirectory(child, Path.Combine(destination, Path.GetFileName(child)));
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
            // 清理失败不阻断主流程；临时目录本就在 %TEMP% 下。
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 同上
        }
    }
}

/// <summary>
/// Windows 版本读取。
///
/// <c>Environment.OSVersion</c> 在 .NET 上仍可能被应用的兼容清单影响（报告成 6.2），
/// 而 <c>RtlGetVersion</c> 直接读内核，不受清单影响 ——
/// 这正是 <c>minimumWindows</c> 校验必须准确的原因。
/// </summary>
internal static class WindowsVersionInfo
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RTL_OSVERSIONINFOW
    {
        public uint dwOSVersionInfoSize;
        public uint dwMajorVersion;
        public uint dwMinorVersion;
        public uint dwBuildNumber;
        public uint dwPlatformId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szCSDVersion;
    }

    [DllImport("ntdll.dll")]
    private static extern int RtlGetVersion(ref RTL_OSVERSIONINFOW lpVersionInformation);

    private static readonly Lazy<Version> Current = new(() =>
    {
        var info = new RTL_OSVERSIONINFOW { dwOSVersionInfoSize = (uint)Marshal.SizeOf<RTL_OSVERSIONINFOW>() };
        try
        {
            if (RtlGetVersion(ref info) == 0)
            {
                return new Version((int)info.dwMajorVersion, (int)info.dwMinorVersion, (int)info.dwBuildNumber);
            }
        }
        catch (DllNotFoundException)
        {
            // 非 Windows 平台（理论上不会，TFM 已限定 -windows）。
        }

        return Environment.OSVersion.Version;
    });

    public static Version Version => Current.Value;

    public static bool IsAtLeast(Version required)
    {
        var current = Version;
        return current.Major > required.Major
            || (current.Major == required.Major && current.Minor > required.Minor)
            || (current.Major == required.Major && current.Minor == required.Minor
                && current.Build >= required.Build);
    }
}
