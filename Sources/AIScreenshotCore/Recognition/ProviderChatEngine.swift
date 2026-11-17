@preconcurrency import Foundation

/// The notice appended to provider output that was cut off by the model's
/// output-token limit, so a truncated result is never reported as complete.
public enum ProviderOutputNotice {
    public static let truncated = "⚠️ 结果因长度上限被截断，内容可能不完整。"

    /// True when `text` ends with the truncation notice.
    public static func isTruncated(_ text: String) -> Bool {
        text.trimmingCharacters(in: .whitespacesAndNewlines).hasSuffix(truncated)
    }

    /// `text` without the trailing truncation notice (unchanged if absent).
    public static func removingNotice(from text: String) -> String {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.hasSuffix(truncated) else { return text }
        return String(trimmed.dropLast(truncated.count))
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    static func appending(to text: String, truncated isTruncated: Bool) -> String {
        isTruncated ? text + "\n\n" + truncated : text
    }
}

// MARK: - HTTP with bounded retries

enum ProviderHTTP {
    static let maximumRetries = 2
    static let defaultBaseDelay: TimeInterval = 0.8
    static let maximumDelay: TimeInterval = 20

    /// Sends `request`, retrying at most `maximumRetries` times on HTTP 429,
    /// 5xx and transient network failures. Honours `Retry-After` and task
    /// cancellation (both between attempts and while waiting).
    static func data(
        for request: URLRequest,
        session: URLSession,
        baseDelay: TimeInterval = defaultBaseDelay
    ) async throws -> (Data, URLResponse) {
        var attempt = 0
        while true {
            try Task.checkCancellation()
            do {
                let (data, response) = try await session.data(for: request)
                guard attempt < maximumRetries,
                      let http = response as? HTTPURLResponse,
                      isRetryable(statusCode: http.statusCode) else {
                    return (data, response)
                }
                try await pause(retryDelay(
                    attempt: attempt,
                    retryAfter: http.value(forHTTPHeaderField: "Retry-After"),
                    baseDelay: baseDelay
                ))
            } catch let error as URLError where attempt < maximumRetries && isTransient(error.code) {
                try await pause(retryDelay(attempt: attempt, retryAfter: nil, baseDelay: baseDelay))
            }
            attempt += 1
        }
    }

    static func isRetryable(statusCode: Int) -> Bool {
        statusCode == 429 || (500...599).contains(statusCode)
    }

    /// Request timeouts are deliberately not retried: with long model
    /// timeouts that would multiply the wait instead of recovering.
    static func isTransient(_ code: URLError.Code) -> Bool {
        switch code {
        case .networkConnectionLost, .cannotConnectToHost, .cannotFindHost,
             .dnsLookupFailed, .notConnectedToInternet, .secureConnectionFailed:
            return true
        default:
            return false
        }
    }

    static func retryDelay(
        attempt: Int,
        retryAfter: String?,
        baseDelay: TimeInterval,
        now: Date = Date()
    ) -> TimeInterval {
        if let retryAfter = retryAfter?.trimmingCharacters(in: .whitespaces), !retryAfter.isEmpty {
            if let seconds = TimeInterval(retryAfter) {
                return min(max(0, seconds), maximumDelay)
            }
            let formatter = DateFormatter()
            formatter.locale = Locale(identifier: "en_US_POSIX")
            formatter.timeZone = TimeZone(identifier: "GMT")
            formatter.dateFormat = "EEE, dd MMM yyyy HH:mm:ss zzz"
            if let date = formatter.date(from: retryAfter) {
                return min(max(0, date.timeIntervalSince(now)), maximumDelay)
            }
        }
        return min(max(0, baseDelay) * pow(2, Double(attempt)), maximumDelay)
    }

