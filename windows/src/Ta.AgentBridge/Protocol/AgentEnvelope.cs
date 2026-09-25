using System.Globalization;

namespace Ta.AgentBridge.Protocol;

/// <summary>当前协议版本。对应 Mac: AgentProtocol.currentVersion。</summary>
public static class AgentProtocol
{
    public const int CurrentVersion = 1;
}

/// <summary>客户端身份。对应 Mac: AgentClientInfo。</summary>
public sealed record AgentClientInfo
{
    public string Name { get; init; }
    public string Version { get; init; }

    public AgentClientInfo(string name, string version)
    {
        Name = name;
        Version = version;
    }
}

/// <summary>
/// 请求信封。对应 Mac 版 TaAgentContracts/AgentEnvelope.swift:17-51。
/// ⚠️ 线格式里 requestId 用 **camelCase**（Swift 的 CodingKeys 把 requestID 映射成 requestId）。
/// </summary>
public sealed record AgentRequestEnvelope
{
    public int ProtocolVersion { get; init; }
    public string RequestId { get; init; }
    public AgentMethod Method { get; init; }
    public IReadOnlyDictionary<string, AgentJsonValue> Params { get; init; }
    public AgentClientInfo Client { get; init; }

    public AgentRequestEnvelope(
        int protocolVersion,
        string requestId,
        AgentMethod method,
        IReadOnlyDictionary<string, AgentJsonValue>? @params = null,
        AgentClientInfo? client = null)
    {
        ProtocolVersion = protocolVersion;
        RequestId = requestId;
        Method = method;
        Params = @params ?? new Dictionary<string, AgentJsonValue>(StringComparer.Ordinal);
        Client = client ?? new AgentClientInfo("ta", "dev");
    }

    public AgentRequestEnvelope(string requestId, AgentMethod method, AgentClientInfo client, IReadOnlyDictionary<string, AgentJsonValue>? @params = null)
        : this(AgentProtocol.CurrentVersion, requestId, method, @params, client)
    {
    }

    /// <summary>对应 Mac: validateProtocolVersion()。</summary>
    public void ValidateProtocolVersion(int supported = AgentProtocol.CurrentVersion)
    {
        if (ProtocolVersion != supported)
        {
            throw new AgentProtocolException(ProtocolVersion, supported);
        }
    }

    // ── params 读取辅助。对应 Mac 的 Dictionary&lt;String, JSONValue&gt; 扩展 ──

    public string? ParamString(string key) =>
        Params.TryGetValue(key, out var v) && v is JsonString s ? s.Value : null;

    public bool? ParamBool(string key) =>
        Params.TryGetValue(key, out var v) && v is JsonBool b ? b.Value : null;

    public double? ParamNumber(string key)
    {
        if (!Params.TryGetValue(key, out var v))
        {
            return null;
        }

        return v switch
        {
            JsonNumber n => n.Value,
            JsonInteger i => i.Value,
            _ => null,
        };
    }

    public long? ParamUInt32(string key)
    {
        var value = ParamNumber(key);
        if (value is null || value < 0 || value > uint.MaxValue)
        {
            return null;
        }

        return (long)value.Value;
    }

    public string[]? ParamStringArray(string key)
    {
        if (!Params.TryGetValue(key, out var v) || v is not JsonArray arr)
        {
            return null;
        }

        return arr.Items
            .OfType<JsonString>()
            .Select(s => s.Value)
            .ToArray();
    }

    /// <summary>params.cloud，映射到 AgentCloudPolicy。</summary>
    public AgentCloudPolicy? ParamCloudPolicy() =>
        ParamString("cloud") is { } raw && AgentCloudPolicyExtensions.TryParse(raw, out var policy)
            ? policy
            : null;
}

/// <summary>响应元数据。对应 Mac: AgentResponseMetadata。</summary>
public sealed record AgentResponseMetadata
{
    public int DurationMs { get; init; }
    public bool CloudUploaded { get; init; }

    public AgentResponseMetadata(int durationMs, bool cloudUploaded)
    {
        DurationMs = durationMs;
        CloudUploaded = cloudUploaded;
    }
}

/// <summary>
/// 工件。对应 Mac 版 TaAgentContracts/AgentArtifacts.swift:3-32。
/// width/height 为 null 时在 JSON 中省略（§14 风险 #40）。
/// </summary>
public sealed record AgentArtifact
{
    public string Id { get; init; }
    public string Path { get; init; }
    public string MimeType { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int Bytes { get; init; }
    public string Sha256 { get; init; }

    /// <summary>ISO8601 序列化，无小数秒（yyyy-MM-ddTHH:mm:ssZ）。</summary>
    public DateTime ExpiresAtUtc { get; init; }

    public AgentArtifact(
        string id,
        string path,
        string mimeType,
        int? width,
        int? height,
        int bytes,
        string sha256,
        DateTime expiresAtUtc)
    {
        Id = id;
        Path = path;
        MimeType = mimeType;
        Width = width;
        Height = height;
        Bytes = bytes;
        Sha256 = sha256;
        ExpiresAtUtc = expiresAtUtc.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(expiresAtUtc, DateTimeKind.Utc)
            : expiresAtUtc.ToUniversalTime();
    }

    public string ExpiresAtIso8601 => ExpiresAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}

/// <summary>
/// 响应信封。对应 Mac 版 TaAgentContracts/AgentEnvelope.swift:63-121。
/// 序列化字节级 parity 见 AgentJson.Encode（sortedKeys / 不转义 / 可选省略）。
/// </summary>
public sealed record AgentResponseEnvelope
{
    public int ProtocolVersion { get; init; }
    public string RequestId { get; init; }
    public bool Ok { get; init; }
    public AgentJsonValue? Data { get; init; }
    public IReadOnlyList<AgentArtifact> Artifacts { get; init; }
    public AgentResponseMetadata? Meta { get; init; }
    public AgentErrorPayload? Error { get; init; }

    public AgentResponseEnvelope(
        string requestId,
        bool ok,
        int protocolVersion = AgentProtocol.CurrentVersion,
        AgentJsonValue? data = null,
        IReadOnlyList<AgentArtifact>? artifacts = null,
        AgentResponseMetadata? meta = null,
        AgentErrorPayload? error = null)
    {
        ProtocolVersion = protocolVersion;
        RequestId = requestId;
        Ok = ok;
        Data = data;
        // ⚠️ artifacts 恒为数组：null 也序列化成 []（§14 风险 #40）。
        Artifacts = artifacts ?? Array.Empty<AgentArtifact>();
        Meta = meta;
        Error = error;
    }

    public static AgentResponseEnvelope Success(
        string requestId,
        AgentJsonValue? data = null,
        IReadOnlyList<AgentArtifact>? artifacts = null,
        AgentResponseMetadata? meta = null) =>
        new(requestId, ok: true, data: data, artifacts: artifacts, meta: meta);

    public static AgentResponseEnvelope Failure(string requestId, AgentErrorPayload error) =>
        new(requestId, ok: false, error: error);
}
