@preconcurrency import Foundation

public enum VisionProviderKind: String, CaseIterable, Codable, Sendable {
    case openAICompatible
    case azureOpenAI
    case anthropic
    case googleGemini

    public var displayName: String {
        switch self {
        case .openAICompatible: "OpenAI-compatible"
        case .azureOpenAI: "Azure OpenAI"
        case .anthropic: "Anthropic Claude"
        case .googleGemini: "Google Gemini"
        }
    }
}

public enum MultimodalTaskTemplate: String, CaseIterable, Codable, Sendable {
    case general
    case extractText
    case translateChinese
    case translateEnglish
    case explainCode
    case tableMarkdown
    case tableCSV
    case formulaLaTeX

    public var displayName: String {
        switch self {
        case .general: "通用识图"
        case .extractText: "精确取字"
        case .translateChinese: "翻译成中文"
        case .translateEnglish: "翻译成英文"
        case .explainCode: "提取并解释代码"
        case .tableMarkdown: "表格转 Markdown"
        case .tableCSV: "表格转 CSV"
        case .formulaLaTeX: "公式转 LaTeX"
        }
    }

    public var prompt: String { prompt(responseLanguage: nil) }

    /// The task prompt. `responseLanguage` is the language explanations
    /// should be written in (a display name such as "English"); nil or
    /// blank keeps the default, Chinese.
    public func prompt(responseLanguage: String?) -> String {
        let requested = responseLanguage?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        let explanationLanguage = requested.isEmpty ? "中文" : requested
        switch self {
        case .general:
            return """
            这是视觉理解任务，不是单纯的文字识别。先判断截图类型：如果截图以文字为主（文档、网页、聊天记录、代码、表格等），直接准确整理其中的文字，保留原语言、段落、列表、代码和表格结构，不需要再描述画面；如果是照片、插图、图表或界面等以画面为主的内容，请描述图片中实际可见的主体、人物或动物、物体、场景、动作、颜色与构图，再整理其中的文字。即使图片完全没有文字，也必须说明画面内容，不能只回答“没有文字”。直接输出结果，不要猜测看不清的内容。
            """
        case .extractText:
            return "逐字提取截图中的全部可见文字，保持阅读顺序、段落和换行；保留代码与列表的缩进；表格按行列输出为 Markdown 表格；看不清的片段用 [无法辨认] 标出，不要猜测。不要总结、翻译或补写。"
        case .translateChinese:
            return "识别截图中的内容并翻译成自然、准确的中文；代码、专有名词和数字保持原意。只输出译文。"
        case .translateEnglish:
            return "识别截图中的内容并翻译成自然、准确的英文；代码、专有名词和数字保持原意。只输出译文。"
        case .explainCode:
            return "提取截图中的代码，先输出可复制的完整代码块（保留缩进），再用简洁的\(explanationLanguage)说明语言、用途和明显问题；不要虚构被遮挡的代码。"
        case .tableMarkdown:
            return "识别截图中的表格，严格按行列输出为 Markdown 表格。合并单元格用最接近的重复值表达，不要输出额外说明。"
        case .tableCSV:
            return "识别截图中的表格并输出合法 CSV。正确转义逗号、引号和换行，只输出 CSV 内容。"
        case .formulaLaTeX:
            return "识别截图中的数学公式并输出可复制的 LaTeX；多行公式使用 aligned 环境。如果公式与普通文字混排，文字原样保留，行内公式用 $...$、独立公式用 $$...$$ 包裹；如果只有公式，只输出 LaTeX 本身。不要解释或猜测模糊符号。"
        }
    }
}

public struct MultimodalProviderClient: @unchecked Sendable {
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
        provider: VisionProviderKind,
        baseURL: String,
        model: String,
        apiKey: String,
        imageData: Data,
        mimeType: String = "image/jpeg",
        prompt: String
    ) async throws -> String {
        if provider == .openAICompatible {
            return try await OpenAICompatibleVisionClient(
                session: session,
                retryBaseDelay: retryBaseDelay
            ).recognize(
                baseURL: baseURL,
                model: model,
                apiKey: apiKey,
                imageData: imageData,
                mimeType: mimeType,
                prompt: prompt
            )
        }

        let trimmedModel = model.trimmingCharacters(in: .whitespacesAndNewlines)
        let trimmedKey = apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedModel.isEmpty else { throw VisionClientError.missingModel }
        guard !trimmedKey.isEmpty else { throw VisionClientError.missingAPIKey }
        let endpoint = try endpoint(provider: provider, baseURL: baseURL, model: trimmedModel)

        do {
            let outcome = try await ProviderChatEngine.complete(
                ProviderChatRequest(
                    provider: provider,
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

    private func endpoint(provider: VisionProviderKind, baseURL: String, model: String) throws -> URL {
        let trimmed = baseURL.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { throw VisionClientError.invalidEndpoint("请先配置 Base URL。") }
        if let validation = ProviderEndpointValidator().validationMessage(for: trimmed) {
            throw VisionClientError.invalidEndpoint(validation)
        }
        guard var components = URLComponents(string: trimmed) else {
            throw VisionClientError.invalidEndpoint("Base URL 格式无效。")
        }
        components.path = ProviderEndpointPath.resolved(provider: provider, path: components.path, model: model)
        guard let url = components.url else { throw VisionClientError.invalidEndpoint("Base URL 格式无效。") }
        return url
    }
}
