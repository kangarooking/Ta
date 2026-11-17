import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

enum ModelImageEncoderError: Error {
    case encodingFailed
}

/// One image encoding for every cloud / self-hosted model request: JPEG with
/// a bounded long edge. PNG at 2560 px made uploads several times larger
/// without improving recognition.
enum ModelImageEncoder {
    static let mimeType = "image/jpeg"
    static let defaultMaximumDimension = 2048
    static let defaultQuality: CGFloat = 0.9

    /// Thread-safe (pure Core Graphics / ImageIO). The image is flattened
    /// onto white because JPEG has no alpha channel.
    static func jpegData(
        from image: CGImage,
        maximumDimension: Int = defaultMaximumDimension,
        quality: CGFloat = defaultQuality
    ) throws -> Data {
        let longEdge = max(image.width, image.height)
        guard longEdge > 0 else { throw ModelImageEncoderError.encodingFailed }
        let scale = maximumDimension > 0 ? min(1, Double(maximumDimension) / Double(longEdge)) : 1
        let width = max(1, Int((Double(image.width) * scale).rounded()))
        let height = max(1, Int((Double(image.height) * scale).rounded()))

        guard let context = CGContext(
            data: nil,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: CGColorSpace(name: CGColorSpace.sRGB) ?? CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue
        ) else { throw ModelImageEncoderError.encodingFailed }
        let bounds = CGRect(x: 0, y: 0, width: width, height: height)
        context.setFillColor(red: 1, green: 1, blue: 1, alpha: 1)
        context.fill(bounds)
        context.interpolationQuality = .high
        context.draw(image, in: bounds)
        guard let flattened = context.makeImage() else { throw ModelImageEncoderError.encodingFailed }

        let data = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(
            data,
            UTType.jpeg.identifier as CFString,
            1,
            nil
        ) else { throw ModelImageEncoderError.encodingFailed }
        let properties: [CFString: Any] = [kCGImageDestinationLossyCompressionQuality: quality]
        CGImageDestinationAddImage(destination, flattened, properties as CFDictionary)
        guard CGImageDestinationFinalize(destination) else { throw ModelImageEncoderError.encodingFailed }
        return data as Data
    }
}
