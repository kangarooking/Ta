import CoreGraphics
import Testing
import TaAgentContracts
@testable import AIScreenshotApp

@Suite("Ta Agent annotation renderer")
struct TaAgentAnnotationRendererTests {
    @Test("arrow uses the same filled tapered geometry as the native editor")
    func arrowMatchesNativeEditorGeometry() throws {
        let source = try solidImage(width: 120, height: 90, color: CGColor(gray: 1, alpha: 1))
        let operation = AnnotationArrowOperation(
            id: "arrow",
            start: .init(x: 10, y: 45),
            end: .init(x: 110, y: 45),
            color: .red,
            lineWidth: 6
        )

        let rendered = try TaAgentAnnotationRenderer().render(
            sourceImage: source,
            cropRect: nil,
            elements: [.arrow(operation)]
        )
        let expected = try nativeArrowImage(source: source, operation: operation)

        #expect(pixelDigest(rendered) == pixelDigest(expected))
    }

    @Test("crop changes the output pixel dimensions")
    func cropChangesDimensions() throws {
        var session = TaAgentAnnotationSession(sourceImage: try fixtureImage(width: 120, height: 90))
        let result = try session.apply(AnnotationRecipe(version: 1, operations: [
            .crop(.init(rect: .init(x: 10, y: 15, width: 70, height: 40)))
        ]))

        #expect(result.image.width == 70)
        #expect(result.image.height == 40)
    }

    @Test("every visible operation changes rendered pixels")
    func eachVisibleOperationChangesPixels() throws {
        for operation in Self.visibleOperations {
            let source = try fixtureImage(width: 120, height: 90)
            var session = TaAgentAnnotationSession(sourceImage: source)

            let result = try session.apply(AnnotationRecipe(version: 1, operations: [operation]))

            #expect(pixelDigest(result.image) != pixelDigest(source), "operation did not change pixels: \(String(describing: operation))")
            #expect(result.elementCount == 1)
        }
    }

    @Test("renderer clamps out-of-range filter sizes instead of trapping")
    func rendererClampsUnvalidatedSizes() throws {
        let source = try fixtureImage(width: 120, height: 90)
        let rect = AnnotationRect(x: 8, y: 8, width: 60, height: 40)

        // Bypass recipe validation on purpose: the renderer must be safe on its own.
        let image = try TaAgentAnnotationRenderer().render(
            sourceImage: source,
            cropRect: nil,
            elements: [
                .mosaic(.init(id: "mosaic", mode: .rect, rect: rect, scale: 1e30)),
                .blur(.init(id: "nan", rect: rect, radius: .nan))
            ]
        )
        // A huge radius is clamped; it may render or fail with an error, but must never trap.
        _ = try? TaAgentAnnotationRenderer().render(
            sourceImage: source,
            cropRect: nil,
            elements: [.blur(.init(id: "blur", rect: rect, radius: 1e30))]
        )

        #expect(image.width == 120)
        #expect(image.height == 90)
        #expect(TaAgentAnnotationRenderer.clamped(.infinity, lower: 2, upper: 512) == 2)
        #expect(TaAgentAnnotationRenderer.clamped(1e30, lower: 2, upper: 512) == 512)
    }

    private static let visibleOperations: [AnnotationOperation] = [
        .rectangle(.init(id: "rect", rect: .init(x: 8, y: 8, width: 42, height: 28))),
        .ellipse(.init(id: "ellipse", rect: .init(x: 8, y: 8, width: 42, height: 28))),
        .arrow(.init(id: "arrow", start: .init(x: 8, y: 40), end: .init(x: 70, y: 12))),
        .pen(.init(id: "pen", points: [.init(x: 8, y: 60), .init(x: 50, y: 72), .init(x: 90, y: 52)])),
        .highlighter(.init(id: "highlight", points: [.init(x: 8, y: 48), .init(x: 96, y: 48)], color: .highlighter, lineWidth: 16)),
        .text(.init(id: "text", origin: .init(x: 8, y: 8), text: "重点", fontSize: 24)),
        .number(.init(id: "number", center: .init(x: 24, y: 24), number: 1, diameter: 30)),
        .mosaic(.init(id: "mosaic-rect", mode: .rect, rect: .init(x: 8, y: 8, width: 60, height: 40))),
        .mosaic(.init(id: "mosaic-brush", mode: .brush, points: [.init(x: 8, y: 20), .init(x: 80, y: 50)], lineWidth: 24)),
        .blur(.init(id: "blur", rect: .init(x: 8, y: 8, width: 60, height: 40), radius: 8)),
        .magnify(.init(id: "magnify", rect: .init(x: 50, y: 20, width: 50, height: 50), factor: 2))
    ]
}

