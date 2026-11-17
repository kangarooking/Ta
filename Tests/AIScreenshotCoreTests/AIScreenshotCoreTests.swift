import XCTest
import AppKit
@testable import AIScreenshotCore

final class AIScreenshotCoreTests: XCTestCase {
    func testVersionIsExposed() {
        XCTAssertEqual(AIScreenshotCore.version, "1.1.0")
    }

    func testCaptureJobRejectsInvalidTransition() {
        var job = CaptureJob(mode: .intelligent)
        XCTAssertThrowsError(try job.transition(to: .clipboardCommitted))
        XCTAssertEqual(job.phase, .idle)
    }

    func testCaptureJobAcceptsHappyPath() throws {
        var job = CaptureJob(mode: .intelligent)
        try job.transition(to: .selecting)
        try job.transition(to: .captured)
        try job.transition(to: .localProcessing)
        try job.transition(to: .clipboardCommitted)
        try job.transition(to: .resultShown)
        XCTAssertEqual(job.phase, .resultShown)
    }

    func testCaptureModesMapToExpressActions() {
        XCTAssertNil(CaptureMode.interactive.directAction)
        XCTAssertEqual(CaptureMode.intelligent.directAction, .localOCR)
        XCTAssertEqual(CaptureMode.translation.directAction, .translateText)
        XCTAssertEqual(CaptureMode.image.directAction, .copyImage)
        XCTAssertEqual(CaptureMode.pin.directAction, .pin)
        XCTAssertNil(CaptureMode.long.directAction)
    }

    func testPreferenceRawValuesAreStable() {
        XCTAssertEqual(PostCaptureAction.choose.rawValue, "choose")
        XCTAssertEqual(PostCaptureAction.recognize.rawValue, "recognize")
        XCTAssertEqual(PostCaptureAction.translateText.rawValue, "translateText")
        XCTAssertEqual(RecognitionRoute.localOCR.rawValue, "localOCR")
        XCTAssertEqual(RecognitionRoute.multimodal.rawValue, "multimodal")
        XCTAssertEqual(RecognitionRoute.smart.rawValue, "smart")
    }

    func testDeepSeekOCR2IsLatestRemoteEngine() {
        XCTAssertEqual(OCREnginePreference.deepSeekOCR2.displayName, "DeepSeek-OCR-2（最新）")
        XCTAssertFalse(OCREnginePreference.deepSeekOCR2.isLocalEngine)
        XCTAssertFalse(OCREnginePreference.deepSeekOCR2.usesOptionalPack)
        XCTAssertEqual(DeepSeekOCR2Client.latestOfficialModel, "deepseek-ai/DeepSeek-OCR-2")
    }

    func testClassifierRecognizesCode() {
        let text = """
        func greet(name: String) -> String {
            return "Hello, \\(name)"
        }
        """
        XCTAssertEqual(ContentClassifier().classify(text), .code)
    }

    func testClassifierRecognizesTabSeparatedTable() {
        let text = "Name\tScore\nAlice\t98\nBob\t95"
        XCTAssertEqual(ContentClassifier().classify(text), .table)
    }

    func testClassifierRecognizesURLPayload() {
        XCTAssertEqual(ContentClassifier().classify("https://example.com/path"), .qrCode)
    }

    func testClassifierRecognizesFormula() {
        XCTAssertEqual(ContentClassifier().classify("∫₀¹ x² dx = 1/3"), .formula)
        XCTAssertEqual(ContentClassifier().classify("\\frac{a+b}{c} = \\sqrt{x}"), .formula)
    }

    func testNormalizerTrimsTrailingWhitespaceAndBlankRuns() {
        let input = "First line   \r\n\r\n\r\nSecond line\t\r\n"
        XCTAssertEqual(OCRTextNormalizer().normalize(input), "First line\n\nSecond line")
    }

    func testNormalizerCanMergeWrappedEnglishLines() {
        let input = "A sentence that wraps\nonto another line"
        XCTAssertEqual(
            OCRTextNormalizer().normalize(input, mergeWrappedLines: true),
            "A sentence that wraps onto another line"
        )
    }

    func testClipboardPolicyProtectsUserCopyAndNewestJob() {
        let policy = ClipboardCommitPolicy()
        XCTAssertTrue(policy.shouldCommit(initialChangeCount: 10, currentChangeCount: 10, jobIsLatest: true))
        XCTAssertFalse(policy.shouldCommit(initialChangeCount: 10, currentChangeCount: 11, jobIsLatest: true))
        XCTAssertFalse(policy.shouldCommit(initialChangeCount: 10, currentChangeCount: 10, jobIsLatest: false))
    }

    func testProviderEndpointRequiresHTTPSForRemoteHosts() {
        let validator = ProviderEndpointValidator()
        XCTAssertNil(validator.validationMessage(for: "https://api.example.com/v1"))
        XCTAssertEqual(
            validator.validationMessage(for: "http://api.example.com/v1"),
            "远程服务必须使用 HTTPS"
        )
    }

