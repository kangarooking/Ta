using System.Text.Json;
using System.Text.Json.Serialization;
using Ta.OCR.Models;

namespace Ta.OCR.Packs;

/// <summary>
/// 增强包 manifest.json。
///
/// 逐字对应 Mac 版 <c>OCRPackManifest</c>
/// （<c>AIScreenshotApp/Recognition/OptionalOCRPackManager.swift:7-36</c>）与
/// <c>docs/ocr-enhancement-pack-spec.md:25-36</c>。
///
/// Windows 差异（参考文档 §14 #32）：
/// · <see cref="MinimumMacOS"/> 在 Windows 上改判「最低 Windows 版本」，
///   同时接受 <c>minimumWindows</c> 键以兼容未来改名。
/// · <see cref="Architecture"/> 与宿主比较时由 <see cref="OcrHostArchitecture"/> 决定，
///   Windows 侧明确选用 <c>x86_64</c>（与协议文档和 Mac 的 Intel 命名一致），
///   并把 RID 形态 <c>win-x64</c> 作为别名接受。
/// </summary>
public sealed record OCRPackManifest
{
    [JsonPropertyName("engine")]
    public required string Engine { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    /// <summary>包内可执行文件，相对包根。协议要求必须是相对路径。</summary>
    [JsonPropertyName("executable")]
    public required string Executable { get; init; }

    [JsonPropertyName("executableSHA256")]
    public string? ExecutableSha256 { get; init; }

    [JsonPropertyName("architecture")]
    public string? Architecture { get; init; }

    [JsonPropertyName("minimumMacOS")]
    public string? MinimumMacOS { get; init; }

    /// <summary>Windows 侧优先读这个键；与 <see cref="MinimumMacOS"/> 取先出现的那个。</summary>
    [JsonPropertyName("minimumWindows")]
    public string? MinimumWindows { get; init; }

    [JsonPropertyName("healthCheckArguments")]
    public IReadOnlyList<string>? HealthCheckArguments { get; init; }

    [JsonPropertyName("workerArguments")]
    public IReadOnlyList<string>? WorkerArguments { get; init; }

    /// <summary>健康检查参数，缺省 <c>["--health-check"]</c>（`:527`）。</summary>
    public IReadOnlyList<string> EffectiveHealthCheckArguments =>
        HealthCheckArguments is { Count: > 0 } ? HealthCheckArguments : ["--health-check"];

    /// <summary>是否声明了常驻 Worker 模式。为空/空数组表示只支持一次性进程协议。</summary>
    public bool SupportsWorker => WorkerArguments is { Count: > 0 };

    /// <summary>生效的最低系统版本字符串（Windows 语义）。</summary>
    public string? MinimumSystemVersion => MinimumWindows ?? MinimumMacOS;

    public static OCRPackManifest? TryParse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<OCRPackManifest>(json, OCRPackManifest.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        // 未知字段忽略 —— 与 Swift 的 JSONDecoder 默认行为一致。
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip,
    };
}

/// <summary>一次性进程协议的 stdout 载荷（<c>OCRPackResponse</c>，`:38-41</c>）。</summary>
public sealed record OCRPackResponse
{
    // 参数类型必须与属性一致（可空），否则 System.Text.Json 的反序列化
    // 构造器绑定会抛 InvalidOperationException。
    public OCRPackResponse(string? text, double? confidence)
    {
        Text = text;
        Confidence = confidence;
    }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; init; }

