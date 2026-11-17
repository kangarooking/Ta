import Foundation
import XCTest
@testable import AIScreenshotCore

final class MultimodalProviderClientTests: XCTestCase {
    override func tearDown() {
        ProviderURLProtocol.handler = nil
        super.tearDown()
    }

    func testAnthropicUsesMessagesVisionContract() async throws {
        ProviderURLProtocol.handler = { request in
            XCTAssertEqual(request.url?.absoluteString, "https://api.anthropic.com/v1/messages")
            XCTAssertEqual(request.value(forHTTPHeaderField: "x-api-key"), "secret")
            XCTAssertEqual(request.value(forHTTPHeaderField: "anthropic-version"), "2023-06-01")
            let body = try XCTUnwrap(Self.jsonBody(request))
            let messages = try XCTUnwrap(body["messages"] as? [[String: Any]])
            let content = try XCTUnwrap(messages.first?["content"] as? [[String: Any]])
            XCTAssertNotNil(content.first { $0["type"] as? String == "image" })
            XCTAssertNil(body["temperature"])
            XCTAssertEqual(body["max_tokens"] as? Int, 8192)
            XCTAssertNotNil(body["system"] as? String)
            return Self.response(request, #"{"content":[{"type":"text","text":"Claude result"}]}"#)
        }

        let result = try await makeClient().recognize(
            provider: .anthropic,
            baseURL: "https://api.anthropic.com",
            model: "claude-vision",
            apiKey: "secret",
            imageData: Data([1, 2]),
            prompt: "read"
        )
        XCTAssertEqual(result, "Claude result")
    }

    func testGeminiUsesGenerateContentInlineDataContract() async throws {
        ProviderURLProtocol.handler = { request in
            XCTAssertEqual(request.url?.absoluteString, "https://generativelanguage.googleapis.com/v1beta/models/gemini-flash:generateContent")
            XCTAssertEqual(request.value(forHTTPHeaderField: "x-goog-api-key"), "secret")
            let body = try XCTUnwrap(Self.jsonBody(request))
            XCTAssertNotNil(body["contents"])
            return Self.response(request, #"{"candidates":[{"content":{"parts":[{"text":"Gemini result"}]}}]}"#)
        }

        let result = try await makeClient().recognize(
            provider: .googleGemini,
            baseURL: "https://generativelanguage.googleapis.com",
            model: "gemini-flash",
            apiKey: "secret",
            imageData: Data([1, 2]),
            prompt: "read"
        )
        XCTAssertEqual(result, "Gemini result")
    }

    func testAzureUsesAPIKeyHeaderAndOpenAIV1Path() async throws {
        ProviderURLProtocol.handler = { request in
            XCTAssertEqual(request.url?.absoluteString, "https://resource.openai.azure.com/openai/v1/chat/completions")
            XCTAssertEqual(request.value(forHTTPHeaderField: "api-key"), "secret")
            return Self.response(request, #"{"choices":[{"message":{"content":"Azure result"}}]}"#)
        }

        let result = try await makeClient().recognize(
            provider: .azureOpenAI,
            baseURL: "https://resource.openai.azure.com",
            model: "gpt-vision",
            apiKey: "secret",
            imageData: Data([1, 2]),
            prompt: "read"
        )
        XCTAssertEqual(result, "Azure result")
    }

    func testTaskTemplatesAreProviderIndependent() {
        XCTAssertTrue(MultimodalTaskTemplate.tableMarkdown.prompt.contains("Markdown"))
        XCTAssertTrue(MultimodalTaskTemplate.formulaLaTeX.prompt.contains("LaTeX"))
        XCTAssertTrue(MultimodalTaskTemplate.translateChinese.prompt.contains("中文"))
    }

    func testGeneralTaskDescribesVisualContentEvenWhenThereIsNoText() {
        let prompt = MultimodalTaskTemplate.general.prompt
        XCTAssertTrue(prompt.contains("视觉理解任务"))
        XCTAssertTrue(prompt.contains("动物"))
        XCTAssertTrue(prompt.contains("即使图片完全没有文字"))
        XCTAssertTrue(prompt.contains("不能只回答“没有文字”"))
    }

    func testClaudeBaseURLEndingInV1DoesNotDuplicateTheVersionSegment() async throws {
        ProviderURLProtocol.handler = { request in
            XCTAssertEqual(request.url?.absoluteString, "https://gateway.example.com/claude/v1/messages")
            return Self.response(request, #"{"content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn"}"#)
        }
        let result = try await makeClient().recognize(
            provider: .anthropic,
            baseURL: "https://gateway.example.com/claude/v1",
            model: "claude-vision",
            apiKey: "secret",
            imageData: Data([1, 2]),
            prompt: "read"
        )
        XCTAssertEqual(result, "ok")
    }

    func testClaudeMaxTokensStopIsReportedAsTruncated() async throws {
        ProviderURLProtocol.handler = { request in
            Self.response(request, #"{"content":[{"type":"text","text":"partial"}],"stop_reason":"max_tokens"}"#)
        }
        let result = try await makeClient().recognize(
            provider: .anthropic,
            baseURL: "https://api.anthropic.com",
            model: "claude-vision",
            apiKey: "secret",
            imageData: Data([1, 2]),
            prompt: "read"
        )
        XCTAssertEqual(result, "partial\n\n" + ProviderOutputNotice.truncated)
    }

    func testGeminiMaxTokensWithTextIsReportedAsTruncated() async throws {
        ProviderURLProtocol.handler = { request in
            let body = try XCTUnwrap(Self.jsonBody(request))
            let config = try XCTUnwrap(body["generationConfig"] as? [String: Any])
            XCTAssertEqual(config["maxOutputTokens"] as? Int, 8192)
            return Self.response(
                request,
                #"{"candidates":[{"content":{"parts":[{"text":"thinking","thought":true},{"text":"partial"}]},"finishReason":"MAX_TOKENS"}]}"#
            )
        }
        let result = try await makeClient().recognize(
            provider: .googleGemini,
            baseURL: "https://generativelanguage.googleapis.com",
            model: "gemini-flash",
            apiKey: "secret",
            imageData: Data([1, 2]),
            prompt: "read"
        )
        XCTAssertEqual(result, "partial\n\n" + ProviderOutputNotice.truncated)
    }

    func testGeminiThinkingThatExhaustsTheBudgetGetsSpecificError() async {
        ProviderURLProtocol.handler = { request in
            Self.response(request, #"{"candidates":[{"content":{"role":"model"},"finishReason":"MAX_TOKENS"}]}"#)
        }
        do {
            _ = try await makeClient().recognize(
                provider: .googleGemini,
                baseURL: "https://generativelanguage.googleapis.com",
                model: "gemini-pro",
                apiKey: "secret",
                imageData: Data([1, 2]),
                prompt: "read"
            )
            XCTFail("Expected thinking-budget error")
        } catch let error as VisionClientError {
            XCTAssertEqual(error, .reasoningOnlyOutput(truncated: true))
            XCTAssertTrue(error.errorDescription?.contains("思考") == true)
        } catch {
            XCTFail("Unexpected error: \(error)")
        }
    }

    func testRetriesTransientServerErrorThenSucceeds() async throws {
        let attempts = AttemptCounter()
        ProviderURLProtocol.handler = { request in
            if attempts.increment() == 1 {
                return (
                    HTTPURLResponse(url: request.url!, statusCode: 529, httpVersion: nil, headerFields: nil)!,
                    Data(#"{"error":{"message":"overloaded"}}"#.utf8)
                )
            }
            return Self.response(request, #"{"content":[{"type":"text","text":"ok"}]}"#)
        }
        let result = try await makeClient().recognize(
            provider: .anthropic,
            baseURL: "https://api.anthropic.com",
            model: "claude-vision",
            apiKey: "secret",
            imageData: Data([1, 2]),
            prompt: "read"
        )
        XCTAssertEqual(result, "ok")
        XCTAssertEqual(attempts.count, 2)
    }

    func testPromptTemplatesCoverAuditedCases() {
        // Pure-text captures are no longer forced into a scene description.
        XCTAssertTrue(MultimodalTaskTemplate.general.prompt.contains("以文字为主"))
        XCTAssertTrue(MultimodalTaskTemplate.extractText.prompt.contains("缩进"))
        XCTAssertTrue(MultimodalTaskTemplate.extractText.prompt.contains("表格"))
        XCTAssertTrue(MultimodalTaskTemplate.extractText.prompt.contains("[无法辨认]"))
        XCTAssertTrue(MultimodalTaskTemplate.formulaLaTeX.prompt.contains("混排"))
        XCTAssertTrue(MultimodalTaskTemplate.explainCode.prompt.contains("简洁的中文"))
        XCTAssertTrue(
            MultimodalTaskTemplate.explainCode.prompt(responseLanguage: "English").contains("简洁的English")
        )
        XCTAssertEqual(
            MultimodalTaskTemplate.explainCode.prompt(responseLanguage: "  "),
            MultimodalTaskTemplate.explainCode.prompt
        )
    }

    private func makeClient() -> MultimodalProviderClient {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [ProviderURLProtocol.self]
        return MultimodalProviderClient(session: URLSession(configuration: configuration), retryBaseDelay: 0)
    }

    private static func jsonBody(_ request: URLRequest) -> [String: Any]? {
        let data: Data
        if let body = request.httpBody {
            data = body
        } else if let stream = request.httpBodyStream {
            stream.open()
            defer { stream.close() }
            var collected = Data()
            var buffer = [UInt8](repeating: 0, count: 4096)
            while stream.hasBytesAvailable {
                let count = stream.read(&buffer, maxLength: buffer.count)
                guard count >= 0 else { return nil }
                if count == 0 { break }
                collected.append(buffer, count: count)
            }
            data = collected
        } else {
            return nil
        }
        return try? JSONSerialization.jsonObject(with: data) as? [String: Any]
    }

    private static func response(_ request: URLRequest, _ json: String) -> (HTTPURLResponse, Data) {
        (HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: ["Content-Type": "application/json"])!, Data(json.utf8))
    }
}

private final class AttemptCounter: @unchecked Sendable {
    private let lock = NSLock()
    private var value = 0

    var count: Int {
        lock.lock()
        defer { lock.unlock() }
        return value
    }

    func increment() -> Int {
        lock.lock()
        defer { lock.unlock() }
        value += 1
        return value
    }
}

private final class ProviderURLProtocol: URLProtocol, @unchecked Sendable {
    nonisolated(unsafe) static var handler: ((URLRequest) throws -> (HTTPURLResponse, Data))?
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        do {
            guard let handler = Self.handler else { throw URLError(.badServerResponse) }
            let (response, data) = try handler(request)
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: data)
            client?.urlProtocolDidFinishLoading(self)
        } catch { client?.urlProtocol(self, didFailWithError: error) }
    }
    override func stopLoading() {}
}
