@preconcurrency import Foundation

public enum VisionClientError: LocalizedError, Equatable {
    case invalidEndpoint(String)
    case missingModel
    case missingAPIKey
    case invalidResponse
    case emptyResponse
    case reasoningOnlyOutput(truncated: Bool)
    case server(statusCode: Int, message: String)

    public var errorDescription: String? {
        switch self {
        case .invalidEndpoint(let message): message
        case .missingModel: "尚未配置视觉模型。"
        case .missingAPIKey: "尚未配置 API Key。"
        case .invalidResponse: "模型服务返回了无法解析的响应。"
        case .emptyResponse: "模型没有返回识别内容。"
        case .reasoningOnlyOutput(let truncated):
            truncated
                ? "识别失败：输出上限被思考过程耗尽，还没生成正文就被截断了。请关闭该模型的深度思考（或改用非思考模型），或调大输出上限后重试。"
                : "模型只返回了思考过程，没有识别正文。请关闭深度思考后重试。"
        case .server(let statusCode, let message): "模型服务错误（\(statusCode)）：\(message)"
        }
    }

    init(_ failure: ProviderChatFailure) {
        switch failure {
        case .invalidResponse: self = .invalidResponse
        case .emptyResponse: self = .emptyResponse
        case .reasoningOnlyOutput(let truncated): self = .reasoningOnlyOutput(truncated: truncated)
        case .server(let statusCode, let message): self = .server(statusCode: statusCode, message: message)
        }
    }
}

/// Shared defaults for the vision (image understanding) requests.
enum VisionRequestDefaults {
    /// Dense screenshots regularly exceed the old 2048/4096 caps.
    static let maxOutputTokens = 8192
    static let systemPrompt = """
        你是截图内容识别助手。只依据图片中实际可见的内容作答，严格按用户要求的格式直接输出结果，不要寒暄、不要复述任务，也不要编造看不清的内容。
        """
}

public struct OpenAICompatibleVisionClient: @unchecked Sendable {
    private let session: URLSession
    let retryBaseDelay: TimeInterval

    public init(session: URLSession = .shared) {
        self.init(session: session, retryBaseDelay: ProviderHTTP.defaultBaseDelay)
    }

    init(session: URLSession, retryBaseDelay: TimeInterval) {
        self.session = session
        self.retryBaseDelay = retryBaseDelay
    }

    /// Recognises the image. When the model stopped at its output limit the
    /// returned text ends with `ProviderOutputNotice.truncated`.
    public func recognize(
        baseURL: String,
        model: String,
        apiKey: String,
        imageData: Data,
        mimeType: String = "image/jpeg",
        prompt: String
    ) async throws -> String {
        let trimmedModel = model.trimmingCharacters(in: .whitespacesAndNewlines)
        let trimmedKey = apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedModel.isEmpty else { throw VisionClientError.missingModel }
        let endpoint = try endpointURL(from: baseURL)
        // Self-hosted servers on this Mac / LAN usually run without a key.
        guard !trimmedKey.isEmpty || ProviderEndpointValidator.isLocalHost(endpoint.host ?? "") else {
            throw VisionClientError.missingAPIKey
        }

        do {
            let outcome = try await ProviderChatEngine.complete(
                ProviderChatRequest(
                    provider: .openAICompatible,
                    endpoint: endpoint,
                    model: trimmedModel,
                    apiKey: trimmedKey,
                    systemPrompt: VisionRequestDefaults.systemPrompt,
                    prompt: prompt,
                    imageData: imageData,
                    mimeType: mimeType,
                    maxTokens: VisionRequestDefaults.maxOutputTokens,
                    timeout: 90
                ),
                session: session,
                retryBaseDelay: retryBaseDelay
            )
            return ProviderOutputNotice.appending(to: outcome.text, truncated: outcome.truncated)
        } catch let failure as ProviderChatFailure {
            throw VisionClientError(failure)
        }
    }

    private func endpointURL(from rawValue: String) throws -> URL {
        let trimmed = rawValue.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else {
            throw VisionClientError.invalidEndpoint("请先配置 Base URL。")
        }
        if let validation = ProviderEndpointValidator().validationMessage(for: trimmed) {
            throw VisionClientError.invalidEndpoint(validation)
        }
        guard var components = URLComponents(string: trimmed), components.url != nil else {
            throw VisionClientError.invalidEndpoint("Base URL 格式无效。")
        }
        components.path = ProviderEndpointPath.resolved(
            provider: .openAICompatible,
            path: components.path,
            model: ""
        )
        guard let url = components.url else {
            throw VisionClientError.invalidEndpoint("Base URL 格式无效。")
        }
        return url
    }
}
