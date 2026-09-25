using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ta.AgentBridge.Protocol;

namespace Ta.AgentBridge;

/// <summary>
/// 审计条目。对应 Mac 版 TaAgentAuditLog.swift:5-17。
/// ⚠️ <see cref="RequestFingerprint"/> 从不存 requestID 明文 —— 只存其 SHA256 指纹。
/// ⚠️ params 与响应 data 的任何内容都不写入（产品承诺）。
/// </summary>
public sealed record AgentAuditEntry
{
    public string RequestFingerprint { get; init; }
    public DateTime OccurredAtUtc { get; init; }
    public string ClientName { get; init; }
    public string ClientVersion { get; init; }
    public AgentMethod Method { get; init; }
    public bool Succeeded { get; init; }
    public int DurationMs { get; init; }
    public bool CloudUploaded { get; init; }
    public AgentErrorCode? ErrorCode { get; init; }

    public AgentAuditEntry(
        string requestFingerprint,
        DateTime occurredAtUtc,
        string clientName,
        string clientVersion,
        AgentMethod method,
        bool succeeded,
        int durationMs,
        bool cloudUploaded,
        AgentErrorCode? errorCode)
    {
        RequestFingerprint = requestFingerprint;
        OccurredAtUtc = occurredAtUtc.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(occurredAtUtc, DateTimeKind.Utc)
            : occurredAtUtc.ToUniversalTime();
        ClientName = clientName;
        ClientVersion = clientVersion;
        Method = method;
        Succeeded = succeeded;
        DurationMs = durationMs;
        CloudUploaded = cloudUploaded;
        ErrorCode = errorCode;
    }

    public string OccurredAtIso8601 => OccurredAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>把条目构造成 JSONValue（键名与 Mac 的 Codable 字段一致，序列化时再排序）。</summary>
    public AgentJsonValue ToJsonValue()
    {
        var fields = new Dictionary<string, AgentJsonValue>(StringComparer.Ordinal)
        {
            ["requestID"] = new JsonString(RequestFingerprint),
            ["occurredAt"] = new JsonString(OccurredAtIso8601),
            ["clientName"] = new JsonString(ClientName),
            ["clientVersion"] = new JsonString(ClientVersion),
            ["method"] = new JsonString(Method.Raw()),
            ["succeeded"] = new JsonBool(Succeeded),
            ["durationMs"] = new JsonInteger(DurationMs),
            ["cloudUploaded"] = new JsonBool(CloudUploaded),
        };

        // errorCode 为 null 时省略 —— 对应 Mac 的 errorCode? 解码语义。
        if (ErrorCode is { } code)
        {
            fields["errorCode"] = new JsonString(code.Raw());
        }

        return new JsonObject(fields);
    }
}

/// <summary>
/// 审计日志。对应 Mac 版 TaAgentAuditLog.swift:19-106。
///
/// ⚠️ 产品承诺：不保存请求参数、识别正文或图片数据；requestID 只存指纹。
/// 上限 100 条 FIFO 裁剪；recent(limit: 20) 返回最新 N 条倒序。
/// 目录/文件每次写入都重设 ACL（对应 POSIX 0700/0600）。
/// </summary>
public sealed class TaAgentAuditLog
{
    public const int DefaultMaximumEntries = 100;

    private readonly object _lock = new();

    public string FileUrl { get; }
    public int MaximumEntries { get; }

    public TaAgentAuditLog(string? fileUrl = null, int maximumEntries = DefaultMaximumEntries)
    {
        FileUrl = Path.GetFullPath(fileUrl ?? DefaultFileUrl());
        MaximumEntries = Math.Max(1, maximumEntries);
    }

    /// <summary>记录一次请求/响应。对应 Mac: record(request:response:occurredAt:)。</summary>
    public void Record(AgentRequestEnvelope request, AgentResponseEnvelope response, DateTime? occurredAt = null)
    {
        lock (_lock)
        {
            var entries = ReadEntries().ToList();
            entries.Add(new AgentAuditEntry(
                RequestFingerprint(request.RequestId),
                occurredAt ?? DateTime.UtcNow,
                SanitizedClientField(request.Client.Name),
                SanitizedClientField(request.Client.Version),
                request.Method,
                response.Ok,
                Math.Max(0, response.Meta?.DurationMs ?? 0),
                response.Meta?.CloudUploaded ?? false,
                response.Error?.Code));

            if (entries.Count > MaximumEntries)
            {
                entries.RemoveRange(0, entries.Count - MaximumEntries);
            }

            Write(entries);
        }
    }

