import AppKit
import AIScreenshotCore

enum ScreenshotTranslationServiceError: LocalizedError {
    case imageEncodingFailed
    case missingTextBoxes
    case localOCRUnavailableForImage

    var errorDescription: String? {
        switch self {
        case .imageEncodingFailed: "无法为翻译模型编码截图。"
        case .missingTextBoxes: "没有找到可靠的文字位置，无法生成翻译图片；可以改用“翻译文字并复制”。"
        case .localOCRUnavailableForImage:
            "本地文字识别暂时无法定位图片中的文字，因此不能生成全文翻译图片。请重试，或在翻译设置中改用“翻译文字并复制”。"
        }
    }
}

struct ScreenshotTranslationService: @unchecked Sendable {
    // Kept as a migration compatibility alias for existing installations.
    static let keychainAccount = PersistentConfigurationIdentity.translationProviderAccount

    private let client: TranslationProviderClient
    private let profileStore: AIProviderProfileStore

    init(
        client: TranslationProviderClient = TranslationProviderClient(),
        profileStore: AIProviderProfileStore = AIProviderProfileStore()
    ) {
        self.client = client
        self.profileStore = profileStore
    }

    var isConfigured: Bool {
        (try? requiredProfile()) != nil
    }

    var selectedTextModelName: String {
        (try? requiredProfile().textModel) ?? "文字模型"
    }

    func validateConfiguration() throws {
        _ = try requiredProfile()
    }

    func translateText(_ text: String) async throws -> String {
        let configuration = TranslationConfiguration.load()
        let profile = try requiredProfile()
        let key = try apiKey(for: profile)
        return try await client.translateText(
            baseURL: profile.baseURL,
            model: profile.textModel,
            apiKey: key,
            text: text,
            sourceLanguage: configuration.sourceLanguage,
            targetLanguage: configuration.targetLanguage,
            provider: profile.providerKind
        )
    }

