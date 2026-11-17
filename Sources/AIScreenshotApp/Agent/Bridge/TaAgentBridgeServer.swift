import Darwin
import Foundation
import TaAgentClient

enum TaAgentBridgeServerError: Error, Equatable {
    case alreadyRunning
    case pathOccupied(String)
    case socketFailure(operation: String, code: Int32)
}

final class TaAgentBridgeServer: @unchecked Sendable {
    let socketURL: URL

    private let router: TaAgentRequestRouter
    private let lock = NSLock()
    private let acceptQueue = DispatchQueue(label: "com.ta.agent-bridge.accept", qos: .userInitiated)
    private let receiveTimeout: TimeInterval
    private let sendTimeout: TimeInterval
    private let maximumConnections: Int
    private var listeningDescriptor: Int32 = -1
    private var activeConnections = 0

    init(
        socketURL: URL,
        router: TaAgentRequestRouter,
        receiveTimeout: TimeInterval = TaAgentBridgeConnection.defaultReceiveTimeout,
        sendTimeout: TimeInterval = TaAgentBridgeConnection.defaultSendTimeout,
        maximumConnections: Int = 64
    ) {
        self.socketURL = socketURL
        self.router = router
        self.receiveTimeout = receiveTimeout
        self.sendTimeout = sendTimeout
        self.maximumConnections = max(1, maximumConnections)
    }

    func start() throws {
        lock.lock()
        defer { lock.unlock() }
        guard listeningDescriptor < 0 else { throw TaAgentBridgeServerError.alreadyRunning }

        let fileManager = FileManager.default
        let parent = socketURL.deletingLastPathComponent()
        try fileManager.createDirectory(at: parent, withIntermediateDirectories: true)
        _ = parent.path.withCString { Darwin.chmod($0, S_IRWXU) }

        if fileManager.fileExists(atPath: socketURL.path) {
            var info = stat()
            let status = socketURL.path.withCString { Darwin.lstat($0, &info) }
            guard status == 0, (info.st_mode & S_IFMT) == S_IFSOCK else {
                throw TaAgentBridgeServerError.pathOccupied(socketURL.path)
            }
            try fileManager.removeItem(at: socketURL)
        }

        let descriptor = Darwin.socket(AF_UNIX, SOCK_STREAM, 0)
        guard descriptor >= 0 else {
            throw TaAgentBridgeServerError.socketFailure(operation: "socket", code: errno)
        }

        do {
            var address = try TaUnixSocketIO.makeAddress(path: socketURL.path)
            let bindResult = withUnsafePointer(to: &address) { pointer in
                pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    Darwin.bind(descriptor, $0, socklen_t(MemoryLayout<sockaddr_un>.size))
                }
            }
            guard bindResult == 0 else {
                throw TaAgentBridgeServerError.socketFailure(operation: "bind", code: errno)
            }
            guard socketURL.path.withCString({ Darwin.chmod($0, S_IRUSR | S_IWUSR) }) == 0 else {
                throw TaAgentBridgeServerError.socketFailure(operation: "chmod", code: errno)
            }
            guard Darwin.listen(descriptor, SOMAXCONN) == 0 else {
                throw TaAgentBridgeServerError.socketFailure(operation: "listen", code: errno)
            }
        } catch {
            Darwin.close(descriptor)
            try? fileManager.removeItem(at: socketURL)
            throw error
        }

        listeningDescriptor = descriptor
        let router = self.router
        let expectedUserID = Darwin.geteuid()
        acceptQueue.async { [weak self] in
            self?.acceptConnections(
                listeningDescriptor: descriptor,
                expectedUserID: expectedUserID,
                router: router
            )
        }
    }

    func stop() {
        lock.lock()
        let descriptor = listeningDescriptor
        listeningDescriptor = -1
        lock.unlock()

        if descriptor >= 0 {
            _ = Darwin.shutdown(descriptor, SHUT_RDWR)
            Darwin.close(descriptor)
        }
        try? FileManager.default.removeItem(at: socketURL)
    }

    deinit {
        stop()
    }

    private func acceptConnections(
        listeningDescriptor: Int32,
        expectedUserID: uid_t,
        router: TaAgentRequestRouter
    ) {
        var backoff: TimeInterval = 0.01
        while isListening(on: listeningDescriptor) {
            let clientDescriptor = Darwin.accept(listeningDescriptor, nil, nil)
            if clientDescriptor >= 0 {
                backoff = 0.01
                startConnection(clientDescriptor, expectedUserID: expectedUserID, router: router)
                continue
            }
            let code = errno
            switch code {
            case EINTR, ECONNABORTED:
                // The call was interrupted or the peer gave up before accept: just try again.
                continue
            case EBADF, EINVAL, ENOTSOCK, EOPNOTSUPP:
                // The listening socket is gone (stop() closed it): leave for good.
                return
            default:
                // EMFILE, ENFILE, ENOBUFS, ENOMEM and friends are transient: back off
                // briefly so in-flight connections can release resources, then resume.
                Thread.sleep(forTimeInterval: backoff)
                backoff = min(backoff * 2, 0.5)
            }
        }
    }

    private func isListening(on descriptor: Int32) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        return listeningDescriptor == descriptor
    }

    /// Hands the connection to its own thread. Blocking socket I/O must never run
    /// on Swift's cooperative pool or on a width-limited dispatch queue.
    private func startConnection(
        _ clientDescriptor: Int32,
        expectedUserID: uid_t,
        router: TaAgentRequestRouter
    ) {
        lock.lock()
        let admitted = activeConnections < maximumConnections
        if admitted { activeConnections += 1 }
        lock.unlock()
        guard admitted else {
            // Too many simultaneous clients: refuse instead of growing without bound.
            Darwin.close(clientDescriptor)
            return
        }

        let receiveTimeout = self.receiveTimeout
        let sendTimeout = self.sendTimeout
        let thread = Thread { [weak self] in
            TaAgentBridgeConnection.serve(
                fileDescriptor: clientDescriptor,
                expectedUserID: expectedUserID,
                router: router,
                receiveTimeout: receiveTimeout,
                sendTimeout: sendTimeout
            )
            self?.connectionFinished()
        }
        thread.name = "com.ta.agent-bridge.connection"
        thread.qualityOfService = .userInitiated
        thread.start()
    }

    private func connectionFinished() {
        lock.lock()
        activeConnections = max(0, activeConnections - 1)
        lock.unlock()
    }
}
