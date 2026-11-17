import CryptoKit
import Foundation
import Testing
@testable import AIScreenshotApp

@Suite("Ta Agent artifact store")
struct TaAgentArtifactStoreTests {
    @Test("writes an artifact atomically with verified metadata")
    func writesArtifact() async throws {
        let root = temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let now = Date(timeIntervalSince1970: 1_787_684_400)
        let store = TaAgentArtifactStore(rootDirectory: root, retention: 86_400)
        let data = Data("fake-png-payload".utf8)

        let artifact = try await store.save(
            data: data,
            requestID: "ta_request_1",
            filename: "capture.png",
            mimeType: "image/png",
            width: 320,
            height: 180,
            now: now
        )

        #expect(try Data(contentsOf: URL(fileURLWithPath: artifact.path)) == data)
        #expect(artifact.width == 320)
        #expect(artifact.height == 180)
        #expect(artifact.bytes == data.count)
        #expect(artifact.expiresAt == now.addingTimeInterval(86_400))
        #expect(artifact.sha256 == SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined())
    }

    @Test("rejects path traversal in request and file names")
    func rejectsUnsafeNames() async throws {
        let root = temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let store = TaAgentArtifactStore(rootDirectory: root)

        await #expect(throws: TaAgentArtifactStoreError.unsafePathComponent("../escape")) {
            try await store.save(
                data: Data(), requestID: "../escape", filename: "capture.png",
                mimeType: "image/png", width: nil, height: nil
            )
        }
        await #expect(throws: TaAgentArtifactStoreError.unsafePathComponent("../capture.png")) {
            try await store.save(
                data: Data(), requestID: "safe", filename: "../capture.png",
                mimeType: "image/png", width: nil, height: nil
            )
        }
    }

    @Test("cleanup removes expired request directories only")
    func cleanupExpiredArtifacts() async throws {
        let root = temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let store = TaAgentArtifactStore(rootDirectory: root, retention: 60, automaticCleanupInterval: nil)
        let old = Date(timeIntervalSince1970: 1_000)
        let current = Date(timeIntervalSince1970: 2_000)

        let expired = try await store.save(
            data: Data("old".utf8), requestID: "old", filename: "capture.png",
            mimeType: "image/png", width: nil, height: nil, now: old
        )
        let fresh = try await store.save(
            data: Data("new".utf8), requestID: "new", filename: "capture.png",
            mimeType: "image/png", width: nil, height: nil, now: current
        )

        let removed = try await store.cleanupExpired(now: current)

        #expect(removed == 1)
        #expect(!FileManager.default.fileExists(atPath: expired.path))
        #expect(FileManager.default.fileExists(atPath: fresh.path))
    }

    @Test("saving a new artifact opportunistically expires old ones, throttled")
    func saveTriggersThrottledCleanup() async throws {
        let root = temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let store = TaAgentArtifactStore(rootDirectory: root, retention: 60, automaticCleanupInterval: 600)
        let start = Date(timeIntervalSince1970: 10_000)

        // First save runs a cleanup (nothing to remove) and starts the throttle window.
        let first = try await store.save(
            data: Data("a".utf8), requestID: "first", filename: "capture.png",
            mimeType: "image/png", width: nil, height: nil, now: start
        )
        // Within the throttle window: "first" is already expired but must survive.
        _ = try await store.save(
            data: Data("b".utf8), requestID: "second", filename: "capture.png",
            mimeType: "image/png", width: nil, height: nil, now: start.addingTimeInterval(300)
        )
        #expect(FileManager.default.fileExists(atPath: first.path))
        #expect(await store.cleanupExpiredIfDue(now: start.addingTimeInterval(301)) == nil)

        // After the window: the next save expires both older request directories.
        let third = try await store.save(
            data: Data("c".utf8), requestID: "third", filename: "capture.png",
            mimeType: "image/png", width: nil, height: nil, now: start.addingTimeInterval(700)
        )
        #expect(!FileManager.default.fileExists(atPath: first.path))
        #expect(FileManager.default.fileExists(atPath: third.path))
        #expect(try FileManager.default.contentsOfDirectory(atPath: root.path) == ["third"])
    }

    @Test("clear removes all cached Agent request directories")
    func clearAllArtifacts() async throws {
        let root = temporaryRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let store = TaAgentArtifactStore(rootDirectory: root)
        _ = try await store.save(
            data: Data("one".utf8), requestID: "one", filename: "capture.png",
            mimeType: "image/png", width: nil, height: nil
        )
        _ = try await store.save(
            data: Data("two".utf8), requestID: "two", filename: "capture.png",
            mimeType: "image/png", width: nil, height: nil
        )

        #expect(try await store.clearAll() == 2)
        #expect(try FileManager.default.contentsOfDirectory(atPath: root.path).isEmpty)
    }

    private func temporaryRoot() -> URL {
        FileManager.default.temporaryDirectory
            .appendingPathComponent("ta-agent-artifact-tests-\(UUID().uuidString)", isDirectory: true)
    }
}
