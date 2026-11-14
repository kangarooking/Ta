import AIScreenshotCore
import Foundation
import XCTest
@testable import AIScreenshotApp

final class PersistentOCRWorkerIdleTimeoutTests: XCTestCase {
    private var workerScriptURL: URL?

    override func tearDown() {
        super.tearDown()
        workerScriptURL = nil
    }

    /// Creates a worker script that reports ready and then stays alive.
    private func makeResidentWorkerScript() throws -> URL {
        let script = #"""
        #!/bin/zsh
        print -r -- '{"event":"ready","ok":true}'
        while true; do sleep 1; done
        """#
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("ocr-idle-worker-\(UUID().uuidString).sh")
        try script.write(to: url, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: url.path)
        workerScriptURL = url
        return url
    }

    func testFiniteIdleTimeoutReleasesWorkerAfterIdle() async throws {
        let script = try makeResidentWorkerScript()
        defer { try? FileManager.default.removeItem(at: script) }
        let worker = PersistentOCRWorker(idleTimeout: 0.5)

        worker.warm(executable: script, arguments: [])
        XCTAssertTrue(worker.isProcessRunning)

        let deadline = Date().addingTimeInterval(5)
        while Date() < deadline && worker.isProcessRunning {
            try await Task.sleep(for: .milliseconds(50))
        }
        XCTAssertFalse(worker.isProcessRunning, "Worker should be released after the 0.5s idle timeout")
        worker.stop()
    }

    func testZeroIdleTimeoutKeepsWorkerResident() async throws {
        let script = try makeResidentWorkerScript()
        defer { try? FileManager.default.removeItem(at: script) }
        let worker = PersistentOCRWorker(idleTimeout: 0.5)

        worker.warm(executable: script, arguments: [])
        XCTAssertTrue(worker.isProcessRunning)

        worker.setIdleTimeout(0)
        try await Task.sleep(for: .seconds(2.5))
        XCTAssertTrue(worker.isProcessRunning, "Resident mode (timeout 0) must not release the worker")
        worker.stop()
        XCTAssertFalse(worker.isProcessRunning)
    }

    func testChangingIdleTimeoutReschedulesPendingStop() async throws {
        let script = try makeResidentWorkerScript()
        defer { try? FileManager.default.removeItem(at: script) }
        let worker = PersistentOCRWorker(idleTimeout: 0.3)

        worker.warm(executable: script, arguments: [])
        XCTAssertTrue(worker.isProcessRunning)

        worker.setIdleTimeout(0)
        try await Task.sleep(for: .seconds(1.2))
        XCTAssertTrue(worker.isProcessRunning, "Pending stop under the old timeout must be cancelled")

        worker.setIdleTimeout(0.3)
        let deadline = Date().addingTimeInterval(5)
        while Date() < deadline && worker.isProcessRunning {
            try await Task.sleep(for: .milliseconds(50))
        }
        XCTAssertFalse(worker.isProcessRunning, "A new finite timeout must re-arm the idle release")
        worker.stop()
    }

    func testIdleTimeoutOptionsHaveStableRawValues() {
        XCTAssertEqual(OCRPackIdleTimeout.fiveMinutes.rawValue, 300)
        XCTAssertEqual(OCRPackIdleTimeout.fifteenMinutes.rawValue, 900)
        XCTAssertEqual(OCRPackIdleTimeout.oneHour.rawValue, 3600)
        XCTAssertEqual(OCRPackIdleTimeout.never.rawValue, 0)
        XCTAssertEqual(OCRPackIdleTimeout.never.seconds, 0)
        XCTAssertEqual(OCRPackIdleTimeout.fiveMinutes.seconds, 300)
        XCTAssertFalse(OCRPackIdleTimeout.allCases.contains { $0 == OCRPackIdleTimeout(rawValue: 12345) })
    }
}