    private static func pause(_ seconds: TimeInterval) async throws {
        guard seconds > 0 else {
            try Task.checkCancellation()
            return
        }
        try await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
    }
}

// MARK: - Endpoint paths

enum ProviderEndpointPath {
    /// Appends the provider's API route to a user-supplied base path without
    /// duplicating segments the user already typed (`/v1`, `/v1beta`, ...).
    static func resolved(provider: VisionProviderKind, path rawPath: String, model: String) -> String {
        let path = rawPath.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        func join(_ suffix: String) -> String {
            "/" + [path, suffix].filter { !$0.isEmpty }.joined(separator: "/")
        }
        func endsWithSegment(_ segment: String) -> Bool {
            path == segment || path.hasSuffix("/" + segment)
        }

        switch provider {
        case .openAICompatible:
            return path.hasSuffix("chat/completions") ? "/" + path : join("chat/completions")
        case .azureOpenAI:
            if path.hasSuffix("chat/completions") { return "/" + path }
            if path.hasSuffix("openai/v1") { return join("chat/completions") }
            if endsWithSegment("openai") { return join("v1/chat/completions") }
            return join("openai/v1/chat/completions")
        case .anthropic:
            if path.hasSuffix("v1/messages") { return "/" + path }
            if endsWithSegment("v1") { return join("messages") }
            return join("v1/messages")
        case .googleGemini:
            if path.contains(":generateContent") { return "/" + path }
            let modelName = model.hasPrefix("models/") ? String(model.dropFirst("models/".count)) : model
            if endsWithSegment("models") { return join("\(modelName):generateContent") }
            if endsWithSegment("v1beta") || endsWithSegment("v1") {
                return join("models/\(modelName):generateContent")
            }
            return join("v1beta/models/\(modelName):generateContent")
        }
    }
}

// MARK: - Chat completion engine

struct ProviderChatRequest {
    var provider: VisionProviderKind
    var endpoint: URL
    var model: String
    var apiKey: String
    var systemPrompt: String?
    var prompt: String
    var imageData: Data?
    var mimeType: String
    var jsonMode: Bool
    var maxTokens: Int
    var timeout: TimeInterval
    /// Sends an explicit `"stream": false` on OpenAI-style requests.
    var sendsStreamFlag: Bool

    init(
        provider: VisionProviderKind,
        endpoint: URL,
        model: String,
        apiKey: String,
        systemPrompt: String? = nil,
        prompt: String,
        imageData: Data? = nil,
        mimeType: String = "image/jpeg",
        jsonMode: Bool = false,
        maxTokens: Int,
        timeout: TimeInterval = 120,
        sendsStreamFlag: Bool = false
    ) {
        self.provider = provider
        self.endpoint = endpoint
        self.model = model
        self.apiKey = apiKey
        self.systemPrompt = systemPrompt
        self.prompt = prompt
        self.imageData = imageData
        self.mimeType = mimeType
        self.jsonMode = jsonMode
        self.maxTokens = maxTokens
        self.timeout = timeout
        self.sendsStreamFlag = sendsStreamFlag
    }
}

struct ProviderChatOutcome: Equatable {
    let text: String
    /// The model stopped because it hit its output-token limit.
    let truncated: Bool
}

enum ProviderChatFailure: Error, Equatable {
    case invalidResponse
    case emptyResponse
    case reasoningOnlyOutput(truncated: Bool)
    case server(statusCode: Int, message: String)
}

enum ProviderChatEngine {
    /// How many times one request may be re-sent with an adjusted body after
    /// the server rejected a parameter (each parameter is adjusted once).
    static let maximumAdaptations = 4

    static func complete(
        _ chat: ProviderChatRequest,
        session: URLSession,
        retryBaseDelay: TimeInterval = ProviderHTTP.defaultBaseDelay
    ) async throws -> ProviderChatOutcome {
        var request = URLRequest(url: chat.endpoint)
        request.httpMethod = "POST"
        request.timeoutInterval = chat.timeout
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        switch chat.provider {
        case .openAICompatible:
            // Local servers (Ollama, LM Studio, vLLM) take no key at all.
            if !chat.apiKey.isEmpty {
                request.setValue("Bearer \(chat.apiKey)", forHTTPHeaderField: "Authorization")
            }
        case .azureOpenAI:
            request.setValue(chat.apiKey, forHTTPHeaderField: "api-key")
        case .anthropic:
            request.setValue(chat.apiKey, forHTTPHeaderField: "x-api-key")
            request.setValue("2023-06-01", forHTTPHeaderField: "anthropic-version")
        case .googleGemini:
            request.setValue(chat.apiKey, forHTTPHeaderField: "x-goog-api-key")
        }

        var body = makeBody(chat)
        var adaptations = 0
        while true {
            request.httpBody = try JSONSerialization.data(withJSONObject: body)
            let (data, response) = try await ProviderHTTP.data(
                for: request,
                session: session,
                baseDelay: retryBaseDelay
            )
            guard let http = response as? HTTPURLResponse else {
                throw ProviderChatFailure.invalidResponse
            }
            if (200..<300).contains(http.statusCode) {
                return try parse(provider: chat.provider, data: data)
            }
            if http.statusCode == 400 || http.statusCode == 422,
               adaptations < maximumAdaptations,
               ProviderRequestAdapter.adapt(
                   &body,
                   provider: chat.provider,
                   errorText: String(decoding: data, as: UTF8.self)
               ) {
                adaptations += 1
                continue
            }
            throw ProviderChatFailure.server(
                statusCode: http.statusCode,
                message: errorMessage(from: data)
                    ?? HTTPURLResponse.localizedString(forStatusCode: http.statusCode)
            )
        }
    }

