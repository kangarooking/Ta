import Foundation
import Testing
@testable import TaCLI
import TaAgentClient
import TaAgentContracts

@Suite("ta CLI output")
struct CLIGoldenOutputTests {
    @Test("JSON output is stable and contains no prose")
    func jsonGolden() throws {
        let response = AgentResponseEnvelope.success(
            requestID: "req-1",
            data: .object(["bridge": .string("ready")]),
            meta: AgentResponseMetadata(durationMs: 7, cloudUploaded: false)
        )

        #expect(try CLIOutput.render(response, format: .json) ==
            #"{"artifacts":[],"data":{"bridge":"ready"},"meta":{"cloudUploaded":false,"durationMs":7},"ok":true,"protocolVersion":1,"requestId":"req-1"}"#
        )
    }

    @Test("human errors include code, message, and hint")
    func humanError() throws {
        let response = AgentResponseEnvelope.failure(
            requestID: "req-2",
            error: AgentErrorPayload(
                code: .screenPermissionRequired,
                message: "拓尚未获得屏幕录制权限。",
                hint: "打开拓的权限设置。",
                retryable: false
            )
        )

        let rendered = try CLIOutput.render(response, format: .human)
        #expect(rendered.contains("SCREEN_PERMISSION_REQUIRED"))
        #expect(rendered.contains("拓尚未获得屏幕录制权限"))
        #expect(rendered.contains("打开拓的权限设置"))
        #expect(CLIExitCode.forResponse(response) == .permissionDenied)
    }

    @Test("transform human output includes history and artifact details")
    func transformHumanOutput() throws {
        let response = AgentResponseEnvelope.success(
            requestID: "transform-1",
            data: .object([
                "action": .string("apply"),
                "elementCount": .integer(3),
                "canUndo": .bool(true),
                "canRedo": .bool(false)
            ]),
            artifacts: [AgentArtifact(
                id: "artifact-1",
                path: "/tmp/transformed.png",
                mimeType: "image/png",
                width: 800,
                height: 600,
                bytes: 42,
                sha256: "abc",
                expiresAt: Date(timeIntervalSince1970: 60)
            )]
        )

        let rendered = try CLIOutput.render(response, format: .human)
        #expect(rendered.contains("elementCount: 3"))
        #expect(rendered.contains("canUndo: 是"))
        #expect(rendered.contains("transformed.png"))
        #expect(rendered.contains("800×600"))
    }

    @Test("--output saves the artifact returned by this invocation, never the global last image")
    func outputSavePassesArtifactPath() {
        let artifact = AgentArtifact(
            id: "artifact-1",
            path: "/tmp/AgentRuns/req-1/capture.png",
            mimeType: "image/png",
            bytes: 42,
            sha256: "abc",
            expiresAt: Date(timeIntervalSince1970: 60)
        )

        #expect(CLIRunner.saveParameters(artifact: artifact, outputPath: "/tmp/out.png", overwrite: false) == [
            "path": .string("/tmp/out.png"),
            "inputPath": .string("/tmp/AgentRuns/req-1/capture.png")
        ])
        #expect(CLIRunner.saveParameters(artifact: artifact, outputPath: "/tmp/out.png", overwrite: true) == [
            "path": .string("/tmp/out.png"),
            "inputPath": .string("/tmp/AgentRuns/req-1/capture.png"),
            "overwrite": .bool(true)
        ])
    }

    @Test("a client-side timeout is its own non-retryable error, not BRIDGE_UNAVAILABLE")
    func timeoutErrorMapping() throws {
        let response = CLIErrorMapper.response(
            for: TaBridgeClientError.timedOut(operation: "read"),
            requestID: "req-timeout",
            timeout: 180
        )

        #expect(response.error?.code == .requestTimeout)
        #expect(response.error?.code.rawValue == "REQUEST_TIMEOUT")
        #expect(response.error?.retryable == false)
        #expect(response.error?.message.contains("可能仍在") == true)
        #expect(response.error?.hint?.contains("不要自动重试") == true)
        #expect(CLIExitCode.forResponse(response) == .timeout)

        // With --json the thrown error is rendered as a parseable envelope.
        let json = try CLIOutput.render(response, format: .json)
        let decoded = try AgentJSONCoding.decoder().decode(AgentResponseEnvelope.self, from: Data(json.utf8))
        #expect(decoded.ok == false)
        #expect(decoded.requestID == "req-timeout")
        #expect(decoded.error?.code == .requestTimeout)
    }

    @Test("thrown client errors map to structured codes and exit codes")
    func thrownErrorMapping() {
        func mapped(_ error: Error) -> AgentResponseEnvelope {
            CLIErrorMapper.response(for: error, requestID: "req")
        }

        let connect = mapped(TaBridgeClientError.socketFailure(operation: "connect", code: 61))
        #expect(connect.error?.code == .bridgeUnavailable)
        #expect(connect.error?.retryable == true)
        #expect(CLIExitCode.forResponse(connect) == .bridgeUnavailable)

        // After the request was sent, a broken connection must not invite a blind retry.
        let closed = mapped(TaBridgeClientError.connectionClosed)
        #expect(closed.error?.code == .bridgeUnavailable)
        #expect(closed.error?.retryable == false)

        let missing = mapped(TaAppLauncherError.appNotInstalled("/Applications/拓.app"))
        #expect(missing.error?.code == .taAppNotInstalled)
        #expect(CLIExitCode.forResponse(missing) == .appNotInstalled)

        let cancelled = mapped(CancellationError())
        #expect(cancelled.error?.code == .cancelled)
        #expect(CLIExitCode.forResponse(cancelled) == .cancelled)

        let usage = mapped(CLIParseError(message: "bad flag"))
        #expect(usage.error?.code == .invalidRequest)
        #expect(usage.error?.message == "bad flag")
    }

    @Test("runner loads recipe contents and removes local path before Bridge send")
    func runnerLoadsRecipe() throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("ta-cli-recipe-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let recipeURL = directory.appendingPathComponent("recipe.json")
        let recipe = #"{"version":1,"operations":[{"type":"crop","rect":{"x":0,"y":0,"width":10,"height":10}}]}"#
        try Data(recipe.utf8).write(to: recipeURL)

        let params = try CLIRunner.preparedParameters(
            method: .transformImage,
            params: [
                "action": .string("apply"),
                "recipePath": .string(recipeURL.path)
            ]
        )

        #expect(params["recipe"] == .string(recipe))
        #expect(params["recipePath"] == nil)
    }
}
