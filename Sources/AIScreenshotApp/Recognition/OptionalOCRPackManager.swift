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

struct OCRPackResponse: Codable, Sendable {
    let text: String
    let confidence: Float
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

    @MainActor
    func chooseAndImport(_ engine: OCREnginePreference) throws -> String? {
        guard engine != .appleVision else { return nil }
        let panel = NSOpenPanel()
        panel.title = "导入 \(engine.displayName)"
        panel.message = "请选择包含 manifest.json 的已解压增强包目录。导入前会校验引擎、架构、可执行路径和 SHA-256。"
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let source = panel.url else { return nil }
        return try installDirectory(source, expectedEngine: engine, performHealthCheck: true).version
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

    func recognize(image: CGImage, engine: OCREnginePreference) async throws -> OCRResult {
        let directory = packDirectory(for: engine)
        let manifest = try readManifest(at: directory)
        guard manifest.engine == engine else { throw OptionalOCRPackError.wrongEngine }
        try validateCompatibility(architecture: manifest.architecture, minimumMacOS: manifest.minimumMacOS)
        let executable = try validateExecutable(manifest: manifest, packDirectory: directory)
        return try await Task.detached(priority: .userInitiated) { [manifest] in
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
            return OCRResult(
                text: response.text,
                contentType: ContentClassifier().classify(response.text),
                confidence: response.confidence,
                engine: engine
            )
        }.value
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
        let data = try runProcessForOutput(
            executable: URL(fileURLWithPath: "/usr/bin/zipinfo"),
            arguments: ["-1", archive.path],
            timeout: 60
        )
        guard let listing = String(data: data, encoding: .utf8) else {
            throw OptionalOCRPackError.unsafeArchive
        }
        for line in listing.split(separator: "\n") {
            let path = String(line)
            let components = path.split(separator: "/", omittingEmptySubsequences: false)
            if path.hasPrefix("/") || path.contains("\\") || components.contains("..") {
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
            return try await packs.recognize(image: image, engine: engine)
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
