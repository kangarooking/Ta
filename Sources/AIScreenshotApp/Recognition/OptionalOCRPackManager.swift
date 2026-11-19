import AppKit
import AIScreenshotCore
import CryptoKit
import Darwin
import Foundation

struct OCRPackManifest: Codable, Equatable, Sendable {
    let engine: OCREnginePreference
    let version: String
    let executable: String
    let executableSHA256: String?
    let architecture: String?
    let minimumMacOS: String?
    let healthCheckArguments: [String]?
    let workerArguments: [String]?

    init(
        engine: OCREnginePreference,
        version: String,
        executable: String,
        executableSHA256: String?,
        architecture: String?,
        minimumMacOS: String?,
        healthCheckArguments: [String]?,
        workerArguments: [String]? = nil
    ) {
        self.engine = engine
        self.version = version
        self.executable = executable
        self.executableSHA256 = executableSHA256
        self.architecture = architecture
        self.minimumMacOS = minimumMacOS
        self.healthCheckArguments = healthCheckArguments
        self.workerArguments = workerArguments
    }
}

/// One recognised line reported by an OCR pack (protocol 2+).
struct OCRPackLine: Codable, Equatable, Sendable {
    let text: String
    let confidence: Float?
    /// `[minX, minY, maxX, maxY]` in pixels of the submitted image, with a
    /// top-left origin. Nil when the pack could not locate the line.
    let box: [Double]?

    init(text: String, confidence: Float? = nil, box: [Double]? = nil) {
        self.text = text
        self.confidence = confidence
        self.box = box
    }
}

struct OCRPackResponse: Codable, Sendable {
    let text: String
    let confidence: Float
    /// Per-line geometry. Absent for packs built before protocol 2, which
    /// keep working with text-only results.
    let lines: [OCRPackLine]?

    init(text: String, confidence: Float, lines: [OCRPackLine]? = nil) {
        self.text = text
        self.confidence = confidence
        self.lines = lines
    }

    /// Maps the pack's pixel boxes into the structure Apple Vision produces
    /// (normalised, bottom-left origin). Returns nil unless every non-empty
    /// line has a usable box, so text is never dropped for lack of geometry.
    func textLines(imageWidth: Int, imageHeight: Int) -> [OCRTextLine]? {
        guard let lines, imageWidth > 0, imageHeight > 0 else { return nil }
        let width = Double(imageWidth)
        let height = Double(imageHeight)
        var result: [OCRTextLine] = []
        for line in lines {
            let text = line.text.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !text.isEmpty else { continue }
            guard let box = line.box, box.count == 4, box.allSatisfy(\.isFinite) else { return nil }
            let minX = min(max(box[0], 0), width)
            let maxX = min(max(box[2], 0), width)
            let minY = min(max(box[1], 0), height)
            let maxY = min(max(box[3], 0), height)
            guard maxX > minX, maxY > minY else { return nil }
            result.append(OCRTextLine(
                text: text,
                confidence: line.confidence ?? confidence,
                boundingBox: CGRect(
                    x: minX / width,
                    y: 1 - maxY / height,
                    width: (maxX - minX) / width,
                    height: (maxY - minY) / height
                )
            ))
        }
        return result.isEmpty ? nil : result
    }
}

struct OCRPackHealthResponse: Codable, Sendable {
    let ok: Bool
    let engine: String
    let packVersion: String?
    let architecture: String?
    let offline: Bool?
}

struct OCRPackCatalog: Codable, Sendable {
    let schemaVersion: Int
    let packages: [OCRPackCatalogPackage]
}

struct OCRPackCatalogPackage: Codable, Equatable, Sendable {
    let engine: OCREnginePreference
    let version: String
    let architecture: String
    let minimumMacOS: String
    let downloadURL: String
    let archiveSHA256: String
    let archiveSize: Int64
}

struct OCRPackAvailability: Equatable, Sendable {
    let package: OCRPackCatalogPackage
    let isLocal: Bool

    var formattedSize: String {
        ByteCountFormatter.string(fromByteCount: package.archiveSize, countStyle: .file)
    }
}

struct OCRPackInstalledInfo: Equatable, Sendable {
    let version: String
    let architecture: String?
}

struct OCRPackInstallProgress: Equatable, Sendable {
    enum Stage: String, Sendable {
        case resolving = "正在查找增强包…"
        case downloading = "正在下载…"
        case verifying = "正在校验…"
        case extracting = "正在解压…"
        case healthChecking = "正在启动检查…"
        case installing = "正在安装…"
    }

