import AppKit
import UniformTypeIdentifiers

enum ImageExportError: LocalizedError {
    case encodingFailed
    case writeFailed(Error)

    var errorDescription: String? {
        switch self {
        case .encodingFailed:
            "无法编码图片。"
        case .writeFailed(let error):
            "无法保存图片：\(error.localizedDescription)"
        }
    }
}

@MainActor
struct ImageExportService {
    func save(
        _ image: CGImage,
        suggestedName: String = "AI-Screenshot-\(Self.timestamp()).png"
    ) throws -> URL? {
        let panel = NSSavePanel()
        panel.title = "保存截图"
        panel.nameFieldStringValue = suggestedName
        panel.allowedContentTypes = [.png, .jpeg]
        panel.canCreateDirectories = true
        guard panel.runModal() == .OK, let url = panel.url else { return nil }

        try write(image, to: url)
        return url
    }

    func save(
        _ images: [CGImage],
        suggestedBaseName: String = "AI-Long-Screenshot.png"
    ) throws -> [URL]? {
        guard !images.isEmpty else { return [] }
        if images.count == 1 {
            return try save(images[0], suggestedName: suggestedBaseName).map { [$0] }
        }

        let panel = NSSavePanel()
        panel.title = "保存分段长截图"
        panel.message = "图片过长，将在所选位置保存为 \(images.count) 个连续编号的 PNG 文件。"
        panel.nameFieldStringValue = suggestedBaseName
        panel.allowedContentTypes = [.png]
        panel.canCreateDirectories = true
        guard panel.runModal() == .OK, let selectedURL = panel.url else { return nil }

        let directory = selectedURL.deletingLastPathComponent()
        let base = selectedURL.deletingPathExtension().lastPathComponent
        let digits = max(2, String(images.count).count)
        var urls: [URL] = []
        for (index, image) in images.enumerated() {
            let suffix = String(format: "%0*d", digits, index + 1)
            let url = directory.appendingPathComponent("\(base)-Part-\(suffix).png")
            try write(image, to: url)
            urls.append(url)
        }
        return urls
    }

    private func write(_ image: CGImage, to url: URL) throws {
        let representation = NSBitmapImageRep(cgImage: image)
        let isJPEG = ["jpg", "jpeg"].contains(url.pathExtension.lowercased())
        let data = isJPEG
            ? representation.representation(using: .jpeg, properties: [.compressionFactor: 0.92])
            : representation.representation(using: .png, properties: [:])
        guard let data else { throw ImageExportError.encodingFailed }
        do {
            try data.write(to: url, options: .atomic)
        } catch {
            throw ImageExportError.writeFailed(error)
        }
    }

    private static func timestamp() -> String {
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyy-MM-dd-HH-mm-ss"
        return formatter.string(from: Date())
    }
}