    // MARK: Request bodies

    static func makeBody(_ chat: ProviderChatRequest) -> [String: Any] {
        let system = chat.systemPrompt?.trimmingCharacters(in: .whitespacesAndNewlines)
        let systemPrompt = (system?.isEmpty == false) ? system : nil

        switch chat.provider {
        case .openAICompatible, .azureOpenAI:
            let userContent: Any
            if let imageData = chat.imageData {
                let dataURL = "data:\(chat.mimeType);base64,\(imageData.base64EncodedString())"
                let imageURL: [String: Any] = ["url": dataURL]
                let parts: [[String: Any]] = [
                    ["type": "text", "text": chat.prompt],
                    ["type": "image_url", "image_url": imageURL]
                ]
                userContent = parts
            } else {
                // Text-only OpenAI-compatible models (notably GLM text models)
                // expect the canonical string form. A multimodal parts array is
                // reserved for requests that actually contain an image.
                userContent = chat.prompt
            }
            var messages: [[String: Any]] = []
            if let systemPrompt {
                messages.append(["role": "system", "content": systemPrompt])
            }
            messages.append(["role": "user", "content": userContent])
            var body: [String: Any] = [
                "model": chat.model,
                "messages": messages,
                "temperature": 0,
                "max_tokens": chat.maxTokens
            ]
            if chat.sendsStreamFlag { body["stream"] = false }
            OpenAIChatResponseSupport.applyThinkingPreference(to: &body, host: chat.endpoint.host)
            if chat.jsonMode { body["response_format"] = ["type": "json_object"] }
            return body

        case .anthropic:
            var content: [[String: Any]] = []
            if let imageData = chat.imageData {
                content.append([
                    "type": "image",
                    "source": [
                        "type": "base64",
                        "media_type": chat.mimeType,
                        "data": imageData.base64EncodedString()
                    ] as [String: Any]
                ])
            }
            content.append(["type": "text", "text": chat.prompt])
            // No `temperature`: newer Claude models reject sampling parameters.
            let message: [String: Any] = ["role": "user", "content": content]
            var body: [String: Any] = [
                "model": chat.model,
                "max_tokens": chat.maxTokens,
                "messages": [message]
            ]
            if let systemPrompt { body["system"] = systemPrompt }
            return body

        case .googleGemini:
            var parts: [[String: Any]] = []
            if let imageData = chat.imageData {
                let inlineData: [String: Any] = [
                    "mimeType": chat.mimeType,
                    "data": imageData.base64EncodedString()
                ]
                parts.append(["inlineData": inlineData])
            }
            parts.append(["text": chat.prompt])
            let userContent: [String: Any] = ["role": "user", "parts": parts]
            let generationConfig: [String: Any] = ["temperature": 0, "maxOutputTokens": chat.maxTokens]
            var body: [String: Any] = [
                "contents": [userContent],
                "generationConfig": generationConfig
            ]
            if let systemPrompt {
                let instruction: [String: Any] = ["parts": [["text": systemPrompt]]]
                body["systemInstruction"] = instruction
            }
            return body
        }
    }

    // MARK: Responses

