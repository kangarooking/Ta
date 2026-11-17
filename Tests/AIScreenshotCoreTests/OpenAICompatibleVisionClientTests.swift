import Foundation
import XCTest
@testable import AIScreenshotCore

final class OpenAICompatibleVisionClientTests: XCTestCase {
    override func tearDown() {
        VisionURLProtocol.handler = nil
        super.tearDown()
    }

    func testBuildsOpenAICompatibleVisionRequestAndParsesText() async throws {
        VisionURLProtocol.handler = { request in
            XCTAssertEqual(request.url?.absoluteString, "https://api.example.com/v1/chat/completions")
            XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer secret")
            let body = try XCTUnwrap(Self.bodyData(from: request))
            let json = try XCTUnwrap(JSONSerialization.jsonObject(with: body) as? [String: Any])
            XCTAssertEqual(json["model"] as? String, "vision-model")
            let messages = try XCTUnwrap(json["messages"] as? [[String: Any]])
            XCTAssertEqual(messages.first?["role"] as? String, "system")
            let content = try XCTUnwrap(messages.last?["content"] as? [[String: Any]])
            let imagePart = try XCTUnwrap(content.first { ($0["type"] as? String) == "image_url" })
            let imageURL = try XCTUnwrap(imagePart["image_url"] as? [String: Any])
            XCTAssertEqual(imageURL["url"] as? String, "data:image/jpeg;base64,AQID")
            let response = HTTPURLResponse(
                url: try XCTUnwrap(request.url),
                statusCode: 200,
                httpVersion: nil,
                headerFields: ["Content-Type": "application/json"]
            )!
            return (response, Data(#"{"choices":[{"message":{"content":"识别结果"}}]}"#.utf8))
        }

        let result = try await makeClient().recognize(
            baseURL: "https://api.example.com/v1",
            model: "vision-model",
            apiKey: "secret",
            imageData: Data([1, 2, 3]),
            prompt: "识别图片"
        )
        XCTAssertEqual(result, "识别结果")
    }

    func testSurfacesProviderErrorMessage() async {
        VisionURLProtocol.handler = { request in
            let response = HTTPURLResponse(url: request.url!, statusCode: 401, httpVersion: nil, headerFields: nil)!
            return (response, Data(#"{"error":{"message":"invalid key"}}"#.utf8))
        }
        do {
            _ = try await makeClient().recognize(
                baseURL: "https://api.example.com/v1",
                model: "vision-model",
                apiKey: "bad",
                imageData: Data([1]),
                prompt: "识别"
            )
            XCTFail("Expected server error")
        } catch let error as VisionClientError {
            XCTAssertEqual(error, .server(statusCode: 401, message: "invalid key"))
        } catch {
            XCTFail("Unexpected error: \(error)")
        }
    }

    private func makeClient() -> OpenAICompatibleVisionClient {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [VisionURLProtocol.self]
        return OpenAICompatibleVisionClient(session: URLSession(configuration: configuration), retryBaseDelay: 0)
    }

    func testZhipuVisionDisablesThinkingAndReasoningOnlyErrorIncludesGuidance() async throws {
        VisionURLProtocol.handler = { request in
            XCTAssertTrue(request.url?.host?.hasSuffix("bigmodel.cn") == true)
            let bodyData = try XCTUnwrap(Self.bodyData(from: request))
            let json = try XCTUnwrap(JSONSerialization.jsonObject(with: bodyData) as? [String: Any])
            XCTAssertEqual((json["thinking"] as? [String: Any])?["type"] as? String, "disabled")
            XCTAssertEqual(json["max_tokens"] as? Int, 8192)
            let payload: [String: Any] = [
                "choices": [[
                    "message": [
                        "content": "",
                        "reasoning_content": "Inspecting layout and text regions first..."
                    ],
                    "finish_reason": "length"
                ]]
            ]
            let response = HTTPURLResponse(
                url: request.url!,
                statusCode: 200,
                httpVersion: nil,
                headerFields: ["Content-Type": "application/json"]
            )!
            return (response, Data(try! JSONSerialization.data(withJSONObject: payload)))
        }

        do {
            _ = try await makeClient().recognize(
                baseURL: "https://open.bigmodel.cn/api/paas/v4",
                model: "glm-4.5v",
                apiKey: "secret",
                imageData: Data([1]),
                prompt: "识别图片"
            )
            XCTFail("Expected reasoning-only error")
        } catch let error as VisionClientError {
            XCTAssertEqual(error, .reasoningOnlyOutput(truncated: true))
        } catch {
            XCTFail("Unexpected error: \(error)")
        }
    }

    func testLengthTruncatedAnswerIsMarkedInsteadOfReportedAsComplete() async throws {
        VisionURLProtocol.handler = { request in
            let response = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!
            return (response, Data(#"{"choices":[{"message":{"content":"第一段"},"finish_reason":"length"}]}"#.utf8))
        }
        let result = try await makeClient().recognize(
            baseURL: "https://api.example.com/v1",
            model: "vision-model",
            apiKey: "secret",
            imageData: Data([1]),
            prompt: "识别"
        )
        XCTAssertEqual(result, "第一段\n\n" + ProviderOutputNotice.truncated)
    }

    func testLocalServerNeedsNoAPIKeyAndSendsNoAuthorizationHeader() async throws {
        VisionURLProtocol.handler = { request in
            XCTAssertEqual(request.url?.absoluteString, "http://localhost:1234/v1/chat/completions")
            XCTAssertNil(request.value(forHTTPHeaderField: "Authorization"))
            let response = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!
            return (response, Data(#"{"choices":[{"message":{"content":"本地结果"},"finish_reason":"stop"}]}"#.utf8))
        }
        let result = try await makeClient().recognize(
            baseURL: "http://localhost:1234/v1",
            model: "llava",
            apiKey: "",
            imageData: Data([1]),
            prompt: "识别"
        )
        XCTAssertEqual(result, "本地结果")
    }

    func testRemoteServerStillRequiresAPIKey() async {
        do {
            _ = try await makeClient().recognize(
                baseURL: "https://api.example.com/v1",
                model: "vision-model",
                apiKey: "",
                imageData: Data([1]),
                prompt: "识别"
            )
            XCTFail("Expected missing key")
        } catch let error as VisionClientError {
            XCTAssertEqual(error, .missingAPIKey)
        } catch {
            XCTFail("Unexpected error: \(error)")
        }
    }

    func testFallsBackToMaxCompletionTokensAndDropsTemperature() async throws {
        VisionURLProtocol.handler = { request in
            let body = try XCTUnwrap(Self.bodyData(from: request))
            let json = try XCTUnwrap(JSONSerialization.jsonObject(with: body) as? [String: Any])
            func reject(_ message: String) throws -> (HTTPURLResponse, Data) {
                let response = HTTPURLResponse(url: request.url!, statusCode: 400, httpVersion: nil, headerFields: nil)!
                return (response, try JSONSerialization.data(withJSONObject: ["error": ["message": message]]))
            }
            if json["max_tokens"] != nil {
                return try reject("Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead.")
            }
            if json["temperature"] != nil {
                return try reject("Unsupported value: 'temperature' does not support 0 with this model. Only the default (1) value is supported.")
            }
            XCTAssertEqual(json["max_completion_tokens"] as? Int, 8192)
            let response = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!
            return (response, Data(#"{"choices":[{"message":{"content":"好了"},"finish_reason":"stop"}]}"#.utf8))
        }
        let result = try await makeClient().recognize(
            baseURL: "https://api.example.com/v1",
            model: "reasoning-vision",
            apiKey: "secret",
            imageData: Data([1]),
            prompt: "识别"
        )
        XCTAssertEqual(result, "好了")
    }

    func testOversizedTokenCapIsLoweredInsteadOfFailing() async throws {
        VisionURLProtocol.handler = { request in
            let body = try XCTUnwrap(Self.bodyData(from: request))
            let json = try XCTUnwrap(JSONSerialization.jsonObject(with: body) as? [String: Any])
            if (json["max_tokens"] as? Int ?? 0) > 4096 {
                let response = HTTPURLResponse(url: request.url!, statusCode: 400, httpVersion: nil, headerFields: nil)!
                return (response, Data(#"{"error":{"message":"Invalid max_tokens value, the valid range of max_tokens is [1, 4096]"}}"#.utf8))
            }
            XCTAssertEqual(json["max_tokens"] as? Int, 4096)
            let response = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!
            return (response, Data(#"{"choices":[{"message":{"content":"好了"},"finish_reason":"stop"}]}"#.utf8))
        }
        let result = try await makeClient().recognize(
            baseURL: "https://api.example.com/v1",
            model: "small-vision",
            apiKey: "secret",
            imageData: Data([1]),
            prompt: "识别"
        )
        XCTAssertEqual(result, "好了")
    }

    func testRetryDelayHonoursRetryAfterAndBacksOffExponentially() {
        XCTAssertEqual(ProviderHTTP.retryDelay(attempt: 0, retryAfter: "3", baseDelay: 0.8), 3)
        XCTAssertEqual(ProviderHTTP.retryDelay(attempt: 0, retryAfter: "9999", baseDelay: 0.8), ProviderHTTP.maximumDelay)
        XCTAssertEqual(ProviderHTTP.retryDelay(attempt: 0, retryAfter: nil, baseDelay: 0.5), 0.5)
        XCTAssertEqual(ProviderHTTP.retryDelay(attempt: 1, retryAfter: nil, baseDelay: 0.5), 1)
        XCTAssertTrue(ProviderHTTP.isRetryable(statusCode: 429))
        XCTAssertTrue(ProviderHTTP.isRetryable(statusCode: 502))
        XCTAssertFalse(ProviderHTTP.isRetryable(statusCode: 400))
        XCTAssertFalse(ProviderHTTP.isRetryable(statusCode: 401))
        XCTAssertTrue(ProviderHTTP.isTransient(.networkConnectionLost))
        XCTAssertFalse(ProviderHTTP.isTransient(.cancelled))
    }

    func testEndpointPathsAreNotDuplicated() {
        func path(_ provider: VisionProviderKind, _ base: String) -> String {
            ProviderEndpointPath.resolved(provider: provider, path: base, model: "m")
        }
        XCTAssertEqual(path(.anthropic, ""), "/v1/messages")
        XCTAssertEqual(path(.anthropic, "/v1"), "/v1/messages")
        XCTAssertEqual(path(.anthropic, "/v1/"), "/v1/messages")
        XCTAssertEqual(path(.anthropic, "/proxy/v1/messages"), "/proxy/v1/messages")
        XCTAssertEqual(path(.anthropic, "/proxy"), "/proxy/v1/messages")
        XCTAssertEqual(path(.openAICompatible, "/v1"), "/v1/chat/completions")
        XCTAssertEqual(path(.openAICompatible, "/v1/chat/completions"), "/v1/chat/completions")
        XCTAssertEqual(path(.azureOpenAI, "/openai/v1"), "/openai/v1/chat/completions")
        XCTAssertEqual(path(.azureOpenAI, ""), "/openai/v1/chat/completions")
        XCTAssertEqual(path(.googleGemini, "/v1beta"), "/v1beta/models/m:generateContent")
        XCTAssertEqual(path(.googleGemini, ""), "/v1beta/models/m:generateContent")
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
}

private final class VisionURLProtocol: URLProtocol, @unchecked Sendable {
    nonisolated(unsafe) static var handler: ((URLRequest) throws -> (HTTPURLResponse, Data))?

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        do {
            let (response, data) = try Self.handler?(request) ?? {
                throw VisionURLProtocolError.missingHandler
            }()
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: data)
            client?.urlProtocolDidFinishLoading(self)
        } catch {
            client?.urlProtocol(self, didFailWithError: error)
        }
    }

    override func stopLoading() {}
}

private enum VisionURLProtocolError: Error {
    case missingHandler
}
