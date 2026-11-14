import AppKit
import Combine
import AIScreenshotCore
import TaAgentClient
import TaAgentContracts

enum AppLaunchMode: Equatable {
    case normal
    case agentBridge
}

struct AppLaunchPolicy: Equatable {
    let mode: AppLaunchMode

    init(arguments: [String]) {
        mode = arguments.contains("--agent-bridge") ? .agentBridge : .normal
    }

    var shouldScheduleWelcome: Bool { mode == .normal }
}

@MainActor
final class AppModel: ObservableObject {
    static weak var current: AppModel?

    @Published private(set) var statusText = "本地识别就绪"
    @Published private(set) var isCapturing = false

    private let hotKeyManager = GlobalHotKeyManager()
    private let captureCoordinator = CaptureCoordinator()
    private let welcomeWindowController = WelcomeWindowController()
    private let settingsWindowController = SettingsWindowController()
    private let launchArguments: [String]
    private let launchPolicy: AppLaunchPolicy
    private var hasStarted = false
    private var hotKeyFailureObserver: NSObjectProtocol?
    private var agentBridgeServer: TaAgentBridgeServer?

    init(arguments: [String] = ProcessInfo.processInfo.arguments) {
        launchArguments = arguments
        launchPolicy = AppLaunchPolicy(arguments: arguments)
        Self.current = self
    }

    func start() {
        guard !hasStarted else { return }
        hasStarted = true
        startAgentBridge()

        do {
            try hotKeyManager.registerDefaults(
                interactiveCapture: { [weak self] in self?.startCapture(.interactive) },
                intelligentCapture: { [weak self] in self?.startCapture(.intelligent) },
                translationCapture: { [weak self] in self?.startCapture(.translation) },
                imageCapture: { [weak self] in self?.startCapture(.image) },
                pinCapture: { [weak self] in self?.startCapture(.pin) },
                longCapture: { [weak self] in self?.startCapture(.long) }
            )
        } catch {
            statusText = "快捷键注册失败"
        }
        hotKeyFailureObserver = NotificationCenter.default.addObserver(
            forName: HotKeyPreferences.registrationFailedNotification,
            object: nil,
            queue: .main
        ) { [weak self] notification in
            let message = notification.userInfo?[HotKeyPreferences.errorMessageUserInfoKey] as? String
            Task { @MainActor in
                self?.statusText = message ?? "快捷键冲突，已恢复上一组配置"
            }
        }

        let ocrService = ConfiguredOCRService()
        ocrService.applyIdleTimeoutPolicy()
        ocrService.prewarmIfNeeded()

        let arguments = launchArguments
        if launchPolicy.mode == .agentBridge {
            return
        } else if arguments.contains("--capture-fixed") {
            scheduleFixedCapture()
        } else if arguments.contains("--ui-smoke-capture-toolbar") {
            Task { [weak self] in
                try? await Task.sleep(for: .milliseconds(180))
                guard let self else { return }
                showWelcome()
                try? await Task.sleep(for: .milliseconds(220))
                captureCoordinator.openCaptureToolbarSmokeFixture { [weak self] in
                    self?.statusText = "本地识别就绪"
                }
            }
        } else if arguments.contains("--ui-smoke-result-bar-success") {
            Task { [weak self] in
                try? await Task.sleep(for: .milliseconds(180))
                self?.captureCoordinator.openResultBarSmokeFixture(kind: .success)
            }
        } else if arguments.contains("--ui-smoke-result-bar-failure") {
            Task { [weak self] in
                try? await Task.sleep(for: .milliseconds(180))
                self?.captureCoordinator.openResultBarSmokeFixture(kind: .failure)
            }
        } else if arguments.contains("--ui-smoke-editor") {
            Task { [weak self] in
                try? await Task.sleep(for: .milliseconds(180))
                self?.captureCoordinator.openEditorSmokeFixture()
            }
        } else if arguments.contains("--ui-smoke-inline-editor") {
            isCapturing = true
            Task { [weak self] in
                try? await Task.sleep(for: .milliseconds(180))
                self?.captureCoordinator.openInlineEditorSmokeFixture { [weak self] in
                    self?.isCapturing = false
                    self?.statusText = "本地识别就绪"
                }
            }
        } else if arguments.contains("--capture-intelligent") {
            scheduleLaunchCapture(.intelligent)
        } else if arguments.contains("--capture-image") {
            scheduleLaunchCapture(.image)
        } else if arguments.contains("--capture-pin") {
            scheduleLaunchCapture(.pin)
        } else if launchPolicy.shouldScheduleWelcome {
            Task { [weak self] in
                try? await Task.sleep(for: .milliseconds(180))
                guard !Task.isCancelled, self?.isCapturing == false else { return }
                self?.showWelcome()
            }
        }
    }

