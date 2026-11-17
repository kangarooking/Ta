import Foundation
import XCTest
@testable import AIScreenshotCore

final class TranslationProviderClientTests: XCTestCase {
    override func tearDown() {
        TranslationURLProtocol.handler = nil
        super.tearDown()
    }

    func testDeepSeekTextTranslationUsesFullEndpointAndDisablesThinking() async throws {
        TranslationURLProtocol.handler = { request in
            XCTAssertEqual(request.url?.absoluteString, "https://api.deepseek.com/chat/completions")
            XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer secret")
            let json = try XCTUnwrap(Self.jsonBody(request))
            XCTAssertEqual(json["model"] as? String, "deepseek-v4-flash")
            XCTAssertEqual((json["thinking"] as? [String: Any])?["type"] as? String, "disabled")
            let messages = try XCTUnwrap(json["messages"] as? [[String: Any]])
            XCTAssertEqual(messages.first?["role"] as? String, "system")
            let prompt = try XCTUnwrap(messages.last?["content"] as? String)
            // "自动检测" is a UI label, not a language the model should translate from.
            XCTAssertFalse(prompt.contains("自动检测"))
            XCTAssertTrue(prompt.contains("Detect the source language"))
            XCTAssertTrue(prompt.contains("简体中文"))
            XCTAssertTrue(prompt.contains("Screenshot translation"))
            return Self.response(for: request, content: "截图翻译")
        }

        let result = try await makeClient().translateText(
            baseURL: "https://api.deepseek.com/chat/completions，",
            model: "deepseek-v4-flash",
            apiKey: "secret",
            text: "Screenshot translation",
            sourceLanguage: "自动检测",
            targetLanguage: "简体中文"
        )
        XCTAssertEqual(result, "截图翻译")
    }

    func testSegmentTranslationRequiresEveryStableID() async throws {
        TranslationURLProtocol.handler = { request in
            let json = try XCTUnwrap(Self.jsonBody(request))
            XCTAssertEqual((json["response_format"] as? [String: Any])?["type"] as? String, "json_object")
            return Self.response(
                for: request,
                content: #"{"translations":[{"id":1,"text":"世界"},{"id":0,"text":"你好"}]}"#
            )
        }
        let results = try await makeClient().translateSegments(
            baseURL: "https://api.example.com/v1",
            model: "translator",
            apiKey: "secret",
            segments: [
                TranslationSourceSegment(id: 0, text: "Hello"),
                TranslationSourceSegment(id: 1, text: "World")
            ],
            sourceLanguage: "英文",
            targetLanguage: "简体中文"
        )
        XCTAssertEqual(results, [
            TranslationSegmentResult(id: 0, text: "你好"),
            TranslationSegmentResult(id: 1, text: "世界")
        ])
    }

    func testVisionTranslationEmbedsImageDataURL() async throws {
        TranslationURLProtocol.handler = { request in
            let json = try XCTUnwrap(Self.jsonBody(request))
            let messages = try XCTUnwrap(json["messages"] as? [[String: Any]])
            let content = try XCTUnwrap(messages.last?["content"] as? [[String: Any]])
            let imagePart = try XCTUnwrap(content.first { ($0["type"] as? String) == "image_url" })
            let imageURL = try XCTUnwrap(imagePart["image_url"] as? [String: Any])
            XCTAssertEqual(imageURL["url"] as? String, "data:image/png;base64,AQID")
            return Self.response(for: request, content: "视觉译文")
        }
        let result = try await makeClient().translateImage(
            baseURL: "https://api.deepseek.com",
            model: "deepseek-v4-flash-vision-exp",
            apiKey: "secret",
            imageData: Data([1, 2, 3]),
            sourceLanguage: "自动检测",
            targetLanguage: "简体中文"
        )
        XCTAssertEqual(result, "视觉译文")
    }

