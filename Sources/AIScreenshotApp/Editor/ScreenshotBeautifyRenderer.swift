import CoreGraphics
import Foundation

/// A background style for "美化": the capture is placed on a padded gradient
/// with rounded corners and a soft drop shadow.
struct ScreenshotBeautifyPreset: Equatable, Sendable, Identifiable {
    struct RGB: Equatable, Sendable {
        let red: CGFloat
        let green: CGFloat
        let blue: CGFloat
    }

    let id: String
    let displayName: String
    /// Gradient runs from the top-left to the bottom-right corner.
    let startColor: RGB
    let endColor: RGB
    /// Padding as a fraction of the capture's longer side.
    let paddingRatio: CGFloat
    /// Corner radius as a fraction of the capture's shorter side.
    let cornerRadiusRatio: CGFloat
    let shadowOpacity: CGFloat

    static let dusk = ScreenshotBeautifyPreset(
        id: "dusk",
        displayName: "暮色",
        startColor: RGB(red: 0.36, green: 0.42, blue: 0.94),
        endColor: RGB(red: 0.84, green: 0.38, blue: 0.72),
        paddingRatio: 0.08,
        cornerRadiusRatio: 0.03,
        shadowOpacity: 0.35
    )

    static let cinnabar = ScreenshotBeautifyPreset(
        id: "cinnabar",
        displayName: "朱砂",
        startColor: RGB(red: 0.98, green: 0.80, blue: 0.56),
        endColor: RGB(red: 0.84, green: 0.25, blue: 0.18),
        paddingRatio: 0.08,
        cornerRadiusRatio: 0.03,
        shadowOpacity: 0.32
    )

    static let paper = ScreenshotBeautifyPreset(
        id: "paper",
        displayName: "宣纸",
        startColor: RGB(red: 0.98, green: 0.96, blue: 0.93),
        endColor: RGB(red: 0.90, green: 0.87, blue: 0.81),
        paddingRatio: 0.07,
        cornerRadiusRatio: 0.025,
        shadowOpacity: 0.22
    )

    static let all: [ScreenshotBeautifyPreset] = [.dusk, .cinnabar, .paper]
    static let defaultsKey = "beautifyPreset"

    static func preset(id: String?) -> ScreenshotBeautifyPreset {
        all.first { $0.id == id } ?? .dusk
    }

    static func loadSelected(defaults: UserDefaults = .standard) -> ScreenshotBeautifyPreset {
        preset(id: defaults.string(forKey: defaultsKey))
    }
}

/// Pure Core Graphics renderer; no AppKit and no global state, so it can run
/// on any thread and is deterministic under test.
enum ScreenshotBeautifyRenderer {
    static let minimumPadding: CGFloat = 32
    static let maximumPadding: CGFloat = 320
    static let minimumCornerRadius: CGFloat = 8
    static let maximumCornerRadius: CGFloat = 48

    static func padding(for imageSize: CGSize, preset: ScreenshotBeautifyPreset) -> CGFloat {
        let longerSide = max(imageSize.width, imageSize.height)
        let proposed = (longerSide * preset.paddingRatio).rounded()
        return min(maximumPadding, max(minimumPadding, proposed))
    }

    static func cornerRadius(for imageSize: CGSize, preset: ScreenshotBeautifyPreset) -> CGFloat {
        let shorterSide = min(imageSize.width, imageSize.height)
        let proposed = (shorterSide * preset.cornerRadiusRatio).rounded()
        let clamped = min(maximumCornerRadius, max(minimumCornerRadius, proposed))
        return min(clamped, shorterSide / 2)
    }

    /// Pixel size of the beautified image for a capture of `imageSize` pixels.
    static func outputSize(for imageSize: CGSize, preset: ScreenshotBeautifyPreset) -> CGSize {
        let inset = padding(for: imageSize, preset: preset)
        return CGSize(
            width: imageSize.width + inset * 2,
            height: imageSize.height + inset * 2
        )
    }

    static func render(_ image: CGImage, preset: ScreenshotBeautifyPreset) -> CGImage? {
        let imageSize = CGSize(width: image.width, height: image.height)
        guard imageSize.width >= 1, imageSize.height >= 1 else { return nil }
        let inset = padding(for: imageSize, preset: preset)
        let radius = cornerRadius(for: imageSize, preset: preset)
        let canvas = outputSize(for: imageSize, preset: preset)
        let colorSpace = CGColorSpace(name: CGColorSpace.sRGB) ?? CGColorSpaceCreateDeviceRGB()

        guard let context = CGContext(
            data: nil,
            width: Int(canvas.width),
            height: Int(canvas.height),
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: colorSpace,
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else {
            return nil
        }
        context.interpolationQuality = .high

        // Background gradient. Core Graphics is y-up, so "top-left" is
        // (0, height).
        let colors: [CGColor] = [preset.startColor, preset.endColor].map { color in
            CGColor(red: color.red, green: color.green, blue: color.blue, alpha: 1)
        }
        let locations: [CGFloat] = [0, 1]
        if let gradient = CGGradient(
            colorsSpace: colorSpace,
            colors: colors as CFArray,
            locations: locations
        ) {
            context.drawLinearGradient(
                gradient,
                start: CGPoint(x: 0, y: canvas.height),
                end: CGPoint(x: canvas.width, y: 0),
                options: [.drawsBeforeStartLocation, .drawsAfterEndLocation]
            )
        } else {
            context.setFillColor(colors[0])
            context.fill(CGRect(origin: .zero, size: canvas))
        }

        let imageRect = CGRect(x: inset, y: inset, width: imageSize.width, height: imageSize.height)
        let cardPath = CGPath(
            roundedRect: imageRect,
            cornerWidth: radius,
            cornerHeight: radius,
            transform: nil
        )

        // Soft shadow: fill the card shape once with the shadow enabled, then
        // draw the capture clipped to the same rounded shape.
        context.saveGState()
        context.setShadow(
            offset: CGSize(width: 0, height: -inset * 0.18),
            blur: inset * 0.55,
            color: CGColor(red: 0, green: 0, blue: 0, alpha: preset.shadowOpacity)
        )
        context.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
        context.addPath(cardPath)
        context.fillPath()
        context.restoreGState()

        context.saveGState()
        context.addPath(cardPath)
        context.clip()
        context.draw(image, in: imageRect)
        context.restoreGState()

        return context.makeImage()
    }
}
