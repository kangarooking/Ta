import AppKit
import ImageIO
import UniformTypeIdentifiers
import AIScreenshotCore

/// Encodes screenshots for the pasteboard. Pure ImageIO, so it is safe to call
/// from any thread.
enum ClipboardImageEncoder {
    struct Payload: Sendable {
        let png: Data
        let tiff: Data?
    }

    /// 72 dpi is one pixel per point; a Retina (2×) capture is therefore
    /// 144 dpi. Without this metadata Notes/Pages/Keynote paste the image at
    /// twice its on-screen size.
    static func dpi(forScale scale: CGFloat) -> Double {
        let safeScale = scale.isFinite && scale >= 1 ? scale : 1
        return Double(72 * safeScale)
    }

    static func pngData(for image: CGImage, scale: CGFloat) -> Data? {
        encode(image, type: UTType.png, scale: scale, extraProperties: [:])
    }

    static func tiffData(for image: CGImage, scale: CGFloat) -> Data? {
        encode(
            image,
            type: UTType.tiff,
            scale: scale,
            extraProperties: [
                // 5 = LZW
                kCGImagePropertyTIFFDictionary: [kCGImagePropertyTIFFCompression: 5] as [CFString: Any]
            ]
        )
    }

    static func payload(for image: CGImage, scale: CGFloat) -> Payload? {
        guard let png = pngData(for: image, scale: scale) else { return nil }
        return Payload(png: png, tiff: tiffData(for: image, scale: scale))
    }

    private static func encode(
        _ image: CGImage,
        type: UTType,
        scale: CGFloat,
        extraProperties: [CFString: Any]
    ) -> Data? {
        let data = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(
            data as CFMutableData,
            type.identifier as CFString,
            1,
            nil
        ) else {
            return nil
        }
        let resolution = dpi(forScale: scale)
        var properties: [CFString: Any] = [
            kCGImagePropertyDPIWidth: resolution,
            kCGImagePropertyDPIHeight: resolution
        ]
        extraProperties.forEach { properties[$0.key] = $0.value }
        CGImageDestinationAddImage(destination, image, properties as CFDictionary)
        guard CGImageDestinationFinalize(destination) else { return nil }
        return data as Data
    }
}

/// `CGImage` is immutable; this lets one cross into a detached encoding task
/// regardless of whether the SDK already marks it `Sendable`.
struct UncheckedSendableImage: @unchecked Sendable {
    let image: CGImage
}

@MainActor
struct ClipboardService {
    private let pasteboard = NSPasteboard.general
    private let policy = ClipboardCommitPolicy()

    var changeCount: Int { pasteboard.changeCount }

    /// Scale used when the caller does not know which display the pixels came
    /// from.
    static var defaultImageScale: CGFloat {
        NSScreen.main?.backingScaleFactor ?? 2
    }

    func copyText(
        _ text: String,
        initialChangeCount: Int,
        jobIsLatest: Bool
    ) -> Bool {
        guard shouldCommit(initialChangeCount: initialChangeCount, jobIsLatest: jobIsLatest) else {
            return false
        }

        pasteboard.clearContents()
        return pasteboard.setString(text, forType: .string)
    }

    func copyImage(
        _ image: CGImage,
        scale: CGFloat? = nil,
        initialChangeCount: Int,
        jobIsLatest: Bool
    ) -> Bool {
        guard shouldCommit(initialChangeCount: initialChangeCount, jobIsLatest: jobIsLatest) else {
            return false
        }
        guard let payload = ClipboardImageEncoder.payload(
            for: image,
            scale: scale ?? Self.defaultImageScale
        ) else {
            return false
        }
        return write(payload)
    }

    /// Same contract as `copyImage`, but PNG/TIFF encoding happens off the
    /// main thread so large captures do not stall the UI. The commit guard is
    /// evaluated again after encoding.
    func copyImageEncodingInBackground(
        _ image: CGImage,
        scale: CGFloat? = nil,
        initialChangeCount: Int,
        jobIsLatest: @MainActor () -> Bool
    ) async -> Bool {
        guard shouldCommit(initialChangeCount: initialChangeCount, jobIsLatest: jobIsLatest()) else {
            return false
        }
        let resolvedScale = scale ?? Self.defaultImageScale
        let boxed = UncheckedSendableImage(image: image)
        let payload = await Task.detached(priority: .userInitiated) {
            ClipboardImageEncoder.payload(for: boxed.image, scale: resolvedScale)
        }.value
        guard let payload,
              shouldCommit(initialChangeCount: initialChangeCount, jobIsLatest: jobIsLatest()) else {
            return false
        }
        return write(payload)
    }

    private func shouldCommit(initialChangeCount: Int, jobIsLatest: Bool) -> Bool {
        policy.shouldCommit(
            initialChangeCount: initialChangeCount,
            currentChangeCount: pasteboard.changeCount,
            jobIsLatest: jobIsLatest
        )
    }

    private func write(_ payload: ClipboardImageEncoder.Payload) -> Bool {
        pasteboard.clearContents()
        let wrotePNG = pasteboard.setData(payload.png, forType: .png)
        if let tiff = payload.tiff {
            pasteboard.setData(tiff, forType: .tiff)
        }
        return wrotePNG
    }
}
