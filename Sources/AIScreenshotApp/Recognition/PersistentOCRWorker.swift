import Darwin
import Foundation

private struct OCRWorkerReadyResponse: Decodable {
    let event: String
    let ok: Bool
}

private struct OCRWorkerRequest: Encodable {
    let id: String
    let command: String
    let input: String?
    let detectionSideLimit: Int?
}

private struct OCRWorkerRecognitionResponse: Decodable {
    let id: String?
    let ok: Bool
    let text: String?
    let confidence: Float?
    let lines: [OCRPackLine]?
    let error: String?
}

/// Cancellation signal shared between the awaiting task and the worker
/// queue, which blocks in `poll` and cannot observe `Task.isCancelled`.
private final class OCRWorkerCancellation: @unchecked Sendable {
    private let lock = NSLock()
    private var cancelled = false

    var isCancelled: Bool {
        lock.lock()
        defer { lock.unlock() }
        return cancelled
    }

    func cancel() {
        lock.lock()
        cancelled = true
        lock.unlock()
    }
}

private struct OCRWorkerTimeout: Error {}

/// Owns the optional OCR subprocess on a private serial queue. Blocking pipe
/// reads never run on the main actor, and serialization prevents responses
/// from separate screenshots from being interleaved.
final class PersistentOCRWorker: @unchecked Sendable {
    static let shared = PersistentOCRWorker()

    private let queue = DispatchQueue(label: "app.ai-screenshot.ocr-worker", qos: .userInitiated)
    private let idleTimeout: TimeInterval
    private var process: Process?
    private var inputHandle: FileHandle?
    private var outputHandle: FileHandle?
    private var outputBuffer = Data()
    private var processKey: String?
    private var activityGeneration = 0

    /// A cold start loads the models; a warm request takes about a second.
    private let startupTimeout: TimeInterval
    private let requestTimeout: TimeInterval

    init(
        idleTimeout: TimeInterval = 300,
        startupTimeout: TimeInterval = 60,
        requestTimeout: TimeInterval = 30
    ) {
        self.idleTimeout = idleTimeout
        self.startupTimeout = startupTimeout
        self.requestTimeout = requestTimeout
    }

    func warm(executable: URL, arguments: [String]) {
        queue.async { [weak self] in
            guard let self else { return }
            do {
                try self.startIfNeeded(executable: executable, arguments: arguments, cancellation: nil)
                self.scheduleIdleStop()
            } catch {
                self.stopSync()
            }
        }
    }

