import SwiftUI
import AIScreenshotCore

struct TranslationSettingsView: View {
    @AppStorage("translationSourceLanguage") private var sourceLanguage = TranslationConfiguration.defaultSourceLanguage
    @AppStorage("translationTargetLanguage") private var targetLanguage = TranslationConfiguration.defaultTargetLanguage
    @AppStorage("translationDefaultMode") private var defaultMode = ScreenshotTranslationMode.textOnly.rawValue
    @AppStorage("translationUsesVisionFallback") private var usesVisionFallback = true
    @AppStorage(TranslationVisionUploadConsent.skipConfirmationDefaultsKey) private var skipsVisionUploadConfirmation = false

    @State private var state = AIProviderProfileState()
    @State private var eligibleProfiles: [AIProviderProfile] = []
    @State private var selectedProfileID: UUID?
    @State private var statusMessage: String?
    @State private var statusIsError = false
    @State private var isTesting = false
    @State private var translationShortcut = HotKeyPreferences().shortcut(for: .translationCapture)

    private let profileStore = AIProviderProfileStore()
    private let translationService = ScreenshotTranslationService()

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 13) {
                compactHeader
                modelSection
                languageSection
                behaviorSection
                privacyNote
            }
            .padding(.horizontal, 4)
            .padding(.bottom, 8)
        }
        .scrollIndicators(.automatic)
        .onAppear(perform: reload)
        .onReceive(NotificationCenter.default.publisher(for: HotKeyPreferences.didChangeNotification)) { _ in
            translationShortcut = HotKeyPreferences().shortcut(for: .translationCapture)
        }
    }

    private var compactHeader: some View {
        HStack(spacing: 12) {
            Image(systemName: "character.book.closed.fill")
                .font(.system(size: 17, weight: .medium))
                .foregroundStyle(TaPalette.cinnabar)
                .frame(width: 38, height: 38)
                .background(TaPalette.cinnabar.opacity(0.10), in: RoundedRectangle(cornerRadius: 11))
            VStack(alignment: .leading, spacing: 2) {
                Text("截图翻译")
                    .font(.title3.weight(.semibold))
                    .foregroundStyle(TaPalette.ink)
                Text("沿用已保存的 AI 模型，只需设置语言和默认行为。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Spacer()
            if selectedProfile != nil {
                Label("已就绪", systemImage: "checkmark.circle.fill")
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(.green)
            }
        }
        .padding(.horizontal, 2)
    }

    private var modelSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            sectionTitle("使用的模型配置", detail: "这里只显示 API Key、文字模型和视觉模型均已就绪的配置。")
            if eligibleProfiles.isEmpty {
                HStack(spacing: 10) {
                    Image(systemName: "exclamationmark.triangle.fill")
                        .foregroundStyle(.orange)
                    VStack(alignment: .leading, spacing: 2) {
                        Text("暂无可用于翻译的模型")
                            .font(.callout.weight(.semibold))
                        Text("先完成一套 AI 模型配置，翻译页会自动复用。")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    Spacer()
                    Button("去配置 AI 模型") {
                        NotificationCenter.default.post(name: .openAIModelSettings, object: nil)
                    }
                    .buttonStyle(.borderedProminent)
                }
                .padding(13)
                .background(surface)
            } else {
                VStack(alignment: .leading, spacing: 9) {
                    HStack(spacing: 10) {
                        Image(systemName: "checkmark.shield.fill")
                            .font(.system(size: 20))
                            .foregroundStyle(.green)
                        Picker("模型配置", selection: $selectedProfileID) {
                            ForEach(eligibleProfiles) { profile in
                                Text(profile.trimmedName).tag(Optional(profile.id))
                            }
                        }
                        .onChange(of: selectedProfileID) { _, newValue in
                            selectTranslationProfile(newValue)
                        }
                        Spacer()
                        Label("可用", systemImage: "circle.fill")
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(.green)
                    }

                    if let profile = selectedProfile {
                        HStack(spacing: 10) {
                            modelValue("文字", profile.textModel)
                            Divider().frame(height: 24)
                            modelValue("视觉", profile.visionModel)
                            Spacer()
                        }
                    }

                    HStack {
                        if state.profiles.count > eligibleProfiles.count {
                            Text("另有 \(state.profiles.count - eligibleProfiles.count) 套配置尚未完成。")
                                .font(.caption2)
                                .foregroundStyle(.secondary)
                        }
                        Spacer()
                        Button("管理模型") {
                            NotificationCenter.default.post(name: .openAIModelSettings, object: nil)
                        }
                        Button("测试") { testSelectedProfile() }
                            .disabled(isTesting || selectedProfile == nil)
                    }
                    .controlSize(.small)
                }
                .padding(13)
                .background(surface)
            }

            if isTesting {
                HStack(spacing: 7) {
                    ProgressView().controlSize(.small)
                    Text("正在测试文字与视觉能力…")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
            if let statusMessage {
                Label(statusMessage, systemImage: statusIsError ? "xmark.octagon.fill" : "checkmark.circle.fill")
                    .font(.caption)
                    .foregroundStyle(statusIsError ? .red : .green)
                    .textSelection(.enabled)
            }
        }
    }

    private var languageSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            sectionTitle("翻译语言", detail: "默认自动识别截图语言，也可以直接输入其他语言。")
            VStack(spacing: 8) {
                languageRow(title: "源语言", value: $sourceLanguage, presets: ["自动检测", "英文", "简体中文", "日文", "韩文"])
                languageRow(title: "目标语言", value: $targetLanguage, presets: ["简体中文", "英文", "繁体中文", "日文", "韩文", "西班牙文"])
            }
            .padding(12)
            .background(surface)
        }
    }

    private var behaviorSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            sectionTitle("默认行为", detail: "截图工具栏点击“翻译”后直接执行，不再二次确认。")
            VStack(alignment: .leading, spacing: 9) {
                Picker("默认翻译方式", selection: $defaultMode) {
                    ForEach(ScreenshotTranslationMode.allCases, id: \.rawValue) { mode in
                        Text(mode.displayName).tag(mode.rawValue)
                    }
                }
                Toggle("本地 OCR 置信度较低时，使用视觉模型读取截图", isOn: $usesVisionFallback)
                Toggle(
                    "上传截图给视觉模型前先询问",
                    isOn: Binding(
                        get: { !skipsVisionUploadConfirmation },
                        set: { skipsVisionUploadConfirmation = !$0 }
                    )
                )
                .disabled(!usesVisionFallback)
                Text("本地识别不到文字或置信度偏低时，拓会先征得你的同意，再把本次框选的图片发送给视觉模型。")
                    .font(.caption2)
                    .foregroundStyle(.secondary)
                Text("快捷键 \(translationShortcut.displayText) 始终执行“翻译文字并复制”。")
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            }
            .padding(12)
            .background(surface)
        }
    }

    private var privacyNote: some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: "lock.shield.fill")
                .foregroundStyle(.green)
            Text("API Key 只保存在这台 Mac 的 Keychain，覆盖升级拓后继续保留；只有主动翻译或识图时才会发送所选截图。")
                .font(.caption2)
                .foregroundStyle(.secondary)
        }
        .padding(.horizontal, 3)
    }

    private func sectionTitle(_ title: String, detail: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Text(title)
                .font(.callout.weight(.semibold))
                .foregroundStyle(TaPalette.ink)
            Text(detail)
                .font(.caption2)
                .foregroundStyle(.secondary)
                .lineLimit(1)
        }
    }

    private func modelValue(_ title: String, _ value: String) -> some View {
        HStack(spacing: 6) {
            Text(title)
                .font(.caption2)
                .foregroundStyle(.secondary)
            Text(value)
                .font(.caption.monospaced())
                .lineLimit(1)
        }
    }

    private func languageRow(title: String, value: Binding<String>, presets: [String]) -> some View {
        LabeledContent(title) {
            HStack(spacing: 8) {
                TextField(title, text: value)
                    .textFieldStyle(.roundedBorder)
                    .frame(minWidth: 360)
                Menu("常用") {
                    ForEach(presets, id: \.self) { language in
                        Button(language) { value.wrappedValue = language }
                    }
                }
                .menuStyle(.borderlessButton)
                .fixedSize()
            }
        }
    }

    private var surface: some View {
        RoundedRectangle(cornerRadius: 12, style: .continuous)
            .fill(TaPalette.elevatedPaper.opacity(0.84))
            .overlay { RoundedRectangle(cornerRadius: 12).stroke(TaPalette.hairline, lineWidth: 1) }
    }

    private var selectedProfile: AIProviderProfile? {
        eligibleProfiles.first { $0.id == selectedProfileID }
    }

    private func reload() {
        do {
            state = try profileStore.loadState()
            eligibleProfiles = profileStore.eligibleTranslationProfiles(in: state)
            if eligibleProfiles.contains(where: { $0.id == state.translationProfileID }) {
                selectedProfileID = state.translationProfileID
            } else {
                selectedProfileID = eligibleProfiles.first?.id
                if let selectedProfileID {
                    _ = try profileStore.setTranslationProfile(id: selectedProfileID)
                }
            }
        } catch {
            status(error.localizedDescription, isError: true)
        }
    }

    private func selectTranslationProfile(_ id: UUID?) {
        do {
            state = try profileStore.setTranslationProfile(id: id)
            status("已切换翻译模型。")
        } catch {
            status(error.localizedDescription, isError: true)
        }
    }

    private func testSelectedProfile() {
        guard let profile = selectedProfile else { return }
        isTesting = true
        status("正在测试…")
        Task {
            do {
                _ = try await translationService.testTextModel(profile: profile)
                _ = try await translationService.testVisionModel(profile: profile)
                status("文字模型与视觉模型均可用。")
            } catch {
                status(error.localizedDescription, isError: true)
            }
            isTesting = false
        }
    }

    private func status(_ message: String, isError: Bool = false) {
        statusMessage = message
        statusIsError = isError
    }
}