    static func parse(provider: VisionProviderKind, data: Data) throws -> ProviderChatOutcome {
        guard let object = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else {
            throw ProviderChatFailure.emptyResponse
        }
        switch provider {
        case .openAICompatible, .azureOpenAI:
            let truncated = OpenAIChatResponseSupport.isLengthTruncated(object)
            if let text = OpenAIChatResponseSupport.contentText(in: object) {
                return ProviderChatOutcome(text: text, truncated: truncated)
            }
            let hasReasoning = OpenAIChatResponseSupport.reasoningText(in: object) != nil
            if hasReasoning || truncated {
                // Reasoning models can spend the whole budget before the answer.
                throw ProviderChatFailure.reasoningOnlyOutput(truncated: truncated)
            }
            throw ProviderChatFailure.emptyResponse

        case .anthropic:
            let truncated = (object["stop_reason"] as? String)?.lowercased() == "max_tokens"
            let blocks = (object["content"] as? [[String: Any]]) ?? []
            let text = blocks
                .filter { ($0["type"] as? String ?? "text") == "text" }
                .compactMap { $0["text"] as? String }
                .joined(separator: "\n")
                .trimmingCharacters(in: .whitespacesAndNewlines)
            if !text.isEmpty { return ProviderChatOutcome(text: text, truncated: truncated) }
            if truncated { throw ProviderChatFailure.reasoningOnlyOutput(truncated: true) }
            throw ProviderChatFailure.emptyResponse

        case .googleGemini:
            let candidate = (object["candidates"] as? [[String: Any]])?.first
            let truncated = (candidate?["finishReason"] as? String)?.uppercased() == "MAX_TOKENS"
            let parts = ((candidate?["content"] as? [String: Any])?["parts"] as? [[String: Any]]) ?? []
            let text = parts
                .filter { ($0["thought"] as? Bool) != true }
                .compactMap { $0["text"] as? String }
                .joined(separator: "\n")
                .trimmingCharacters(in: .whitespacesAndNewlines)
            if !text.isEmpty { return ProviderChatOutcome(text: text, truncated: truncated) }
            // Gemini thinking models can burn the whole output budget on
            // thoughts and return no visible part at all.
            if truncated { throw ProviderChatFailure.reasoningOnlyOutput(truncated: true) }
            throw ProviderChatFailure.emptyResponse
        }
    }

    static func errorMessage(from data: Data) -> String? {
        guard let object = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else { return nil }
        if let error = object["error"] as? [String: Any] {
            return error["message"] as? String
                ?? error["detail"] as? String
                ?? error["status"] as? String
        }
        if let error = object["error"] as? String, !error.isEmpty { return error }
        return object["message"] as? String ?? object["detail"] as? String
    }
}

// MARK: - Graceful parameter fallback

/// Rewrites a request body after the server answered HTTP 400 for a
/// parameter it does not accept, so the request can be retried once without
/// it. Every rule removes or converts the offending parameter, so a rule can
/// never fire twice for the same request.
enum ProviderRequestAdapter {
    /// Phrases servers use when they do not accept a parameter at all.
    private static let parameterRejectionHints = [
        "unsupported", "not supported", "does not support", "doesn't support", "unknown",
        "unrecognized", "unrecognised", "unexpected", "not allowed", "not permitted",
        "extra inputs", "extra fields", "no longer supported", "deprecated"
    ]

    static func adapt(_ body: inout [String: Any], provider: VisionProviderKind, errorText: String) -> Bool {
        let text = errorText.lowercased()
        switch provider {
        case .openAICompatible, .azureOpenAI:
            return adaptOpenAI(&body, text: text)
        case .anthropic:
            if text.contains("max_tokens"), let cap = body["max_tokens"] as? Int, cap > 4096 {
                body["max_tokens"] = 4096
                return true
            }
            if text.contains("temperature"), body["temperature"] != nil {
                body.removeValue(forKey: "temperature")
                return true
            }
            if text.contains("system"), isRejection(text), let system = body["system"] as? String {
                body.removeValue(forKey: "system")
                foldIntoAnthropicUserMessage(system, body: &body)
                return true
            }
            return false
        case .googleGemini:
            return adaptGemini(&body, text: text)
        }
    }

    /// The server refuses the parameter itself (as opposed to its value).
    private static func isParameterRejection(_ text: String) -> Bool {
        parameterRejectionHints.contains { text.contains($0) }
    }

    private static func isRejection(_ text: String) -> Bool {
        isParameterRejection(text) || text.contains("invalid")
    }