    /// <summary>读取最近 N 条，倒序。对应 Mac: recent(limit:)。</summary>
    public IReadOnlyList<AgentAuditEntry> Recent(int limit = 20)
    {
        if (limit <= 0)
        {
            return Array.Empty<AgentAuditEntry>();
        }

        lock (_lock)
        {
            var entries = ReadEntries().ToList();
            return entries
                .Skip(Math.Max(0, entries.Count - limit))
                .Reverse()
                .ToList();
        }
    }

    /// <summary>清空审计日志。对应 Mac: clear()。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            if (File.Exists(FileUrl))
            {
                File.Delete(FileUrl);
            }
        }
    }

    private IReadOnlyList<AgentAuditEntry> ReadEntries()
    {
        if (!File.Exists(FileUrl))
        {
            return Array.Empty<AgentAuditEntry>();
        }

        var decoded = AgentJson.DecodeValue(File.ReadAllBytes(FileUrl));
        if (decoded is not JsonArray array)
        {
            return Array.Empty<AgentAuditEntry>();
        }

        return array.Items.Select(ReadEntry).ToList();
    }

    private static AgentAuditEntry ReadEntry(AgentJsonValue value)
    {
        if (value is not JsonObject obj)
        {
            throw new InvalidDataException("审计条目不是对象。");
        }

        string ReadString(string key) => obj.TryGet(key, out var v) && v is JsonString s ? s.Value : "";
        long ReadInt(string key) => obj.TryGet(key, out var v) && v is JsonInteger i ? i.Value : 0;
        bool ReadBool(string key) => obj.TryGet(key, out var v) && v is JsonBool b ? b.Value : false;

        AgentMethod method = AgentMethodExtensions.TryParse(ReadString("method"), out var m) ? m : AgentMethod.SystemStatus;
        // errorCode 以 Raw()（如 SCREEN_PERMISSION_REQUIRED）写入，不能用枚举名 TryParse 判断。
        AgentErrorCode? errorCode = obj.TryGet("errorCode", out var ec) && ec is JsonString ecs
            ? ParseErrorCode(ecs.Value)
            : null;

        return new AgentAuditEntry(
            ReadString("requestID"),
            DateTime.Parse(ReadString("occurredAt"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            ReadString("clientName"),
            ReadString("clientVersion"),
            method,
            ReadBool("succeeded"),
            (int)ReadInt("durationMs"),
            ReadBool("cloudUploaded"),
            errorCode);
    }

    private static AgentErrorCode? ParseErrorCode(string raw)
    {
        foreach (var code in Enum.GetValues<AgentErrorCode>())
        {
            if (code.Raw() == raw)
            {
                return code;
            }
        }

        return null;
    }

    private void Write(IReadOnlyList<AgentAuditEntry> entries)
    {
        var directory = Path.GetDirectoryName(FileUrl)!;
        // 只对我们**新建**的专用目录收紧 ACL（对应 Mac 的 0700）。对既有共享目录
        // （如测试或用户指定的任意位置）设置受保护 DACL 既可能长时间阻塞 SetNamedSecurityInfoW，
        // 也会错误地剥夺其他条目对共享目录的继承权限 —— 文件级 ACL 已足够保护审计内容。
        var directoryExisted = Directory.Exists(directory);
        Directory.CreateDirectory(directory);
        if (!directoryExisted)
        {
            WindowsSecurity.RestrictDirectoryToCurrentUser(directory);
        }

        var array = new JsonArray(entries.Select(e => e.ToJsonValue()).ToArray());
        var bytes = AgentJson.EncodeValue(array);

        // 原子替换：写临时文件后 MoveFileEx(…, MOVEFILE_REPLACE_EXISTING)。
        var tempPath = FileUrl + ".tmp";
        File.WriteAllBytes(tempPath, bytes);
        File.Move(tempPath, FileUrl, overwrite: true);
        WindowsSecurity.RestrictFileToCurrentUser(FileUrl);
    }

    // ── 静态辅助 ────────────────────────────────────────────────────

    /// <summary>requestID 指纹：sha256:<SHA256 前 8 字节 hex>。对应 Mac: requestFingerprint()（:90-96）。</summary>
    public static string RequestFingerprint(string requestId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(requestId));
        var builder = new StringBuilder("sha256:", 8 + 16);
        for (var i = 0; i < 8; i++)
        {
            builder.Append(digest[i].ToString("x2"));
        }

        return builder.ToString();
    }

    /// <summary>剥离控制字符并截断到 80 字符。对应 Mac: sanitizedClientField()（:83-88）。</summary>
    private static string SanitizedClientField(string value)
    {
        var filtered = new string(value.Where(c => !char.IsControl(c)).ToArray());
        return filtered.Length <= 80 ? filtered : filtered[..80];
    }

    private static string DefaultFileUrl()
    {
        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(basePath))
        {
            basePath = Path.GetTempPath();
        }

        return Path.Combine(basePath, "Ta", "Agent", "audit-v1.json");
    }
}
