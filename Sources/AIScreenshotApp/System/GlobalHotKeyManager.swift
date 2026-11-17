@preconcurrency import Carbon
import Foundation

enum GlobalHotKeyError: LocalizedError {
    case eventHandler(OSStatus)
    case registration(action: GlobalHotKeyAction, status: OSStatus)

    var errorDescription: String? {
        switch self {
        case .eventHandler(let status):
            "无法安装快捷键处理器（\(status)）"
        case .registration(let action, let status):
            "“\(action.displayName)”快捷键已被系统或其他应用占用（\(status)）。"
        }
    }
}

private let aiScreenshotHotKeySignature: OSType = 0x41495353 // AISS

/// One shortcut that could not be registered with the system.
struct GlobalHotKeyRegistrationFailure: Equatable, Sendable {
    let action: GlobalHotKeyAction
    let shortcut: HotKeyShortcut
    let status: OSStatus

    var message: String {
        "“\(action.displayName)”快捷键 \(shortcut.displayText) 已被系统或其他应用占用（\(status)）"
    }

    static func summary(for failures: [GlobalHotKeyRegistrationFailure]) -> String? {
        guard !failures.isEmpty else { return nil }
        return failures.map(\.message).joined(separator: "；") + "。请在“设置 › 快捷键”中换一个组合。"
    }
}

@MainActor
final class GlobalHotKeyManager: NSObject {
    private let preferences = HotKeyPreferences()
    private var eventHandlerRef: EventHandlerRef?
    private var hotKeyRefs: [GlobalHotKeyAction: EventHotKeyRef] = [:]
    private var actions: [UInt32: () -> Void] = [:]
    private var lastSuccessfulShortcuts: [GlobalHotKeyAction: HotKeyShortcut] = [:]
    private var preferencesObserver: NSObjectProtocol?

    /// Registers every shortcut independently and returns the ones the system
    /// refused. The preferences observer is installed no matter what, so a
    /// conflicting shortcut can be rebound without relaunching. Throws only
    /// when the Carbon event handler itself cannot be installed.
    @discardableResult
    func registerDefaults(
        interactiveCapture: @escaping () -> Void,
        intelligentCapture: @escaping () -> Void,
        translationCapture: @escaping () -> Void,
        imageCapture: @escaping () -> Void,
        pinCapture: @escaping () -> Void,
        longCapture: @escaping () -> Void
    ) throws -> [GlobalHotKeyRegistrationFailure] {
        actions = [
            GlobalHotKeyAction.interactiveCapture.hotKeyID: interactiveCapture,
            GlobalHotKeyAction.intelligentCapture.hotKeyID: intelligentCapture,
            GlobalHotKeyAction.translationCapture.hotKeyID: translationCapture,
            GlobalHotKeyAction.imageCapture.hotKeyID: imageCapture,
            GlobalHotKeyAction.pinCapture.hotKeyID: pinCapture,
            GlobalHotKeyAction.longCapture.hotKeyID: longCapture
        ]

        observePreferenceChangesIfNeeded()
        try installEventHandlerIfNeeded()
        let shortcuts = preferences.allShortcuts()
        unregisterHotKeys()
        let failures = register(shortcuts)
        let failedActions = Set(failures.map(\.action))
        lastSuccessfulShortcuts = shortcuts.filter { !failedActions.contains($0.key) }
        return failures
    }

    private func installEventHandlerIfNeeded() throws {
        guard eventHandlerRef == nil else { return }
        var eventSpec = EventTypeSpec(
            eventClass: OSType(kEventClassKeyboard),
            eventKind: UInt32(kEventHotKeyPressed)
        )

        let installStatus = InstallEventHandler(
            GetEventDispatcherTarget(),
            { _, event, userData in
                guard let event, let userData else { return OSStatus(eventNotHandledErr) }
                var hotKeyID = EventHotKeyID()
                let status = GetEventParameter(
                    event,
                    EventParamName(kEventParamDirectObject),
                    EventParamType(typeEventHotKeyID),
                    nil,
                    MemoryLayout<EventHotKeyID>.size,
                    nil,
                    &hotKeyID
                )
                guard status == noErr else { return status }

                let manager = Unmanaged<GlobalHotKeyManager>
                    .fromOpaque(userData)
                    .takeUnretainedValue()
                Task { @MainActor in manager.invoke(id: hotKeyID.id) }
                return noErr
            },
            1,
            &eventSpec,
            Unmanaged.passUnretained(self).toOpaque(),
            &eventHandlerRef
        )

        guard installStatus == noErr else {
            throw GlobalHotKeyError.eventHandler(installStatus)
        }
    }

    private func observePreferenceChangesIfNeeded() {
        guard preferencesObserver == nil else { return }
        preferencesObserver = NotificationCenter.default.addObserver(
            forName: HotKeyPreferences.didChangeNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            Task { @MainActor in self?.reloadFromPreferences() }
        }
    }

    private func reloadFromPreferences() {
        if eventHandlerRef == nil {
            try? installEventHandlerIfNeeded()
        }
        guard eventHandlerRef != nil else { return }
        let requested = preferences.allShortcuts()
        unregisterHotKeys()
        let failures = register(requested)
        let failedActions = Set(failures.map(\.action))
        var successful = requested.filter { !failedActions.contains($0.key) }

        // Only the shortcuts that failed fall back to their previous working
        // binding; every other change is kept.
        for failure in failures {
            guard let previous = lastSuccessfulShortcuts[failure.action],
                  previous != failure.shortcut else { continue }
            try? preferences.replaceAll([failure.action: previous], notify: false)
            if register([failure.action: previous]).isEmpty {
                successful[failure.action] = previous
            }
        }
        lastSuccessfulShortcuts = successful

        if let message = GlobalHotKeyRegistrationFailure.summary(for: failures) {
            NotificationCenter.default.post(
                name: HotKeyPreferences.registrationFailedNotification,
                object: nil,
                userInfo: [HotKeyPreferences.errorMessageUserInfoKey: message]
            )
        }
    }

    /// Registers each shortcut on its own; one conflict never prevents the
    /// remaining shortcuts from working.
    private func register(
        _ shortcuts: [GlobalHotKeyAction: HotKeyShortcut]
    ) -> [GlobalHotKeyRegistrationFailure] {
        var failures: [GlobalHotKeyRegistrationFailure] = []
        for action in GlobalHotKeyAction.allCases {
            guard let shortcut = shortcuts[action] else { continue }
            if let existing = hotKeyRefs.removeValue(forKey: action) {
                UnregisterEventHotKey(existing)
            }
            var reference: EventHotKeyRef?
            let hotKeyID = EventHotKeyID(signature: aiScreenshotHotKeySignature, id: action.hotKeyID)
            let status = RegisterEventHotKey(
                shortcut.keyCode,
                shortcut.modifiers,
                hotKeyID,
                GetEventDispatcherTarget(),
                0,
                &reference
            )
            guard status == noErr, let reference else {
                failures.append(
                    GlobalHotKeyRegistrationFailure(action: action, shortcut: shortcut, status: status)
                )
                continue
            }
            hotKeyRefs[action] = reference
        }
        return failures
    }

    private func invoke(id: UInt32) {
        actions[id]?()
    }

    private func unregisterHotKeys() {
        hotKeyRefs.values.forEach { UnregisterEventHotKey($0) }
        hotKeyRefs.removeAll()
    }
}
