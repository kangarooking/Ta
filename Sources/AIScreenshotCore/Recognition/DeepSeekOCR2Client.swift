@preconcurrency import Foundation

public enum DeepSeekOCRPromptMode: String, CaseIterable, Codable, Sendable {
    case plainText
    case documentMarkdown

    public var displayName: String {
        switch self {
        case .plainText: "纯文本（推荐截图取字）"
        case .documentMarkdown: "Markdown（保留文档结构）"
        }
    }

    public var prompt: String {
        switch self {
        // The image travels as an `image_url` part; the server's chat
        // template inserts the image placeholder itself, so a literal
        // "<image>" here would be sent to the model as plain text.
        case .plainText: "Free OCR."
        case .documentMarkdown: "<|grounding|>Convert the document to markdown."
        }
    }
}

public enum DeepSeekOCR2ClientError: LocalizedError, Equatable {
    case invalidEndpoint(String)
    case officialAPIUnsupported
    case missingModel
    case invalidResponse
    case emptyResponse
    case server(statusCode: Int, message: String)

    public var errorDescription: String? {
        switch self {
        case .invalidEndpoint(let message): message
        case .officialAPIUnsupported:
            "DeepSeek 官方 API 当前没有提供 DeepSeek-OCR-2 模型端点；请填写部署了该模型的 vLLM、SGLang 或兼容服务地址。"
        case .missingModel: "尚未配置 DeepSeek OCR 模型名。"
        case .invalidResponse: "DeepSeek OCR 服务返回了无法解析的响应。"
        case .emptyResponse: "DeepSeek OCR 没有返回识别内容。"
        case .server(let statusCode, let message): "DeepSeek OCR 服务错误（\(statusCode)）：\(message)"
        }
    }
}

public struct DeepSeekOCR2Client: @unchecked Sendable {
    public static let latestOfficialModel = "deepseek-ai/DeepSeek-OCR-2"
    public static let recommendedLocalBaseURL = "http://127.0.0.1:8000/v1"
    static let maxOutputTokens = 8192

    private let session: URLSession
    let retryBaseDelay: TimeInterval

    public init(session: URLSession = .shared) {
        self.init(session: session, retryBaseDelay: ProviderHTTP.defaultBaseDelay)
    }

    init(session: URLSession, retryBaseDelay: TimeInterval) {
        self.session = session
        self.retryBaseDelay = retryBaseDelay
    }

    /// Recognises the image. Grounding tags are removed from the output, and
    /// when the model stopped at its output limit the returned text ends
    /// with `ProviderOutputNotice.truncated`.
    public func recognize(
        baseURL: String,
        model: String,
        apiKey: String,
        imageData: Data,
        mimeType: String = "image/png",
        mode: DeepSeekOCRPromptMode
    ) async throws -> String {
        let trimmedModel = model.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedModel.isEmpty else { throw DeepSeekOCR2ClientError.missingModel }
        let endpoint = try endpointURL(from: baseURL)

        let outcome: ProviderChatOutcome
        do {
            outcome = try await ProviderChatEngine.complete(
                ProviderChatRequest(
                    provider: .openAICompatible,
                    endpoint: endpoint,
                    model: trimmedModel,
                    apiKey: apiKey.trimmingCharacters(in: .whitespacesAndNewlines),
                    prompt: mode.prompt,
                    imageData: imageData,
                    mimeType: mimeType,
                    maxTokens: Self.maxOutputTokens,
                    timeout: 120
                ),
                session: session,
                retryBaseDelay: retryBaseDelay
            )
        } catch let failure as ProviderChatFailure {
            switch failure {
            case .invalidResponse:
                throw DeepSeekOCR2ClientError.invalidResponse
            case .emptyResponse, .reasoningOnlyOutput:
                throw DeepSeekOCR2ClientError.emptyResponse
            case .server(let statusCode, let message):
                throw DeepSeekOCR2ClientError.server(statusCode: statusCode, message: message)
            }
        }
        let text = Self.strippingGroundingTags(from: outcome.text)
        guard !text.isEmpty else { throw DeepSeekOCR2ClientError.emptyResponse }
        return ProviderOutputNotice.appending(to: text, truncated: outcome.truncated)
    }

    /// Removes DeepSeek-OCR grounding markup such as
    /// `<|ref|>title<|/ref|><|det|>[[12,34,560,78]]<|/det|>` and any other
    /// leftover `<|...|>` special tokens, keeping only the document text.
    static func strippingGroundingTags(from text: String) -> String {
        var result = text
        let patterns = [
            #"<\|ref\|>[\s\S]*?<\|/ref\|>\s*<\|det\|>[\s\S]*?<\|/det\|>[ \t]*\n?"#,
            #"<\|det\|>[\s\S]*?<\|/det\|>[ \t]*\n?"#,
            #"<\|/?[A-Za-z_][A-Za-z0-9_▁]*\|>"#
        ]
        for pattern in patterns {
            result = result.replacingOccurrences(of: pattern, with: "", options: .regularExpression)
        }
        result = result.replacingOccurrences(of: #"\n{3,}"#, with: "\n\n", options: .regularExpression)
        return result.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private func endpointURL(from rawValue: String) throws -> URL {
        let trimmed = rawValue.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else {
            throw DeepSeekOCR2ClientError.invalidEndpoint("请先配置 DeepSeek OCR 服务地址。")
        }
        if let validation = ProviderEndpointValidator().validationMessage(for: trimmed) {
            throw DeepSeekOCR2ClientError.invalidEndpoint(validation)
        }
        guard var components = URLComponents(string: trimmed), components.url != nil else {
            throw DeepSeekOCR2ClientError.invalidEndpoint("DeepSeek OCR 服务地址格式无效。")
        }
        if components.host?.lowercased() == "api.deepseek.com" {
            throw DeepSeekOCR2ClientError.officialAPIUnsupported
        }
        components.path = ProviderEndpointPath.resolved(
            provider: .openAICompatible,
            path: components.path,
            model: ""
        )
        guard let url = components.url else {
            throw DeepSeekOCR2ClientError.invalidEndpoint("DeepSeek OCR 服务地址格式无效。")
        }
        return url
    }
}