    func testProviderEndpointAllowsLocalHTTP() {
        let validator = ProviderEndpointValidator()
        XCTAssertNil(validator.validationMessage(for: "http://localhost:11434/v1"))
        XCTAssertNil(validator.validationMessage(for: "http://127.0.0.1:11434/v1"))
        XCTAssertNil(validator.validationMessage(for: "http://127.8.9.10:8000/v1"))
        XCTAssertNil(validator.validationMessage(for: "http://[::1]:8000/v1"))
        XCTAssertNil(validator.validationMessage(for: "http://10.0.0.12:8000/v1"))
        XCTAssertNil(validator.validationMessage(for: "http://172.16.4.2:8000/v1"))
        XCTAssertNil(validator.validationMessage(for: "http://172.31.255.1:8000/v1"))
        XCTAssertNil(validator.validationMessage(for: "http://192.168.1.20:1234/v1"))
        XCTAssertNil(validator.validationMessage(for: "http://studio.local:1234/v1"))
    }

    func testProviderEndpointStillRejectsPublicHTTPLookalikes() {
        let validator = ProviderEndpointValidator()
        for endpoint in [
            "http://172.32.0.1/v1",
            "http://11.0.0.1/v1",
            "http://192.169.1.1/v1",
            "http://10.evil.example.com/v1",
            "http://127.0.0.1.evil.example.com/v1",
            "http://localhost.evil.example.com/v1",
            "http://notlocal/v1"
        ] {
            XCTAssertEqual(validator.validationMessage(for: endpoint), "远程服务必须使用 HTTPS", endpoint)
        }
        XCTAssertTrue(validator.isLocalEndpoint("http://192.168.0.2:11434"))
        XCTAssertTrue(validator.isLocalEndpoint("https://localhost:8443/v1"))
        XCTAssertFalse(validator.isLocalEndpoint("https://api.example.com/v1"))
    }

    func testClassifierKeepsEnglishProseWithKeywordsAsPlainText() {
        let prose = """
        This guide is for users migrating from the old dashboard. Please return the form to the
        public office if you need a new type of account, or import your data while it is still available.
        """
        XCTAssertEqual(ContentClassifier().classify(prose), .plainText)
        XCTAssertEqual(
            ContentClassifier().classify("Thanks for the update; we will let the class know from Monday."),
            .plainText
        )
    }

    func testClassifierRecognizesPythonAndMarkupAsCode() {
        XCTAssertEqual(ContentClassifier().classify("def area(r):\n    return 3.14 * r * r"), .code)
        XCTAssertEqual(
            ContentClassifier().classify("const total = items.length;\nif (total == 0) {\n  console.log(total);\n}"),
            .code
        )
    }

    func testClassifierDoesNotTreatUnitsOrSingleSymbolsAsFormula() {
        XCTAssertEqual(ContentClassifier().classify("The flat is 85 m² and close to the station."), .plainText)
        XCTAssertEqual(ContentClassifier().classify("温度 ≥ 30°C 时请开启空调"), .plainText)
        XCTAssertEqual(ContentClassifier().classify("面积 20 m²，温度 ≥ 30°C"), .plainText)
    }

    func testClassifierStillRecognizesUnicodeEquations() {
        XCTAssertEqual(ContentClassifier().classify("x² + y² = r²"), .formula)
        XCTAssertEqual(ContentClassifier().classify("√2 ≈ 1.414"), .formula)
    }

    func testVisionOCRRecognizesGeneratedEnglishText() async throws {
        let image = try makeTextImage("HELLO 2026")
        let result = try await VisionOCRService().recognize(
            image: image,
            languages: ["en-US"],
            mergeWrappedLines: false
        )

        XCTAssertTrue(result.text.uppercased().contains("HELLO"), "Vision returned: \(result.text)")
        XCTAssertFalse(result.text.isEmpty)
    }

    func testVisionOCRDetectsQRCodePayload() async throws {
        let payload = "https://example.com/qr-test"
        let filter = CIFilter(name: "CIQRCodeGenerator")!
        filter.setValue(Data(payload.utf8), forKey: "inputMessage")
        filter.setValue("H", forKey: "inputCorrectionLevel")
        let output = try XCTUnwrap(filter.outputImage?.transformed(by: CGAffineTransform(scaleX: 12, y: 12)))
        let image = try XCTUnwrap(CIContext().createCGImage(output, from: output.extent))

        let result = try await VisionOCRService().recognize(
            image: image,
            languages: ["en-US"],
            mergeWrappedLines: false
        )

        XCTAssertTrue(result.barcodes.contains { $0.payload == payload })
    }

    private func makeTextImage(_ text: String) throws -> CGImage {
        guard let representation = NSBitmapImageRep(
            bitmapDataPlanes: nil,
            pixelsWide: 1200,
            pixelsHigh: 320,
            bitsPerSample: 8,
            samplesPerPixel: 4,
            hasAlpha: true,
            isPlanar: false,
            colorSpaceName: .deviceRGB,
            bytesPerRow: 0,
            bitsPerPixel: 0
        ) else {
            throw TestImageError.couldNotCreateBitmap
        }

        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: representation)
        NSColor.white.setFill()
        NSRect(x: 0, y: 0, width: 1200, height: 320).fill()
        text.draw(
            at: CGPoint(x: 60, y: 100),
            withAttributes: [
                .font: NSFont.systemFont(ofSize: 96, weight: .bold),
                .foregroundColor: NSColor.black
            ]
        )
        NSGraphicsContext.restoreGraphicsState()

        guard let image = representation.cgImage else {
            throw TestImageError.couldNotCreateImage
        }
        return image
    }
}

private enum TestImageError: Error {
    case couldNotCreateBitmap
    case couldNotCreateImage
}