    let stage: Stage
    let fraction: Double?
}

private struct ResolvedCatalogPackage: Sendable {
    let package: OCRPackCatalogPackage
    let archiveURL: URL
    let isLocal: Bool
}

enum OptionalOCRPackError: LocalizedError {
    case notInstalled
    case invalidManifest
    case invalidCatalog
    case packageUnavailable
    case wrongEngine
    case unsupportedArchitecture(String)
    case unsupportedSystem(String)
    case insecureDownloadURL
    case unsafeArchive
    case unsafeExecutablePath
    case checksumMismatch
    case healthCheckFailed(String)
    case executionFailed(String)
    case invalidResponse

    var errorDescription: String? {
        switch self {
        case .notInstalled: "所选 OCR 增强包尚未安装。"
        case .invalidManifest: "增强包缺少有效的 manifest.json。"
        case .invalidCatalog: "增强包发行清单格式无效。"
        case .packageUnavailable: "没有找到适合这台 Mac 的 PaddleOCR 增强包。"
        case .wrongEngine: "增强包类型与所选引擎不一致。"
        case .unsupportedArchitecture(let architecture): "增强包不支持当前架构：\(architecture)。"
        case .unsupportedSystem(let minimum): "增强包要求 macOS \(minimum) 或更高版本。"
        case .insecureDownloadURL: "增强包下载地址必须使用 HTTPS，开发版也可以使用本地文件。"
        case .unsafeArchive: "增强包压缩文件包含不安全路径，已拒绝安装。"
        case .unsafeExecutablePath: "增强包的可执行文件路径不安全。"
        case .checksumMismatch: "增强包校验失败，未安装。"
        case .healthCheckFailed(let message): "增强包启动检查失败：\(message)"
        case .executionFailed(let message): "OCR 增强包运行失败：\(message)"
        case .invalidResponse: "OCR 增强包返回格式无效。"
        }
    }
}

struct OptionalOCRPackManager: @unchecked Sendable {
    private let fileManager: FileManager
    private let applicationSupportRoot: URL
    private let bundleURL: URL
    private let session: URLSession

    init(
        fileManager: FileManager = .default,
        applicationSupportRoot: URL? = nil,
        bundleURL: URL = Bundle.main.bundleURL,
        session: URLSession = .shared
    ) {
        self.fileManager = fileManager
        self.applicationSupportRoot = applicationSupportRoot
            ?? fileManager.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        self.bundleURL = bundleURL
        self.session = session
    }

    func isInstalled(_ engine: OCREnginePreference) -> Bool {
        guard engine != .appleVision else { return true }
        return (try? validatedExecutable(for: engine)) != nil
    }

    func installedInfo(_ engine: OCREnginePreference) -> OCRPackInstalledInfo? {
        guard engine != .appleVision else { return nil }
        let directory = packDirectory(for: engine)
        guard let manifest = try? readManifest(at: directory),
              (try? validateExecutable(manifest: manifest, packDirectory: directory)) != nil else {
            return nil
        }
        return OCRPackInstalledInfo(version: manifest.version, architecture: manifest.architecture)
    }

    func availablePackage(for engine: OCREnginePreference) async throws -> OCRPackAvailability {
        let resolved = try await resolveRecommendedPackage(for: engine)
        return OCRPackAvailability(package: resolved.package, isLocal: resolved.isLocal)
    }

    /// Synchronous import kept for existing callers. It blocks the main
    /// thread during the health check and copy; prefer
    /// `chooseAndImportInBackground(_:)`.
    @MainActor
    func chooseAndImport(_ engine: OCREnginePreference) throws -> String? {
        guard engine != .appleVision else { return nil }
        guard let source = Self.choosePackDirectory(for: engine) else { return nil }
        return try installDirectory(source, expectedEngine: engine, performHealthCheck: true).version
    }

    /// Same as `chooseAndImport`, but only the open panel runs on the main
    /// actor: validation (SHA-256, health check that launches the pack) and
    /// the multi-hundred-megabyte copy run on a background task.
    @MainActor
    func chooseAndImportInBackground(_ engine: OCREnginePreference) async throws -> String? {
        guard engine != .appleVision else { return nil }
        guard let source = Self.choosePackDirectory(for: engine) else { return nil }
        return try await importDirectoryInBackground(source, expectedEngine: engine).version
    }

