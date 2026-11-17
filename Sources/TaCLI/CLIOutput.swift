import Foundation
import TaAgentClient
import TaAgentContracts

enum CLIExitCode: Int32, Equatable, Sendable {
    case success = 0
    case usage = 2
    case appNotInstalled = 3
    case bridgeUnavailable = 4
    case permissionDenied = 5
    case privacyBlocked = 6
    case requestFailed = 7
    case timeout = 8
    case cancelled = 130

    static func forResponse(_ response: AgentResponseEnvelope) -> CLIExitCode {
        guard !response.ok else { return .success }
        switch response.error?.code {
        case .taAppNotInstalled: return .appNotInstalled
        case .bridgeUnavailable: return .bridgeUnavailable
        case .screenPermissionRequired, .accessibilityPermissionRequired: return .permissionDenied
        case .targetBlockedByPrivacyPolicy, .cloudUploadNotAllowed: return .privacyBlocked
        case .cancelled: return .cancelled
        case .requestTimeout: return .timeout
        default: return .requestFailed
        }
    }
}

/// Converts errors thrown on the client side (before or instead of a Bridge
/// response) into the same structured envelope the Bridge returns, so `--json`
/// callers never have to parse prose.
enum CLIErrorMapper {
    static func payload(for error: Error, timeout: TimeInterval? = nil) -> AgentErrorPayload {
        switch error {
        case is CancellationError:
            return AgentErrorPayload(code: .cancelled, message: "请求已取消。", retryable: false)
        case let error as CLIParseError:
            return AgentErrorPayload(code: .invalidRequest, message: error.message, retryable: false)
        case let error as TaAppLauncherError:
            switch error {
            case .appNotInstalled:
                return AgentErrorPayload(
                    code: .taAppNotInstalled,
                    message: error.localizedDescription,
                    hint: "请先安装拓 App，或用 TA_APP_PATH 指定 App 路径。",
                    retryable: false
                )
            case .launchFailed:
                return AgentErrorPayload(
                    code: .bridgeUnavailable,
                    message: error.localizedDescription,
                    hint: "请手动打开拓后再试一次。",
                    retryable: true
                )
            }
        case let error as TaBridgeClientError:
            switch error {
            case .timedOut:
                let waited = timeout.map { "（已等待 \(Int($0.rounded())) 秒）" } ?? ""
                return AgentErrorPayload(
                    code: .requestTimeout,
                    message: "等待拓响应超时\(waited)。请求可能仍在拓中执行，图片或文字也可能已经发送到云端模型。",
                    hint: "不要自动重试，否则可能重复上传。请先确认结果，或在确有需要时用更大的 --timeout 重新发起。",
                    retryable: false
                )
            case .socketFailure(let operation, _) where operation == "connect":
                return AgentErrorPayload(
                    code: .bridgeUnavailable,
                    message: error.localizedDescription,
                    hint: "确认拓已启动且已开启 Agent 调用，然后最多重试一次。",
                    retryable: true
                )
            default:
                // The connection broke after it was established: the request may
                // already have been delivered, so this is not safely retryable.
                return AgentErrorPayload(
                    code: .bridgeUnavailable,
                    message: error.localizedDescription,
                    hint: "连接在请求发出后中断，请求可能已被执行；重试前请先确认结果。",
                    retryable: false
                )
            }
        default:
            return AgentErrorPayload(
                code: .internalError,
                message: error.localizedDescription,
                retryable: false
            )
        }
    }

    static func response(
        for error: Error,
        requestID: String,
        timeout: TimeInterval? = nil
    ) -> AgentResponseEnvelope {
        .failure(requestID: requestID, error: payload(for: error, timeout: timeout))
    }
}

enum CLIOutput {
    static func render(
        _ response: AgentResponseEnvelope,
        format: CLIOutputFormat
    ) throws -> String {
        switch format {
        case .json:
            return String(decoding: try AgentJSONCoding.encoder().encode(response), as: UTF8.self)
        case .human:
            return human(response)
        }
    }

    private static func human(_ response: AgentResponseEnvelope) -> String {
        if let error = response.error {
            var lines = ["\(error.code.rawValue): \(error.message)"]
            if let hint = error.hint, !hint.isEmpty { lines.append("提示：\(hint)") }
            return lines.joined(separator: "\n")
        }
        if case .object(let object) = response.data,
           case .string(let text) = object["text"] {
            return text
        }
        var lines: [String] = []
        if case .object(let object) = response.data {
            for key in object.keys.sorted() {
                lines.append("\(key): \(display(object[key] ?? .null))")
            }
        }
        lines.append(contentsOf: response.artifacts.map { "图片：\($0.path)（\($0.width ?? 0)×\($0.height ?? 0)）" })
        return lines.isEmpty ? "完成" : lines.joined(separator: "\n")
    }

    private static func display(_ value: JSONValue) -> String {
        switch value {
        case .null: "-"
        case .bool(let value): value ? "是" : "否"
        case .integer(let value): String(value)
        case .number(let value): String(value)
        case .string(let value): value
        case .array(let values): values.map(display).joined(separator: ", ")
        case .object:
            (try? String(decoding: AgentJSONCoding.encoder().encode(value), as: UTF8.self)) ?? "{}"
        }
    }
}