    private func startAgentBridge() {
        let capabilityService = TaAgentCapabilityService()
        let router = TaAgentRequestRouter { request in
            await capabilityService.handle(request)
        }
        let server = TaAgentBridgeServer(
            socketURL: TaBridgeEndpoint.defaultSocketURL,
            router: router
        )
        do {
            try server.start()
            agentBridgeServer = server
        } catch {
            statusText = "Agent Bridge 启动失败"
        }
    }

    func showWelcome() {
        guard !isCapturing else { return }
        NotificationCenter.default.post(name: .taMenuBarShouldClose, object: nil)
        welcomeWindowController.show(appModel: self)
    }

    func showSettings() {
        NotificationCenter.default.post(name: .taMenuBarShouldClose, object: nil)
        NSApplication.shared.activate()
        if let settingsItem = NSApplication.shared.mainMenu?.items
            .compactMap(\.submenu)
            .flatMap(\.items)
            .first(where: {
                $0.keyEquivalent == "," && $0.keyEquivalentModifierMask.contains(.command)
            }),
           let action = settingsItem.action,
           NSApplication.shared.sendAction(action, to: settingsItem.target, from: settingsItem) {
            return
        }
        settingsWindowController.show()
    }

    func startCapture(_ mode: CaptureMode) {
        guard !isCapturing else { return }
        NotificationCenter.default.post(name: .taMenuBarShouldClose, object: nil)
        isCapturing = true
        statusText = switch mode {
        case .interactive: "框选后选择操作"
        case .intelligent: "选择需要识别的区域"
        case .translation: "选择需要翻译的区域"
        case .image: "选择要复制的区域"
        case .pin: "选择要钉住的区域"
        case .long: "从起点框到滚动区域底部"
        }

        captureCoordinator.start(mode: mode) { [weak self] outcome in
            guard let self else { return }
            isCapturing = false
            switch outcome {
            case .cancelled:
                statusText = "本地识别就绪"
            case .completed(let message):
                statusText = message
            case .failed(let message):
                statusText = message
            }
        }
    }

    func pinClipboardContent() {
        statusText = captureCoordinator.pinClipboardContent() ? "已从剪贴板生成钉图" : "剪贴板中没有可钉住的内容"
    }

    func hideAllPins() {
        captureCoordinator.hideAllPins()
        statusText = "已隐藏全部钉图"
    }

    func showAllPins() {
        captureCoordinator.showAllPins()
        statusText = "已显示全部钉图"
    }

    func restorePinInteraction() {
        captureCoordinator.enableAllPinInteraction()
        statusText = "已恢复钉图鼠标交互"
    }

    func restoreLastClosedPin() {
        statusText = captureCoordinator.restoreLastClosedPin() ? "已恢复最近关闭的钉图" : "没有可恢复的钉图"
    }

    private func scheduleLaunchCapture(_ mode: CaptureMode) {
        Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(500))
            guard !Task.isCancelled else { return }
            self?.startCapture(mode)
        }
    }

    private func scheduleFixedCapture() {
        Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(500))
            guard !Task.isCancelled, let self else { return }
            isCapturing = true
            captureCoordinator.startFixedTestRegion(mode: .intelligent) { [weak self] outcome in
                guard let self else { return }
                isCapturing = false
                switch outcome {
                case .cancelled:
                    statusText = "本地识别就绪"
                case .completed(let message):
                    statusText = message
                case .failed(let message):
                    statusText = message
                }
            }
        }
    }
}