    /// Validates and installs a pack directory off the calling actor.
    func importDirectoryInBackground(
        _ source: URL,
        expectedEngine: OCREnginePreference,
        performHealthCheck: Bool = true
    ) async throws -> OCRPackInstalledInfo {
        let task = Task.detached(priority: .userInitiated) {
            try self.installDirectory(
                source,
                expectedEngine: expectedEngine,
                performHealthCheck: performHealthCheck
            )
        }
        return try await withTaskCancellationHandler {
            try await task.value
        } onCancel: {
            task.cancel()
        }
    }

    @MainActor
    private static func choosePackDirectory(for engine: OCREnginePreference) -> URL? {
        let panel = NSOpenPanel()
        panel.title = "导入 \(engine.displayName)"
        panel.message = "请选择包含 manifest.json 的已解压增强包目录。导入前会校验引擎、架构、可执行路径和 SHA-256。"
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK else { return nil }
        return panel.url
    }

    func installRecommended(
        for engine: OCREnginePreference,
        progress: @escaping @Sendable (OCRPackInstallProgress) -> Void
    ) async throws -> OCRPackInstalledInfo {
        guard engine != .appleVision else { throw OptionalOCRPackError.wrongEngine }
        progress(.init(stage: .resolving, fraction: nil))
        let resolved = try await resolveRecommendedPackage(for: engine)
        try validateCompatibility(architecture: resolved.package.architecture, minimumMacOS: resolved.package.minimumMacOS)

        let temporaryRoot = fileManager.temporaryDirectory
            .appendingPathComponent("ai-screenshot-ocr-download-\(UUID().uuidString)", isDirectory: true)
        try fileManager.createDirectory(at: temporaryRoot, withIntermediateDirectories: true)
        defer { try? fileManager.removeItem(at: temporaryRoot) }

        let archive = temporaryRoot.appendingPathComponent("pack.zip")
        if resolved.archiveURL.isFileURL {
            progress(.init(stage: .downloading, fraction: 1))
            try fileManager.copyItem(at: resolved.archiveURL, to: archive)
        } else {
            try await download(resolved.archiveURL, to: archive, progress: progress)
        }
        try Task.checkCancellation()

        progress(.init(stage: .verifying, fraction: 0))
        let archiveChecksum = try sha256(of: archive) { fraction in
            progress(.init(stage: .verifying, fraction: fraction * 0.9))
        }
        guard archiveChecksum == resolved.package.archiveSHA256.lowercased() else {
            throw OptionalOCRPackError.checksumMismatch
        }
        try validateArchiveEntries(archive)
        progress(.init(stage: .verifying, fraction: 1))

        progress(.init(stage: .extracting, fraction: nil))
        let extracted = temporaryRoot.appendingPathComponent("extracted", isDirectory: true)
        try fileManager.createDirectory(at: extracted, withIntermediateDirectories: true)
        try runProcess(
            executable: URL(fileURLWithPath: "/usr/bin/ditto"),
            arguments: ["-x", "-k", archive.path, extracted.path],
            timeout: 300
        )
        try validateExtractedSymlinks(in: extracted)
        let source = try locatePackRoot(in: extracted)

        progress(.init(stage: .healthChecking, fraction: nil))
        let manifest = try validateDirectory(source, expectedEngine: engine, performHealthCheck: true)
        guard manifest.version == resolved.package.version else { throw OptionalOCRPackError.invalidManifest }

        progress(.init(stage: .installing, fraction: nil))
        return try installValidatedDirectory(source, manifest: manifest, expectedEngine: engine)
    }

    func remove(_ engine: OCREnginePreference) throws {
        guard engine != .appleVision else { return }
        PersistentOCRWorker.shared.stop()
        let directory = packDirectory(for: engine)
        if fileManager.fileExists(atPath: directory.path) {
            try fileManager.removeItem(at: directory)
        }
    }