    func testAnthropicTranslationUsesMessagesProtocol() async throws {
        TranslationURLProtocol.handler = { request in
            XCTAssertEqual(request.url?.absoluteString, "https://api.anthropic.com/v1/messages")
            XCTAssertEqual(request.value(forHTTPHeaderField: "x-api-key"), "secret")
            XCTAssertEqual(request.value(forHTTPHeaderField: "anthropic-version"), "2023-06-01")
            let json = try XCTUnwrap(Self.jsonBody(request))
            XCTAssertEqual(json["model"] as? String, "claude-sonnet")
            // Newer Claude models reject sampling parameters.
            XCTAssertNil(json["temperature"])
            XCTAssertNotNil(json["system"] as? String)
            let messages = try XCTUnwrap(json["messages"] as? [[String: Any]])
            let content = try XCTUnwrap(messages.first?["content"] as? [[String: Any]])
            XCTAssertEqual(content.first?["type"] as? String, "text")
            return Self.anthropicResponse(for: request, content: "你好")
        }
        let result = try await makeClient().translateText(
            baseURL: "https://api.anthropic.com",
            model: "claude-sonnet",
            apiKey: "secret",
            text: "Hello",
            sourceLanguage: "英文",
            targetLanguage: "简体中文",
            provider: .anthropic
        )
        XCTAssertEqual(result, "你好")
    }

    func testGeminiVisionTranslationUsesInlineData() async throws {
        TranslationURLProtocol.handler = { request in
            XCTAssertEqual(
                request.url?.absoluteString,
                "https://generativelanguage.googleapis.com/v1beta/models/gemini-vision:generateContent"
            )
            XCTAssertEqual(request.value(forHTTPHeaderField: "x-goog-api-key"), "secret")
            let json = try XCTUnwrap(Self.jsonBody(request))
            let contents = try XCTUnwrap(json["contents"] as? [[String: Any]])
            let parts = try XCTUnwrap(contents.first?["parts"] as? [[String: Any]])
            let inlineData = try XCTUnwrap(parts.first?["inlineData"] as? [String: Any])
            XCTAssertEqual(inlineData["data"] as? String, "AQID")
            return Self.geminiResponse(for: request, content: "视觉译文")
        }
        let result = try await makeClient().translateImage(
            baseURL: "https://generativelanguage.googleapis.com",
            model: "gemini-vision",
            apiKey: "secret",
            imageData: Data([1, 2, 3]),
            sourceLanguage: "自动检测",
            targetLanguage: "简体中文",
            provider: .googleGemini
        )
        XCTAssertEqual(result, "视觉译文")
    }

    func testAzureTranslationUsesAPIKeyHeaderAndAzureEndpoint() async throws {
        TranslationURLProtocol.handler = { request in
            XCTAssertEqual(
                request.url?.absoluteString,
                "https://example.openai.azure.com/openai/v1/chat/completions"
            )
            XCTAssertEqual(request.value(forHTTPHeaderField: "api-key"), "secret")
            XCTAssertNil(request.value(forHTTPHeaderField: "Authorization"))
            return Self.response(for: request, content: "OK")
        }
        let result = try await makeClient().testConnection(
            baseURL: "https://example.openai.azure.com",
            model: "deployment-name",
            apiKey: "secret",
            provider: .azureOpenAI
        )
        XCTAssertEqual(result, "OK")
    }

    func testZhipuConnectionTestDisablesThinkingAndRaisesTokenBudget() async throws {
        TranslationURLProtocol.handler = { request in
            XCTAssertEqual(request.url?.absoluteString, "https://open.bigmodel.cn/api/paas/v4/chat/completions")
            let json = try XCTUnwrap(Self.jsonBody(request))
            XCTAssertEqual(json["model"] as? String, "glm-4.6")
            XCTAssertEqual((json["thinking"] as? [String: Any])?["type"] as? String, "disabled")
            let messages = try XCTUnwrap(json["messages"] as? [[String: Any]])
            XCTAssertEqual(messages.first?["content"] as? String, "Reply with exactly: OK")
            let maxTokens = try XCTUnwrap(json["max_tokens"] as? Int)
            XCTAssertGreaterThanOrEqual(maxTokens, 256)
            return Self.response(for: request, content: "OK")
        }
        let result = try await makeClient().testConnection(
            baseURL: "https://open.bigmodel.cn/api/paas/v4",
            model: "glm-4.6",
            apiKey: "secret"
        )
        XCTAssertEqual(result, "OK")
    }

