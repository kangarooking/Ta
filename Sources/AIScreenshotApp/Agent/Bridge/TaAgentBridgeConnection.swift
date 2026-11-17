import Darwin
import Foundation
import TaAgentClient
import TaAgentContracts

/// Serves one accepted Bridge connection.
///
/// All socket I/O here is blocking and therefore must run on a dedicated thread
/// (see `TaAgentBridgeServer`), never on Swift's cooperative pool: an idle or
/// slow peer would otherwise pin a pool thread and could starve the whole app.
enum TaAgentBridgeConnection {
    static let defaultReceiveTimeout: TimeInterval = 10
    static let defaultSendTimeout: TimeInterval = 30

    /// Blocking. Reads one request, routes it, writes one response, closes the socket.
    static func serve(
        fileDescriptor: Int32,
        expectedUserID: uid_t,
        router: TaAgentRequestRouter,
        receiveTimeout: TimeInterval = defaultReceiveTimeout,
        sendTimeout: TimeInterval = defaultSendTimeout
    ) {
        defer { Darwin.close(fileDescriptor) }

        var peerUserID = uid_t(0)
        var peerGroupID = gid_t(0)
        guard getpeereid(fileDescriptor, &peerUserID, &peerGroupID) == 0,
              peerUserID == expectedUserID else {
            return
        }

        // A client that connects and then stays silent is dropped after the
        // receive timeout instead of holding its thread forever.
        TaUnixSocketIO.setTimeouts(
            fileDescriptor: fileDescriptor,
            receive: receiveTimeout,
            send: sendTimeout
        )
        let deadline = receiveTimeout > 0 ? Date().addingTimeInterval(receiveTimeout) : nil

        do {
            let requestData = try TaUnixSocketIO.readPayload(
                fileDescriptor: fileDescriptor,
                deadline: deadline
            )
            let request = try AgentJSONCoding.decoder().decode(AgentRequestEnvelope.self, from: requestData)
            let response = routeBlocking(request, router: router)
            let responseData = try AgentJSONCoding.encoder().encode(response)
            try TaUnixSocketIO.writePayload(responseData, fileDescriptor: fileDescriptor)
        } catch let decodingError as DecodingError {
            let response = AgentResponseEnvelope.failure(
                requestID: "unknown",
                error: AgentErrorPayload(
                    code: .invalidRequest,
                    message: "无法解析 Bridge 请求：\(decodingError)",
                    retryable: false
                )
            )
            if let data = try? AgentJSONCoding.encoder().encode(response) {
                try? TaUnixSocketIO.writePayload(data, fileDescriptor: fileDescriptor)
            }
        } catch {
            return
        }
    }

    /// Runs the async router and parks the calling (dedicated, non-pool) thread
    /// until the response is ready. The request itself runs on the cooperative
    /// pool, where it only ever suspends and never blocks.
    private static func routeBlocking(
        _ request: AgentRequestEnvelope,
        router: TaAgentRequestRouter
    ) -> AgentResponseEnvelope {
        let box = ResponseBox()
        let semaphore = DispatchSemaphore(value: 0)
        Task {
            let response = await router.route(request)
            box.store(response)
            semaphore.signal()
        }
        semaphore.wait()
        return box.take() ?? .failure(
            requestID: request.requestID,
            error: AgentErrorPayload(code: .internalError, message: "Bridge 未产生响应。", retryable: false)
        )
    }
}

private final class ResponseBox: @unchecked Sendable {
    private let lock = NSLock()
    private var response: AgentResponseEnvelope?

    func store(_ value: AgentResponseEnvelope) {
        lock.lock()
        response = value
        lock.unlock()
    }

    func take() -> AgentResponseEnvelope? {
        lock.lock()
        defer { lock.unlock() }
        return response
    }
}