    private static func adaptOpenAI(_ body: inout [String: Any], text: String) -> Bool {
        // OpenAI reasoning models: "Use 'max_completion_tokens' instead."
        if text.contains("max_completion_tokens"), let cap = body["max_tokens"] {
            body.removeValue(forKey: "max_tokens")
            body["max_completion_tokens"] = cap
            return true
        }
        if text.contains("max_tokens"), let cap = body["max_tokens"] as? Int {
            if isParameterRejection(text), !mentionsRange(text) {
                body.removeValue(forKey: "max_tokens")
                body["max_completion_tokens"] = cap
                return true
            }
            if cap > 4096 {
                body["max_tokens"] = 4096
                return true
            }
            if cap > 1024 {
                body["max_tokens"] = cap / 2
                return true
            }
            body.removeValue(forKey: "max_tokens")
            return true
        }
        if text.contains("max_completion_tokens"),
           let cap = body["max_completion_tokens"] as? Int, cap > 4096 {
            body["max_completion_tokens"] = 4096
            return true
        }
        if text.contains("context length") || text.contains("context window") || text.contains("maximum context") {
            for key in ["max_tokens", "max_completion_tokens"] {
                if let cap = body[key] as? Int, cap > 1024 {
                    body[key] = cap / 2
                    return true
                }
            }
        }
        if text.contains("temperature"), body["temperature"] != nil {
            body.removeValue(forKey: "temperature")
            return true
        }
        if text.contains("response_format") || text.contains("json_object") || text.contains("json mode"),
           body["response_format"] != nil {
            body.removeValue(forKey: "response_format")
            return true
        }
        if text.contains("thinking"), body["thinking"] != nil {
            body.removeValue(forKey: "thinking")
            return true
        }
        if text.contains("system"), isRejection(text),
           var messages = body["messages"] as? [[String: Any]],
           messages.count >= 2,
           messages[0]["role"] as? String == "system",
           let system = messages[0]["content"] as? String {
            messages.removeFirst()
            if let userText = messages[0]["content"] as? String {
                messages[0]["content"] = system + "\n\n" + userText
            } else if var parts = messages[0]["content"] as? [[String: Any]],
                      let index = parts.firstIndex(where: { $0["type"] as? String == "text" }),
                      let userText = parts[index]["text"] as? String {
                parts[index]["text"] = system + "\n\n" + userText
                messages[0]["content"] = parts
            }
            body["messages"] = messages
            return true
        }
        return false
    }

    private static func adaptGemini(_ body: inout [String: Any], text: String) -> Bool {
        let mentionsSystem = text.contains("system instruction") || text.contains("systeminstruction")
            || text.contains("system_instruction") || text.contains("developer instruction")
        if mentionsSystem,
           let instruction = body["systemInstruction"] as? [String: Any] {
            body.removeValue(forKey: "systemInstruction")
            let system = ((instruction["parts"] as? [[String: Any]]) ?? [])
                .compactMap { $0["text"] as? String }
                .joined(separator: "\n")
            if !system.isEmpty,
               var contents = body["contents"] as? [[String: Any]], !contents.isEmpty,
               var parts = contents[0]["parts"] as? [[String: Any]],
               let index = parts.firstIndex(where: { $0["text"] is String }),
               let userText = parts[index]["text"] as? String {
                parts[index]["text"] = system + "\n\n" + userText
                contents[0]["parts"] = parts
                body["contents"] = contents
            }
            return true
        }
        let mentionsTokens = text.contains("maxoutputtokens") || text.contains("max_output_tokens")
            || text.contains("max output tokens")
        if mentionsTokens,
           var config = body["generationConfig"] as? [String: Any],
           let cap = config["maxOutputTokens"] as? Int, cap > 4096 {
            config["maxOutputTokens"] = 4096
            body["generationConfig"] = config
            return true
        }
        return false
    }

    private static func foldIntoAnthropicUserMessage(_ system: String, body: inout [String: Any]) {
        guard var messages = body["messages"] as? [[String: Any]], !messages.isEmpty,
              var content = messages[0]["content"] as? [[String: Any]],
              let index = content.firstIndex(where: { $0["type"] as? String == "text" }),
              let userText = content[index]["text"] as? String else { return }
        content[index]["text"] = system + "\n\n" + userText
        messages[0]["content"] = content
        body["messages"] = messages
    }

    /// The server objects to the value (too large), not to the parameter.
    private static func mentionsRange(_ text: String) -> Bool {
        ["at most", "less than", "greater than", "exceed", "too large", "maximum", "must be <=", "range", "<="]
            .contains { text.contains($0) }
    }
}