    func testReasoningOnlyLengthTruncatedResponseExplainsGuidance() async throws {
        TranslationURLProtocol.handler = { request in
            let payload: [String: Any] = [
                "choices": [[
                    "message": [
                        "content": "",
                        "reasoning_content": "Breaking the sentence down before translating..."
                    ],
                    "finish_reason": "length"
                ]]
            ]
            return (
                HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!,
                try! JSONSerialization.data(withJSONObject: payload)
            )
        }
        do {
            _ = try await makeClient().translateText(
                baseURL: "https://open.bigmodel.cn/api/paas/v4",
                model: "glm-4.6",
                apiKey: "secret",
                text: "Hello",
                sourceLanguage: "英文",
                targetLanguage: "简体中文"
            )
            XCTFail("Expected reasoning-only error")
        } catch let error as TranslationProviderError {
            XCTAssertEqual(error, .reasoningOnlyOutput(truncated: true))
        } catch {
            XCTFail("Unexpected error: \(error)")
        }
    }

    func testExplicitSourceLanguageIsNamedInPrompt() {
        XCTAssertEqual(
            TranslationProviderClient.translationDirective(source: "英文", target: "简体中文", subject: "it"),
            "Translate it from 英文 to 简体中文."
        )
        XCTAssertTrue(TranslationProviderClient.isAutomaticSource("自动检测"))
        XCTAssertTrue(TranslationProviderClient.isAutomaticSource(" Auto "))
        XCTAssertTrue(TranslationProviderClient.isAutomaticSource(""))
        XCTAssertFalse(TranslationProviderClient.isAutomaticSource("日文"))
    }

    func testSegmentPromptExplainsThatItemsAreConsecutiveLines() throws {
        let prompt = try TranslationProviderClient.segmentPrompt(
            segments: [TranslationSourceSegment(id: 0, text: "Hello")],
            sourceLanguage: "自动检测",
            targetLanguage: "简体中文"
        )
        XCTAssertTrue(prompt.contains("consecutive lines of one passage"))
        XCTAssertTrue(prompt.contains("Detect the source language"))
        XCTAssertFalse(prompt.contains("自动检测"))
    }