    func recognize(
        image: CGImage,
        engine: OCREnginePreference,
        mergeWrappedLines: Bool = false
    ) async throws -> OCRResult {
        let directory = packDirectory(for: engine)
        let manifest = try readManifest(at: directory)
        guard manifest.engine == engine else { throw OptionalOCRPackError.wrongEngine }
        try validateCompatibility(architecture: manifest.architecture, minimumMacOS: manifest.minimumMacOS)
        let executable = try validateExecutable(manifest: manifest, packDirectory: directory)
        // A detached task does not inherit cancellation; forward it so a
        // cancelled capture stops waiting on a slow or hung OCR worker.
        let work = Task.detached(priority: .userInitiated) { [manifest] () throws -> OCRResult in
            let temporary = FileManager.default.temporaryDirectory
                .appendingPathComponent("ai-screenshot-ocr-\(UUID().uuidString).png")
            defer { try? FileManager.default.removeItem(at: temporary) }
            let preparedImage = prepareOCRImage(image, maximumDimension: 2560)
            let rep = NSBitmapImageRep(cgImage: preparedImage)
            guard let png = rep.representation(using: .png, properties: [:]) else {
                throw OptionalOCRPackError.invalidResponse
            }
            try png.write(to: temporary, options: .atomic)

            let response: OCRPackResponse
            if let arguments = manifest.workerArguments, !arguments.isEmpty {
                response = try await PersistentOCRWorker.shared.recognize(
                    executable: executable,
                    arguments: arguments,
                    imageURL: temporary,
                    detectionSideLimit: 2560
                )
            } else {
                let data = try runProcessForOutput(
                    executable: executable,
                    arguments: ["--input", temporary.path, "--output", "json"],
                    timeout: 120
                )
                guard let decoded = try? JSONDecoder().decode(OCRPackResponse.self, from: data) else {
                    throw OptionalOCRPackError.invalidResponse
                }
                response = decoded
            }
            return Self.makeResult(
                from: response,
                imageWidth: preparedImage.width,
                imageHeight: preparedImage.height,
                engine: engine,
                mergeWrappedLines: mergeWrappedLines
            )
        }
        return try await withTaskCancellationHandler {
            try await work.value
        } onCancel: {
            work.cancel()
        }
    }

    /// Packs that report line boxes get the same structured result as Apple
    /// Vision (layout blocks for overlay translation, tables, Markdown);
    /// older packs fall back to their plain text.
    static func makeResult(
        from response: OCRPackResponse,
        imageWidth: Int,
        imageHeight: Int,
        engine: OCREnginePreference,
        mergeWrappedLines: Bool = false
    ) -> OCRResult {
        if let lines = response.textLines(imageWidth: imageWidth, imageHeight: imageHeight) {
            return OCRResultAssembler().assemble(
                lines: lines,
                mergeWrappedLines: mergeWrappedLines,
                engine: engine,
                confidence: response.confidence
            )
        }
        return OCRResult(
            text: response.text,
            contentType: ContentClassifier().classify(response.text),
            confidence: response.confidence,
            engine: engine
        )
    }

    func prewarm(_ engine: OCREnginePreference) {
        guard engine != .appleVision else {
            PersistentOCRWorker.shared.stop()
            return
        }
        let directory = packDirectory(for: engine)
        guard let manifest = try? readManifest(at: directory),
              let arguments = manifest.workerArguments,
              !arguments.isEmpty,
              let executable = try? validateExecutable(manifest: manifest, packDirectory: directory) else {
            PersistentOCRWorker.shared.stop()
            return
        }
        PersistentOCRWorker.shared.warm(executable: executable, arguments: arguments)
    }

    @discardableResult
    func installDirectory(
        _ source: URL,
        expectedEngine: OCREnginePreference,
        performHealthCheck: Bool = true
    ) throws -> OCRPackInstalledInfo {
        let manifest = try validateDirectory(
            source,
            expectedEngine: expectedEngine,
            performHealthCheck: performHealthCheck
        )
        return try installValidatedDirectory(source, manifest: manifest, expectedEngine: expectedEngine)
    }

    private func validateDirectory(
        _ source: URL,
        expectedEngine: OCREnginePreference,
        performHealthCheck: Bool
    ) throws -> OCRPackManifest {
        let manifest = try readManifest(at: source)
        guard manifest.engine == expectedEngine else { throw OptionalOCRPackError.wrongEngine }
        try validateCompatibility(architecture: manifest.architecture, minimumMacOS: manifest.minimumMacOS)
        let executable = try validateExecutable(manifest: manifest, packDirectory: source)
        if performHealthCheck { try healthCheck(executable: executable, manifest: manifest) }
        return manifest
    }

