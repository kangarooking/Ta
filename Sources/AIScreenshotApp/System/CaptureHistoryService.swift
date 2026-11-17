import AppKit
import Foundation

/// Optional local screenshot history. When the user enables "在本机保存截图历史"
/// every completed capture is also written as a PNG into
/// `~/Pictures/Ta/History`; only the newest `maximumFileCount` files are kept.
struct CaptureHistoryService: Sendable {
    static let enabledDefaultsKey = "saveHistory"
    static let defaultMaximumFileCount = 200
    static let filePrefix = "Ta-"

    let directory: URL
    let maximumFileCount: Int

    init(
        directory: URL = CaptureHistoryService.defaultDirectory,
        maximumFileCount: Int = CaptureHistoryService.defaultMaximumFileCount
    ) {
        self.directory = directory
        self.maximumFileCount = max(1, maximumFileCount)
    }

    static var defaultDirectory: URL {
        let pictures = FileManager.default.urls(for: .picturesDirectory, in: .userDomainMask).first
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Pictures", isDirectory: true)
        return pictures
            .appendingPathComponent("Ta", isDirectory: true)
            .appendingPathComponent("History", isDirectory: true)
    }

    static var isEnabled: Bool {
        UserDefaults.standard.bool(forKey: enabledDefaultsKey)
    }

    /// Fire-and-forget entry point used by the capture flows. Does nothing
    /// unless the user enabled history; encoding and disk I/O happen off the
    /// main thread and failures are deliberately silent (history must never
    /// break a capture).
    func recordIfEnabled(_ images: [CGImage], scale: CGFloat? = nil) {
        guard Self.isEnabled, !images.isEmpty else { return }
        let boxed = images.map(UncheckedSendableImage.init(image:))
        let service = self
        Task.detached(priority: .utility) {
            for item in boxed {
                _ = try? service.store(item.image, scale: scale ?? 2)
            }
        }
    }

    func recordIfEnabled(_ image: CGImage, scale: CGFloat? = nil) {
        recordIfEnabled([image], scale: scale)
    }

    /// Writes one PNG and prunes the folder. Synchronous; call off the main
    /// thread for large images.
    @discardableResult
    func store(_ image: CGImage, scale: CGFloat = 2, date: Date = Date()) throws -> URL {
        let fileManager = FileManager.default
        try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
        guard let data = ClipboardImageEncoder.pngData(for: image, scale: scale) else {
            throw CocoaError(.fileWriteUnknown)
        }
        let url = uniqueURL(for: date, fileManager: fileManager)
        try data.write(to: url, options: .atomic)
        prune(fileManager: fileManager)
        return url
    }

    /// History files, newest first.
    func historyFiles(fileManager: FileManager = .default) -> [URL] {
        guard let contents = try? fileManager.contentsOfDirectory(
            at: directory,
            includingPropertiesForKeys: nil,
            options: [.skipsHiddenFiles]
        ) else {
            return []
        }
        // File names start with a sortable timestamp, so name order is
        // chronological and independent of file-system date resolution.
        return contents
            .filter {
                $0.pathExtension.lowercased() == "png"
                    && $0.lastPathComponent.hasPrefix(Self.filePrefix)
            }
            .sorted { $0.lastPathComponent > $1.lastPathComponent }
    }

    func prune(fileManager: FileManager = .default) {
        let files = historyFiles(fileManager: fileManager)
        guard files.count > maximumFileCount else { return }
        for url in files[maximumFileCount...] {
            try? fileManager.removeItem(at: url)
        }
    }

    /// Creates the folder if needed and reveals it in Finder.
    @MainActor
    func openFolder() {
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        NSWorkspace.shared.open(directory)
    }

    static func fileName(for date: Date, sequence: Int = 0) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyyMMdd-HHmmss-SSS"
        let suffix = sequence > 0 ? String(format: "-%03d", sequence) : ""
        return "\(filePrefix)\(formatter.string(from: date))\(suffix).png"
    }

    private func uniqueURL(for date: Date, fileManager: FileManager) -> URL {
        var sequence = 0
        while true {
            let url = directory.appendingPathComponent(Self.fileName(for: date, sequence: sequence))
            if !fileManager.fileExists(atPath: url.path) { return url }
            sequence += 1
        }
    }
}