    func testSegmentDecodingToleratesFencesProseAndStringIDs() {
        let fenced = """
        Here is the result:
        ```json
        {"translations":[{"id":"0","text":"你好"},{"id":1.0,"text":"世界"}]}
        ```
        """
        XCTAssertEqual(TranslationProviderClient.decodeSegments(fenced), [0: "你好", 1: "世界"])
        XCTAssertEqual(
            TranslationProviderClient.decodeSegments(#"Sure! [{"id": 3, "translation": "好"}] Done."#),
            [3: "好"]
        )
        XCTAssertEqual(TranslationProviderClient.decodeSegments(#"{"0":"甲","1":"乙"}"#), [0: "甲", 1: "乙"])
        XCTAssertEqual(TranslationProviderClient.decodeSegments("not json"), [:])
    }

    func testOmittedSegmentFallsBackToOriginalText() async throws {
        TranslationURLProtocol.handler = { request in
            Self.response(for: request, content: #"{"translations":[{"id":0,"text":"你好"}]}"#)
        }
        let results = try await makeClient().translateSegments(
            baseURL: "https://api.example.com/v1",
            model: "translator",
            apiKey: "secret",
            segments: [
                TranslationSourceSegment(id: 0, text: "Hello"),
                TranslationSourceSegment(id: 1, text: "World")
            ],
            sourceLanguage: "英文",
            targetLanguage: "简体中文"
        )
        XCTAssertEqual(results, [
            TranslationSegmentResult(id: 0, text: "你好"),
            TranslationSegmentResult(id: 1, text: "World")
        ])
    }

    func testUnusableSegmentAnswerStillFailsWhenNothingWasTranslated() async {
        TranslationURLProtocol.handler = { request in
            Self.response(for: request, content: "I cannot do that.")
        }
        do {
            _ = try await makeClient().translateSegments(
                baseURL: "https://api.example.com/v1",
                model: "translator",
                apiKey: "secret",
                segments: [TranslationSourceSegment(id: 0, text: "Hello")],
                sourceLanguage: "英文",
                targetLanguage: "简体中文"
            )
            XCTFail("Expected invalid segment response")
        } catch let error as TranslationProviderError {
            XCTAssertEqual(error, .invalidSegmentResponse)
        } catch {
            XCTFail("Unexpected error: \(error)")
        }
    }

    func testLargeSegmentListsAreTranslatedInSequentialBatches() async throws {
        let batchSizes = Counter()
        TranslationURLProtocol.handler = { request in
            let json = try XCTUnwrap(Self.jsonBody(request))
            let messages = try XCTUnwrap(json["messages"] as? [[String: Any]])
            let prompt = try XCTUnwrap(messages.last?["content"] as? String)
            let inputJSON = try XCTUnwrap(prompt.components(separatedBy: "Input JSON:\n").last)
            let items = try XCTUnwrap(
                JSONSerialization.jsonObject(with: Data(inputJSON.utf8)) as? [[String: Any]]
            )
            batchSizes.append(items.count)
            let translations = items.map { item -> [String: Any] in
                ["id": item["id"] as? Int ?? -1, "text": "译\(item["id"] as? Int ?? -1)"]
            }
            let content = String(
                decoding: try JSONSerialization.data(withJSONObject: ["translations": translations]),
                as: UTF8.self
            )
            return Self.response(for: request, content: content)
        }
        let segments = (0..<100).map { TranslationSourceSegment(id: $0, text: "Line \($0)") }

        let results = try await makeClient().translateSegments(
            baseURL: "https://api.example.com/v1",
            model: "translator",
            apiKey: "secret",
            segments: segments,
            sourceLanguage: "英文",
            targetLanguage: "简体中文"
        )

        XCTAssertEqual(batchSizes.values, [40, 40, 20])
        XCTAssertEqual(results.map(\.id), Array(0..<100))
        XCTAssertEqual(results[57].text, "译57")
    }

    func testBatchesRespectCharacterBudget() {
        let long = String(repeating: "x", count: 1000)
        let batches = TranslationProviderClient.batches(
            of: (0..<5).map { TranslationSourceSegment(id: $0, text: long) }
        )
        XCTAssertEqual(batches.map(\.count), [2, 2, 1])
    }

    func testTruncatedTranslationCarriesNotice() async throws {
        TranslationURLProtocol.handler = { request in
            Self.response(for: request, content: "被截断的译文", finishReason: "length")
        }
        let result = try await makeClient().translateText(
            baseURL: "https://api.example.com/v1",
            model: "translator",
            apiKey: "secret",
            text: "A very long text",
            sourceLanguage: "英文",
            targetLanguage: "简体中文"
        )
        XCTAssertTrue(result.hasPrefix("被截断的译文"))
        XCTAssertTrue(ProviderOutputNotice.isTruncated(result))
        XCTAssertEqual(ProviderOutputNotice.removingNotice(from: result), "被截断的译文")
    }

    func testRetriesRateLimitHonouringRetryAfter() async throws {
        let attempts = Counter()
        TranslationURLProtocol.handler = { request in
            attempts.append(1)
            if attempts.values.count == 1 {
                return (
                    HTTPURLResponse(
                        url: request.url!,
                        statusCode: 429,
                        httpVersion: nil,
                        headerFields: ["Retry-After": "0"]
                    )!,
                    Data(#"{"error":{"message":"slow down"}}"#.utf8)
                )
            }
            return Self.response(for: request, content: "好")
        }
        let result = try await makeClient().translateText(
            baseURL: "https://api.example.com/v1",
            model: "translator",
            apiKey: "secret",
            text: "OK",
            sourceLanguage: "英文",
            targetLanguage: "简体中文"
        )
        XCTAssertEqual(result, "好")
        XCTAssertEqual(attempts.values.count, 2)
    }

    func testGivesUpAfterTwoRetriesOnServerErrors() async {
        let attempts = Counter()
        TranslationURLProtocol.handler = { request in
            attempts.append(1)
            return (
                HTTPURLResponse(url: request.url!, statusCode: 503, httpVersion: nil, headerFields: nil)!,
                Data(#"{"error":{"message":"overloaded"}}"#.utf8)
            )
        }
        do {
            _ = try await makeClient().testConnection(
                baseURL: "https://api.example.com/v1",
                model: "translator",
                apiKey: "secret"
            )
            XCTFail("Expected server error")
        } catch let error as TranslationProviderError {
            XCTAssertEqual(error, .server(statusCode: 503, message: "overloaded"))
            XCTAssertEqual(attempts.values.count, 3)
        } catch {
            XCTFail("Unexpected error: \(error)")
        }
    }

    func testRetriesWithoutParametersTheServerRejects() async throws {
        let bodies = BodyRecorder()
        TranslationURLProtocol.handler = { request in
            let json = try XCTUnwrap(Self.jsonBody(request))
            bodies.append(json)
            let message: String?
            if json["temperature"] != nil {
                message = "Unsupported value: 'temperature' does not support 0 with this model."
            } else if json["max_tokens"] != nil {
                message = "Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead."
            } else if json["response_format"] != nil {
                message = "response_format json_object is not supported by this gateway"
            } else {
                message = nil
            }
            if let message {
                let data = try JSONSerialization.data(withJSONObject: ["error": ["message": message]])
                return (
                    HTTPURLResponse(url: request.url!, statusCode: 400, httpVersion: nil, headerFields: nil)!,
                    data
                )
            }
            return Self.response(for: request, content: #"{"translations":[{"id":0,"text":"你好"}]}"#)
        }
        let results = try await makeClient().translateSegments(
            baseURL: "https://api.example.com/v1",
            model: "reasoning-model",
            apiKey: "secret",
            segments: [TranslationSourceSegment(id: 0, text: "Hello")],
            sourceLanguage: "英文",
            targetLanguage: "简体中文"
        )
        XCTAssertEqual(results, [TranslationSegmentResult(id: 0, text: "你好")])
        XCTAssertEqual(bodies.values.count, 4)
        let last = try XCTUnwrap(bodies.values.last)
        XCTAssertNil(last["temperature"])
        XCTAssertNil(last["max_tokens"])
        XCTAssertNil(last["response_format"])
        XCTAssertEqual(last["max_completion_tokens"] as? Int, 8192)
    }

    func testLocalEndpointWorksWithoutAPIKey() async throws {
        TranslationURLProtocol.handler = { request in
            XCTAssertEqual(request.url?.absoluteString, "http://localhost:11434/v1/chat/completions")
            XCTAssertNil(request.value(forHTTPHeaderField: "Authorization"))
            return Self.response(for: request, content: "OK")
        }
        let result = try await makeClient().testConnection(
            baseURL: "http://localhost:11434/v1",
            model: "qwen3",
            apiKey: ""
        )
        XCTAssertEqual(result, "OK")
    }

    func testRemoteEndpointStillRequiresAPIKey() async {
        do {
            _ = try await makeClient().testConnection(
                baseURL: "https://api.example.com/v1",
                model: "translator",
                apiKey: " "
            )
            XCTFail("Expected missing key")
        } catch let error as TranslationProviderError {
            XCTAssertEqual(error, .missingAPIKey)
        } catch {
            XCTFail("Unexpected error: \(error)")
        }
    }

    func testAnthropicBaseURLEndingInV1IsNotDuplicated() async throws {
        TranslationURLProtocol.handler = { request in
            XCTAssertEqual(request.url?.absoluteString, "https://api.anthropic.com/v1/messages")
            let data = try JSONSerialization.data(withJSONObject: [
                "content": [["type": "text", "text": "半截"]],
                "stop_reason": "max_tokens"
            ])
            return (HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!, data)
        }
        let result = try await makeClient().translateText(
            baseURL: "https://api.anthropic.com/v1/",
            model: "claude-sonnet",
            apiKey: "secret",
            text: "Hello",
            sourceLanguage: "英文",
            targetLanguage: "简体中文",
            provider: .anthropic
        )
        XCTAssertTrue(ProviderOutputNotice.isTruncated(result))
    }

    private func makeClient() -> TranslationProviderClient {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [TranslationURLProtocol.self]
        return TranslationProviderClient(session: URLSession(configuration: configuration), retryBaseDelay: 0)
    }

    private static func jsonBody(_ request: URLRequest) -> [String: Any]? {
        guard let data = bodyData(from: request) else { return nil }
        return try? JSONSerialization.jsonObject(with: data) as? [String: Any]
    }

    private static func bodyData(from request: URLRequest) -> Data? {
        if let body = request.httpBody { return body }
        guard let stream = request.httpBodyStream else { return nil }
        stream.open()
        defer { stream.close() }
        var data = Data()
        var buffer = [UInt8](repeating: 0, count: 4096)
        while stream.hasBytesAvailable {
            let count = stream.read(&buffer, maxLength: buffer.count)
            guard count >= 0 else { return nil }
            if count == 0 { break }
            data.append(buffer, count: count)
        }
        return data
    }

    private static func response(
        for request: URLRequest,
        content: String,
        finishReason: String = "stop"
    ) -> (HTTPURLResponse, Data) {
        let escaped = try! JSONSerialization.data(withJSONObject: [
            "choices": [["message": ["content": content], "finish_reason": finishReason]]
        ])
        return (
            HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!,
            escaped
        )
    }

    private static func anthropicResponse(for request: URLRequest, content: String) -> (HTTPURLResponse, Data) {
        let data = try! JSONSerialization.data(withJSONObject: [
            "content": [["type": "text", "text": content]]
        ])
        return (
            HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!,
            data
        )
    }

    private static func geminiResponse(for request: URLRequest, content: String) -> (HTTPURLResponse, Data) {
        let data = try! JSONSerialization.data(withJSONObject: [
            "candidates": [["content": ["parts": [["text": content]]]]]
        ])
        return (
            HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!,
            data
        )
    }
}

private final class TranslationURLProtocol: URLProtocol, @unchecked Sendable {
    nonisolated(unsafe) static var handler: ((URLRequest) throws -> (HTTPURLResponse, Data))?

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        do {
            guard let handler = Self.handler else { throw TranslationURLProtocolError.missingHandler }
            let (response, data) = try handler(request)
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: data)
            client?.urlProtocolDidFinishLoading(self)
        } catch {
            client?.urlProtocol(self, didFailWithError: error)
        }
    }

    override func stopLoading() {}
}

private final class Counter: @unchecked Sendable {
    private let lock = NSLock()
    private var storage: [Int] = []

    var values: [Int] {
        lock.lock()
        defer { lock.unlock() }
        return storage
    }

    func append(_ value: Int) {
        lock.lock()
        storage.append(value)
        lock.unlock()
    }
}

private final class BodyRecorder: @unchecked Sendable {
    private let lock = NSLock()
    private var storage: [[String: Any]] = []

    var values: [[String: Any]] {
        lock.lock()
        defer { lock.unlock() }
        return storage
    }

    func append(_ value: [String: Any]) {
        lock.lock()
        storage.append(value)
        lock.unlock()
    }
}

private enum TranslationURLProtocolError: Error {
    case missingHandler
}
