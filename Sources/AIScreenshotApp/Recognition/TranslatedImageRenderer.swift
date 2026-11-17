import AppKit
import AIScreenshotCore

struct TranslatedOCRLine: Equatable, Sendable {
    let sourceText: String
    let translatedText: String
    let boundingBox: CGRect
    let confidence: Float
}

struct TranslatedImageRenderer {
    func render(
        image: CGImage,
        lines: [TranslatedOCRLine],
        mode: ScreenshotTranslationMode,
        targetLanguage: String
    ) throws -> CGImage {
        guard !lines.isEmpty else { throw ScreenshotTranslationServiceError.missingTextBoxes }
        switch mode {
        case .textOnly:
            return image
        case .fullImage:
            return try renderFullTranslation(image: image, lines: lines)
        case .bilingualImage:
            return try renderBilingualPanel(image: image, lines: lines, targetLanguage: targetLanguage)
        }
    }

    /// Smallest font (in image pixels) used for overlay text. Below this a
    /// long translation is wrapped / allowed to overflow instead of shrunk.
    static let minimumOverlayFontSize: CGFloat = 10

    // Explicit colours for the bilingual panel: it is always drawn on a
    // light background, so appearance-dependent system colours (which turn
    // white in Dark Mode) must not be used.
    private static let panelBackground = NSColor(deviceWhite: 0.97, alpha: 1)
    private static let panelPrimaryText = NSColor(deviceWhite: 0.10, alpha: 1)
    private static let panelSecondaryText = NSColor(deviceWhite: 0.40, alpha: 1)
    private static let panelSeparator = NSColor(deviceWhite: 0.80, alpha: 1)

    private struct OverlayPlacement {
        let line: TranslatedOCRLine
        /// The opaque patch that hides the source text (bottom-left origin).
        let patch: CGRect
        let background: NSColor
    }

    private func renderFullTranslation(
        image: CGImage,
        lines: [TranslatedOCRLine]
    ) throws -> CGImage {
        let size = CGSize(width: image.width, height: image.height)
        let bounds = CGRect(origin: .zero, size: size)
        let representation = try bitmap(width: image.width, height: image.height)
        NSGraphicsContext.saveGraphicsState()
        defer { NSGraphicsContext.restoreGraphicsState() }
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: representation)
        NSImage(cgImage: image, size: size).draw(in: bounds)

        let placements: [OverlayPlacement] = lines.compactMap { line in
            let box = pixelRect(from: line.boundingBox, image: image)
            let patch = box.insetBy(dx: -max(2, box.height * 0.08), dy: -max(2, box.height * 0.12))
                .intersection(bounds)
            guard !patch.isNull, patch.width >= 4, patch.height >= 4 else { return nil }
            return OverlayPlacement(
                line: line,
                patch: patch,
                background: surroundingColor(image: image, drawingRect: patch)
            )
        }

        // Pass 1: hide every source line with an opaque patch in the colour
        // of its surroundings.
        for placement in placements {
            placement.background.setFill()
            placement.patch.fill()
        }

        // Pass 2: draw the translations. Text may grow into free space only,
        // never over another line.
        for (index, placement) in placements.enumerated() {
            let others = placements.enumerated().filter { $0.offset != index }.map { $0.element.patch }
            drawOverlayText(
                placement.line.translatedText,
                patch: placement.patch,
                background: placement.background,
                obstacles: others,
                bounds: bounds
            )
        }

