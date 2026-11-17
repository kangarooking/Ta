import Foundation
import Testing
@testable import AIScreenshotApp

@Suite("Ta Agent file policy")
struct TaAgentFilePolicyTests {
    @Test("save destinations must be visible PNG paths outside protected locations")
    func saveDestinationRules() throws {
        let fixture = try PolicyFixture()
        defer { fixture.cleanUp() }
        let policy = fixture.policy

        let allowed = try policy.saveDestination(
            path: fixture.documents.appendingPathComponent("shot.png").path,
            overwrite: false
        )
        #expect(allowed.lastPathComponent == "shot.png")
        let completed = try policy.saveDestination(
            path: fixture.documents.appendingPathComponent("shot").path,
            overwrite: false
        )
        #expect(completed.lastPathComponent == "shot.png")
        let upperCase = try policy.saveDestination(
            path: fixture.documents.appendingPathComponent("SHOT.PNG").path,
            overwrite: false
        )
        #expect(upperCase.lastPathComponent == "SHOT.PNG")

        let rejected = [
            "relative/shot.png",
            fixture.documents.appendingPathComponent("notes.txt").path,
            fixture.documents.appendingPathComponent("run.sh").path,
            fixture.documents.appendingPathComponent(".hidden.png").path,
            fixture.home.appendingPathComponent(".ssh/authorized_keys.png").path,
            fixture.home.appendingPathComponent("Library/LaunchAgents/agent.png").path,
            fixture.home.appendingPathComponent("Library/Caches/Other/shot.png").path,
            fixture.documents.appendingPathComponent("missing-folder/shot.png").path,
            "/System/Library/shot.png",
            "/usr/local/bin/shot.png",
            "/etc/shot.png",
            "/private/etc/shot.png",
            "/Library/LaunchDaemons/shot.png",
            "/Applications/shot.png"
        ]
        for path in rejected {
            #expect(throws: TaAgentFilePolicyError.self, "accepted \(path)") {
                _ = try policy.saveDestination(path: path, overwrite: true)
            }
        }
    }

    @Test("Ta's own artifact directory inside ~/Library stays writable")
    func artifactRootException() throws {
        let fixture = try PolicyFixture()
        defer { fixture.cleanUp() }

        let destination = try fixture.policy.saveDestination(
            path: fixture.artifactRoot.appendingPathComponent("copy.png").path,
            overwrite: false
        )
        #expect(destination.lastPathComponent == "copy.png")
    }

    @Test("existing files require overwrite, and non-regular targets are always refused")
    func overwriteRules() throws {
        let fixture = try PolicyFixture()
        defer { fixture.cleanUp() }
        let existing = fixture.documents.appendingPathComponent("existing.png")
        try Data("png".utf8).write(to: existing)
        let folder = fixture.documents.appendingPathComponent("folder.png", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let link = fixture.documents.appendingPathComponent("link.png")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: existing)

        #expect(throws: TaAgentFilePolicyError.self) {
            _ = try fixture.policy.saveDestination(path: existing.path, overwrite: false)
        }
        #expect(try fixture.policy.saveDestination(path: existing.path, overwrite: true)
            .lastPathComponent == "existing.png")
        for path in [folder.path, link.path] {
            #expect(throws: TaAgentFilePolicyError.self) {
                _ = try fixture.policy.saveDestination(path: path, overwrite: true)
            }
        }
    }

    @Test("a symlinked parent cannot redirect a save into a protected directory")
    func symlinkedParentIsResolved() throws {
        let fixture = try PolicyFixture()
        defer { fixture.cleanUp() }
        let launchAgents = fixture.home.appendingPathComponent("Library/LaunchAgents", isDirectory: true)
        try FileManager.default.createDirectory(at: launchAgents, withIntermediateDirectories: true)
        let shortcut = fixture.documents.appendingPathComponent("shortcut", isDirectory: true)
        try FileManager.default.createSymbolicLink(at: shortcut, withDestinationURL: launchAgents)

        #expect(throws: TaAgentFilePolicyError.self) {
            _ = try fixture.policy.saveDestination(
                path: shortcut.appendingPathComponent("agent.png").path,
                overwrite: false
            )
        }
    }

    @Test("readable inputs are regular files with an image extension")
    func readableImageRules() throws {
        let fixture = try PolicyFixture()
        defer { fixture.cleanUp() }
        for name in ["a.png", "b.JPG", "c.jpeg", "d.tiff", "e.heic", "f.webp", "g.gif", "h.bmp"] {
            let url = fixture.documents.appendingPathComponent(name)
            try Data("x".utf8).write(to: url)
            #expect(try fixture.policy.readableImage(path: url.path).lastPathComponent == name)
        }

        let text = fixture.documents.appendingPathComponent("id_rsa")
        try Data("key".utf8).write(to: text)
        let disguised = fixture.documents.appendingPathComponent("disguised.png")
        try FileManager.default.createSymbolicLink(at: disguised, withDestinationURL: text)
        let folder = fixture.documents.appendingPathComponent("folder.png", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)

        for path in [
            text.path, disguised.path, folder.path,
            fixture.documents.appendingPathComponent("missing.png").path,
            fixture.documents.appendingPathComponent("doc.pdf").path,
            "relative.png"
        ] {
            #expect(throws: TaAgentFilePolicyError.self, "accepted \(path)") {
                _ = try fixture.policy.readableImage(path: path)
            }
        }
    }

    @Test("path canonicalization treats /private aliases alike")
    func canonicalPaths() {
        #expect(TaAgentFilePolicy.canonicalPath(URL(fileURLWithPath: "/private/etc/hosts")) == "/etc/hosts")
        #expect(TaAgentFilePolicy.isPath("/Users/me/Library/x.png", inside: "/Users/me/Library"))
        #expect(!TaAgentFilePolicy.isPath("/Users/me/LibraryBackup/x.png", inside: "/Users/me/Library"))
    }
}

private struct PolicyFixture {
    let root: URL
    let home: URL
    let documents: URL
    let artifactRoot: URL
    let policy: TaAgentFilePolicy

    init() throws {
        // Resolve symlinks up front so expectations do not depend on /var vs /private/var.
        root = FileManager.default.temporaryDirectory
            .appendingPathComponent("ta-file-policy-\(UUID().uuidString)", isDirectory: true)
            .resolvingSymlinksInPath()
        home = root.appendingPathComponent("home", isDirectory: true)
        documents = home.appendingPathComponent("Documents", isDirectory: true)
        artifactRoot = home.appendingPathComponent("Library/Caches/Ta/AgentRuns", isDirectory: true)
        for directory in [documents, artifactRoot, home.appendingPathComponent("Library/Caches/Other")] {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        }
        policy = TaAgentFilePolicy(homeDirectory: home, artifactRoot: artifactRoot)
    }

    func cleanUp() {
        try? FileManager.default.removeItem(at: root)
    }
}
