import Foundation
import Testing
@testable import AIScreenshotApp
import TaAgentContracts

@Suite("Ta Agent privacy settings and audit")
struct AgentSettingsTests {
    @Test("one-line installer uses the published latest release asset")
    func installerCommand() {
        #expect(!TaAgentInstallationGuide.installCommand.contains("\n"))
        #expect(TaAgentInstallationGuide.installCommand.hasPrefix("curl -fsSL --retry 3"))
        #expect(TaAgentInstallationGuide.installCommand.contains("--retry-all-errors"))
        #expect(TaAgentInstallationGuide.installCommand.contains("/releases/latest/download/install.sh"))
        #expect(TaAgentInstallationGuide.installCommand.hasSuffix("| bash"))
    }

    @Test("agent prompt installs, verifies, and preserves API key boundaries")
    func agentInstallerPrompt() {
        #expect(TaAgentInstallationGuide.agentPrompt.contains(TaAgentInstallationGuide.installCommand))
        #expect(TaAgentInstallationGuide.agentPrompt.contains("~/.local/bin/ta status --json"))
        #expect(TaAgentInstallationGuide.agentPrompt.contains("data.bridge 为 ready"))
        #expect(TaAgentInstallationGuide.agentPrompt.contains("不要索要、读取或输出任何 API Key"))
    }

