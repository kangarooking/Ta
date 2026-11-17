import CryptoKit
import Foundation
import TaAgentContracts

struct TaAgentAuditEntry: Codable, Equatable, Identifiable, Sendable {
    let requestID: String
    let occurredAt: Date
    let clientName: String
    let clientVersion: String
    let method: AgentMethod
    let succeeded: Bool
    let durationMs: Int
    let cloudUploaded: Bool
    let errorCode: AgentErrorCode?
    /// Capture target summary (kind plus app name / bundle ID). Never a window title.
    let target: String?
    /// Destination written by deliver.save, when the call saved a file.
    let savedPath: String?

    init(
        requestID: String,
        occurredAt: Date,
        clientName: String,
        clientVersion: String,
        method: AgentMethod,
        succeeded: Bool,
        durationMs: Int,
        cloudUploaded: Bool,
        errorCode: AgentErrorCode?,
        target: String? = nil,
        savedPath: String? = nil
    ) {
        self.requestID = requestID
        self.occurredAt = occurredAt
        self.clientName = clientName
        self.clientVersion = clientVersion
        self.method = method
        self.succeeded = succeeded
        self.durationMs = durationMs
        self.cloudUploaded = cloudUploaded
        self.errorCode = errorCode
        self.target = target
        self.savedPath = savedPath
    }

    var detailText: String? {
        let parts = [target.map { "目标：\($0)" }, savedPath.map { "保存到：\($0)" }].compactMap { $0 }
        return parts.isEmpty ? nil : parts.joined(separator: " · ")
    }

    var id: String { "\(requestID)-\(occurredAt.timeIntervalSince1970)" }
}

actor TaAgentAuditLog {
    static let defaultMaximumEntries = 2_000

    /// Cheap read-only probes are not audited, so that polling `ta status`
    /// cannot evict capture and upload records from the ring.
    static let unloggedMethods: Set<AgentMethod> = [
        .systemHandshake, .systemStatus, .systemCapabilities, .systemPermissions
    ]

    let fileURL: URL
    let maximumEntries: Int

    init(
        fileURL: URL = TaAgentAuditLog.defaultFileURL(),
        maximumEntries: Int = TaAgentAuditLog.defaultMaximumEntries
    ) {
        self.fileURL = fileURL.standardizedFileURL
        self.maximumEntries = max(1, maximumEntries)
    }

    func record(
        request: AgentRequestEnvelope,
        response: AgentResponseEnvelope,
        occurredAt: Date = Date()
    ) throws {
        guard !Self.unloggedMethods.contains(request.method) else { return }
        var entries = readEntriesRecoveringFromCorruption()
        entries.append(TaAgentAuditEntry(
            requestID: Self.requestFingerprint(request.requestID),
            occurredAt: occurredAt,
            clientName: Self.sanitizedClientField(request.client.name),
            clientVersion: Self.sanitizedClientField(request.client.version),
            method: request.method,
            succeeded: response.ok,
            durationMs: max(0, response.meta?.durationMs ?? 0),
            cloudUploaded: response.meta?.cloudUploaded ?? false,
            errorCode: response.error?.code,
            target: Self.targetSummary(response),
            savedPath: Self.savedPath(request: request, response: response)
        ))
        if entries.count > maximumEntries {
            entries.removeFirst(entries.count - maximumEntries)
        }
        try write(entries)
    }

    func recent(limit: Int = 20) throws -> [TaAgentAuditEntry] {
        guard limit > 0 else { return [] }
        return Array(readEntriesRecoveringFromCorruption().suffix(limit).reversed())
    }

    func clear() throws {
        guard FileManager.default.fileExists(atPath: fileURL.path) else { return }
        try FileManager.default.removeItem(at: fileURL)
    }

    private func readEntries() throws -> [TaAgentAuditEntry] {
        guard FileManager.default.fileExists(atPath: fileURL.path) else { return [] }
        return try AgentJSONCoding.decoder().decode(
            [TaAgentAuditEntry].self,
            from: Data(contentsOf: fileURL)
        )
    }

    /// A truncated or otherwise undecodable log must not disable auditing forever:
    /// the bad file is moved aside (one copy is kept for inspection) and the log
    /// starts fresh.
    private func readEntriesRecoveringFromCorruption() -> [TaAgentAuditEntry] {
        do {
            return try readEntries()
        } catch {
            let fileManager = FileManager.default
            let quarantineURL = fileURL.appendingPathExtension("corrupt")
            try? fileManager.removeItem(at: quarantineURL)
            do {
                try fileManager.moveItem(at: fileURL, to: quarantineURL)
            } catch {
                try? fileManager.removeItem(at: fileURL)
            }
            return []
        }
    }

    private static func targetSummary(_ response: AgentResponseEnvelope) -> String? {
        guard case .object(let data) = response.data,
              case .object(let target) = data["target"] else { return nil }
        var parts: [String] = []
        if case .string(let kind) = target["kind"] { parts.append(kind) }
        if case .string(let appName) = target["appName"] { parts.append(appName) }
        if case .string(let bundleIdentifier) = target["bundleIdentifier"] { parts.append(bundleIdentifier) }
        if parts.count <= 1, case .integer(let displayID) = target["displayId"] {
            parts.append("display \(displayID)")
        }
        guard !parts.isEmpty else { return nil }
        return sanitizedField(parts.joined(separator: " "), limit: 200)
    }

    private static func savedPath(
        request: AgentRequestEnvelope,
        response: AgentResponseEnvelope
    ) -> String? {
        guard request.method == .deliverSave, response.ok,
              case .object(let data) = response.data,
              case .string(let path) = data["path"] else { return nil }
        return sanitizedField(path, limit: 1_024)
    }

    private func write(_ entries: [TaAgentAuditEntry]) throws {
        let fileManager = FileManager.default
        let directory = fileURL.deletingLastPathComponent()
        try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
        try fileManager.setAttributes([.posixPermissions: 0o700], ofItemAtPath: directory.path)
        try AgentJSONCoding.encoder().encode(entries).write(to: fileURL, options: .atomic)
        try fileManager.setAttributes([.posixPermissions: 0o600], ofItemAtPath: fileURL.path)
    }

    private static func sanitizedClientField(_ value: String) -> String {
        sanitizedField(value, limit: 80)
    }

    private static func sanitizedField(_ value: String, limit: Int) -> String {
        var scalars = String.UnicodeScalarView()
        for scalar in value.unicodeScalars where !CharacterSet.controlCharacters.contains(scalar) {
            if scalars.count >= limit { break }
            scalars.append(scalar)
        }
        return String(scalars)
    }

    private static func requestFingerprint(_ requestID: String) -> String {
        let digest = SHA256.hash(data: Data(requestID.utf8))
            .prefix(8)
            .map { String(format: "%02x", $0) }
            .joined()
        return "sha256:\(digest)"
    }

    private static func defaultFileURL() -> URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory
        return base
            .appendingPathComponent("Ta", isDirectory: true)
            .appendingPathComponent("Agent", isDirectory: true)
            .appendingPathComponent("audit-v1.json", isDirectory: false)
    }
}