    /// <summary>
    /// 解析一次性协议的输出。字段缺失时返回 <c>null</c>（对应 Mac 侧
    /// <c>guard let decoded = try? JSONDecoder().decode(…)</c> 解码失败 → <c>invalidResponse</c>，`:279-281</c>）。
    /// </summary>
    public static OCRPackResponse? TryParse(string json)
    {
        try
        {
            var decoded = JsonSerializer.Deserialize<OCRPackResponse>(json, OCRPackManifest.JsonOptions);
            return decoded is { Text: not null, Confidence: not null } ? decoded : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>健康检查的 stdout 载荷（<c>OCRPackHealthResponse</c>，`:43-49`）。</summary>
public sealed record OCRPackHealthResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("engine")]
    public string? Engine { get; init; }

    [JsonPropertyName("packVersion")]
    public string? PackVersion { get; init; }

    [JsonPropertyName("architecture")]
    public string? Architecture { get; init; }

    [JsonPropertyName("offline")]
    public bool? Offline { get; init; }

    public static OCRPackHealthResponse? TryParse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<OCRPackHealthResponse>(json, OCRPackManifest.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Worker 就绪首行（<c>OCRWorkerReadyResponse</c>，<c>PersistentOCRWorker.swift:4-7</c>）。</summary>
public sealed record OCRWorkerReadyResponse
{
    [JsonPropertyName("event")]
    public string? Event { get; init; }

    [JsonPropertyName("ok")]
    public bool Ok { get; init; }
}

/// <summary>Worker 识别请求（<c>OCRWorkerRequest</c>，<c>PersistentOCRWorker.swift:9-14</c>）。</summary>
public sealed record OCRWorkerRequest
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("command")]
    public string Command { get; init; } = "recognize";

    [JsonPropertyName("input")]
    public string? Input { get; init; }

    /// <summary>null 时**省略**该字段 —— Mac 侧 <c>Int?</c> 编码策略就是 omit（JSONEncoder 默认省略 nil）。</summary>
    [JsonPropertyName("detectionSideLimit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DetectionSideLimit { get; init; }
}

/// <summary>Worker 识别响应（<c>OCRWorkerRecognitionResponse</c>，<c>PersistentOCRWorker.swift:16-22</c>）。</summary>
public sealed record OCRWorkerRecognitionResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    public static OCRWorkerRecognitionResponse? TryParse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<OCRWorkerRecognitionResponse>(json, OCRPackManifest.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>发行清单（<c>OCRPackCatalog</c>，`:51-54`）。</summary>
public sealed record OCRPackCatalog
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("packages")]
    public IReadOnlyList<OCRPackCatalogPackage>? Packages { get; init; }

    public static OCRPackCatalog? TryParse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<OCRPackCatalog>(json, OCRPackManifest.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>发行清单里的单个包（<c>OCRPackCatalogPackage</c>，`:56-64`）。</summary>
public sealed record OCRPackCatalogPackage
{
    [JsonPropertyName("engine")]
    public required string Engine { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("architecture")]
    public required string Architecture { get; init; }

    [JsonPropertyName("minimumMacOS")]
    public string? MinimumMacOS { get; init; }

    [JsonPropertyName("minimumWindows")]
    public string? MinimumWindows { get; init; }

    [JsonPropertyName("downloadURL")]
    public required string DownloadUrl { get; init; }

    [JsonPropertyName("archiveSHA256")]
    public required string ArchiveSha256 { get; init; }

    [JsonPropertyName("archiveSize")]
    public long ArchiveSize { get; init; }

    public string? MinimumSystemVersion => MinimumWindows ?? MinimumMacOS;
}

/// <summary>某个引擎当前可安装的包（<c>OCRPackAvailability</c>，`:66-73`）。</summary>
public sealed record OCRPackAvailability
{
    public OCRPackAvailability(OCRPackCatalogPackage package, bool isLocal)
    {
        Package = package;
        IsLocal = isLocal;
    }

    public OCRPackCatalogPackage Package { get; }

    /// <summary>是否来自本地文件（file:// / 相对路径解析到磁盘），而非 HTTPS 下载。</summary>
    public bool IsLocal { get; }

    public string FormattedSize => FormatByteCount(Package.ArchiveSize);

    /// <summary>
    /// 字节数的人类可读形式。Mac 用 <c>ByteCountFormatter</c>（`.file` 样式，十进制 1000 进制），
    /// 这里同样按 1000 进制，避免与 Mac 显示不一致。
    /// </summary>
    public static string FormatByteCount(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        if (bytes < 1000)
        {
            return $"{bytes} bytes";
        }

        var value = (double)bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        return unit == 1 ? $"{Math.Round(value)} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }
}

/// <summary>已安装信息（<c>OCRPackInstalledInfo</c>，`:75-78`）。</summary>
public sealed record OCRPackInstalledInfo
{
    public OCRPackInstalledInfo(string version, string? architecture)
    {
        Version = version;
        Architecture = architecture;
    }

    public string Version { get; }

    public string? Architecture { get; }
}

/// <summary>安装进度（<c>OCRPackInstallProgress</c>，`:80-92`）。</summary>
public sealed record OCRPackInstallProgress
{
    public OCRPackInstallProgress(OCRPackInstallStage stage, double? fraction)
    {
        Stage = stage;
        Fraction = fraction;
    }

    public OCRPackInstallStage Stage { get; }

    /// <summary>0..1；<c>null</c> 表示不确定进度。</summary>
    public double? Fraction { get; }
}

/// <summary>
/// 安装阶段。中文标签逐字对应 Mac 的 <c>Stage</c> rawValue（`:82-87`）——
/// 设置页直接显示这些字符串，改动会改变用户可见文案。
/// </summary>
public enum OCRPackInstallStage
{
    [System.ComponentModel.Description("正在查找增强包…")]
    Resolving,

    [System.ComponentModel.Description("正在下载…")]
    Downloading,

    [System.ComponentModel.Description("正在校验…")]
    Verifying,

    [System.ComponentModel.Description("正在解压…")]
    Extracting,

    [System.ComponentModel.Description("正在启动检查…")]
    HealthChecking,

    [System.ComponentModel.Description("正在安装…")]
    Installing,
}

/// <summary>
/// 增强包错误。逐字对应 Mac 的 <c>OptionalOCRPackError</c>（`:100-134`）。
/// 中文文案逐字保留（`unsupportedSystem` 一条把 macOS 换成 Windows）。
/// </summary>
public enum OCRPackError
{
    NotInstalled,
    InvalidManifest,
    InvalidCatalog,
    PackageUnavailable,
    WrongEngine,
    UnsupportedArchitecture,
    UnsupportedSystem,
    InsecureDownloadUrl,
    UnsafeArchive,
    UnsafeExecutablePath,
    ChecksumMismatch,
    HealthCheckFailed,
    ExecutionFailed,
    InvalidResponse,
}

/// <summary>带详细信息的增强包异常。<see cref="OCRPackError"/> 的运行时载体。</summary>
public sealed class OCRPackException : Exception
{
    public OCRPackException(OCRPackError code, string? detail = null)
        : base(Describe(code, detail))
    {
        Code = code;
        Detail = detail;
    }

    public OCRPackError Code { get; }

    public string? Detail { get; }

    /// <summary>错误文案。逐字对应 Mac 的 <c>errorDescription</c>（`:116-133`）。</summary>
    public static string Describe(OCRPackError code, string? detail = null) => code switch
    {
        OCRPackError.NotInstalled => "所选 OCR 增强包尚未安装。",
        OCRPackError.InvalidManifest => "增强包缺少有效的 manifest.json。",
        OCRPackError.InvalidCatalog => "增强包发行清单格式无效。",
        OCRPackError.PackageUnavailable => "没有找到适合这台 Mac 的 PaddleOCR 增强包。",
        OCRPackError.WrongEngine => "增强包类型与所选引擎不一致。",
        OCRPackError.UnsupportedArchitecture => $"增强包不支持当前架构：{detail}。",
        OCRPackError.UnsupportedSystem => $"增强包要求 Windows {detail} 或更高版本。",
        OCRPackError.InsecureDownloadUrl => "增强包下载地址必须使用 HTTPS，开发版也可以使用本地文件。",
        OCRPackError.UnsafeArchive => "增强包压缩文件包含不安全路径，已拒绝安装。",
        OCRPackError.UnsafeExecutablePath => "增强包的可执行文件路径不安全。",
        OCRPackError.ChecksumMismatch => "增强包校验失败，未安装。",
        OCRPackError.HealthCheckFailed => $"增强包启动检查失败：{detail}",
        OCRPackError.ExecutionFailed => $"OCR 增强包运行失败：{detail}",
        OCRPackError.InvalidResponse => "OCR 增强包返回格式无效。",
        _ => "OCR 增强包出现未知错误。",
    };
}