    private func installValidatedDirectory(
        _ source: URL,
        manifest: OCRPackManifest,
        expectedEngine: OCREnginePreference
    ) throws -> OCRPackInstalledInfo {
        PersistentOCRWorker.shared.stop()
        let destination = packDirectory(for: expectedEngine)
        try fileManager.createDirectory(at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
        let staged = destination.deletingLastPathComponent()
            .appendingPathComponent(".\(expectedEngine.rawValue)-staged-\(UUID().uuidString)")
        try fileManager.copyItem(at: source, to: staged)
        do {
            _ = try validateDirectory(staged, expectedEngine: expectedEngine, performHealthCheck: false)
            if fileManager.fileExists(atPath: destination.path) {
                _ = try fileManager.replaceItemAt(destination, withItemAt: staged)
            } else {
                try fileManager.moveItem(at: staged, to: destination)
            }
            _ = try validatedExecutable(for: expectedEngine)
            return OCRPackInstalledInfo(version: manifest.version, architecture: manifest.architecture)
        } catch {
            try? fileManager.removeItem(at: staged)
            throw error
        }
    }

    private func resolveRecommendedPackage(for engine: OCREnginePreference) async throws -> ResolvedCatalogPackage {
        let catalogURL = try catalogLocation()
        let data: Data
        if catalogURL.isFileURL {
            data = try Data(contentsOf: catalogURL)
        } else {
            let (downloaded, response) = try await session.data(from: catalogURL)
            guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
                throw OptionalOCRPackError.invalidCatalog
            }
            data = downloaded
        }
        guard let catalog = try? JSONDecoder().decode(OCRPackCatalog.self, from: data),
              catalog.schemaVersion == 1 else {
            throw OptionalOCRPackError.invalidCatalog
        }
        let architecture = Self.currentArchitecture
        guard let package = catalog.packages
            .filter({ $0.engine == engine && $0.architecture == architecture })
            .sorted(by: { $0.version.compare($1.version, options: .numeric) == .orderedDescending })
            .first else {
            throw OptionalOCRPackError.packageUnavailable
        }
        let archiveURL: URL
        if let absolute = URL(string: package.downloadURL), absolute.scheme != nil {
            archiveURL = absolute
        } else {
            archiveURL = catalogURL.deletingLastPathComponent().appendingPathComponent(package.downloadURL)
        }
        guard archiveURL.isFileURL || archiveURL.scheme?.lowercased() == "https" else {
            throw OptionalOCRPackError.insecureDownloadURL
        }
        return ResolvedCatalogPackage(package: package, archiveURL: archiveURL, isLocal: archiveURL.isFileURL)
    }

    private func catalogLocation() throws -> URL {
        if let configured = UserDefaults.standard.string(forKey: "ocrPackCatalogURL"),
           let url = URL(string: configured),
           url.scheme?.lowercased() == "https" {
            return url
        }
        let candidates = [
            bundleURL.deletingLastPathComponent().appendingPathComponent("ocr-packs/catalog.json"),
            bundleURL.appendingPathComponent("Contents/Resources/OCRPacks/catalog.json")
        ]
        if let local = candidates.first(where: { fileManager.fileExists(atPath: $0.path) }) {
            return local
        }
        throw OptionalOCRPackError.packageUnavailable
    }

