import AppKit
import Darwin
import Foundation
import TaAgentClient
import TaAgentContracts

struct CLIRunner: Sendable {
    private let launcher: TaAppLauncher

    init(applicationURL: URL? = nil) {
        launcher = TaAppLauncher(applicationURL: applicationURL ?? Self.applicationURLFromEnvironment())
    }

    func execute(_ invocation: CLIInvocation) async throws -> AgentResponseEnvelope {
        switch invocation.action {
        case .request(let method, let params):
            let preparedParams = try Self.preparedParameters(method: method, params: params)
            let response = try await send(
                method: method,
                params: preparedParams,
                invocation: invocation,
                requestIDSuffix: nil
            )
            return try await saveCaptureOutputIfNeeded(response, invocation: invocation)

        case .ocrThenTranslate(let params):
            let ocr = try await send(
                method: .recognizeOCR,
                params: params,
                invocation: invocation,
                requestIDSuffix: "ocr"
            )
            guard ocr.ok else { return ocr }
            guard case .object(let data) = ocr.data,
                  case .string(let text) = data["text"],
                  !text.isEmpty else {
                return .failure(
                    requestID: invocation.requestID,
                    error: AgentErrorPayload(
                        code: .invalidRequest,
                        message: "OCR 没有返回可翻译文字。",
                        retryable: false
                    )
                )
            }
            var translationParams: [String: JSONValue] = ["text": .string(text)]
            if let cloud = params["cloud"] { translationParams["cloud"] = cloud }
            return try await send(
                method: .translateText,
                params: translationParams,
                invocation: invocation,
                requestIDSuffix: "translate"
            )
        }
    }

    static func preparedParameters(
        method: AgentMethod,
        params: [String: JSONValue]
    ) throws -> [String: JSONValue] {
        guard method == .transformImage,
              params["action"] == .string("apply") else { return params }
        guard case .string(let path) = params["recipePath"], !path.isEmpty else {
            throw CLIRecipeError.missingPath
        }
        let url = URL(fileURLWithPath: path).standardizedFileURL
        let data: Data
        do {
            data = try Data(contentsOf: url)
        } catch {
            throw CLIRecipeError.unreadable(path: url.path, underlying: error.localizedDescription)
        }
        guard let recipe = String(data: data, encoding: .utf8),
              !recipe.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw CLIRecipeError.invalidUTF8(path: url.path)
        }
        var prepared = params
        prepared.removeValue(forKey: "recipePath")
        prepared["recipe"] = .string(recipe)
        return prepared
    }

    private func send(
        method: AgentMethod,
        params: [String: JSONValue],
        invocation: CLIInvocation,
        requestIDSuffix: String?
    ) async throws -> AgentResponseEnvelope {
        let requestID = requestIDSuffix.map { "\(invocation.requestID)-\($0)" } ?? invocation.requestID
        let request = AgentRequestEnvelope(
            requestID: requestID,
            method: method,
            params: params,
            client: AgentClientInfo(name: "ta-cli", version: "1.0.1")
        )
        let client = TaBridgeClient(socketURL: invocation.socketURL, timeout: invocation.timeout)
        do {
            return try await client.send(request)
        } catch let error as TaBridgeClientError where Self.isUnavailable(error) {
            // Only a failed connect is retried: nothing was sent, so nothing can run twice.
            try await launcher.launch()
            return try await waitForBridge(client: client, request: request, timeout: invocation.timeout)
        }
    }

    private func waitForBridge(
        client: TaBridgeClient,
        request: AgentRequestEnvelope,
        timeout: TimeInterval
    ) async throws -> AgentResponseEnvelope {
        let deadline = Date().addingTimeInterval(min(timeout, 5))
        var lastError: Error = TaBridgeClientError.connectionClosed
        while Date() < deadline {
            try Task.checkCancellation()
            do {
                return try await client.send(request)
            } catch let error as TaBridgeClientError where Self.isUnavailable(error) {
                lastError = error
                try await Task.sleep(for: .milliseconds(80))
            }
            // Any other failure (timeout, closed connection) happened after the
            // request may have been delivered: surface it instead of re-sending.
        }
        throw lastError
    }

    private func saveCaptureOutputIfNeeded(
        _ response: AgentResponseEnvelope,
        invocation: CLIInvocation
    ) async throws -> AgentResponseEnvelope {
        guard response.ok,
              let outputPath = invocation.outputPath,
              !response.artifacts.isEmpty else { return response }
        let saveResponse = try await send(
            method: .deliverSave,
            params: Self.saveParameters(
                artifact: response.artifacts[0],
                outputPath: outputPath,
                overwrite: invocation.overwrite
            ),
            invocation: invocation,
            requestIDSuffix: "save"
        )
        guard saveResponse.ok else { return saveResponse }
        var object: [String: JSONValue] = [:]
        if case .object(let existing) = response.data { object = existing }
        // Report the path Ta actually wrote (it may have appended ".png").
        var savedPath = outputPath
        if case .object(let saved) = saveResponse.data, case .string(let path) = saved["path"] {
            savedPath = path
        }
        object["savedPath"] = .string(savedPath)
        return AgentResponseEnvelope(
            requestID: response.requestID,
            ok: true,
            data: .object(object),
            artifacts: response.artifacts,
            meta: response.meta
        )
    }

    /// `--output` saves exactly the artifact this invocation produced. It must not
    /// rely on Ta's process-wide "last image", which another client may have
    /// replaced between the two requests.
    static func saveParameters(
        artifact: AgentArtifact,
        outputPath: String,
        overwrite: Bool
    ) -> [String: JSONValue] {
        var params: [String: JSONValue] = [
            "path": .string(outputPath),
            "inputPath": .string(artifact.path)
        ]
        if overwrite { params["overwrite"] = .bool(true) }
        return params
    }

    private static func isUnavailable(_ error: TaBridgeClientError) -> Bool {
        guard case .socketFailure(let operation, let code) = error, operation == "connect" else {
            return false
        }
        return code == ENOENT || code == ECONNREFUSED
    }

    private static func applicationURLFromEnvironment() -> URL {
        if let path = ProcessInfo.processInfo.environment["TA_APP_PATH"], !path.isEmpty {
            return URL(fileURLWithPath: path).standardizedFileURL
        }
        return TaAppLauncher.defaultApplicationURL
    }
}

private enum CLIRecipeError: Error, LocalizedError {
    case missingPath
    case unreadable(path: String, underlying: String)
    case invalidUTF8(path: String)

    var errorDescription: String? {
        switch self {
        case .missingPath:
            "transform 缺少本地 recipePath。"
        case .unreadable(let path, let underlying):
            "无法读取标注配方 \(path)：\(underlying)"
        case .invalidUTF8(let path):
            "标注配方必须是非空 UTF-8 JSON：\(path)"
        }
    }
}
