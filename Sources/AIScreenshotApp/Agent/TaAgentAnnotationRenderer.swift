import Accelerate
import CoreGraphics
import CoreText
import Foundation
import TaAgentContracts

struct TaAgentAnnotationRenderer {
    func render(
        sourceImage: CGImage,
        cropRect: AnnotationRect?,
        elements: [AnnotationOperation]
    ) throws -> CGImage {
        let base = try croppedImage(sourceImage, rect: cropRect)
        let width = base.width
        let height = base.height
        guard let context = CGContext(
            data: nil,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: width * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else { throw TaAgentAnnotationRenderError.contextCreationFailed }
        context.setShouldAntialias(true)
        context.setAllowsAntialiasing(true)
        let canvas = CGRect(x: 0, y: 0, width: width, height: height)
        context.draw(base, in: canvas)

        var mosaicImage: CGImage?
        var blurImages: [Int: CGImage] = [:]
        for (index, element) in elements.enumerated() {
            switch element {
            case .rectangle(let operation):
                drawRect(operation.rect, style: operation, ellipse: false, height: height, context: context)
            case .ellipse(let operation):
                drawRect(operation.rect, style: operation, ellipse: true, height: height, context: context)
            case .arrow(let operation):
                drawArrow(operation, height: height, context: context)
            case .pen(let operation):
                drawStroke(operation, height: height, context: context)
            case .highlighter(let operation):
                drawStroke(operation, height: height, context: context)
            case .text(let operation):
                drawText(operation, height: height, context: context)
            case .number(let operation):
                drawNumber(operation, height: height, context: context)
            case .mosaic(let operation):
                if mosaicImage == nil {
                    mosaicImage = try pixelated(base, scale: operation.scale)
                }
                if let mosaicImage {
                    drawMosaic(operation, filtered: mosaicImage, canvas: canvas, height: height, context: context)
                }
            case .blur(let operation):
                let radius = Self.clamped(
                    operation.radius,
                    lower: 1,
                    upper: AnnotationRecipeLimits.maximumBlurRadius
                )
                let key = Int(radius.rounded())
                let filteredImage: CGImage
                if let cached = blurImages[key] {
                    filteredImage = cached
                } else {
                    filteredImage = try blurred(base, radius: radius)
                    blurImages[key] = filteredImage
                }
                drawFilteredRect(operation.rect, filtered: filteredImage, canvas: canvas, height: height, context: context)
            case .magnify(let operation):
                try drawMagnify(operation, source: base, height: height, context: context)
            case .crop, .eraser:
                break
            }
            _ = index
        }
        guard let image = context.makeImage() else {
            throw TaAgentAnnotationRenderError.imageCreationFailed
        }
        return image
    }

    private func croppedImage(_ image: CGImage, rect: AnnotationRect?) throws -> CGImage {
        guard let rect else { return image }
        let crop = CGRect(x: rect.x, y: rect.y, width: rect.width, height: rect.height).integral
        guard let result = image.cropping(to: crop), result.width > 0, result.height > 0 else {
            throw TaAgentAnnotationRenderError.invalidCrop
        }
        return result
    }

    private func drawRect(
        _ rect: AnnotationRect,
        style: AnnotationRectOperation,
        ellipse: Bool,
        height: Int,
        context: CGContext
    ) {
        configureStroke(style.color, width: style.lineWidth, dashed: style.dashed, context: context)
        let mapped = map(rect, height: height)
        if ellipse {
            context.strokeEllipse(in: mapped)
        } else {
            context.addPath(CGPath(roundedRect: mapped, cornerWidth: 5, cornerHeight: 5, transform: nil))
            context.strokePath()
        }
        context.setLineDash(phase: 0, lengths: [])
    }

    private func drawArrow(_ operation: AnnotationArrowOperation, height: Int, context: CGContext) {
        let start = map(operation.start, height: height)
        let end = map(operation.end, height: height)
        let points = TaperedArrowGeometry.polygon(
            from: start,
            to: end,
            width: operation.lineWidth
        )
        guard let first = points.first else { return }
        let path = CGMutablePath()
        path.move(to: first)
        points.dropFirst().forEach { path.addLine(to: $0) }
        path.closeSubpath()
        context.addPath(path)
        context.setFillColor(cgColor(operation.color))
        context.fillPath()
    }

    private func drawStroke(_ operation: AnnotationStrokeOperation, height: Int, context: CGContext) {
        guard let first = operation.points.first else { return }
        configureStroke(operation.color, width: operation.lineWidth, dashed: operation.dashed, context: context)
        context.setLineCap(.round)
        context.setLineJoin(.round)
        let start = map(first, height: height)
        if operation.points.count == 1 {
            let radius = operation.lineWidth / 2
            context.setFillColor(cgColor(operation.color))
            context.fillEllipse(in: CGRect(x: start.x - radius, y: start.y - radius, width: operation.lineWidth, height: operation.lineWidth))
        } else {
            context.move(to: start)
            operation.points.dropFirst().forEach { context.addLine(to: map($0, height: height)) }
            context.strokePath()
        }
        context.setLineDash(phase: 0, lengths: [])
    }

    private func drawText(_ operation: AnnotationTextOperation, height: Int, context: CGContext) {
        let font = CTFontCreateUIFontForLanguage(.system, operation.fontSize, "zh-Hans" as CFString)
            ?? CTFontCreateWithName("Helvetica Neue" as CFString, operation.fontSize, nil)
        let attributed = NSAttributedString(
            string: operation.text,
            attributes: [
                NSAttributedString.Key(kCTFontAttributeName as String): font,
                NSAttributedString.Key(kCTForegroundColorAttributeName as String): cgColor(operation.color)
            ]
        )
        let line = CTLineCreateWithAttributedString(attributed)
        context.saveGState()
        context.textMatrix = .identity
        context.textPosition = CGPoint(x: operation.origin.x, y: Double(height) - operation.origin.y - operation.fontSize)
        CTLineDraw(line, context)
        context.restoreGState()
    }

    private func drawNumber(_ operation: AnnotationNumberOperation, height: Int, context: CGContext) {
        let center = map(operation.center, height: height)
        let radius = operation.diameter / 2
        context.setFillColor(cgColor(operation.color))
        context.fillEllipse(in: CGRect(x: center.x - radius, y: center.y - radius, width: operation.diameter, height: operation.diameter))

        let fontSize = operation.diameter * 0.56
        let font = CTFontCreateUIFontForLanguage(.system, fontSize, nil)
            ?? CTFontCreateWithName("Helvetica Neue Bold" as CFString, fontSize, nil)
        let attributed = NSAttributedString(
            string: String(operation.number),
            attributes: [
                NSAttributedString.Key(kCTFontAttributeName as String): font,
                NSAttributedString.Key(kCTForegroundColorAttributeName as String): CGColor(gray: 1, alpha: 1)
            ]
        )
        let line = CTLineCreateWithAttributedString(attributed)
        let bounds = CTLineGetBoundsWithOptions(line, [.useGlyphPathBounds])
        context.textPosition = CGPoint(x: center.x - bounds.width / 2 - bounds.minX, y: center.y - bounds.height / 2 - bounds.minY)
        CTLineDraw(line, context)
    }

    private func drawMosaic(
        _ operation: AnnotationMosaicOperation,
        filtered: CGImage,
        canvas: CGRect,
        height: Int,
        context: CGContext
    ) {
        context.saveGState()
        switch operation.mode {
        case .rect:
            if let rect = operation.rect { context.clip(to: map(rect, height: height)) }
        case .brush:
            guard let points = operation.points, let first = points.first else {
                context.restoreGState()
                return
            }
            let path = CGMutablePath()
            path.move(to: map(first, height: height))
            points.dropFirst().forEach { path.addLine(to: map($0, height: height)) }
            context.addPath(path)
            context.setLineWidth(operation.lineWidth)
            context.setLineCap(.round)
            context.setLineJoin(.round)
            context.replacePathWithStrokedPath()
            context.clip()
        }
        context.draw(filtered, in: canvas)
        context.restoreGState()
    }

    private func drawFilteredRect(
        _ rect: AnnotationRect,
        filtered: CGImage,
        canvas: CGRect,
        height: Int,
        context: CGContext
    ) {
        context.saveGState()
        context.clip(to: map(rect, height: height))
        context.draw(filtered, in: canvas)
        context.restoreGState()
    }

    private func drawMagnify(
        _ operation: AnnotationMagnifyOperation,
        source: CGImage,
        height: Int,
        context: CGContext
    ) throws {
        let target = map(operation.rect, height: height)
        let sourceWidth = operation.rect.width / operation.factor
        let sourceHeight = operation.rect.height / operation.factor
        let sourceRect = CGRect(
            x: operation.rect.x + (operation.rect.width - sourceWidth) / 2,
            y: operation.rect.y + (operation.rect.height - sourceHeight) / 2,
            width: sourceWidth,
            height: sourceHeight
        ).integral.intersection(CGRect(x: 0, y: 0, width: source.width, height: source.height))
        guard let magnified = source.cropping(to: sourceRect), !sourceRect.isEmpty else {
            throw TaAgentAnnotationRenderError.invalidMagnifyRegion
        }
        context.saveGState()
        context.addEllipse(in: target)
        context.clip()
        context.draw(magnified, in: target)
        context.restoreGState()
        context.setStrokeColor(CGColor(gray: 1, alpha: 1))
        context.setLineWidth(3)
        context.strokeEllipse(in: target.insetBy(dx: 1.5, dy: 1.5))
    }

    private func pixelated(_ image: CGImage, scale: Double) throws -> CGImage {
        let blockSize = Int(Self.clamped(
            scale,
            lower: 2,
            upper: AnnotationRecipeLimits.maximumMosaicScale
        ).rounded())
        let smallWidth = max(1, (image.width + blockSize - 1) / blockSize)
        let smallHeight = max(1, (image.height + blockSize - 1) / blockSize)
        guard let smallContext = CGContext(
            data: nil,
            width: smallWidth,
            height: smallHeight,
            bitsPerComponent: 8,
            bytesPerRow: smallWidth * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else { throw TaAgentAnnotationRenderError.contextCreationFailed }
        smallContext.interpolationQuality = .low
        smallContext.draw(image, in: CGRect(x: 0, y: 0, width: smallWidth, height: smallHeight))
        guard let smallImage = smallContext.makeImage(),
              let outputContext = CGContext(
                data: nil,
                width: image.width,
                height: image.height,
                bitsPerComponent: 8,
                bytesPerRow: image.width * 4,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
              ) else { throw TaAgentAnnotationRenderError.imageCreationFailed }
        outputContext.interpolationQuality = .none
        outputContext.draw(
            smallImage,
            in: CGRect(x: 0, y: 0, width: image.width, height: image.height)
        )
        guard let result = outputContext.makeImage() else {
            throw TaAgentAnnotationRenderError.imageCreationFailed
        }
        return result
    }

    private func blurred(_ image: CGImage, radius: Double) throws -> CGImage {
        let colorSpace = CGColorSpaceCreateDeviceRGB()
        var format = vImage_CGImageFormat(
            bitsPerComponent: 8,
            bitsPerPixel: 32,
            colorSpace: Unmanaged.passUnretained(colorSpace),
            bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue),
            version: 0,
            decode: nil,
            renderingIntent: .defaultIntent
        )
        var source = vImage_Buffer()
        var destination = vImage_Buffer()
        let flags = vImage_Flags(kvImageNoFlags)
        guard vImageBuffer_InitWithCGImage(&source, &format, nil, image, flags) == kvImageNoError else {
            throw TaAgentAnnotationRenderError.filterFailed("blur")
        }
        defer { free(source.data) }
        guard vImageBuffer_Init(
            &destination,
            source.height,
            source.width,
            format.bitsPerPixel,
            flags
        ) == kvImageNoError else {
            throw TaAgentAnnotationRenderError.filterFailed("blur")
        }
        defer { free(destination.data) }

        let safeRadius = Self.clamped(radius, lower: 1, upper: AnnotationRecipeLimits.maximumBlurRadius)
        // safeRadius is bounded, so these conversions cannot trap. vImage needs an odd kernel.
        var kernel = UInt32(max(3, Int((safeRadius * 2 + 1).rounded())))
        if kernel.isMultiple(of: 2) { kernel += 1 }
        guard vImageBoxConvolve_ARGB8888(
            &source,
            &destination,
            nil,
            0,
            0,
            kernel,
            kernel,
            nil,
            vImage_Flags(kvImageEdgeExtend)
        ) == kvImageNoError else {
            throw TaAgentAnnotationRenderError.filterFailed("blur")
        }
        var creationError = kvImageNoError
        guard let unmanaged = vImageCreateCGImageFromBuffer(
            &destination,
            &format,
            nil,
            nil,
            flags,
            &creationError
        ), creationError == kvImageNoError else {
            throw TaAgentAnnotationRenderError.filterFailed("blur")
        }
        return unmanaged.takeRetainedValue()
    }

    private func configureStroke(
        _ color: AnnotationColor,
        width: Double,
        dashed: Bool,
        context: CGContext
    ) {
        context.setStrokeColor(cgColor(color))
        context.setLineWidth(width)
        context.setLineDash(phase: 0, lengths: dashed ? [max(4, width * 2), max(3, width * 1.4)] : [])
    }

    /// Clamps a possibly non-finite value into a range that is always safe to
    /// convert to an integer type.
    static func clamped(_ value: Double, lower: Double, upper: Double) -> Double {
        guard value.isFinite else { return lower }
        return min(upper, max(lower, value))
    }

    private func cgColor(_ color: AnnotationColor) -> CGColor {
        CGColor(red: color.red, green: color.green, blue: color.blue, alpha: color.alpha)
    }

    private func map(_ point: AnnotationPoint, height: Int) -> CGPoint {
        CGPoint(x: point.x, y: Double(height) - point.y)
    }

    private func map(_ rect: AnnotationRect, height: Int) -> CGRect {
        CGRect(x: rect.x, y: Double(height) - rect.y - rect.height, width: rect.width, height: rect.height)
    }
}

enum TaAgentAnnotationRenderError: Error, LocalizedError {
    case contextCreationFailed
    case imageCreationFailed
    case invalidCrop
    case invalidMagnifyRegion
    case filterFailed(String)

    var errorDescription: String? {
        switch self {
        case .contextCreationFailed: "无法创建标注画布。"
        case .imageCreationFailed: "无法生成标注图片。"
        case .invalidCrop: "无法裁剪指定图片区域。"
        case .invalidMagnifyRegion: "放大镜区域没有可用像素。"
        case .filterFailed(let name): "图像滤镜执行失败：\(name)。"
        }
    }
}