    private func download(
        _ source: URL,
        to destination: URL,
        progress: @escaping @Sendable (OCRPackInstallProgress) -> Void
    ) async throws {
        let (bytes, response) = try await session.bytes(from: source)
        guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
            throw OptionalOCRPackError.packageUnavailable
        }
        _ = fileManager.createFile(atPath: destination.path, contents: nil)
        let handle = try FileHandle(forWritingTo: destination)
        defer { try? handle.close() }
        let total = response.expectedContentLength
        var received: Int64 = 0
        var buffer = Data()
        buffer.reserveCapacity(64 * 1024)
        for try await byte in bytes {
            try Task.checkCancellation()
            buffer.append(byte)
            received += 1
            if buffer.count >= 64 * 1024 {
                try handle.write(contentsOf: buffer)
                buffer.removeAll(keepingCapacity: true)
                let fraction = total > 0 ? min(1, Double(received) / Double(total)) : nil
                progress(.init(stage: .downloading, fraction: fraction))
            }
        }
        if !buffer.isEmpty { try handle.write(contentsOf: buffer) }
        progress(.init(stage: .downloading, fraction: 1))
    }

    private func validateArchiveEntries(_ archive: URL) throws {
        let names = try runProcessForOutput(
            executable: URL(fileURLWithPath: "/usr/bin/zipinfo"),
            arguments: ["-1", archive.path],
            timeout: 60
        )
        guard let listing = String(data: names, encoding: .utf8) else {
            throw OptionalOCRPackError.unsafeArchive
        }
        for line in listing.split(separator: "\n") {
            guard Self.isSafeArchivePath(String(line)) else { throw OptionalOCRPackError.unsafeArchive }
        }

        // The name-only listing cannot show entry types. Symlinks are legal
        // inside a pack (the bundled Python runtime uses them), but no entry
        // may be written *through* a symlink: that is how an archive escapes
        // its destination. Where each link points is verified after
        // extraction by `validateExtractedSymlinks`.
        let details = try runProcessForOutput(
            executable: URL(fileURLWithPath: "/usr/bin/zipinfo"),
            arguments: ["-s", archive.path],
            timeout: 60
        )
        guard let detailed = String(data: details, encoding: .utf8) else {
            throw OptionalOCRPackError.unsafeArchive
        }
        let entryPaths = listing.split(separator: "\n").map(String.init)
        guard !Self.archiveWritesThroughSymlink(
            symlinks: Self.archiveSymlinkPaths(inDetailedListing: detailed),
            entries: entryPaths
        ) else {
            throw OptionalOCRPackError.unsafeArchive
        }
    }

    static func isSafeArchivePath(_ path: String) -> Bool {
        let components = path.split(separator: "/", omittingEmptySubsequences: false)
        return !path.hasPrefix("/") && !path.contains("\\")
            && !components.contains("..")
    }

    /// Paths of symlink entries in `zipinfo -s` output. Entry lines look like
    /// `lrwxr-xr-x  3.0 unx  14 bx stor 26-Nov-17 16:25 dir/name with spaces`.
    static func archiveSymlinkPaths(inDetailedListing listing: String) -> [String] {
        listing.split(separator: "\n").compactMap { line -> String? in
            guard line.range(of: #"^l[-rwxsStT]{9}\s"#, options: .regularExpression) != nil else {
                return nil
            }
            let fields = line.split(separator: " ", maxSplits: 8, omittingEmptySubsequences: true)
            guard fields.count == 9 else { return "" }
            return String(fields[8])
        }
    }

    /// True when any entry lies below a symlink entry (or a symlink's path
    /// could not be parsed, which is treated as unsafe).
    static func archiveWritesThroughSymlink(symlinks: [String], entries: [String]) -> Bool {
        for symlink in symlinks {
            let link = symlink.hasSuffix("/") ? String(symlink.dropLast()) : symlink
            guard !link.isEmpty else { return true }
            if entries.contains(where: { $0.hasPrefix(link + "/") }) { return true }
        }
        return false
    }

    /// After extraction every symlink must resolve to a location inside the
    /// extracted tree; absolute targets and links that climb out are refused.
    func validateExtractedSymlinks(in directory: URL) throws {
        let root = directory.standardizedFileURL.resolvingSymlinksInPath().path
        guard let enumerator = fileManager.enumerator(
            at: directory,
            includingPropertiesForKeys: [.isSymbolicLinkKey],
            options: []
        ) else { throw OptionalOCRPackError.unsafeArchive }
        for case let url as URL in enumerator {
            guard (try? url.resourceValues(forKeys: [.isSymbolicLinkKey]))?.isSymbolicLink == true else {
                continue
            }
            guard let target = try? fileManager.destinationOfSymbolicLink(atPath: url.path),
                  !target.hasPrefix("/"), !target.hasPrefix("~") else {
                throw OptionalOCRPackError.unsafeArchive
            }
            let parent = url.deletingLastPathComponent().standardizedFileURL.resolvingSymlinksInPath()
            let resolved = parent.appendingPathComponent(target).standardizedFileURL
                .resolvingSymlinksInPath().path
            guard resolved == root || resolved.hasPrefix(root + "/") else {
                throw OptionalOCRPackError.unsafeArchive
            }
        }
    }

    private func locatePackRoot(in extracted: URL) throws -> URL {
        if fileManager.fileExists(atPath: extracted.appendingPathComponent("manifest.json").path) {
            return extracted
        }
        let children = try fileManager.contentsOfDirectory(
            at: extracted,
            includingPropertiesForKeys: [.isDirectoryKey],
            options: [.skipsHiddenFiles]
        )
        let roots = children.filter {
            fileManager.fileExists(atPath: $0.appendingPathComponent("manifest.json").path)
        }
        guard roots.count == 1, let root = roots.first else {
            throw OptionalOCRPackError.invalidManifest
        }
        return root
    }

    private func validatedExecutable(for engine: OCREnginePreference) throws -> URL {
        guard engine != .appleVision else { throw OptionalOCRPackError.notInstalled }
        let directory = packDirectory(for: engine)
        let manifest = try readManifest(at: directory)
        guard manifest.engine == engine else { throw OptionalOCRPackError.wrongEngine }
        try validateCompatibility(architecture: manifest.architecture, minimumMacOS: manifest.minimumMacOS)
        return try validateExecutable(manifest: manifest, packDirectory: directory)
    }

    private func readManifest(at directory: URL) throws -> OCRPackManifest {
        let url = directory.appendingPathComponent("manifest.json")
        guard let data = try? Data(contentsOf: url),
              let manifest = try? JSONDecoder().decode(OCRPackManifest.self, from: data) else {
            throw OptionalOCRPackError.invalidManifest
        }
        return manifest
    }

    private func validateExecutable(manifest: OCRPackManifest, packDirectory: URL) throws -> URL {
        let root = packDirectory.standardizedFileURL.resolvingSymlinksInPath()
        let executable = packDirectory.appendingPathComponent(manifest.executable)
            .standardizedFileURL.resolvingSymlinksInPath()
        guard executable.path.hasPrefix(root.path + "/"),
              fileManager.isExecutableFile(atPath: executable.path) else {
            throw OptionalOCRPackError.unsafeExecutablePath
        }
        if let expected = manifest.executableSHA256?.lowercased() {
            guard try sha256(of: executable) == expected else {
                throw OptionalOCRPackError.checksumMismatch
            }
        }
        return executable
    }

    private func validateCompatibility(architecture: String?, minimumMacOS: String?) throws {
        if let architecture, architecture != Self.currentArchitecture {
            throw OptionalOCRPackError.unsupportedArchitecture(Self.currentArchitecture)
        }
        if let minimumMacOS {
            let required = Self.operatingSystemVersion(minimumMacOS)
            if !ProcessInfo.processInfo.isOperatingSystemAtLeast(required) {
                throw OptionalOCRPackError.unsupportedSystem(minimumMacOS)
            }
        }
    }

    private func healthCheck(executable: URL, manifest: OCRPackManifest) throws {
        let arguments = manifest.healthCheckArguments ?? ["--health-check"]
        let data: Data
        do {
            data = try runProcessForOutput(
                executable: executable,
                arguments: arguments,
                timeout: 120
            )
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            throw OptionalOCRPackError.healthCheckFailed(error.localizedDescription)
        }
        guard let response = try? JSONDecoder().decode(OCRPackHealthResponse.self, from: data),
              response.ok,
              response.engine == manifest.engine.rawValue else {
            throw OptionalOCRPackError.healthCheckFailed("返回格式无效")
        }
        if let reported = response.architecture, reported != Self.currentArchitecture {
            throw OptionalOCRPackError.unsupportedArchitecture(Self.currentArchitecture)
        }
    }

    private func sha256(
        of url: URL,
        progress: ((Double) -> Void)? = nil
    ) throws -> String {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        let expectedBytes = try url.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0
        var processedBytes = 0
        var hasher = SHA256()
        while let data = try handle.read(upToCount: 1024 * 1024), !data.isEmpty {
            try Task.checkCancellation()
            hasher.update(data: data)
            processedBytes += data.count
            if expectedBytes > 0 {
                progress?(min(1, Double(processedBytes) / Double(expectedBytes)))
            }
        }
        progress?(1)
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }

    private func packDirectory(for engine: OCREnginePreference) -> URL {
        applicationSupportRoot
            .appendingPathComponent("AI Screenshot/OCRPacks", isDirectory: true)
            .appendingPathComponent(engine.rawValue, isDirectory: true)
    }

    private static var currentArchitecture: String {
#if arch(arm64)
        "arm64"
#elseif arch(x86_64)
        "x86_64"
#else
        "unknown"
#endif
    }

    private static func operatingSystemVersion(_ string: String) -> OperatingSystemVersion {
        let parts = string.split(separator: ".").compactMap { Int($0) }
        return OperatingSystemVersion(
            majorVersion: parts.indices.contains(0) ? parts[0] : 0,
            minorVersion: parts.indices.contains(1) ? parts[1] : 0,
            patchVersion: parts.indices.contains(2) ? parts[2] : 0
        )
    }
}

