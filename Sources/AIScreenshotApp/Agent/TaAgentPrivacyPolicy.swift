import Foundation
import TaAgentContracts

enum TaAgentPreferenceKey {
    static let accessEnabled = "agentAccessEnabled"
    static let automaticCaptureAllowed = "agentAutomaticCaptureAllowed"
    static let cloudPolicy = "agentCloudPolicy"
    static let privacyDenylist = "agentPrivacyDenylist"
    static let allowCaptureTa = "agentAllowCaptureTa"
}

struct TaAgentPrivacyPolicy: Equatable, Sendable {
    static let taBundleIdentifier = PersistentConfigurationIdentity.bundleIdentifier

    /// Password managers and credential apps that are blocked until the user edits
    /// the denylist. Used only when the preference has never been written; an
    /// explicitly emptied list stays empty.
    static let defaultBlockedBundleIdentifiers: [String] = [
        "com.1password.1password",
        "com.agilebits.onepassword7",
        "com.agilebits.onepassword-osx",
        "com.bitwarden.desktop",
        "com.apple.keychainaccess",
        "com.apple.Passwords",
        "com.lastpass.LastPass",
        "com.lastpass.lastpassmacdesktop",
        "com.dashlane.Dashlane",
        "com.dashlane.dashlanephonefinal",
        "org.keepassxc.keepassxc",
        "in.sinew.Enpass-Desktop",
        "in.sinew.Enpass-Desktop.App",
        "me.proton.pass.electron",
        "com.markmcguill.strongbox.mac",
        "com.hicknhacksoftware.MacPass",
        "com.callpod.keepermac.lite",
        "com.keepersecurity.passwordmanager",
        "com.nordpass.macos.NordPass"
    ]

    static let defaultDenylistText = defaultBlockedBundleIdentifiers.joined(separator: "\n")

    let isEnabled: Bool
    let automaticCaptureAllowed: Bool
    let cloudPolicy: AgentCloudPolicy
    let blockedBundleIdentifiers: Set<String>
    let allowCaptureTa: Bool

    init(
        isEnabled: Bool,
        automaticCaptureAllowed: Bool,
        cloudPolicy: AgentCloudPolicy,
        blockedBundleIdentifiers: Set<String>,
        allowCaptureTa: Bool
    ) {
        self.isEnabled = isEnabled
        self.automaticCaptureAllowed = automaticCaptureAllowed
        self.cloudPolicy = cloudPolicy
        self.blockedBundleIdentifiers = Set(blockedBundleIdentifiers.map { $0.lowercased() })
        self.allowCaptureTa = allowCaptureTa
    }

    static func load(defaults: UserDefaults = .standard) -> TaAgentPrivacyPolicy {
        let enabled = defaults.object(forKey: TaAgentPreferenceKey.accessEnabled) == nil
            ? true
            : defaults.bool(forKey: TaAgentPreferenceKey.accessEnabled)
        let captureAllowed = defaults.object(forKey: TaAgentPreferenceKey.automaticCaptureAllowed) == nil
            ? true
            : defaults.bool(forKey: TaAgentPreferenceKey.automaticCaptureAllowed)
        let cloudRaw = defaults.string(forKey: TaAgentPreferenceKey.cloudPolicy) ?? AgentCloudPolicy.auto.rawValue
        let denylist = defaults.string(forKey: TaAgentPreferenceKey.privacyDenylist)
            ?? Self.defaultDenylistText
        let blocked = Set(denylist
            .components(separatedBy: CharacterSet(charactersIn: ",;\n"))
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty })
        let allowTa = defaults.object(forKey: TaAgentPreferenceKey.allowCaptureTa) == nil
            ? true
            : defaults.bool(forKey: TaAgentPreferenceKey.allowCaptureTa)
        return TaAgentPrivacyPolicy(
            isEnabled: enabled,
            automaticCaptureAllowed: captureAllowed,
            cloudPolicy: AgentCloudPolicy(rawValue: cloudRaw) ?? .auto,
            blockedBundleIdentifiers: blocked,
            allowCaptureTa: allowTa
        )
    }

    func captureError(
        for target: TaAgentResolvedTarget,
        visibleBundleIdentifiers: Set<String> = []
    ) -> AgentErrorPayload? {
        guard isEnabled else {
            return AgentErrorPayload(
                code: .targetBlockedByPrivacyPolicy,
                message: "拓的 Agent 调用已关闭。",
                hint: "打开拓 → 设置 → Agent 与自动化后启用。",
                retryable: false
            )
        }
        guard automaticCaptureAllowed else {
            return AgentErrorPayload(
                code: .targetBlockedByPrivacyPolicy,
                message: "Agent 自动截图已关闭。",
                hint: "可在拓的 Agent 与自动化设置中启用。",
                retryable: false
            )
        }
        let visible = Set(visibleBundleIdentifiers.map { $0.lowercased() })
        let targetBundle = target.bundleIdentifier?.lowercased()
        let containsBlockedApp = !visible.isDisjoint(with: blockedBundleIdentifiers)
        let containsTa = visible.contains(Self.taBundleIdentifier.lowercased())
            || targetBundle == Self.taBundleIdentifier.lowercased()
        if targetBundle.map(blockedBundleIdentifiers.contains) == true
            || containsBlockedApp
            || (!allowCaptureTa && containsTa) {
            return AgentErrorPayload(
                code: .targetBlockedByPrivacyPolicy,
                message: "目标 App 已被 Agent 截图隐私策略阻止。",
                hint: "如确有需要，请在拓的隐私 App 黑名单中调整。",
                retryable: false
            )
        }
        return nil
    }

    func allowsWindowMetadata(bundleIdentifier: String?) -> Bool {
        guard let bundleIdentifier = bundleIdentifier?.lowercased() else { return true }
        if blockedBundleIdentifiers.contains(bundleIdentifier) { return false }
        if !allowCaptureTa && bundleIdentifier == Self.taBundleIdentifier.lowercased() { return false }
        return true
    }

    /// The user's setting is an upper bound: a request may be stricter than the
    /// setting but never looser. Strictness order is deny > auto > allow.
    func effectiveCloudPolicy(requested: AgentCloudPolicy?) -> AgentCloudPolicy {
        if cloudPolicy == .deny || requested == .deny { return .deny }
        if cloudPolicy == .auto || requested == .auto { return .auto }
        return requested ?? cloudPolicy
    }

    func cloudError(requested: AgentCloudPolicy?) -> AgentErrorPayload? {
        guard effectiveCloudPolicy(requested: requested) == .deny else { return nil }
        if cloudPolicy == .deny {
            return AgentErrorPayload(
                code: .cloudUploadNotAllowed,
                message: "用户已在拓的设置中禁止把图片或文字发送到云端模型，请求参数无法覆盖该设置。",
                hint: "改用本地能力（例如本地 OCR）；如需云端能力，请让用户在拓 → 设置 → Agent 与自动化中调整云端策略。",
                retryable: false
            )
        }
        return AgentErrorPayload(
            code: .cloudUploadNotAllowed,
            message: "当前请求禁止把图片或文字发送到云端模型。",
            hint: "改用本地能力，或在明确获得用户许可后去掉 cloud=deny。",
            retryable: false
        )
    }
}