    /// Honours task cancellation: a cancelled request stops waiting within
    /// ~100 ms and the in-flight worker is terminated. The worst-case wait
    /// is one start-up plus one request timeout (no retry after a timeout).
    func recognize(
        executable: URL,
        arguments: [String],
        imageURL: URL,
        detectionSideLimit: Int = 2560
    ) async throws -> OCRPackResponse {
        let cancellation = OCRWorkerCancellation()
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation {
                (continuation: CheckedContinuation<OCRPackResponse, Error>) in
                queue.async { [weak self] in
                    guard let self else {
                        continuation.resume(throwing: OptionalOCRPackError.executionFailed("OCR Worker 已释放"))
                        return
                    }
                    do {
                        let response = try self.recognizeSync(
                            executable: executable,
                            arguments: arguments,
                            imageURL: imageURL,
                            detectionSideLimit: detectionSideLimit,
                            cancellation: cancellation
                        )
                        continuation.resume(returning: response)
                    } catch {
                        continuation.resume(throwing: error)
                    }
                }
            }
        } onCancel: {
            cancellation.cancel()
        }
    }

    func stop() {
        queue.async { [weak self] in self?.stopSync() }
    }

    private func recognizeSync(
        executable: URL,
        arguments: [String],
        imageURL: URL,
        detectionSideLimit: Int,
        cancellation: OCRWorkerCancellation
    ) throws -> OCRPackResponse {
        var lastError: Error = OptionalOCRPackError.invalidResponse
        for attempt in 0..<2 {
            if cancellation.isCancelled { throw CancellationError() }
            do {
                try startIfNeeded(executable: executable, arguments: arguments, cancellation: cancellation)
                let id = UUID().uuidString
                let request = OCRWorkerRequest(
                    id: id,
                    command: "recognize",
                    input: imageURL.path,
                    detectionSideLimit: detectionSideLimit
                )
                try write(request)
                let data = try readLine(timeout: requestTimeout, cancellation: cancellation)
                let envelope = try JSONDecoder().decode(OCRWorkerRecognitionResponse.self, from: data)
                guard envelope.id == id else { throw OptionalOCRPackError.invalidResponse }
                guard envelope.ok else {
                    throw OptionalOCRPackError.executionFailed(envelope.error ?? "Worker 识别失败")
                }
                guard let text = envelope.text, let confidence = envelope.confidence else {
                    throw OptionalOCRPackError.invalidResponse
                }
                scheduleIdleStop()
                return OCRPackResponse(text: text, confidence: confidence, lines: envelope.lines)
            } catch is CancellationError {
                // The worker may be mid-request; restart it next time.
                stopSync()
                throw CancellationError()
            } catch is OCRWorkerTimeout {
                // A hung worker is not retried: that would double the wait.
                stopSync()
                throw OptionalOCRPackError.executionFailed("OCR Worker 响应超时")
            } catch {
                lastError = error
                stopSync()
                if attempt == 0 { continue }
            }
        }
        throw lastError
    }

    private func startIfNeeded(
        executable: URL,
        arguments: [String],
        cancellation: OCRWorkerCancellation?
    ) throws {
        let key = ([executable.path] + arguments).joined(separator: "\u{0}")
        if let process, process.isRunning, processKey == key {
            return
        }
        stopSync()

        let inputPipe = Pipe()
        let outputPipe = Pipe()
        let newProcess = Process()
        newProcess.executableURL = executable
        newProcess.arguments = arguments
        newProcess.standardInput = inputPipe
        newProcess.standardOutput = outputPipe
        newProcess.standardError = FileHandle.nullDevice
        try newProcess.run()

        // Writing to a worker that just died must fail with EPIPE instead of
        // raising SIGPIPE, which would terminate the whole app.
        _ = fcntl(inputPipe.fileHandleForWriting.fileDescriptor, F_SETNOSIGPIPE, 1)

        process = newProcess
        inputHandle = inputPipe.fileHandleForWriting
        outputHandle = outputPipe.fileHandleForReading
        outputBuffer.removeAll(keepingCapacity: true)
        processKey = key

        do {
            let readyData = try readLine(timeout: startupTimeout, cancellation: cancellation)
            let ready = try JSONDecoder().decode(OCRWorkerReadyResponse.self, from: readyData)
            guard ready.ok, ready.event == "ready" else {
                throw OptionalOCRPackError.invalidResponse
            }
        } catch {
            stopSync()
            throw error
        }
    }

    private func write<T: Encodable>(_ value: T) throws {
        guard let inputHandle else {
            throw OptionalOCRPackError.executionFailed("OCR Worker 输入管道不可用")
        }
        var data = try JSONEncoder().encode(value)
        data.append(0x0A)
        // POSIX write so a closed pipe surfaces as an error code (EPIPE)
        // rather than an Objective-C exception or a signal.
        let descriptor = inputHandle.fileDescriptor
        var offset = 0
        while offset < data.count {
            let written = data.withUnsafeBytes { buffer -> Int in
                guard let base = buffer.baseAddress else { return 0 }
                return Darwin.write(descriptor, base + offset, buffer.count - offset)
            }
            if written < 0 {
                if errno == EINTR { continue }
                let reason = String(cString: strerror(errno))
                throw OptionalOCRPackError.executionFailed("OCR Worker 写入失败：\(reason)")
            }
            if written == 0 {
                throw OptionalOCRPackError.executionFailed("OCR Worker 写入失败")
            }
            offset += written
        }
    }

    private func readLine(timeout: TimeInterval, cancellation: OCRWorkerCancellation?) throws -> Data {
        guard let outputHandle else {
            throw OptionalOCRPackError.executionFailed("OCR Worker 输出管道不可用")
        }
        let deadline = Date().addingTimeInterval(timeout)
        while true {
            if let newline = outputBuffer.firstIndex(of: 0x0A) {
                let line = outputBuffer[..<newline]
                outputBuffer.removeSubrange(...newline)
                guard !line.isEmpty else { continue }
                return Data(line)
            }
            guard let process, process.isRunning else {
                throw OptionalOCRPackError.executionFailed("OCR Worker 已退出")
            }
            if cancellation?.isCancelled == true { throw CancellationError() }
            let remaining = deadline.timeIntervalSinceNow
            guard remaining > 0 else { throw OCRWorkerTimeout() }
            var descriptor = pollfd(
                fd: outputHandle.fileDescriptor,
                events: Int16(POLLIN | POLLHUP | POLLERR),
                revents: 0
            )
            let milliseconds = Int32(max(1, min(100, remaining * 1000)))
            let status = Darwin.poll(&descriptor, 1, milliseconds)
            if status < 0 {
                if errno == EINTR { continue }
                throw OptionalOCRPackError.executionFailed("OCR Worker 管道读取失败")
            }
            if status == 0 { continue }
            if descriptor.revents & Int16(POLLIN | POLLHUP) != 0 {
                var bytes = [UInt8](repeating: 0, count: 64 * 1024)
                let count = Darwin.read(outputHandle.fileDescriptor, &bytes, bytes.count)
                guard count > 0 else {
                    if count < 0, errno == EINTR { continue }
                    throw OptionalOCRPackError.executionFailed("OCR Worker 输出已关闭")
                }
                outputBuffer.append(contentsOf: bytes.prefix(count))
            } else if descriptor.revents & Int16(POLLERR) != 0 {
                throw OptionalOCRPackError.executionFailed("OCR Worker 输出管道异常")
            }
        }
    }

    private func scheduleIdleStop() {
        activityGeneration += 1
        let expectedGeneration = activityGeneration
        queue.asyncAfter(deadline: .now() + idleTimeout) { [weak self] in
            guard let self, self.activityGeneration == expectedGeneration else { return }
            self.stopSync()
        }
    }

    private func stopSync() {
        activityGeneration += 1
        try? inputHandle?.close()
        inputHandle = nil
        try? outputHandle?.close()
        outputHandle = nil
        outputBuffer.removeAll(keepingCapacity: false)
        processKey = nil
        if let process, process.isRunning {
            terminateOCRProcess(process)
        }
        process = nil
    }
}