private func runProcess(
    executable: URL,
    arguments: [String],
    timeout: TimeInterval = 300
) throws {
    _ = try runProcessForOutput(
        executable: executable,
        arguments: arguments,
        timeout: timeout
    )
}

private func runProcessForOutput(
    executable: URL,
    arguments: [String],
    timeout: TimeInterval = 300
) throws -> Data {
    let fileManager = FileManager.default
    let captureDirectory = fileManager.temporaryDirectory
        .appendingPathComponent("ai-screenshot-process-\(UUID().uuidString)", isDirectory: true)
    try fileManager.createDirectory(at: captureDirectory, withIntermediateDirectories: true)
    defer { try? fileManager.removeItem(at: captureDirectory) }

    let outputURL = captureDirectory.appendingPathComponent("stdout")
    let errorURL = captureDirectory.appendingPathComponent("stderr")
    guard fileManager.createFile(atPath: outputURL.path, contents: nil),
          fileManager.createFile(atPath: errorURL.path, contents: nil) else {
        throw OptionalOCRPackError.executionFailed("无法创建子进程输出文件")
    }
    let output = try FileHandle(forWritingTo: outputURL)
    let errors = try FileHandle(forWritingTo: errorURL)
    defer {
        try? output.close()
        try? errors.close()
    }

    let process = Process()
    process.executableURL = executable
    process.arguments = arguments
    process.standardOutput = output
    process.standardError = errors
    try process.run()

    let deadline = Date().addingTimeInterval(timeout)
    while process.isRunning {
        if Task.isCancelled {
            terminateOCRProcess(process)
            throw CancellationError()
        }
        if Date() >= deadline {
            terminateOCRProcess(process)
            throw OptionalOCRPackError.executionFailed(
                "子进程执行超时：\(executable.lastPathComponent)"
            )
        }
        Thread.sleep(forTimeInterval: 0.05)
    }
    process.waitUntilExit()
    try output.synchronize()
    try errors.synchronize()
    let outputData = try Data(contentsOf: outputURL)
    let errorData = try Data(contentsOf: errorURL)
    guard process.terminationStatus == 0 else {
        let message = String(data: errorData, encoding: .utf8)
            ?? "exit \(process.terminationStatus)"
        throw OptionalOCRPackError.executionFailed(
            message.trimmingCharacters(in: .whitespacesAndNewlines)
        )
    }
    return outputData
}

