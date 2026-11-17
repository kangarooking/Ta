import Foundation

/// A file-system request from an Agent that the policy refuses. The message is
/// safe to return to the caller as an INVALID_REQUEST error.
struct TaAgentFilePolicyError: Error, Equatable, LocalizedError, Sendable {
    let message: String

    init(_ message: String) { self.message = message }

    var errorDescription: String? { message }
}

/// Restricts where `deliver.save` may write and what `inputPath` may read, so a
/// local Agent cannot use Ta as a generic "write/read any file" primitive.
struct TaAgentFilePolicy: Sendable {
    static let readableImageExtensions: Set<String> = [
        "png", "jpg", "jpeg", "tiff", "heic", "webp", "gif", "bmp"
    ]

    /// Top-level locations that are never valid save destinations.
    private static let systemDirectories: [String] = [
        "/System", "/Library", "/Applications", "/bin", "/sbin", "/usr", "/etc", "/dev",
        "/opt", "/cores", "/Network", "/var", "/private"
    ]
    /// Exceptions inside `systemDirectories`: per-user and shared temporary storage.
    private static let allowedSystemSubdirectories: [String] = [
        "/var/folders", "/var/tmp", "/tmp"
    ]

    let homeDirectory: URL
    let artifactRoot: URL

    init(
        homeDirectory: URL = FileManager.default.homeDirectoryForCurrentUser,
        artifactRoot: URL
    ) {
        self.homeDirectory = homeDirectory
        self.artifactRoot = artifactRoot
    }

    /// Validates a `deliver.save` destination and returns the URL to write.
    func saveDestination(path: String, overwrite: Bool) throws -> URL {
        guard path.hasPrefix("/") else {
            throw TaAgentFilePolicyError("deliver.save 需要绝对路径 path。")
        }
        var url = URL(fileURLWithPath: path).standardizedFileURL
        guard !url.lastPathComponent.isEmpty, url.path != "/", !path.hasSuffix("/") else {
            throw TaAgentFilePolicyError("deliver.save 的 path 必须是文件路径。")
        }
        let pathExtension = url.pathExtension.lowercased()
        if pathExtension.isEmpty {
            url = url.appendingPathExtension("png")
        } else if pathExtension != "png" {
            throw TaAgentFilePolicyError("deliver.save 只能保存 PNG，文件扩展名必须是 .png：\(url.path)")
        }

        // Resolve symlinks in the parent so a link cannot redirect the write into
        // a protected location; the file name itself is kept as given.
        let parent = url.deletingLastPathComponent().resolvingSymlinksInPath()
        let resolved = parent.appendingPathComponent(url.lastPathComponent, isDirectory: false)
        for candidate in [url, resolved] {
            try validateWritableLocation(candidate)
        }

        let fileManager = FileManager.default
        var isDirectory: ObjCBool = false
        guard fileManager.fileExists(atPath: parent.path, isDirectory: &isDirectory),
              isDirectory.boolValue else {
            throw TaAgentFilePolicyError("保存目录不存在：\(parent.path)")
        }

        // attributesOfItem does not follow a trailing symlink, so links are detected here.
        if let attributes = try? fileManager.attributesOfItem(atPath: resolved.path) {
            guard (attributes[.type] as? FileAttributeType) == .typeRegular else {
                throw TaAgentFilePolicyError("保存路径已存在且不是普通文件：\(resolved.path)")
            }
            guard overwrite else {
                throw TaAgentFilePolicyError(
                    "文件已存在：\(resolved.path)。如需覆盖请传入 overwrite=true（CLI：--overwrite）。"
                )
            }
        }
        return resolved
    }

    /// Validates an `inputPath` and returns the resolved regular image file URL.
    func readableImage(path: String) throws -> URL {
        guard path.hasPrefix("/") else {
            throw TaAgentFilePolicyError("inputPath 必须是绝对路径。")
        }
        let url = URL(fileURLWithPath: path).standardizedFileURL
        let resolved = url.resolvingSymlinksInPath()
        for candidate in [url, resolved] {
            guard Self.readableImageExtensions.contains(candidate.pathExtension.lowercased()) else {
                throw TaAgentFilePolicyError(
                    "inputPath 只接受图片文件（png、jpg、jpeg、tiff、heic、webp、gif、bmp）：\(candidate.path)"
                )
            }
        }
        guard let attributes = try? FileManager.default.attributesOfItem(atPath: resolved.path) else {
            throw TaAgentFilePolicyError("找不到图片文件：\(resolved.path)")
        }
        guard (attributes[.type] as? FileAttributeType) == .typeRegular else {
            throw TaAgentFilePolicyError("inputPath 必须是普通图片文件：\(resolved.path)")
        }
        return resolved
    }

    private func validateWritableLocation(_ url: URL) throws {
        let path = Self.canonicalPath(url)
        let components = path.split(separator: "/").map(String.init)
        if components.contains(where: { $0.hasPrefix(".") }) {
            throw TaAgentFilePolicyError("不能保存到隐藏文件或隐藏目录（以 . 开头）：\(path)")
        }

        let library = Self.canonicalPath(homeDirectory.appendingPathComponent("Library", isDirectory: true))
        if Self.isPath(path, inside: library),
           !Self.isPath(path, inside: Self.canonicalPath(artifactRoot)) {
            throw TaAgentFilePolicyError("不能保存到 ~/Library（拓自己的 Agent 缓存目录除外）：\(path)")
        }

        if Self.allowedSystemSubdirectories.contains(where: { Self.isPath(path, inside: $0) }) {
            return
        }
        if Self.systemDirectories.contains(where: { Self.isPath(path, inside: $0) }) {
            throw TaAgentFilePolicyError("不能保存到系统目录：\(path)")
        }
    }

    /// Standardized path with the `/private` alias of `/var`, `/tmp` and `/etc`
    /// removed, so both spellings are judged identically.
    static func canonicalPath(_ url: URL) -> String {
        var path = url.standardizedFileURL.path
        for alias in ["/private/var", "/private/tmp", "/private/etc"]
        where path == alias || path.hasPrefix(alias + "/") {
            path.removeFirst("/private".count)
            break
        }
        while path.count > 1, path.hasSuffix("/") { path.removeLast() }
        return path
    }

    /// True when `path` is `directory` itself or located below it.
    static func isPath(_ path: String, inside directory: String) -> Bool {
        let lhs = path.lowercased()
        let rhs = directory.lowercased()
        return lhs == rhs || lhs.hasPrefix(rhs.hasSuffix("/") ? rhs : rhs + "/")
    }
}
