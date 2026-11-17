import Darwin
import Foundation
import Testing
@testable import TaAgentClient
import TaAgentContracts

@Suite("Ta Bridge client framing")
struct TaBridgeClientTests {
    @Test("length-prefixed frames preserve multiline JSON")
    func frameRoundTrip() throws {
        let request = AgentRequestEnvelope(
            requestID: "frame-1",
            method: .analyzeImage,
            params: ["prompt": .string("line one\nline two")],
            client: AgentClientInfo(name: "test", version: "1")
        )
        let payload = try AgentJSONCoding.encoder().encode(request)
        let frame = try AgentFrameCodec.frame(payload)

        let decodedPayload = try AgentFrameCodec.payload(from: frame)
        let decoded = try AgentJSONCoding.decoder().decode(AgentRequestEnvelope.self, from: decodedPayload)

        #expect(decoded == request)
    }

    @Test("a silent peer produces a distinct timeout error instead of blocking forever")
    func readTimesOut() throws {
        var descriptors: [Int32] = [0, 0]
        #expect(socketpair(AF_UNIX, SOCK_STREAM, 0, &descriptors) == 0)
        defer { descriptors.forEach { _ = Darwin.close($0) } }
        TaUnixSocketIO.setTimeouts(fileDescriptor: descriptors[0], receive: 0.2, send: 0.2)

        let started = Date()
        #expect(throws: TaBridgeClientError.timedOut(operation: "read")) {
            _ = try TaUnixSocketIO.readPayload(fileDescriptor: descriptors[0])
        }
        #expect(Date().timeIntervalSince(started) < 5)
    }

    @Test("an overall deadline stops a peer that trickles a partial frame")
    func readDeadline() throws {
        var descriptors: [Int32] = [0, 0]
        #expect(socketpair(AF_UNIX, SOCK_STREAM, 0, &descriptors) == 0)
        defer { descriptors.forEach { _ = Darwin.close($0) } }
        TaUnixSocketIO.setTimeouts(fileDescriptor: descriptors[0], receive: 0.2, send: 0.2)
        // Two of the four header bytes: the frame never completes.
        let partial: [UInt8] = [0, 0]
        #expect(Darwin.send(descriptors[1], partial, partial.count, 0) == 2)

        #expect(throws: TaBridgeClientError.timedOut(operation: "read")) {
            _ = try TaUnixSocketIO.readPayload(
                fileDescriptor: descriptors[0],
                deadline: Date().addingTimeInterval(-1)
            )
        }
    }

    @Test("frames still round-trip over a socket with timeouts applied")
    func socketRoundTrip() throws {
        var descriptors: [Int32] = [0, 0]
        #expect(socketpair(AF_UNIX, SOCK_STREAM, 0, &descriptors) == 0)
        defer { descriptors.forEach { _ = Darwin.close($0) } }
        TaUnixSocketIO.setTimeouts(fileDescriptor: descriptors[0], receive: 2, send: 2)
        let payload = Data("{\"hello\":\"拓\"}".utf8)

        try TaUnixSocketIO.writePayload(payload, fileDescriptor: descriptors[1])
        let received = try TaUnixSocketIO.readPayload(
            fileDescriptor: descriptors[0],
            deadline: Date().addingTimeInterval(2)
        )

        #expect(received == payload)
    }

    @Test("truncated and oversized frames are rejected")
    func invalidFrames() throws {
        #expect(throws: AgentFrameError.truncatedHeader) {
            try AgentFrameCodec.payload(from: Data([0, 0, 0]))
        }

        let declaredLength = UInt32(12).bigEndian
        var frame = withUnsafeBytes(of: declaredLength) { Data($0) }
        frame.append(Data([1, 2]))
        #expect(throws: AgentFrameError.truncatedPayload(expected: 12, actual: 2)) {
            try AgentFrameCodec.payload(from: frame)
        }

        #expect(throws: AgentFrameError.frameTooLarge(AgentFrameCodec.maximumPayloadBytes + 1)) {
            try AgentFrameCodec.frame(Data(count: AgentFrameCodec.maximumPayloadBytes + 1))
        }
    }
}

