import Darwin
import Foundation
import Testing
@testable import AIScreenshotApp
import TaAgentClient
import TaAgentContracts

@Suite("Ta Agent local bridge")
struct TaAgentBridgeServerTests {
    @Test("handshake succeeds over a local Unix socket")
    func handshake() async throws {
        let fixture = try makeFixture()
        defer { fixture.server.stop() }

        let response = try await fixture.client.send(
            AgentRequestEnvelope(
                requestID: "handshake-1",
                method: .systemHandshake,
                client: AgentClientInfo(name: "test", version: "1")
            )
        )

        #expect(response.ok)
        #expect(response.data == .object([
            "protocolVersion": .integer(1),
            "server": .string("Ta")
        ]))
    }

    @Test("unsupported protocol versions return a structured error")
    func rejectsFutureProtocol() async throws {
        let fixture = try makeFixture()
        defer { fixture.server.stop() }

        let response = try await fixture.client.send(
            AgentRequestEnvelope(
                protocolVersion: 99,
                requestID: "future-1",
                method: .systemStatus,
                client: AgentClientInfo(name: "test", version: "1")
            )
        )

        #expect(!response.ok)
        #expect(response.error?.code == .protocolVersionMismatch)
        #expect(response.requestID == "future-1")
    }

    @Test("server accepts concurrent independent requests")
    func concurrentRequests() async throws {
        let fixture = try makeFixture()
        defer { fixture.server.stop() }

        let responses = try await withThrowingTaskGroup(of: AgentResponseEnvelope.self) { group in
            for index in 0..<8 {
                group.addTask {
                    try await fixture.client.send(
                        AgentRequestEnvelope(
                            requestID: "request-\(index)",
                            method: .systemStatus,
                            client: AgentClientInfo(name: "test", version: "1")
                        )
                    )
                }
            }
            return try await group.reduce(into: []) { $0.append($1) }
        }

        #expect(responses.count == 8)
        #expect(responses.allSatisfy { $0.ok })
        #expect(Set(responses.map(\.requestID)).count == 8)
    }

    @Test("client cancellation interrupts a pending response")
    func cancellation() async throws {
        let fixture = try makeFixture { request in
            try? await Task.sleep(for: .seconds(5))
            return .success(requestID: request.requestID)
        }
        defer { fixture.server.stop() }

        let task = Task {
            try await fixture.client.send(
                AgentRequestEnvelope(
                    requestID: "cancel-1",
                    method: .systemStatus,
                    client: AgentClientInfo(name: "test", version: "1")
                )
            )
        }
        try await Task.sleep(for: .milliseconds(20))
        task.cancel()

        do {
            _ = try await task.value
            Issue.record("Cancelled Bridge request unexpectedly succeeded")
        } catch is CancellationError {
            // Expected: cancelling the caller shuts down the pending socket read.
        }
    }

    @Test("idle connections are dropped after the receive timeout and never starve real requests")
    func idleConnectionsDoNotStarveServer() async throws {
        let fixture = try makeFixture(receiveTimeout: 0.3)
        defer { fixture.server.stop() }

        // Open many connections that never send a byte (like `nc -U`).
        var idle: [Int32] = []
        defer { idle.forEach { _ = Darwin.close($0) } }
        for _ in 0..<24 {
            idle.append(try connectRaw(to: fixture.server.socketURL))
        }

        // Real requests are still served while the idle connections are open.
        let response = try await fixture.client.send(
            AgentRequestEnvelope(
                requestID: "while-idle",
                method: .systemStatus,
                client: AgentClientInfo(name: "test", version: "1")
            )
        )
        #expect(response.ok)

        // The server closes each idle connection once the receive timeout passes:
        // a blocking read then sees end-of-file instead of hanging.
        let first = try #require(idle.first)
        TaUnixSocketIO.setTimeouts(fileDescriptor: first, receive: 5, send: 5)
        #expect(throws: TaBridgeClientError.connectionClosed) {
            _ = try TaUnixSocketIO.readPayload(fileDescriptor: first)
        }
    }

    @Test("the server keeps accepting after clients abort their connections")
    func acceptLoopSurvivesAbortedClients() async throws {
        let fixture = try makeFixture()
        defer { fixture.server.stop() }

        for _ in 0..<16 {
            Darwin.close(try connectRaw(to: fixture.server.socketURL))
        }
        let response = try await fixture.client.send(
            AgentRequestEnvelope(
                requestID: "after-aborts",
                method: .systemStatus,
                client: AgentClientInfo(name: "test", version: "1")
            )
        )

        #expect(response.ok)
    }

    private func connectRaw(to socketURL: URL) throws -> Int32 {
        let descriptor = Darwin.socket(AF_UNIX, SOCK_STREAM, 0)
        guard descriptor >= 0 else {
            throw TaBridgeClientError.socketFailure(operation: "socket", code: errno)
        }
        var address = try TaUnixSocketIO.makeAddress(path: socketURL.path)
        let result = withUnsafePointer(to: &address) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.connect(descriptor, $0, socklen_t(MemoryLayout<sockaddr_un>.size))
            }
        }
        guard result == 0 else {
            let code = errno
            Darwin.close(descriptor)
            throw TaBridgeClientError.socketFailure(operation: "connect", code: code)
        }
        return descriptor
    }

    @Test("socket is private to the current user")
    func socketPermissions() throws {
        let fixture = try makeFixture()
        defer { fixture.server.stop() }

        let attributes = try FileManager.default.attributesOfItem(atPath: fixture.server.socketURL.path)
        let permissions = try #require(attributes[.posixPermissions] as? NSNumber)
        #expect(permissions.intValue & 0o777 == 0o600)
    }

    private func makeFixture(
        receiveTimeout: TimeInterval = TaAgentBridgeConnection.defaultReceiveTimeout,
        handler: @escaping TaAgentRequestRouter.Handler = { request in
            .success(
                requestID: request.requestID,
                data: .object(["method": .string(request.method.rawValue)])
            )
        }
    ) throws -> (server: TaAgentBridgeServer, client: TaBridgeClient) {
        let name = "ta-agent-\(UUID().uuidString.prefix(8)).sock"
        let socketURL = FileManager.default.temporaryDirectory.appendingPathComponent(name)
        let router = TaAgentRequestRouter(handler: handler)
        let server = TaAgentBridgeServer(socketURL: socketURL, router: router, receiveTimeout: receiveTimeout)
        try server.start()
        return (server, TaBridgeClient(socketURL: socketURL, timeout: 2))
    }
}