    @Test("installation status finds executable CLI and common skill locations")
    func installationStatusDetection() throws {
        let root = temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let cli = root.appendingPathComponent(".local/bin/ta")
        let codexSkill = root.appendingPathComponent(".codex/skills/ta/SKILL.md")
        let genericSkill = root.appendingPathComponent(".agents/skills/ta/SKILL.md")
        try FileManager.default.createDirectory(
            at: cli.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try Data("#!/bin/sh\n".utf8).write(to: cli)
        try FileManager.default.setAttributes(
            [.posixPermissions: 0o755],
            ofItemAtPath: cli.path
        )
        for skill in [codexSkill, genericSkill] {
            try FileManager.default.createDirectory(
                at: skill.deletingLastPathComponent(),
                withIntermediateDirectories: true
            )
            try Data("---\nname: ta\n---\n".utf8).write(to: skill)
        }

        let status = TaAgentToolInstallationStatus.detect(homeDirectory: root)

        #expect(status.cliInstalled)
        #expect(status.skillInstalled)
        #expect(status.installedSkillLocations.count == 2)
    }

    @Test("privacy defaults allow silent local automation without weakening cloud policy")
    func privacyDefaults() throws {
        let suite = "ta-agent-settings-\(UUID().uuidString)"
        let defaults = try #require(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }

        let policy = TaAgentPrivacyPolicy.load(defaults: defaults)

        #expect(policy.isEnabled)
        #expect(policy.automaticCaptureAllowed)
        #expect(policy.cloudPolicy == .auto)
        #expect(policy.allowCaptureTa)
        // Never-set preference: common credential apps are blocked out of the box.
        #expect(policy.blockedBundleIdentifiers.contains("com.1password.1password"))
        #expect(policy.blockedBundleIdentifiers.contains("com.bitwarden.desktop"))
        #expect(policy.blockedBundleIdentifiers.contains("com.apple.keychainaccess"))
        #expect(policy.blockedBundleIdentifiers.contains("com.apple.passwords"))
        #expect(policy.blockedBundleIdentifiers.contains("org.keepassxc.keepassxc"))
        #expect(!policy.allowsWindowMetadata(bundleIdentifier: "com.1Password.1password"))
    }

    @Test("an explicitly emptied denylist stays empty instead of re-seeding defaults")
    func explicitEmptyDenylist() throws {
        let suite = "ta-agent-settings-\(UUID().uuidString)"
        let defaults = try #require(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        defaults.set("", forKey: TaAgentPreferenceKey.privacyDenylist)

        #expect(TaAgentPrivacyPolicy.load(defaults: defaults).blockedBundleIdentifiers.isEmpty)
    }

    @Test("the user's cloud setting is an upper bound that request params cannot loosen")
    func cloudPolicyIsUpperBound() {
        func policy(_ cloud: AgentCloudPolicy) -> TaAgentPrivacyPolicy {
            TaAgentPrivacyPolicy(
                isEnabled: true,
                automaticCaptureAllowed: true,
                cloudPolicy: cloud,
                blockedBundleIdentifiers: [],
                allowCaptureTa: true
            )
        }

        // User says deny: denied whatever the caller asks for.
        for requested in [nil, AgentCloudPolicy.auto, .allow, .deny] {
            #expect(policy(.deny).cloudError(requested: requested)?.code == .cloudUploadNotAllowed)
            #expect(policy(.deny).effectiveCloudPolicy(requested: requested) == .deny)
        }
        // A request may be stricter than the setting.
        #expect(policy(.allow).cloudError(requested: .deny)?.code == .cloudUploadNotAllowed)
        #expect(policy(.auto).cloudError(requested: .deny)?.code == .cloudUploadNotAllowed)
        // Otherwise uploads are permitted, and "allow" never loosens "auto".
        #expect(policy(.auto).cloudError(requested: nil) == nil)
        #expect(policy(.auto).cloudError(requested: .allow) == nil)
        #expect(policy(.allow).cloudError(requested: .auto) == nil)
        #expect(policy(.auto).effectiveCloudPolicy(requested: .allow) == .auto)
        #expect(policy(.allow).effectiveCloudPolicy(requested: nil) == .allow)
    }

    @Test("privacy settings persist and normalize blocked bundle identifiers")
    func privacyPersistence() throws {
        let suite = "ta-agent-settings-\(UUID().uuidString)"
        let defaults = try #require(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        defaults.set(false, forKey: TaAgentPreferenceKey.accessEnabled)
        defaults.set(false, forKey: TaAgentPreferenceKey.automaticCaptureAllowed)
        defaults.set(AgentCloudPolicy.deny.rawValue, forKey: TaAgentPreferenceKey.cloudPolicy)
        defaults.set(" com.apple.Safari\nCOM.1PASSWORD.1PASSWORD ", forKey: TaAgentPreferenceKey.privacyDenylist)
        defaults.set(false, forKey: TaAgentPreferenceKey.allowCaptureTa)

        let policy = TaAgentPrivacyPolicy.load(defaults: defaults)

        #expect(!policy.isEnabled)
        #expect(!policy.automaticCaptureAllowed)
        #expect(policy.cloudPolicy == .deny)
        #expect(policy.blockedBundleIdentifiers == ["com.apple.safari", "com.1password.1password"])
        #expect(!policy.allowCaptureTa)
    }

    @Test("audit entries never persist request parameters or response content")
    func auditRedaction() async throws {
        let root = temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let file = root.appendingPathComponent("audit.json")
        let log = TaAgentAuditLog(fileURL: file, maximumEntries: 20)
        let request = AgentRequestEnvelope(
            requestID: "request-safe-id",
            method: .recognizeOCR,
            params: [
                "apiKey": .string("sk-super-secret"),
                "text": .string("private OCR paragraph")
            ],
            client: AgentClientInfo(name: "test-agent", version: "1.2.3")
        )
        let response = AgentResponseEnvelope.success(
            requestID: request.requestID,
            data: .object(["text": .string("private OCR paragraph")]),
            meta: AgentResponseMetadata(durationMs: 37, cloudUploaded: false)
        )

        try await log.record(request: request, response: response, occurredAt: Date(timeIntervalSince1970: 1_787_684_400))

        let raw = try String(contentsOf: file, encoding: .utf8)
        let recent = try await log.recent(limit: 10)
        let entry = try #require(recent.first)
        #expect(!raw.contains("sk-super-secret"))
        #expect(!raw.contains("private OCR paragraph"))
        #expect(!raw.contains("request-safe-id"))
        #expect(!raw.localizedCaseInsensitiveContains("apiKey"))
        #expect(entry.method == .recognizeOCR)
        #expect(entry.clientName == "test-agent")
        #expect(entry.durationMs == 37)
        #expect(!entry.cloudUploaded)
        #expect(entry.succeeded)
    }

    @Test("audit log stays bounded and can be cleared")
    func auditRetentionAndClear() async throws {
        let root = temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let log = TaAgentAuditLog(fileURL: root.appendingPathComponent("audit.json"), maximumEntries: 2)

        for index in 0..<3 {
            let request = AgentRequestEnvelope(
                requestID: "request-\(index)",
                method: .captureFrontmost,
                client: AgentClientInfo(name: "agent", version: "1")
            )
            try await log.record(
                request: request,
                response: .success(requestID: request.requestID),
                occurredAt: Date(timeIntervalSince1970: TimeInterval(index))
            )
        }

        let recent = try await log.recent(limit: 10)
        #expect(recent.count == 2)
        #expect(recent.map(\.occurredAt) == [
            Date(timeIntervalSince1970: 2),
            Date(timeIntervalSince1970: 1)
        ])
        try await log.clear()
        #expect(try await log.recent(limit: 10).isEmpty)
    }

    @Test("cheap read-only probes are not logged and cannot evict capture records")
    func auditSkipsProbes() async throws {
        let root = temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let log = TaAgentAuditLog(fileURL: root.appendingPathComponent("audit.json"), maximumEntries: 2)
        #expect(TaAgentAuditLog.defaultMaximumEntries >= 1_000)

        func record(_ method: AgentMethod, _ id: String) async throws {
            let request = AgentRequestEnvelope(
                requestID: id,
                method: method,
                client: AgentClientInfo(name: "agent", version: "1")
            )
            try await log.record(request: request, response: .success(requestID: id))
        }
        try await record(.captureFrontmost, "capture")
        for (index, method) in [
            AgentMethod.systemStatus, .systemCapabilities, .systemPermissions, .systemHandshake
        ].enumerated() {
            try await record(method, "probe-\(index)")
        }

        let recent = try await log.recent(limit: 10)
        #expect(recent.map(\.method) == [.captureFrontmost])
    }

    @Test("an undecodable audit file is moved aside and logging starts fresh")
    func auditRecoversFromCorruptFile() async throws {
        let root = temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let file = root.appendingPathComponent("audit.json")
        try Data("{ this is not an audit log".utf8).write(to: file)
        let log = TaAgentAuditLog(fileURL: file)
        let request = AgentRequestEnvelope(
            requestID: "after-corruption",
            method: .captureFrontmost,
            client: AgentClientInfo(name: "agent", version: "1")
        )

        #expect(try await log.recent(limit: 10).isEmpty)
        try await log.record(request: request, response: .success(requestID: request.requestID))

        #expect(try await log.recent(limit: 10).count == 1)
        let quarantined = file.appendingPathExtension("corrupt")
        #expect(try String(contentsOf: quarantined, encoding: .utf8) == "{ this is not an audit log")
    }

    @Test("audit records the capture target app and the saved path, but no window title")
    func auditRecordsTargetAndSavedPath() async throws {
        let root = temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let file = root.appendingPathComponent("audit.json")
        let log = TaAgentAuditLog(fileURL: file)
        let capture = AgentRequestEnvelope(
            requestID: "capture-1",
            method: .captureWindow,
            client: AgentClientInfo(name: "agent", version: "1")
        )
        try await log.record(
            request: capture,
            response: .success(requestID: capture.requestID, data: .object([
                "target": .object([
                    "kind": .string("window"),
                    "appName": .string("Safari"),
                    "bundleIdentifier": .string("com.apple.Safari"),
                    "title": .string("Private banking tab")
                ])
            ])),
            occurredAt: Date(timeIntervalSince1970: 1)
        )
        let save = AgentRequestEnvelope(
            requestID: "save-1",
            method: .deliverSave,
            params: ["path": .string("/tmp/out.png")],
            client: AgentClientInfo(name: "agent", version: "1")
        )
        try await log.record(
            request: save,
            response: .success(requestID: save.requestID, data: .object(["path": .string("/tmp/out.png")])),
            occurredAt: Date(timeIntervalSince1970: 2)
        )

        let recent = try await log.recent(limit: 10)
        #expect(recent.first?.savedPath == "/tmp/out.png")
        #expect(recent.last?.target == "window Safari com.apple.Safari")
        #expect(recent.last?.savedPath == nil)
        let raw = try String(contentsOf: file, encoding: .utf8)
        #expect(!raw.contains("Private banking tab"))
    }

    @Test("audit files written before target and savedPath existed still decode")
    func auditDecodesLegacyEntries() throws {
        let legacy = #"""
        [{"requestID":"sha256:00","occurredAt":"2026-01-01T00:00:00Z","clientName":"a","clientVersion":"1","method":"capture.frontmost","succeeded":true,"durationMs":5,"cloudUploaded":false}]
        """#
        let entries = try AgentJSONCoding.decoder().decode([TaAgentAuditEntry].self, from: Data(legacy.utf8))
        #expect(entries.first?.target == nil)
        #expect(entries.first?.detailText == nil)
    }

    private func temporaryRoot() -> URL {
        FileManager.default.temporaryDirectory
            .appendingPathComponent("ta-agent-settings-tests-\(UUID().uuidString)", isDirectory: true)
    }
}
