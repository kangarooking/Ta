import SwiftUI
import AIScreenshotCore

struct SettingsView: View {
    @State private var selectedTab: SettingsTab = .permissions

    var body: some View {
        ZStack {
            TaPaperBackground()

            VStack(spacing: 0) {
                HStack(spacing: 8) {
                    ForEach(SettingsTab.allCases) { tab in
                        settingsTabButton(tab)
                    }
                }
                .padding(.horizontal, 22)
                .padding(.vertical, 14)
                .background(TaPalette.elevatedPaper.opacity(0.96))

                Divider()
                    .overlay(TaPalette.hairline)

                Group {
                    switch selectedTab {
                    case .permissions: PermissionsSettingsView()
                    case .general: GeneralSettingsView()
                    case .hotKeys: HotKeySettingsView()
                    case .recognition: RecognitionSettingsView()
                    case .translation: TranslationSettingsView()
                    case .models: ModelSettingsView()
                    case .agent: AgentSettingsView()
                    }
                }
                .padding(18)
            }
        }
        .frame(width: 780, height: 600)
        .accentColor(TaPalette.cinnabar)
        .tint(TaPalette.cinnabar)
        .onReceive(NotificationCenter.default.publisher(for: .openAIModelSettings)) { _ in
            withAnimation(.easeOut(duration: 0.16)) { selectedTab = .models }
        }
    }

    private func settingsTabButton(_ tab: SettingsTab) -> some View {
        let isSelected = selectedTab == tab
        return Button {
            withAnimation(.easeOut(duration: 0.16)) {
                selectedTab = tab
            }
        } label: {
            VStack(spacing: 6) {
                Image(systemName: tab.icon)
                    .font(.system(size: 17, weight: .medium))
                    .frame(width: 30, height: 25)
                Text(tab.title)
                    .font(.system(size: 12, weight: isSelected ? .semibold : .medium))
                    .lineLimit(1)
            }
            .foregroundStyle(isSelected ? TaPalette.cinnabar : TaPalette.mutedInk)
            .frame(maxWidth: .infinity)
            .padding(.vertical, 8)
            .contentShape(Rectangle())
            .background(
                RoundedRectangle(cornerRadius: 11, style: .continuous)
                    .fill(isSelected ? TaPalette.cinnabar.opacity(0.10) : Color.clear)
            )
            .overlay(alignment: .bottom) {
                Capsule()
                    .fill(isSelected ? TaPalette.cinnabar : Color.clear)
                    .frame(width: 18, height: 2)
                    .offset(y: 2)
            }
        }
        .buttonStyle(.plain)
        .help(tab.title)
    }
}

extension Notification.Name {
    static let openAIModelSettings = Notification.Name("Ta.OpenAIModelSettings")
}

private enum SettingsTab: String, CaseIterable, Identifiable {
    case permissions
    case general
    case hotKeys
    case recognition
    case translation
    case models
    case agent

    var id: String { rawValue }

    var title: String {
        switch self {
        case .permissions: "权限"
        case .general: "常规"
        case .hotKeys: "快捷键"
        case .recognition: "识别"
        case .translation: "翻译"
        case .models: "AI 模型"
        case .agent: "Agent"
        }
    }

    var icon: String {
        switch self {
        case .permissions: "lock.shield"
        case .general: "gearshape"
        case .hotKeys: "command"
        case .recognition: "text.viewfinder"
        case .translation: "character.book.closed"
        case .models: "sparkles"
        case .agent: "cpu"
        }
    }
}

private struct PermissionsSettingsView: View {
    @State private var isGranted = ScreenCapturePermissionService.isGranted
    @State private var accessibilityGranted = AccessibilityAutoScrollService.isGranted

