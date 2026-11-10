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
    let error: String?
}

/// Owns the optional OCR subprocess on a private serial queue. Blocking pipe
/// reads never run on the main actor, and serialization prevents responses
/// from separate screenshots from being interleaved.
final class PersistentOCRWorker: @unchecked Sendable {
    static let shared = PersistentOCRWorker()

    private let queue = DispatchQueue(label: "app.ai-screenshot.ocr-worker", qos: .userInitiated)
    private var idleTimeout: TimeInterval
    private var process: Process?
    private var inputHandle: FileHandle?
    private var outputHandle: FileHandle?
    private var outputBuffer = Data()
    private var processKey: String?
    private var activityGeneration = 0

    init(idleTimeout: TimeInterval = 300) {
        self.idleTimeout = idleTimeout
    }

    func warm(executable: URL, arguments: [String]) {
        queue.async { [weak self] in
            guard let self else { return }
            do {
                try self.startIfNeeded(executable: executable, arguments: arguments)
                self.scheduleIdleStop()
            } catch {
                self.stopSync()
            }
        }
    }

    func recognize(
        executable: URL,
        arguments: [String],
        imageURL: URL,
        detectionSideLimit: Int = 2560
    ) async throws -> OCRPackResponse {
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
                        detectionSideLimit: detectionSideLimit
                    )
                    continuation.resume(returning: response)
                } catch {
                    continuation.resume(throwing: error)
                }
            }
        }
    }

    func stop() {
        queue.async { [weak self] in self?.stopSync() }
    }

    /// Applies a new idle-release timeout, rescheduling the pending stop when
    /// the worker is already running. A timeout of zero (or below) switches
    /// the worker into resident mode: it stays alive until `stop()` is called.
    func setIdleTimeout(_ timeout: TimeInterval) {
        queue.async { [weak self] in
            guard let self else { return }
            self.idleTimeout = max(0, timeout)
            if self.process != nil {
                self.scheduleIdleStop()
            }
        }
    }

    /// Test hook: reports whether the OCR subprocess is currently alive.
    var isProcessRunning: Bool {
        queue.sync { process?.isRunning ?? false }
    }

    private func recognizeSync(
        executable: URL,
        arguments: [String],
        imageURL: URL,
        detectionSideLimit: Int
    ) throws -> OCRPackResponse {
        var lastError: Error = OptionalOCRPackError.invalidResponse
        for attempt in 0..<2 {
            do {
                try startIfNeeded(executable: executable, arguments: arguments)
                let id = UUID().uuidString
                let request = OCRWorkerRequest(
                    id: id,
                    command: "recognize",
                    input: imageURL.path,
                    detectionSideLimit: detectionSideLimit
                )
                try write(request)
                let data = try readLine(timeout: 30)
                let envelope = try JSONDecoder().decode(OCRWorkerRecognitionResponse.self, from: data)
                guard envelope.id == id else { throw OptionalOCRPackError.invalidResponse }
                guard envelope.ok else {
                    throw OptionalOCRPackError.executionFailed(envelope.error ?? "Worker 识别失败")
                }
                guard let text = envelope.text, let confidence = envelope.confidence else {
                    throw OptionalOCRPackError.invalidResponse
                }
                scheduleIdleStop()
                return OCRPackResponse(text: text, confidence: confidence)
            } catch {
                lastError = error
                stopSync()
                if attempt == 0 { continue }
            }
        }
        throw lastError
    }

    private func startIfNeeded(executable: URL, arguments: [String]) throws {
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

        process = newProcess
        inputHandle = inputPipe.fileHandleForWriting
        outputHandle = outputPipe.fileHandleForReading
        outputBuffer.removeAll(keepingCapacity: true)
        processKey = key

        do {
            let readyData = try readLine(timeout: 120)
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
        do {
            try inputHandle.write(contentsOf: data)
        } catch {
            throw OptionalOCRPackError.executionFailed("OCR Worker 写入失败：\(error.localizedDescription)")
        }
    }

    private func readLine(timeout: TimeInterval) throws -> Data {
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
            let remaining = deadline.timeIntervalSinceNow
            guard remaining > 0 else {
                throw OptionalOCRPackError.executionFailed("OCR Worker 响应超时")
            }
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
        // Bumping the generation also cancels any pending stop scheduled
        // under a previous finite timeout.
        guard idleTimeout > 0 else { return }
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
