import Foundation

public struct ProviderEndpointValidator: Sendable {
    public init() {}

    public func validationMessage(for rawValue: String) -> String? {
        let trimmed = rawValue.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }
        guard let components = URLComponents(string: trimmed),
              let scheme = components.scheme?.lowercased(),
              let host = components.host?.lowercased(),
              !host.isEmpty else {
            return "Base URL 格式无效"
        }

        if scheme == "https" { return nil }
        if scheme == "http", Self.isLocalHost(host) { return nil }
        return "远程服务必须使用 HTTPS"
    }

    /// True when the endpoint points at this Mac or a private network, where
    /// self-hosted servers (Ollama, LM Studio, vLLM...) usually need no API key.
    public func isLocalEndpoint(_ rawValue: String) -> Bool {
        let trimmed = rawValue.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let host = URLComponents(string: trimmed)?.host else { return false }
        return Self.isLocalHost(host)
    }

    /// Loopback, RFC 1918 private ranges, link-local addresses, IPv6
    /// loopback / unique-local / link-local, and mDNS `.local` names.
    public static func isLocalHost(_ rawHost: String) -> Bool {
        var host = rawHost.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        if host.hasPrefix("["), host.hasSuffix("]") {
            host = String(host.dropFirst().dropLast())
        }
        if let zone = host.firstIndex(of: "%") {
            host = String(host[..<zone])
        }
        while host.hasSuffix(".") { host.removeLast() }
        guard !host.isEmpty else { return false }

        if host == "localhost" || host.hasSuffix(".localhost") || host.hasSuffix(".local") {
            return true
        }
        if let octets = ipv4Octets(host) {
            switch (octets[0], octets[1]) {
            case (127, _), (10, _), (192, 168), (169, 254):
                return true
            case (172, 16...31):
                return true
            default:
                return false
            }
        }
        if host.contains(":") {
            if host == "::1" || host == "0:0:0:0:0:0:0:1" { return true }
            if host.hasPrefix("::ffff:"), let octets = ipv4Octets(String(host.dropFirst(7))) {
                return isLocalHost(octets.map(String.init).joined(separator: "."))
            }
            // fc00::/7 unique-local and fe80::/10 link-local.
            if host.hasPrefix("fc") || host.hasPrefix("fd") { return true }
            if host.hasPrefix("fe8") || host.hasPrefix("fe9") || host.hasPrefix("fea") || host.hasPrefix("feb") {
                return true
            }
        }
        return false
    }

    private static func ipv4Octets(_ host: String) -> [Int]? {
        let parts = host.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 4 else { return nil }
        var octets: [Int] = []
        for part in parts {
            guard !part.isEmpty, part.count <= 3,
                  part.allSatisfy({ $0.isASCII && $0.isNumber }),
                  let value = Int(part), (0...255).contains(value) else {
                return nil
            }
            octets.append(value)
        }
        return octets
    }
}
