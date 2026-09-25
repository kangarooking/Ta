namespace Ta.Settings.Core;

/// <summary>
/// 服务地址校验。1:1 对应 macOS <c>ProviderEndpointValidator</c>
/// （AIScreenshotCore/Models/ProviderEndpointValidator.swift:3-24）。
///
/// 规则（照抄）：
/// - 空串视为合法（返回 null，由调用方另外判断必填）；
/// - 解析不出 scheme / host → <c>Base URL 格式无效</c>；
/// - 非 https，且不是「localhost / 127.0.0.1 / ::1 + http」→ <c>远程服务必须使用 HTTPS</c>。
/// </summary>
public static class EndpointValidator
{
    /// <summary>允许明文 http 的本机主机名。</summary>
    public static IReadOnlyList<string> LocalHosts { get; } = new[] { "localhost", "127.0.0.1", "::1" };

    /// <summary>返回校验消息；合法时返回 null。</summary>
    public static string? ValidationMessage(string? rawValue)
    {
        var trimmed = (rawValue ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
        {
            return "Base URL 格式无效";
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        var host = uri.Host.ToLowerInvariant();
        var isLocal = LocalHosts.Contains(host);
        if (!string.Equals(scheme, "https", StringComparison.Ordinal)
            && !(isLocal && string.Equals(scheme, "http", StringComparison.Ordinal)))
        {
            return "远程服务必须使用 HTTPS";
        }

        return null;
    }

    /// <summary>是否合法（<see cref="ValidationMessage"/> 为 null）。</summary>
    public static bool IsValid(string? rawValue) => ValidationMessage(rawValue) is null;
}