    func translateLines(_ lines: [OCRTextLine]) async throws -> [TranslatedOCRLine] {
        let usableLines = lines.filter { !$0.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
        guard !usableLines.isEmpty else { throw ScreenshotTranslationServiceError.missingTextBoxes }
        let configuration = TranslationConfiguration.load()
        let profile = try requiredProfile()
        let key = try apiKey(for: profile)
        let segments = usableLines.enumerated().map {
            TranslationSourceSegment(id: $0.offset, text: $0.element.text)
        }
        let translated = try await client.translateSegments(
            baseURL: profile.baseURL,
            model: profile.textModel,
            apiKey: key,
            segments: segments,
            sourceLanguage: configuration.sourceLanguage,
            targetLanguage: configuration.targetLanguage,
            provider: profile.providerKind
        )
        let byID = Dictionary(translated.map { ($0.id, $0.text) }) { first, _ in first }
        let allLines = usableLines.enumerated().compactMap { index, line -> TranslatedOCRLine? in
            guard let translatedText = byID[index], !translatedText.isEmpty else { return nil }
            return TranslatedOCRLine(
                sourceText: line.text,
                translatedText: translatedText,
                boundingBox: line.boundingBox,
                confidence: line.confidence
            )
        }
        // The client falls back to the source text for lines the model
        // skipped. Repainting those would only blur the original pixels, so
        // leave them untouched unless nothing else changed.
        let changedLines = allLines.filter { $0.translatedText != $0.sourceText }
        return changedLines.isEmpty ? allLines : changedLines
    }

    func translateWithVision(image: CGImage) async throws -> String {
        let configuration = TranslationConfiguration.load()
        let profile = try requiredProfile()
        let key = try apiKey(for: profile)
        let imageData = try encodeForModel(image)
        return try await client.translateImage(
            baseURL: profile.baseURL,
            model: profile.visionModel,
            apiKey: key,
            imageData: imageData,
            mimeType: ModelImageEncoder.mimeType,
            sourceLanguage: configuration.sourceLanguage,
            targetLanguage: configuration.targetLanguage,
            provider: profile.providerKind
        )
    }

    func testTextModel(profile: AIProviderProfile, apiKey: String? = nil) async throws -> String {
        let key = try apiKey ?? self.apiKey(for: profile)
        return try await client.testConnection(
            baseURL: profile.baseURL,
            model: profile.textModel,
            apiKey: key,
            provider: profile.providerKind
        )
    }

    func testVisionModel(profile: AIProviderProfile, apiKey: String? = nil) async throws -> String {
        let image = try makeVisionTestImage()
        let key = try apiKey ?? self.apiKey(for: profile)
        let data = try encodeForModel(image)
        return try await client.translateImage(
            baseURL: profile.baseURL,
            model: profile.visionModel,
            apiKey: key,
            imageData: data,
            mimeType: ModelImageEncoder.mimeType,
            sourceLanguage: "英文",
            targetLanguage: "简体中文",
            provider: profile.providerKind
        )
    }

    /// Self-hosted OpenAI-compatible servers on this Mac / LAN run without
    /// an API key, so a missing key does not disqualify those profiles.
    private func apiKeyIsOptional(for profile: AIProviderProfile) -> Bool {
        profile.providerKind == .openAICompatible
            && ProviderEndpointValidator().isLocalEndpoint(profile.baseURL)
    }

    private func apiKey(for profile: AIProviderProfile) throws -> String {
        do {
            return try profileStore.apiKey(for: profile.id)
        } catch {
            guard apiKeyIsOptional(for: profile) else { throw error }
            return ""
        }
    }

    private func isEligible(_ profile: AIProviderProfile) -> Bool {
        if profileStore.translationEligibility(of: profile) == nil { return true }
        return apiKeyIsOptional(for: profile)
            && profile.validationMessage(requiresTextModel: true) == nil
    }

    private func requiredProfile() throws -> AIProviderProfile {
        let state = try profileStore.loadState()
        if let selected = state.translationProfile, isEligible(selected) {
            return selected
        }
        let eligible = state.profiles.filter(isEligible)
        if eligible.count == 1, let only = eligible.first {
            // Persisting the choice is best-effort: the store may still
            // refuse a key-less local profile.
            _ = try? profileStore.setTranslationProfile(id: only.id)
            return only
        }
        if state.translationProfile != nil {
            throw AIProviderProfileStoreError.translationIneligible(
                "当前翻译模型缺少文字模型、视觉模型或 API Key，请到“AI 模型”中补全。"
            )
        }
        throw AIProviderProfileStoreError.translationIneligible(
            "请先到“AI 模型”配置一套带文字模型和视觉模型的服务，然后在翻译设置中选择它。"
        )
    }

    private func encodeForModel(_ image: CGImage) throws -> Data {
        do {
            return try ModelImageEncoder.jpegData(from: image)
        } catch {
            throw ScreenshotTranslationServiceError.imageEncodingFailed
        }
    }

    private func makeVisionTestImage() throws -> CGImage {
        guard let representation = NSBitmapImageRep(
            bitmapDataPlanes: nil,
            pixelsWide: 520,
            pixelsHigh: 180,
            bitsPerSample: 8,
            samplesPerPixel: 4,
            hasAlpha: true,
            isPlanar: false,
            colorSpaceName: .deviceRGB,
            bytesPerRow: 0,
            bitsPerPixel: 0
        ) else { throw ScreenshotTranslationServiceError.imageEncodingFailed }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: representation)
        NSColor.white.setFill()
        NSRect(x: 0, y: 0, width: 520, height: 180).fill()
        "Screenshot translation ready".draw(
            at: CGPoint(x: 32, y: 72),
            withAttributes: [
                .font: NSFont.systemFont(ofSize: 30, weight: .semibold),
                .foregroundColor: NSColor.black
            ]
        )
        NSGraphicsContext.restoreGraphicsState()
        guard let image = representation.cgImage else {
            throw ScreenshotTranslationServiceError.imageEncodingFailed
        }
        return image
    }
}