    var body: some View {
        Form {
            Section("屏幕录制") {
                HStack {
                    VStack(alignment: .leading, spacing: 4) {
                        Label(
                            isGranted ? "已允许" : "尚未允许",
                            systemImage: isGranted ? "checkmark.circle.fill" : "exclamationmark.triangle.fill"
                        )
                        .foregroundStyle(isGranted ? .green : .orange)
                        Text("只读取你主动框选的区域；应用不会在后台持续录屏。")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    Spacer()
                    Button(isGranted ? "重新检测" : "请求权限") {
                        if !isGranted {
                            _ = ScreenCapturePermissionService.request()
                        }
                        isGranted = ScreenCapturePermissionService.isGranted
                    }
                    Button("打开系统设置") {
                        ScreenCapturePermissionService.openSystemSettings()
                    }
                }
            }

            Section("辅助功能") {
                HStack {
                    VStack(alignment: .leading, spacing: 4) {
                        Label(
                            accessibilityGranted ? "已允许" : "仅自动滚动时需要",
                            systemImage: accessibilityGranted ? "checkmark.circle.fill" : "hand.raised.fill"
                        )
                        .foregroundStyle(accessibilityGranted ? .green : .secondary)
                        Text("手动长截图不需要此权限；只有你主动开启自动滚动时才使用。")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    Spacer()
                    Button(accessibilityGranted ? "重新检测" : "请求权限") {
                        if !accessibilityGranted { _ = AccessibilityAutoScrollService.request() }
                        accessibilityGranted = AccessibilityAutoScrollService.isGranted
                    }
                    Button("打开系统设置") { AccessibilityAutoScrollService.openSystemSettings() }
                }
            }
        }
        .formStyle(.grouped)
        .scrollContentBackground(.hidden)
        .background(Color.clear)
        .onAppear {
            isGranted = ScreenCapturePermissionService.isGranted
            accessibilityGranted = AccessibilityAutoScrollService.isGranted
        }
    }
}

private struct GeneralSettingsView: View {
    @AppStorage("resultBarDuration") private var resultBarDuration = 3.0
    @AppStorage("saveHistory") private var saveHistory = false
    @AppStorage("postCaptureAction") private var postCaptureAction = PostCaptureAction.choose.rawValue
    @AppStorage(AppIconVisibility.dockIconKey) private var hideDockIcon = false
    @AppStorage(AppIconVisibility.menuBarIconKey) private var hideMenuBarIcon = false

    var body: some View {
        Form {
            Section("截图完成后") {
                Picker("默认动作", selection: $postCaptureAction) {
                    Text("每次让我选择（推荐）").tag(PostCaptureAction.choose.rawValue)
                    Text("识别内容并复制").tag(PostCaptureAction.recognize.rawValue)
                    Text("翻译文字并复制").tag(PostCaptureAction.translateText.rawValue)
                    Text("复制图片").tag(PostCaptureAction.copyImage.rawValue)
                    Text("钉在屏幕上").tag(PostCaptureAction.pin.rawValue)
                    Text("打开标注").tag(PostCaptureAction.edit.rawValue)
                    Text("记住上一次操作").tag(PostCaptureAction.rememberLast.rawValue)
                }
                Text("这个设置只影响通用截图；极速识别、截图翻译、复制图片和钉图快捷键会直接执行。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section("结果胶囊") {
                HStack {
                    Text("自动隐藏")
                    Slider(value: $resultBarDuration, in: 1.5...8, step: 0.5)
                    Text("\(resultBarDuration, specifier: "%.1f") 秒")
                        .monospacedDigit()
                        .frame(width: 54, alignment: .trailing)
                }
            }

            Section("历史与隐私") {
                Toggle("在本机保存截图历史", isOn: $saveHistory)
                Text(saveHistory ? "截图只保存在本机；云端调用仍会单独提示。" : "当前不会持久化截图文件。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section("图标显示") {
                Toggle("隐藏 Dock 图标", isOn: $hideDockIcon)
                Toggle("隐藏菜单栏图标", isOn: $hideMenuBarIcon)
                Text(hideDockIcon && hideMenuBarIcon
                     ? "两个图标都隐藏后，应用只能通过全局快捷键操作；如果找不到入口，请先恢复其中一个图标。"
                     : "菜单栏图标是面板入口，Dock 图标用于前台/后台切换；随时可以恢复显示。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .scrollContentBackground(.hidden)
        .background(Color.clear)
        .onChange(of: hideDockIcon) {
            AppIconVisibilityController.shared.apply()
        }
        .onChange(of: hideMenuBarIcon) {
            AppIconVisibilityController.shared.apply()
        }
    }
}

private struct RecognitionSettingsView: View {
    @AppStorage("recognitionLanguages") private var languages = "zh-Hans,en-US"
    @AppStorage("mergeWrappedLines") private var mergeWrappedLines = false
    @AppStorage("recognitionRoute") private var recognitionRoute = RecognitionRoute.localOCR.rawValue
    @AppStorage("ocrEngine") private var ocrEngine = OCREnginePreference.appleVision.rawValue
    @State private var packStatus: String?
    @State private var showingRemoveConfirmation = false
    @StateObject private var packInstaller = OCRPackInstallationViewModel()

    var body: some View {
        Form {
            Section("默认识别路径") {
                Picker("识别方式", selection: $recognitionRoute) {
                    Text("OCR 引擎（推荐）").tag(RecognitionRoute.localOCR.rawValue)
                    Text("多模态大模型").tag(RecognitionRoute.multimodal.rawValue)
                    Text("智能路由").tag(RecognitionRoute.smart.rawValue)
                }
                Text(routeDescription)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section("OCR") {
                Picker("OCR 引擎", selection: $ocrEngine) {
                    ForEach(OCREnginePreference.allCases, id: \.rawValue) { engine in
                        Text(engine.displayName).tag(engine.rawValue)
                    }
                }
                TextField("识别语言", text: $languages)
                Text(languageDescription)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Toggle("自动合并疑似断行", isOn: $mergeWrappedLines)
                if selectedEngine.usesOptionalPack {
                    VStack(alignment: .leading, spacing: 10) {
                        HStack {
                            Label(
                                installedLabel,
                                systemImage: packInstaller.installedInfo == nil ? "shippingbox" : "checkmark.circle.fill"
                            )
                            .foregroundStyle(packInstaller.installedInfo == nil ? .orange : .green)
                            Spacer()
                            if selectedEngine == .paddleOCR {
                                Button(installButtonLabel) {
                                    packInstaller.install(selectedEngine)
                                }
                                .buttonStyle(.borderedProminent)
                                .disabled(packInstaller.isBusy)
                            }
                            Button("手动导入…") { importPack() }
                                .disabled(packInstaller.isBusy)
                            if packInstaller.installedInfo != nil {
                                Button("卸载", role: .destructive) {
                                    showingRemoveConfirmation = true
                                }
                                .disabled(packInstaller.isBusy)
                            }
                        }
                        if let progress = packInstaller.progress {
                            VStack(alignment: .leading, spacing: 4) {
                                if let fraction = progress.fraction {
                                    ProgressView(value: fraction)
                                } else {
                                    ProgressView()
                                        .controlSize(.small)
                                }
                                HStack {
                                    Text(progress.stage.rawValue)
                                    Spacer()
                                    if packInstaller.isBusy {
                                        Button("取消") { packInstaller.cancel() }
                                            .buttonStyle(.link)
                                    }
                                }
                                .font(.caption)
                                .foregroundStyle(.secondary)
                            }
                        }
                        if let availability = packInstaller.availability, selectedEngine == .paddleOCR {
                            Text("可用版本 \(availability.package.version) · \(availability.formattedSize) · \(availability.isLocal ? "本机发行包" : "在线下载") · Apple Silicon")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                        if let message = packInstaller.message {
                            Text(message)
                                .font(.caption)
                                .foregroundStyle(packInstaller.isError ? .red : .secondary)
                        }
                    }
                    if let packStatus {
                        Text(packStatus).font(.caption).foregroundStyle(.secondary)
                    }
                    Text(selectedEngine == .paddleOCR
                         ? "PaddleOCR 在本机离线运行；选择后会后台预热并复用模型，闲置 5 分钟自动释放约 700–800 MB 的临时内存。安装时仍会完整校验增强包。"
                         : "增强包必须包含 manifest.json 和可执行适配器；未安装时自动回退 Apple Vision。")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                } else if selectedEngine == .deepSeekOCR2 {
                    DeepSeekOCRConfigurationRows()
                }
            }

            Section("数据路径") {
                Label(
                    selectedEngine.isLocalEngine ? "普通识别始终在本机完成" : "截图会发送到你配置的 DeepSeek OCR 服务",
                    systemImage: selectedEngine.isLocalEngine ? "lock.shield.fill" : "network"
                )
                .foregroundStyle(selectedEngine.isLocalEngine ? .green : .orange)
                Text(selectedEngine.isLocalEngine
                     ? "低置信度结果只会提示增强，不会静默上传。"
                     : "只有选择 DeepSeek-OCR-2 时才上传；切回 Apple Vision 或 PaddleOCR 即恢复本机离线识别。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .scrollContentBackground(.hidden)
        .background(Color.clear)
        .onAppear {
            packInstaller.refresh(selectedEngine)
            packInstaller.prewarm(selectedEngine)
        }
        .onChange(of: ocrEngine) { _, _ in
            packInstaller.refresh(selectedEngine)
            packInstaller.prewarm(selectedEngine)
        }
        .confirmationDialog(
            "卸载 \(selectedEngine.displayName)？",
            isPresented: $showingRemoveConfirmation,
            titleVisibility: .visible
        ) {
            Button("卸载增强包", role: .destructive) {
                packInstaller.remove(selectedEngine)
            }
            Button("取消", role: .cancel) {}
        } message: {
            Text("卸载后截图识别会立即回退到 Apple Vision，主功能仍可使用。")
        }
    }

    private var selectedEngine: OCREnginePreference {
        OCREnginePreference(rawValue: ocrEngine) ?? .appleVision
    }

    private func importPack() {
        do {
            if let version = try packInstaller.manager.chooseAndImport(selectedEngine) {
                packStatus = "已导入版本 \(version)"
                packInstaller.refresh(selectedEngine)
                packInstaller.prewarm(selectedEngine)
            }
        } catch {
            packStatus = error.localizedDescription
        }
    }

    private var installedLabel: String {
        if let info = packInstaller.installedInfo {
            return "已安装版本 \(info.version)"
        }
        return "增强包未安装，当前自动回退 Apple Vision"
    }

    private var languageDescription: String {
        switch selectedEngine {
        case .appleVision:
            "按优先级填写 Apple Vision 语言代码，以英文逗号分隔。"
        case .rapidOCR, .paddleOCR:
            "增强包使用内置中英文模型；此语言列表只在回退 Apple Vision 时生效。"
        case .deepSeekOCR2:
            "DeepSeek-OCR-2 自动识别多语言；这里的代码用于结果元数据和回退提示。"
        }
    }

    private var installButtonLabel: String {
        guard let installed = packInstaller.installedInfo else { return "下载并安装" }
        guard let available = packInstaller.availability else { return "重新安装" }
        return available.package.version.compare(installed.version, options: .numeric) == .orderedDescending
            ? "更新到 \(available.package.version)"
            : "重新安装"
    }

    private var routeDescription: String {
        switch RecognitionRoute(rawValue: recognitionRoute) ?? .localOCR {
        case .localOCR:
            selectedEngine == .deepSeekOCR2
                ? "使用 DeepSeek-OCR-2 服务识别，本次截图会发送到你配置的端点。"
                : "使用所选 OCR 引擎在本机识别；增强包未安装时回退 Apple Vision，不需要 API Key。"
        case .multimodal:
            "把主动框选的图片发送给已配置的 OpenAI-compatible 视觉模型，并复制模型结果。"
        case .smart:
            selectedEngine == .deepSeekOCR2
                ? "DeepSeek-OCR-2 本身是远程识别；如需“本机优先、低置信度再上传”，请选择 Apple Vision 或 PaddleOCR。"
                : "先在本机 OCR；低置信度时提示 AI 增强，不会静默上传图片。"
        }
    }
}

private struct DeepSeekOCRConfigurationRows: View {
    @AppStorage("deepSeekOCRBaseURL") private var baseURL = ""
    @AppStorage("deepSeekOCRModel") private var model = DeepSeekOCR2Client.latestOfficialModel
    @AppStorage("deepSeekOCRPromptMode") private var promptMode = DeepSeekOCRPromptMode.plainText.rawValue

    @State private var apiKey = ""
    @State private var hasStoredKey = false
    @State private var statusMessage: String?

    private let secretStore = KeychainSecretStore()

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Label(
                    configurationReady ? "DeepSeek-OCR-2 服务已配置" : "需要配置 DeepSeek-OCR-2 服务",
                    systemImage: configurationReady ? "checkmark.circle.fill" : "exclamationmark.triangle.fill"
                )
                .foregroundStyle(configurationReady ? .green : .orange)
                Spacer()
                Link(
                    "官方模型页",
                    destination: URL(string: "https://huggingface.co/deepseek-ai/DeepSeek-OCR-2")!
                )
                .font(.caption)
            }

            TextField(
                "服务地址",
                text: $baseURL,
                prompt: Text(DeepSeekOCR2Client.recommendedLocalBaseURL)
            )
            TextField("模型名", text: $model)
            Picker("输出格式", selection: $promptMode) {
                ForEach(DeepSeekOCRPromptMode.allCases, id: \.rawValue) { mode in
                    Text(mode.displayName).tag(mode.rawValue)
                }
            }
            SecureField(hasStoredKey ? "输入新 Key 以更新（本机服务可留空）" : "API Key（本机服务可留空）", text: $apiKey)

            HStack {
                Button("填入本机默认地址") {
                    baseURL = DeepSeekOCR2Client.recommendedLocalBaseURL
                    model = DeepSeekOCR2Client.latestOfficialModel
                    statusMessage = "已填入 vLLM 默认地址；请先确保服务已启动。"
                }
                if hasStoredKey {
                    Button("移除 Key", role: .destructive) { removeKey() }
                }
                Spacer()
                Button(hasStoredKey ? "更新 Key" : "保存 Key") { saveKey() }
                    .disabled(apiKey.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }

            if let validationMessage {
                Label(validationMessage, systemImage: "exclamationmark.triangle.fill")
                    .font(.caption)
                    .foregroundStyle(.orange)
            }
            if let statusMessage {
                Text(statusMessage)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Text("当前最新专用模型为 DeepSeek-OCR-2。它约 6.79 GB，官方推理方案面向 CUDA，因此本应用连接 vLLM/SGLang 或兼容服务，不会在 Mac 上静默下载模型。DeepSeek 官方聊天 API 地址不能代替 OCR-2 服务地址。")
                .font(.caption)
                .foregroundStyle(.secondary)
            Text("服务地址、模型名和 Key 在同一台 Mac 上覆盖升级拓时会继续保留。")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .onAppear {
            hasStoredKey = secretStore.contains(account: DeepSeekOCRRecognitionService.keychainAccount)
        }
    }

    private var validationMessage: String? {
        let trimmedModel = model.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmedModel.isEmpty { return "请填写服务端实际使用的模型名。" }
        let trimmedURL = baseURL.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmedURL.isEmpty { return "请填写部署了 DeepSeek-OCR-2 的服务地址。" }
        if URLComponents(string: trimmedURL)?.host?.lowercased() == "api.deepseek.com" {
            return "DeepSeek 官方聊天 API 没有提供 DeepSeek-OCR-2 端点，请改用自托管或兼容服务。"
        }
        return ProviderEndpointValidator().validationMessage(for: trimmedURL)
    }

    private var configurationReady: Bool {
        validationMessage == nil
    }

    private func saveKey() {
        do {
            try secretStore.save(apiKey, account: DeepSeekOCRRecognitionService.keychainAccount)
            apiKey = ""
            hasStoredKey = true
            statusMessage = "DeepSeek OCR API Key 已保存到 Keychain。"
        } catch {
            statusMessage = error.localizedDescription
        }
    }

    private func removeKey() {
        do {
            try secretStore.delete(account: DeepSeekOCRRecognitionService.keychainAccount)
            apiKey = ""
            hasStoredKey = false
            statusMessage = "DeepSeek OCR API Key 已移除。"
        } catch {
            statusMessage = error.localizedDescription
        }
    }
}

@MainActor
private final class OCRPackInstallationViewModel: ObservableObject {
    let manager = OptionalOCRPackManager()

    @Published var availability: OCRPackAvailability?
    @Published var installedInfo: OCRPackInstalledInfo?
    @Published var progress: OCRPackInstallProgress?
    @Published var message: String?
    @Published var isBusy = false
    @Published var isError = false

    private var installationTask: Task<Void, Never>?

    init() {
        let rawValue = UserDefaults.standard.string(forKey: "ocrEngine")
            ?? OCREnginePreference.appleVision.rawValue
        let initialEngine = OCREnginePreference(rawValue: rawValue) ?? .appleVision
        Task { [weak self] in
            await self?.refreshAvailability(initialEngine)
        }
    }

    func refresh(_ engine: OCREnginePreference) {
        Task { await refreshAvailability(engine) }
    }

    private func refreshAvailability(_ engine: OCREnginePreference) async {
        installedInfo = manager.installedInfo(engine)
        guard engine == .paddleOCR else {
            availability = nil
            return
        }
        do {
            availability = try await manager.availablePackage(for: engine)
            if !isBusy {
                message = nil
                isError = false
            }
        } catch {
            availability = nil
            if installedInfo == nil {
                message = "尚未发现可一键安装的发行包，也可以先使用手动导入。"
                isError = false
            }
        }
    }

    func install(_ engine: OCREnginePreference) {
        installationTask?.cancel()
        isBusy = true
        isError = false
        message = nil
        progress = .init(stage: .resolving, fraction: nil)
        installationTask = Task {
            do {
                let info = try await manager.installRecommended(for: engine) { [weak self] update in
                    Task { @MainActor in self?.progress = update }
                }
                installedInfo = info
                message = "PaddleOCR \(info.version) 已安装，正在后台预热模型。"
                progress = nil
                manager.prewarm(engine)
            } catch is CancellationError {
                message = "已取消安装，原有 OCR 配置没有改变。"
                progress = nil
            } catch {
                message = error.localizedDescription
                isError = true
                progress = nil
            }
            isBusy = false
            await refreshAvailability(engine)
        }
    }

    func cancel() {
        installationTask?.cancel()
    }

    func prewarm(_ engine: OCREnginePreference) {
        guard engine == .paddleOCR, manager.isInstalled(engine) else { return }
        manager.prewarm(engine)
    }

    func remove(_ engine: OCREnginePreference) {
        do {
            try manager.remove(engine)
            installedInfo = nil
            message = "已卸载增强包，当前回退到 Apple Vision。"
            isError = false
        } catch {
            message = error.localizedDescription
            isError = true
        }
    }
}
