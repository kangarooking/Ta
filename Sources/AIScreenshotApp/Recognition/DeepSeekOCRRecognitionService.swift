import CoreGraphics
import Foundation
import AIScreenshotCore

enum DeepSeekOCRRecognitionError: LocalizedError {
    case imageEncodingFailed

    var errorDescription: String? {
        switch self {
        case .imageEncodingFailed: "无法为 DeepSeek-OCR-2 编码截图。"
        }
    }
}

struct DeepSeekOCRRecognitionService: @unchecked Sendable {
    static let keychainAccount = PersistentConfigurationIdentity.deepSeekOCRAccount

    private let client = DeepSeekOCR2Client()
    private let secretStore = KeychainSecretStore()

    func recognize(image: CGImage, languages: [String]) async throws -> OCRResult {
        let defaults = UserDefaults.standard
        let baseURL = defaults.string(forKey: "deepSeekOCRBaseURL") ?? ""
        let model = defaults.string(forKey: "deepSeekOCRModel") ?? DeepSeekOCR2Client.latestOfficialModel
        let modeRaw = defaults.string(forKey: "deepSeekOCRPromptMode") ?? DeepSeekOCRPromptMode.plainText.rawValue
        let mode = DeepSeekOCRPromptMode(rawValue: modeRaw) ?? .plainText
        let apiKey = try secretStore.read(account: Self.keychainAccount) ?? ""
        let imageData: Data
        do {
            imageData = try ModelImageEncoder.jpegData(from: image)
        } catch {
            throw DeepSeekOCRRecognitionError.imageEncodingFailed
        }
        let response = try await client.recognize(
            baseURL: baseURL,
            model: model,
            apiKey: apiKey,
            imageData: imageData,
            mimeType: ModelImageEncoder.mimeType,
            mode: mode
        )
        // Classify the recognised text only; the truncation notice (if any)
        // stays at the end of what the user receives.
        let text = ProviderOutputNotice.removingNotice(from: response)
        return OCRResult(
            text: response,
            contentType: ContentClassifier().classify(text),
            confidence: ProviderOutputNotice.isTruncated(response) ? 0.6 : 0.85,
            languages: languages,
            engine: .deepSeekOCR2
        )
    }
}