private func solidImage(width: Int, height: Int, color: CGColor) throws -> CGImage {
    guard let context = CGContext(
        data: nil,
        width: width,
        height: height,
        bitsPerComponent: 8,
        bytesPerRow: width * 4,
        space: CGColorSpaceCreateDeviceRGB(),
        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
    ) else { throw AnnotationTestError.context }
    context.setFillColor(color)
    context.fill(CGRect(x: 0, y: 0, width: width, height: height))
    guard let image = context.makeImage() else { throw AnnotationTestError.image }
    return image
}

private func nativeArrowImage(
    source: CGImage,
    operation: AnnotationArrowOperation
) throws -> CGImage {
    guard let context = CGContext(
        data: nil,
        width: source.width,
        height: source.height,
        bitsPerComponent: 8,
        bytesPerRow: source.width * 4,
        space: CGColorSpaceCreateDeviceRGB(),
        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
    ) else { throw AnnotationTestError.context }
    context.setShouldAntialias(true)
    context.setAllowsAntialiasing(true)
    context.draw(source, in: CGRect(x: 0, y: 0, width: source.width, height: source.height))
    let start = CGPoint(x: operation.start.x, y: Double(source.height) - operation.start.y)
    let end = CGPoint(x: operation.end.x, y: Double(source.height) - operation.end.y)
    let points = TaperedArrowGeometry.polygon(from: start, to: end, width: operation.lineWidth)
    guard let first = points.first else { throw AnnotationTestError.image }
    let path = CGMutablePath()
    path.move(to: first)
    points.dropFirst().forEach { path.addLine(to: $0) }
    path.closeSubpath()
    context.addPath(path)
    context.setFillColor(CGColor(
        red: operation.color.red,
        green: operation.color.green,
        blue: operation.color.blue,
        alpha: operation.color.alpha
    ))
    context.fillPath()
    guard let image = context.makeImage() else { throw AnnotationTestError.image }
    return image
}

func fixtureImage(width: Int, height: Int) throws -> CGImage {
    guard let context = CGContext(
        data: nil,
        width: width,
        height: height,
        bitsPerComponent: 8,
        bytesPerRow: width * 4,
        space: CGColorSpaceCreateDeviceRGB(),
        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
    ) else { throw AnnotationTestError.context }
    for y in 0..<height {
        for x in 0..<width {
            let light = ((x / 8) + (y / 8)).isMultiple(of: 2)
            context.setFillColor(light
                ? CGColor(red: 0.94, green: 0.96, blue: 0.99, alpha: 1)
                : CGColor(red: 0.42, green: 0.55, blue: 0.72, alpha: 1))
            context.fill(CGRect(x: x, y: y, width: 1, height: 1))
        }
    }
    guard let image = context.makeImage() else { throw AnnotationTestError.image }
    return image
}

func pixelDigest(_ image: CGImage) -> UInt64 {
    let width = image.width
    let height = image.height
    var bytes = [UInt8](repeating: 0, count: width * height * 4)
    bytes.withUnsafeMutableBytes { buffer in
        let context = CGContext(
            data: buffer.baseAddress,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: width * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        )!
        context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
    }
    return bytes.reduce(UInt64(1469598103934665603)) { hash, byte in
        (hash ^ UInt64(byte)) &* 1099511628211
    }
}

enum AnnotationTestError: Error { case context, image }