        guard let output = representation.cgImage else {
            throw ScreenshotTranslationServiceError.imageEncodingFailed
        }
        return output
    }

    private func renderBilingualPanel(
        image: CGImage,
        lines: [TranslatedOCRLine],
        targetLanguage: String
    ) throws -> CGImage {
        let width = image.width
        let horizontalPadding = max(28, CGFloat(width) * 0.035)
        let contentWidth = CGFloat(width) - horizontalPadding * 2
        let sourceFont = NSFont.systemFont(ofSize: max(15, min(24, CGFloat(width) / 70)), weight: .medium)
        let targetFont = NSFont.systemFont(ofSize: max(16, min(27, CGFloat(width) / 62)), weight: .semibold)
        let headerFont = NSFont.systemFont(ofSize: max(18, min(30, CGFloat(width) / 55)), weight: .bold)
        let rowSpacing: CGFloat = max(14, targetFont.pointSize * 0.7)
        let textSpacing: CGFloat = max(4, targetFont.pointSize * 0.22)

        let rowHeights = lines.map { line -> CGFloat in
            let sourceHeight = textHeight(line.sourceText, width: contentWidth, font: sourceFont)
            let targetHeight = textHeight(line.translatedText, width: contentWidth, font: targetFont)
            return sourceHeight + textSpacing + targetHeight + rowSpacing
        }
        let headerHeight = headerFont.pointSize * 2.4
        let panelHeight = Int(ceil(headerHeight + rowHeights.reduce(0, +) + horizontalPadding))
        let representation = try bitmap(width: width, height: image.height + panelHeight)
        let totalSize = CGSize(width: width, height: image.height + panelHeight)

        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: representation)
        Self.panelBackground.setFill()
        CGRect(origin: .zero, size: totalSize).fill()
        NSImage(cgImage: image, size: CGSize(width: image.width, height: image.height)).draw(
            in: CGRect(x: 0, y: panelHeight, width: image.width, height: image.height)
        )

        let separatorY = CGFloat(panelHeight) - 1
        Self.panelSeparator.setFill()
        CGRect(x: 0, y: separatorY, width: CGFloat(width), height: 1).fill()

        var cursorY = CGFloat(panelHeight) - headerFont.pointSize * 1.65
        "双语翻译 · \(targetLanguage)".draw(
            at: CGPoint(x: horizontalPadding, y: cursorY),
            withAttributes: [.font: headerFont, .foregroundColor: Self.panelPrimaryText]
        )
        cursorY -= headerFont.pointSize * 1.25

        for (index, line) in lines.enumerated() {
            let sourceHeight = textHeight(line.sourceText, width: contentWidth, font: sourceFont)
            cursorY -= sourceHeight
            line.sourceText.draw(
                in: CGRect(x: horizontalPadding, y: cursorY, width: contentWidth, height: sourceHeight),
                withAttributes: panelAttributes(font: sourceFont, color: Self.panelSecondaryText)
            )
            cursorY -= textSpacing
            let targetHeight = textHeight(line.translatedText, width: contentWidth, font: targetFont)
            cursorY -= targetHeight
            line.translatedText.draw(
                in: CGRect(x: horizontalPadding, y: cursorY, width: contentWidth, height: targetHeight),
                withAttributes: panelAttributes(font: targetFont, color: Self.panelPrimaryText)
            )
            cursorY -= rowSpacing
            if index < lines.count - 1 {
                Self.panelSeparator.withAlphaComponent(0.7).setFill()
                CGRect(x: horizontalPadding, y: cursorY + rowSpacing * 0.45, width: contentWidth, height: 1).fill()
            }
        }

        NSGraphicsContext.restoreGraphicsState()
        guard let output = representation.cgImage else {
            throw ScreenshotTranslationServiceError.imageEncodingFailed
        }
        return output
    }

    private func bitmap(width: Int, height: Int) throws -> NSBitmapImageRep {
        guard let representation = NSBitmapImageRep(
            bitmapDataPlanes: nil,
            pixelsWide: width,
            pixelsHigh: height,
            bitsPerSample: 8,
            samplesPerPixel: 4,
            hasAlpha: true,
            isPlanar: false,
            colorSpaceName: .deviceRGB,
            bytesPerRow: 0,
            bitsPerPixel: 0
        ) else { throw ScreenshotTranslationServiceError.imageEncodingFailed }
        return representation
    }

    private func pixelRect(from normalized: CGRect, image: CGImage) -> CGRect {
        CGRect(
            x: normalized.minX * CGFloat(image.width),
            y: normalized.minY * CGFloat(image.height),
            width: normalized.width * CGFloat(image.width),
            height: normalized.height * CGFloat(image.height)
        ).integral
    }

    /// Median colour of a thin ring just outside `drawingRect`. Unlike the
    /// mean of the box itself, this ignores the glyph pixels inside the box,
    /// so the patch matches the real background instead of turning grey.
    func surroundingColor(image: CGImage, drawingRect: CGRect) -> NSColor {
        let ring = max(2, min(8, (drawingRect.height * 0.18).rounded()))
        let imageBounds = CGRect(x: 0, y: 0, width: image.width, height: image.height)
        // CGImage cropping uses a top-left origin.
        let patch = CGRect(
            x: drawingRect.minX,
            y: CGFloat(image.height) - drawingRect.maxY,
            width: drawingRect.width,
            height: drawingRect.height
        )
        let outer = patch.insetBy(dx: -ring, dy: -ring).intersection(imageBounds).integral
            .intersection(imageBounds)
        guard !outer.isNull, outer.width >= 1, outer.height >= 1,
              let crop = image.cropping(to: outer) else { return .white }
        let width = crop.width
        let height = crop.height
        let bytesPerRow = width * 4
        var pixels = [UInt8](repeating: 0, count: bytesPerRow * height)
        let drawn = pixels.withUnsafeMutableBytes { buffer -> Bool in
            guard let context = CGContext(
                data: buffer.baseAddress,
                width: width,
                height: height,
                bitsPerComponent: 8,
                bytesPerRow: bytesPerRow,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
            ) else { return false }
            context.interpolationQuality = .none
            context.draw(crop, in: CGRect(x: 0, y: 0, width: width, height: height))
            return true
        }
        guard drawn else { return .white }

        // Rows in the buffer run top to bottom, like the crop rectangle.
        let inner = patch.intersection(outer).offsetBy(dx: -outer.minX, dy: -outer.minY)
        let step = max(1, Int((Double(width * height) / 60_000).squareRoot()))
        func collect(skippingInner: Bool) -> [[UInt8]] {
            var channels: [[UInt8]] = [[], [], []]
            var y = 0
            while y < height {
                var x = 0
                while x < width {
                    let isInner = inner.contains(CGPoint(x: CGFloat(x) + 0.5, y: CGFloat(y) + 0.5))
                    if !(skippingInner && isInner) {
                        let offset = y * bytesPerRow + x * 4
                        channels[0].append(pixels[offset])
                        channels[1].append(pixels[offset + 1])
                        channels[2].append(pixels[offset + 2])
                    }
                    x += step
                }
                y += step
            }
            return channels
        }
        var channels = collect(skippingInner: true)
        if channels[0].count < 8 {
            // The box touches every image edge: fall back to all pixels.
            channels = collect(skippingInner: false)
        }
        guard !channels[0].isEmpty else { return .white }
        func median(_ values: [UInt8]) -> CGFloat {
            CGFloat(values.sorted()[values.count / 2]) / 255
        }
        return NSColor(
            deviceRed: median(channels[0]),
            green: median(channels[1]),
            blue: median(channels[2]),
            alpha: 1
        )
    }

    /// Black or white, whichever has the higher WCAG contrast against `color`.
    func contrastingTextColor(for color: NSColor) -> NSColor {
        let rgb = color.usingColorSpace(.deviceRGB) ?? color
        func linear(_ component: CGFloat) -> CGFloat {
            component <= 0.03928 ? component / 12.92 : pow((component + 0.055) / 1.055, 2.4)
        }
        let luminance = 0.2126 * linear(rgb.redComponent)
            + 0.7152 * linear(rgb.greenComponent)
            + 0.0722 * linear(rgb.blueComponent)
        let contrastWithBlack = (luminance + 0.05) / 0.05
        let contrastWithWhite = 1.05 / (luminance + 0.05)
        return contrastWithBlack >= contrastWithWhite ? .black : .white
    }

    /// Draws `text` over `patch`. The font shrinks only down to
    /// `minimumOverlayFontSize`; a translation that still does not fit is
    /// wrapped and may overflow into free space to the right of and below
    /// the box (never over another text line).
    private func drawOverlayText(
        _ text: String,
        patch: CGRect,
        background: NSColor,
        obstacles: [CGRect],
        bounds: CGRect
    ) {
        let color = contrastingTextColor(for: background)
        let padding = CGSize(width: max(2, patch.height * 0.07), height: max(1, patch.height * 0.05))
        let content = patch.insetBy(dx: padding.width, dy: padding.height)
        guard content.width >= 2, content.height >= 2 else { return }

        let preferred = min(72, max(8, content.height * 0.72))
        let minimumSize = min(preferred, Self.minimumOverlayFontSize)
        var fontSize = preferred
        var attributes = textAttributes(fontSize: fontSize, color: color)
        var measured = measure(text, width: content.width, attributes: attributes)
        while fontSize > minimumSize, measured.width > content.width + 0.5 || measured.height > content.height + 0.5 {
            fontSize = max(minimumSize, fontSize - 1)
            attributes = textAttributes(fontSize: fontSize, color: color)
            measured = measure(text, width: content.width, attributes: attributes)
        }

        var target = content
        if measured.height > content.height + 0.5 || measured.width > content.width + 0.5 {
            // Still too long at the minimum size: grow into free space.
            let allowed = overflowRect(for: patch, obstacles: obstacles, bounds: bounds)
                .insetBy(dx: padding.width, dy: padding.height)
            if allowed.width >= content.width, allowed.height >= content.height {
                let wide = measure(text, width: allowed.width, attributes: attributes)
                let width = min(allowed.width, max(content.width, ceil(wide.width)))
                let wrapped = measure(text, width: width, attributes: attributes)
                let height = min(allowed.height, max(content.height, ceil(wrapped.height)))
                target = CGRect(x: content.minX, y: content.maxY - height, width: width, height: height)
                background.setFill()
                target.insetBy(dx: -padding.width, dy: -padding.height).intersection(bounds).fill()
                measured = wrapped
            }
        }

        // Centre vertically when the text is shorter than its box.
        let textHeight = min(target.height, ceil(measured.height))
        let drawRect = CGRect(
            x: target.minX,
            y: target.minY + (target.height - textHeight) / 2,
            width: target.width,
            height: textHeight
        )
        (text as NSString).draw(
            with: drawRect,
            options: [.usesLineFragmentOrigin, .usesFontLeading, .truncatesLastVisibleLine],
            attributes: attributes
        )
    }

    /// The area `patch` may grow into: at most 60% wider and one extra box
    /// height downwards, stopping short of any other text line.
    private func overflowRect(for patch: CGRect, obstacles: [CGRect], bounds: CGRect) -> CGRect {
        let gap = max(2, patch.height * 0.1)
        var maxX = min(bounds.maxX, patch.maxX + patch.width * 0.6)
        var minY = max(bounds.minY, patch.minY - patch.height)
        for obstacle in obstacles {
            let overlapsVertically = obstacle.maxY > minY && obstacle.minY < patch.maxY
            if overlapsVertically, obstacle.minX >= patch.maxX - 1 {
                maxX = min(maxX, obstacle.minX - gap)
            }
        }
        maxX = max(maxX, patch.maxX)
        for obstacle in obstacles {
            let overlapsHorizontally = obstacle.maxX > patch.minX && obstacle.minX < maxX
            if overlapsHorizontally, obstacle.maxY <= patch.minY + 1 {
                minY = max(minY, obstacle.maxY + gap)
            }
        }
        minY = min(minY, patch.minY)
        return CGRect(x: patch.minX, y: minY, width: maxX - patch.minX, height: patch.maxY - minY)
    }

    private func measure(
        _ text: String,
        width: CGFloat,
        attributes: [NSAttributedString.Key: Any]
    ) -> CGSize {
        (text as NSString).boundingRect(
            with: CGSize(width: width, height: .greatestFiniteMagnitude),
            options: [.usesLineFragmentOrigin, .usesFontLeading],
            attributes: attributes
        ).size
    }

    private func textAttributes(fontSize: CGFloat, color: NSColor) -> [NSAttributedString.Key: Any] {
        let paragraph = NSMutableParagraphStyle()
        paragraph.lineBreakMode = .byWordWrapping
        paragraph.alignment = .left
        return [
            .font: NSFont.systemFont(ofSize: fontSize, weight: .medium),
            .foregroundColor: color,
            .paragraphStyle: paragraph
        ]
    }

    private func panelAttributes(font: NSFont, color: NSColor) -> [NSAttributedString.Key: Any] {
        let paragraph = NSMutableParagraphStyle()
        paragraph.lineBreakMode = .byWordWrapping
        return [.font: font, .foregroundColor: color, .paragraphStyle: paragraph]
    }

    private func textHeight(_ text: String, width: CGFloat, font: NSFont) -> CGFloat {
        let paragraph = NSMutableParagraphStyle()
        paragraph.lineBreakMode = .byWordWrapping
        return ceil((text as NSString).boundingRect(
            with: CGSize(width: width, height: .greatestFiniteMagnitude),
            options: [.usesLineFragmentOrigin, .usesFontLeading],
            attributes: [.font: font, .paragraphStyle: paragraph]
        ).height)
    }
}
