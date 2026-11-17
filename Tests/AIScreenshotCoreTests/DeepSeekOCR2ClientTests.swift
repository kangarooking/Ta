import Foundation
import XCTest
@testable import AIScreenshotCore

final class DeepSeekOCR2ClientTests: XCTestCase {
    override func tearDown() {
        DeepSeekOCRURLProtocol.handler = nil
        super.tearDown()
    }

    func testBuildsLatestOCR2RequestWithoutKeyForLocalService() async throws {
        DeepSeekOCRURLProtocol.handler = { request in
            XCTAssertEqual(request.url?.absoluteString, "http://127.0.0.1:8000/v1/chat/completions")
            XCTAssertNil(request.value(forHTTPHeaderField: "Authorization"))
            let body = try XCTUnwrap(Self.bodyData(from: request))
            let json = try XCTUnwrap(JSONSerialization.jsonObject(with: body) as? [String: Any])
            XCTAssertEqual(json["model"] as? String, DeepSeekOCR2Client.latestOfficialModel)
            let messages = try XCTUnwrap(json["messages"] as? [[String: Any]])
            let content = try XCTUnwrap(messages.first?["content"] as? [[String: Any]])
            // The image is an image_url part; no literal "<image>" placeholder.
            XCTAssertEqual(content.first?["text"] as? String, "Free OCR.")
            XCTAssertEqual(messages.count, 1)
            let imagePart = try XCTUnwrap(content.first { ($0["type"] as? String) == "image_url" })
            let imageURL = try XCTUnwrap(imagePart["image_url"] as? [String: Any])
            XCTAssertEqual(imageURL["url"] as? String, "data:image/png;base64,AQID")
            let response = HTTPURLResponse(
                url: try XCTUnwrap(request.url),
                statusCode: 200,
                httpVersion: nil,
                headerFields: ["Content-Type": "application/json"]
            )!
            return (response, Data(#"{"choices":[{"message":{"content":"最新识别结果"}}]}"#.utf8))
        }

        let result = try await makeClient().recognize(
            baseURL: DeepSeekOCR2Client.recommendedLocalBaseURL,
            model: DeepSeekOCR2Client.latestOfficialModel,
            apiKey: "",
            imageData: Data([1, 2, 3]),
            mode: .plainText
        )
        XCTAssertEqual(result, "最新识别结果")
    }

    func testUsesOfficialDocumentMarkdownPromptAndBearerKey() async throws {
        DeepSeekOCRURLProtocol.handler = { request in
            XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer secret")
            let body = try XCTUnwrap(Self.bodyData(from: request))
            let json = try XCTUnwrap(JSONSerialization.jsonObject(with: body) as? [String: Any])
            let messages = try XCTUnwrap(json["messages"] as? [[String: Any]])
            let content = try XCTUnwrap(messages.first?["content"] as? [[String: Any]])
            XCTAssertEqual(
                content.first?["text"] as? String,
                "<|grounding|>Convert the document to markdown."
            )
            let response = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!
            return (response, Data(##"{"choices":[{"message":{"content":"# 标题"}}]}"##.utf8))
        }

        let result = try await makeClient().recognize(
            baseURL: "https://ocr.example.com/v1/chat/completions",
            model: "deepseek-ai/DeepSeek-OCR-2",
            apiKey: "secret",
            imageData: Data([1]),
            mode: .documentMarkdown
        )
        XCTAssertEqual(result, "# 标题")
    }

    func testRejectsOfficialChatAPIBecauseItDoesNotServeOCR2() async {
        do {
            _ = try await makeClient().recognize(
                baseURL: "https://api.deepseek.com",
                model: DeepSeekOCR2Client.latestOfficialModel,
                apiKey: "secret",
                imageData: Data([1]),
                mode: .plainText
            )
            XCTFail("Expected official API rejection")
        } catch let error as DeepSeekOCR2ClientError {
            XCTAssertEqual(error, .officialAPIUnsupported)
        } catch {
            XCTFail("Unexpected error: \(error)")
        }
    }

    func testGroundingTagsAreStrippedFromOutput() async throws {
        DeepSeekOCRURLProtocol.handler = { request in
            let content = "<|ref|>title<|/ref|><|det|>[[52, 40, 900, 96]]<|/det|>\n# 标题\n\n"
                + "<|ref|>text<|/ref|><|det|>[[52, 120, 900, 300]]<|/det|>\n正文第一段。"
            let data = try JSONSerialization.data(withJSONObject: [
                "choices": [["message": ["content": content], "finish_reason": "stop"]]
            ])
            return (HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!, data)
        }
        let result = try await makeClient().recognize(
            baseURL: "https://ocr.example.com/v1",
            model: "deepseek-ai/DeepSeek-OCR-2",
            apiKey: "secret",
            imageData: Data([1]),
            mode: .documentMarkdown
        )
        XCTAssertEqual(result, "# 标题\n\n正文第一段。")
    }

    func testStrippingLeavesPlainTextUntouched() {
        XCTAssertEqual(DeepSeekOCR2Client.strippingGroundingTags(from: "a < b |> c\nline"), "a < b |> c\nline")
        XCTAssertEqual(DeepSeekOCR2Client.strippingGroundingTags(from: "<|grounding|>文本"), "文本")
    }

    func testTruncatedOCRIsMarked() async throws {
        DeepSeekOCRURLProtocol.handler = { request in
            let response = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!
            return (response, Data(#"{"choices":[{"message":{"content":"半页"},"finish_reason":"length"}]}"#.utf8))
        }
        let result = try await makeClient().recognize(
            baseURL: "http://127.0.0.1:8000/v1",
            model: "deepseek-ai/DeepSeek-OCR-2",
            apiKey: "",
            imageData: Data([1]),
            mode: .plainText
        )
        XCTAssertTrue(ProviderOutputNotice.isTruncated(result))
    }

    private func makeClient() -> DeepSeekOCR2Client {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [DeepSeekOCRURLProtocol.self]
        return DeepSeekOCR2Client(session: URLSession(configuration: configuration), retryBaseDelay: 0)
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

private final class DeepSeekOCRURLProtocol: URLProtocol, @unchecked Sendable {
    nonisolated(unsafe) static var handler: ((URLRequest) throws -> (HTTPURLResponse, Data))?

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        do {
            guard let handler = Self.handler else { throw DeepSeekOCRURLProtocolError.missingHandler }
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

private enum DeepSeekOCRURLProtocolError: Error {
    case missingHandler
}
