import AppKit
import XCTest
import AIScreenshotCore
@testable import AIScreenshotApp

final class TranslatedImageRendererTests: XCTestCase {
    func testFullTranslationPreservesOriginalDimensions() throws {
        let image = try makeImage(width: 320, height: 160)
        let output = try TranslatedImageRenderer().render(
            image: image,
            lines: [TranslatedOCRLine(
                sourceText: "Hello",
                translatedText: "你好",
                boundingBox: CGRect(x: 0.1, y: 0.35, width: 0.4, height: 0.24),
                confidence: 0.95
            )],
            mode: .fullImage,
            targetLanguage: "简体中文"
        )
        XCTAssertEqual(output.width, image.width)
        XCTAssertEqual(output.height, image.height)
    }

    func testBilingualTranslationAppendsPanelBelowOriginal() throws {
        let image = try makeImage(width: 360, height: 180)
        let output = try TranslatedImageRenderer().render(
            image: image,
            lines: [TranslatedOCRLine(
                sourceText: "Screenshot translation",
                translatedText: "截图翻译",
                boundingBox: CGRect(x: 0.08, y: 0.35, width: 0.72, height: 0.2),
                confidence: 0.95
            )],
            mode: .bilingualImage,
            targetLanguage: "简体中文"
        )
        XCTAssertEqual(output.width, image.width)
        XCTAssertGreaterThan(output.height, image.height)
        XCTAssertEqual(try pixel(output, x: 4, y: 4), try pixel(image, x: 4, y: 4))
    }

    func testPatchUsesSurroundingBackgroundInsteadOfGlyphAverage() throws {
        // White page with a solid black "glyph" block filling most of the text box.
        let image = try makeImage(width: 400, height: 200, background: .white) { context in
            context.setFillColor(CGColor(red: 0, green: 0, blue: 0, alpha: 1))
            context.fill(CGRect(x: 104, y: 84, width: 152, height: 32))
        }
        let renderer = TranslatedImageRenderer()

        let color = try XCTUnwrap(
            renderer.surroundingColor(image: image, drawingRect: CGRect(x: 100, y: 80, width: 160, height: 40))
                .usingColorSpace(.deviceRGB)
        )
        XCTAssertGreaterThan(color.redComponent, 0.95)
        XCTAssertGreaterThan(color.greenComponent, 0.95)
        XCTAssertGreaterThan(color.blueComponent, 0.95)
        XCTAssertEqual(renderer.contrastingTextColor(for: color), .black)
        XCTAssertEqual(renderer.contrastingTextColor(for: .black), .white)

        let output = try renderer.render(
            image: image,
            lines: [TranslatedOCRLine(
                sourceText: "Hello",
                translatedText: "好",
                boundingBox: CGRect(x: 0.25, y: 0.4, width: 0.4, height: 0.2),
                confidence: 0.95
            )],
            mode: .fullImage,
            targetLanguage: "简体中文"
        )
        // Far right of the patch: the black block is gone and the patch is
        // opaque page white, not a grey blend.
        let sample = try pixel(output, x: 250, y: 100)
        XCTAssertGreaterThan(sample[0], 235)
        XCTAssertGreaterThan(sample[1], 235)
        XCTAssertGreaterThan(sample[2], 235)
    }

    func testLongTranslationInSmallBoxKeepsOriginalDimensions() throws {
        let image = try makeImage(width: 600, height: 300)
        let output = try TranslatedImageRenderer().render(
            image: image,
            lines: [
                TranslatedOCRLine(
                    sourceText: "OK",
                    translatedText: String(repeating: "这是一段明显放不进原始文字框的很长的译文。", count: 4),
                    boundingBox: CGRect(x: 0.1, y: 0.6, width: 0.12, height: 0.06),
                    confidence: 0.95
                ),
                TranslatedOCRLine(
                    sourceText: "Next",
                    translatedText: "下一行",
                    boundingBox: CGRect(x: 0.1, y: 0.5, width: 0.3, height: 0.06),
                    confidence: 0.95
                )
            ],
            mode: .fullImage,
            targetLanguage: "简体中文"
        )
        XCTAssertEqual(output.width, image.width)
        XCTAssertEqual(output.height, image.height)
        XCTAssertGreaterThanOrEqual(TranslatedImageRenderer.minimumOverlayFontSize, 10)
    }

    func testBilingualPanelUsesLightBackgroundRegardlessOfAppearance() throws {
        let image = try makeImage(width: 360, height: 180)
        let output = try TranslatedImageRenderer().render(
            image: image,
            lines: [TranslatedOCRLine(
                sourceText: "Screenshot translation",
                translatedText: "截图翻译",
                boundingBox: CGRect(x: 0.08, y: 0.35, width: 0.72, height: 0.2),
                confidence: 0.95
            )],
            mode: .bilingualImage,
            targetLanguage: "简体中文"
        )
        // Bottom-right corner of the appended panel is empty panel background.
        let corner = try pixel(output, x: output.width - 3, y: output.height - 3)
        XCTAssertGreaterThan(corner[0], 230)
        XCTAssertGreaterThan(corner[1], 230)
        XCTAssertGreaterThan(corner[2], 230)
    }

    private func makeImage(
        width: Int,
        height: Int,
        background: NSColor = .systemBlue,
        draw: ((CGContext) -> Void)? = nil
    ) throws -> CGImage {
        guard let context = CGContext(
            data: nil,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: width * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else { throw RendererTestError.imageCreationFailed }
        context.setFillColor(background.cgColor)
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        draw?(context)
        guard let image = context.makeImage() else { throw RendererTestError.imageCreationFailed }
        return image
    }

    private func pixel(_ image: CGImage, x: Int, y: Int) throws -> [UInt8] {
        guard let crop = image.cropping(to: CGRect(x: x, y: y, width: 1, height: 1)),
              let provider = crop.dataProvider,
              let data = provider.data else { throw RendererTestError.imageCreationFailed }
        return Array((data as Data).prefix(4))
    }
}

private enum RendererTestError: Error {
    case imageCreationFailed
}
