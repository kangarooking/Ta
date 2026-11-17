import Darwin
import Foundation
import TaAgentClient
import TaAgentContracts

@main
struct TaCLI {
    static func main() async {
        let arguments = Array(CommandLine.arguments.dropFirst())
        let invocation: CLIInvocation
        do {
            invocation = try CLIParser.parse(arguments)
        } catch {
            // The invocation could not be parsed, so honour --json from the raw arguments.
            if arguments.contains("--json") {
                let response = CLIErrorMapper.response(for: error, requestID: "unknown")
                emit(response, format: .json)
            } else {
                let message = (error as? CLIParseError)?.message ?? error.localizedDescription
                write(message, to: .standardError)
            }
            Darwin.exit(CLIExitCode.usage.rawValue)
        }

        signal(SIGINT, SIG_IGN)
        let execution = Task { try await CLIRunner().execute(invocation) }
        let signalSource = DispatchSource.makeSignalSource(signal: SIGINT, queue: .global())
        signalSource.setEventHandler { execution.cancel() }
        signalSource.resume()
        defer { signalSource.cancel() }

        let response: AgentResponseEnvelope
        do {
            response = try await execution.value
        } catch {
            // Thrown errors use the same envelope as Bridge failures, so with
            // --json the caller always receives JSON (on stderr, like any failure).
            response = CLIErrorMapper.response(
                for: error,
                requestID: invocation.requestID,
                timeout: invocation.timeout
            )
        }
        emit(response, format: invocation.outputFormat)
        Darwin.exit(CLIExitCode.forResponse(response).rawValue)
    }

    /// Successes go to stdout and failures to stderr, in the requested format.
    private static func emit(_ response: AgentResponseEnvelope, format: CLIOutputFormat) {
        let rendered: String
        do {
            rendered = try CLIOutput.render(response, format: format)
        } catch {
            rendered = format == .json
                ? #"{"ok":false,"error":{"code":"INTERNAL_ERROR","message":"无法编码输出。","retryable":false}}"#
                : "INTERNAL_ERROR: 无法编码输出。"
        }
        let destination: FileHandle = response.ok ? .standardOutput : .standardError
        write(rendered, to: destination)
    }

    private static func write(_ string: String, to handle: FileHandle) {
        if let data = "\(string)\n".data(using: .utf8) {
            try? handle.write(contentsOf: data)
        }
    }
}