func terminateOCRProcess(_ process: Process) {
    guard process.isRunning else { return }
    process.terminate()
    let deadline = Date().addingTimeInterval(1)
    while process.isRunning, Date() < deadline {
        Thread.sleep(forTimeInterval: 0.02)
    }
    if process.isRunning {
        kill(process.processIdentifier, SIGKILL)
    }
    process.waitUntilExit()
}

struct ConfiguredOCRService: Sendable {
    private let vision = VisionOCRService()
    private let packs = OptionalOCRPackManager()

    func prewarmIfNeeded() {
        let raw = UserDefaults.standard.string(forKey: "ocrEngine")
            ?? OCREnginePreference.appleVision.rawValue
        let engine = OCREnginePreference(rawValue: raw) ?? .appleVision
        if engine.usesOptionalPack, packs.isInstalled(engine) {
            packs.prewarm(engine)
        }
    }

    /// Applies the user's idle-release policy to the shared OCR worker before
    /// prewarming, so the pending stop is scheduled with the right timeout.
    func applyIdleTimeoutPolicy() {
        let stored = UserDefaults.standard.object(forKey: "ocrPackIdleTimeout") as? Int
        let seconds = stored.flatMap { timeout in
            OCRPackIdleTimeout(rawValue: timeout)?.rawValue
        }
        PersistentOCRWorker.shared.setIdleTimeout(TimeInterval(seconds ?? OCRPackIdleTimeout.fiveMinutes.rawValue))
    }

    func recognize(image: CGImage, languages: [String], mergeWrappedLines: Bool) async throws -> OCRResult {
        let raw = UserDefaults.standard.string(forKey: "ocrEngine")
            ?? OCREnginePreference.appleVision.rawValue
        let engine = OCREnginePreference(rawValue: raw) ?? .appleVision
        if engine == .deepSeekOCR2 {
            return try await DeepSeekOCRRecognitionService().recognize(
                image: image,
                languages: languages
            )
        }
        if engine.usesOptionalPack, packs.isInstalled(engine) {
            return try await packs.recognize(
                image: image,
                engine: engine,
                mergeWrappedLines: mergeWrappedLines
            )
        }
        return try await vision.recognize(
            image: image,
            languages: languages,
            mergeWrappedLines: mergeWrappedLines
        )
    }
}

func prepareOCRImage(_ image: CGImage, maximumDimension: Int) -> CGImage {
    let largestDimension = max(image.width, image.height)
    guard maximumDimension > 0, largestDimension > maximumDimension else { return image }
    let scale = Double(maximumDimension) / Double(largestDimension)
    let width = max(1, Int((Double(image.width) * scale).rounded()))
    let height = max(1, Int((Double(image.height) * scale).rounded()))
    guard let context = CGContext(
        data: nil,
        width: width,
        height: height,
        bitsPerComponent: 8,
        bytesPerRow: 0,
        space: CGColorSpaceCreateDeviceRGB(),
        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
    ) else {
        return image
    }
    context.interpolationQuality = .high
    context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
    return context.makeImage() ?? image
}
