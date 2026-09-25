namespace Ta.AI;

/// <summary>
/// Base URL 校验。
///
/// 逐条对应 Mac 版 <c>Models/ProviderEndpointValidator.swift:3-21</c>：
///   · 必须有 scheme + host，否则 <c>"Base URL 格式无效"</c>
///   · 非 HTTPS 一律拒绝，例外仅 <c>localhost</c> / <c>127.0.0.1</c> / <c>::1</c>（本机可用 http）
///   · 错误文案 <c>"远程服务必须使用 HTTPS"</c>
///
/// 空字符串返回 null（与 Mac 一致：空白由各客户端另行报「请先配置 …」）。
/// </summary>
public sealed class ProviderEndpointValidator
{
    /// <summary>返回错误消息；合法返回 null。</summary>
    public string? ValidationMessage(string? rawValue)
    {
        var trimmed = TaText.Trim(rawValue);
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return "Base URL 格式无效";
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        var host = NormalizeHost(uri);

        var isLocal = host is "localhost" or "127.0.0.1" or "::1";
        if (scheme != "https" && !(isLocal && scheme == "http"))
        {
            return "远程服务必须使用 HTTPS";
        }

        return null;
    }

    /// <summary>
    /// 小写 host；IPv6 字面量去掉方括号（.NET 的 <see cref="Uri.Host"/> 对 <c>::1</c> 会返回
    /// <c>[::1]</c>，而 Mac 的 <c>URLComponents.host</c> 返回 <c>::1</c>）。
    /// </summary>
    private static string NormalizeHost(Uri uri)
    {
        var host = (uri.IdnHost ?? uri.Host ?? string.Empty).ToLowerInvariant();
        if (host.Length >= 2 && host[0] == '[' && host[^1] == ']')
        {
            host = host[1..^1];
        }

        return host;
    }
}

/// <summary>
/// 与 Swift <c>String.trimmingCharacters(in: .whitespacesAndNewlines)</c> 对齐的裁剪。
/// </summary>
internal static class TaText
{
    /// <summary>去掉首尾空白（空格 / 制表 / 换行）。</summary>
    internal static string Trim(string? value) => value is null ? string.Empty : value.Trim();

    /// <summary>去掉首尾空白；结果为空时返回 null（对应 Mac 的 <c>trimmed()</c> 辅助函数）。</summary>
    internal static string? TrimOrNull(string? value)
    {
        var trimmed = Trim(value);
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>按行 join，与 Swift 的 <c>joined(separator: "\n")</c> 一致。</summary>
    internal static string JoinLines(IEnumerable<string> lines) => string.Join("\n", lines);
}
