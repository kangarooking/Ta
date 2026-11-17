import AppKit
import Carbon
import ImageIO
import XCTest
import AIScreenshotCore
@testable import AIScreenshotApp

final class CaptureFlowFixTests: XCTestCase {
    // MARK: Pinned image zoom / transform

    func testScrollZoomAlwaysPreservesAspectRatio() {
        let start = CGSize(width: 1000, height: 250)
        var size = start
        for _ in 0..<60 {
            size = PinnedImageZoom.scaledSize(current: size, factor: 1.06)
            XCTAssertEqual(size.width / size.height, 4, accuracy: 0.0001)
        }
        XCTAssertLessThanOrEqual(size.width, PinnedImageZoom.maximumSize.width + 0.001)
        XCTAssertLessThanOrEqual(size.height, PinnedImageZoom.maximumSize.height + 0.001)

        for _ in 0..<120 {
            size = PinnedImageZoom.scaledSize(current: size, factor: 0.94)
            XCTAssertEqual(size.width / size.height, 4, accuracy: 0.0001)
        }
        XCTAssertGreaterThanOrEqual(size.width, PinnedImageZoom.minimumSize.width - 0.001)
        XCTAssertGreaterThanOrEqual(size.height, PinnedImageZoom.minimumSize.height - 0.001)
    }

    func testTallPinDoesNotStretchAtTheHeightLimit() {
        let size = PinnedImageZoom.scaledSize(current: CGSize(width: 300, height: 880), factor: 1.06)

        XCTAssertEqual(size.height, 900, accuracy: 0.001)
        XCTAssertEqual(size.width / size.height, 300.0 / 880.0, accuracy: 0.0001)
    }

    func testSmallPinIsNotForcedUpToTheMinimumSize() {
        let size = PinnedImageZoom.scaledSize(current: CGSize(width: 80, height: 50), factor: 0.94)

        XCTAssertEqual(size, CGSize(width: 80, height: 50))
    }

    func testAspectFitAndContentSizingKeepProportions() {
        let thumbnail = PinnedImageZoom.aspectFit(CGSize(width: 400, height: 1600), into: CGSize(width: 160, height: 220))
        XCTAssertEqual(thumbnail.height, 220, accuracy: 0.001)
        XCTAssertEqual(thumbnail.width, 55, accuracy: 0.001)

        let resized = PinnedImageZoom.size(forContent: CGSize(width: 200, height: 1000), preferredWidth: 400)
        XCTAssertEqual(resized.width / resized.height, 0.2, accuracy: 0.0001)
        XCTAssertLessThanOrEqual(resized.height, 900.001)
    }

    func testCropMappingWithoutTransformUsesTopLeftImageOrigin() {
        let unit = PinnedImageTransform.imageUnitRect(
            forViewRect: CGRect(x: 0, y: 150, width: 100, height: 50),
            viewSize: CGSize(width: 400, height: 200),
            quarterTurns: 0,
            mirroredHorizontally: false,
            mirroredVertically: false
        )

        assertRect(unit, CGRect(x: 0, y: 0, width: 0.25, height: 0.25))
    }

    func testCropMappingFollowsHorizontalMirror() {
        let unit = PinnedImageTransform.imageUnitRect(
            forViewRect: CGRect(x: 0, y: 150, width: 100, height: 50),
            viewSize: CGSize(width: 400, height: 200),
            quarterTurns: 0,
            mirroredHorizontally: true,
            mirroredVertically: false
        )

        // The view's top-left shows the image's top-right when mirrored.
        assertRect(unit, CGRect(x: 0.75, y: 0, width: 0.25, height: 0.25))
    }

    func testCropMappingFollowsQuarterTurnRotation() {
        // One counter-clockwise quarter turn: the image's top-right corner is
        // displayed at the view's top-left. The view is 200 × 400 for a
        // 400 × 200 image.
        let unit = PinnedImageTransform.imageUnitRect(
            forViewRect: CGRect(x: 0, y: 300, width: 50, height: 100),
            viewSize: CGSize(width: 200, height: 400),
            quarterTurns: 1,
            mirroredHorizontally: false,
            mirroredVertically: false
        )

        assertRect(unit, CGRect(x: 0.75, y: 0, width: 0.25, height: 0.25))
    }

    func testCropMappingAgreesWithRenderedPixels() throws {
        // Paint the image's top-right quadrant red, rotate/mirror it, then
        // check that mapping the red area of the *rendered* picture back
        // yields that same top-right quadrant.
        let image = try makeQuadrantImage()
        for turns in 0..<4 {
            for mirrored in [false, true] {
                let rendered = try XCTUnwrap(PinnedImageTransform.apply(
                    to: image,
                    quarterTurns: turns,
                    mirroredHorizontally: mirrored,
                    mirroredVertically: false
                ))
                let viewSize = CGSize(width: rendered.width, height: rendered.height)
                let redViewRect = try XCTUnwrap(redQuadrant(of: rendered))
                let unit = PinnedImageTransform.imageUnitRect(
                    forViewRect: redViewRect,
                    viewSize: viewSize,
                    quarterTurns: turns,
                    mirroredHorizontally: mirrored,
                    mirroredVertically: false
                )
                assertRect(
                    unit,
                    CGRect(x: 0.5, y: 0, width: 0.5, height: 0.5),
                    "turns=\(turns) mirrored=\(mirrored)"
                )
            }
        }
    }

    func testCopyRendersRotationIntoTheImage() throws {
        let image = try makeQuadrantImage()

        let rotated = try XCTUnwrap(PinnedImageTransform.apply(
            to: image, quarterTurns: 1, mirroredHorizontally: false, mirroredVertically: false
        ))
        XCTAssertEqual(rotated.width, image.height)
        XCTAssertEqual(rotated.height, image.width)

        let untouched = try XCTUnwrap(PinnedImageTransform.apply(
            to: image, quarterTurns: 4, mirroredHorizontally: false, mirroredVertically: false
        ))
        XCTAssertEqual(untouched.width, image.width)
        XCTAssertEqual(untouched.height, image.height)
    }

    // MARK: Beautify

    func testBeautifyOutputAddsUniformPaddingAroundTheCapture() throws {
        let image = try makeQuadrantImage()
        for preset in ScreenshotBeautifyPreset.all {
            let imageSize = CGSize(width: image.width, height: image.height)
            let padding = ScreenshotBeautifyRenderer.padding(for: imageSize, preset: preset)
            let expected = ScreenshotBeautifyRenderer.outputSize(for: imageSize, preset: preset)
            XCTAssertGreaterThanOrEqual(padding, ScreenshotBeautifyRenderer.minimumPadding)
            XCTAssertEqual(expected.width, imageSize.width + padding * 2)
            XCTAssertEqual(expected.height, imageSize.height + padding * 2)

            let rendered = try XCTUnwrap(ScreenshotBeautifyRenderer.render(image, preset: preset))
            XCTAssertEqual(CGFloat(rendered.width), expected.width)
            XCTAssertEqual(CGFloat(rendered.height), expected.height)
        }
    }

    func testBeautifyPaddingScalesWithLargeCapturesButIsCapped() {
        let preset = ScreenshotBeautifyPreset.dusk
        XCTAssertEqual(
            ScreenshotBeautifyRenderer.padding(for: CGSize(width: 2000, height: 1000), preset: preset),
            160
        )
        XCTAssertEqual(
            ScreenshotBeautifyRenderer.padding(for: CGSize(width: 20_000, height: 1000), preset: preset),
            ScreenshotBeautifyRenderer.maximumPadding
        )
        XCTAssertEqual(ScreenshotBeautifyPreset.preset(id: "missing"), .dusk)
        XCTAssertEqual(ScreenshotBeautifyPreset.preset(id: "paper"), .paper)
    }

    // MARK: History

    func testHistoryKeepsOnlyTheNewestFiles() throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("ta-history-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let service = CaptureHistoryService(directory: directory, maximumFileCount: 3)
        let image = try makeQuadrantImage()
        let base = Date(timeIntervalSince1970: 1_700_000_000)

        var written: [URL] = []
        for index in 0..<5 {
            written.append(try service.store(image, scale: 2, date: base.addingTimeInterval(Double(index))))
        }

        let remaining = service.historyFiles().map(\.lastPathComponent)
        XCTAssertEqual(remaining.count, 3)
        XCTAssertEqual(remaining, written.suffix(3).reversed().map(\.lastPathComponent))
        XCTAssertFalse(FileManager.default.fileExists(atPath: written[0].path))
    }

    func testHistoryNeverOverwritesCapturesFromTheSameInstant() throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("ta-history-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let service = CaptureHistoryService(directory: directory, maximumFileCount: 10)
        let image = try makeQuadrantImage()
        let date = Date(timeIntervalSince1970: 1_700_000_000)

        let first = try service.store(image, date: date)
        let second = try service.store(image, date: date)

        XCTAssertNotEqual(first, second)
        XCTAssertEqual(service.historyFiles().count, 2)
    }

    // MARK: Clipboard DPI

    func testRetinaPNGCarries144DPI() throws {
        let image = try makeQuadrantImage()

        let retina = try XCTUnwrap(ClipboardImageEncoder.pngData(for: image, scale: 2))
        XCTAssertEqual(try dpi(of: retina), 144, accuracy: 0.5)

        XCTAssertEqual(ClipboardImageEncoder.dpi(forScale: 1), 72)
        XCTAssertEqual(ClipboardImageEncoder.dpi(forScale: 0), 72)
        XCTAssertNotNil(ClipboardImageEncoder.pngData(for: image, scale: 1))

        let payload = try XCTUnwrap(ClipboardImageEncoder.payload(for: image, scale: 2))
        let tiff = try XCTUnwrap(payload.tiff)
        XCTAssertEqual(try dpi(of: tiff), 144, accuracy: 0.5)
    }

    // MARK: Remembered action / routing / consent

    func testDedicatedHotkeysNeverOverwriteTheRememberedAction() throws {
        let (defaults, suiteName) = makeDefaults()
        defer { defaults.removePersistentDomain(forName: suiteName) }

        RememberedCaptureAction.rememberIfNeeded(mode: .interactive, toolbarChoice: .pin, defaults: defaults)
        XCTAssertEqual(RememberedCaptureAction.load(defaults: defaults), .pin)

        for mode in [CaptureMode.intelligent, .translation, .image, .pin, .long] {
            RememberedCaptureAction.rememberIfNeeded(mode: mode, toolbarChoice: .copyImage, defaults: defaults)
        }
        // A general capture that ran the remembered action without a toolbar
        // choice does not rewrite it either.
        RememberedCaptureAction.rememberIfNeeded(mode: .interactive, toolbarChoice: nil, defaults: defaults)
        XCTAssertEqual(RememberedCaptureAction.load(defaults: defaults), .pin)

        RememberedCaptureAction.reset(defaults: defaults)
        XCTAssertNil(RememberedCaptureAction.load(defaults: defaults))
    }

    func testSmartRouteNeverAsksTheCloudForAnExplanation() {
        XCTAssertEqual(SmartRecognitionRoutePolicy.cloudTask(for: .code), .extractText)
        XCTAssertEqual(SmartRecognitionRoutePolicy.cloudTask(for: .formula), .extractText)
        XCTAssertEqual(SmartRecognitionRoutePolicy.cloudTask(for: .plainText), .extractText)
        XCTAssertEqual(SmartRecognitionRoutePolicy.cloudTask(for: .qrCode), .extractText)
        XCTAssertEqual(SmartRecognitionRoutePolicy.cloudTask(for: .image), .extractText)
        XCTAssertEqual(SmartRecognitionRoutePolicy.cloudTask(for: .table), .tableMarkdown)
    }

    func testTranslationVisionUploadAsksUntilTheUserOptsOut() {
        let (defaults, suiteName) = makeDefaults()
        defer { defaults.removePersistentDomain(forName: suiteName) }

        XCTAssertTrue(TranslationVisionUploadConsent.needsConfirmation(defaults: defaults))
        TranslationVisionUploadConsent.setSkipsConfirmation(true, defaults: defaults)
        XCTAssertFalse(TranslationVisionUploadConsent.needsConfirmation(defaults: defaults))
        TranslationVisionUploadConsent.setSkipsConfirmation(false, defaults: defaults)
        XCTAssertTrue(TranslationVisionUploadConsent.needsConfirmation(defaults: defaults))
    }

    func testCancelledRequestsAreNotReportedAsFailures() {
        XCTAssertTrue(CaptureCancellation.isCancellation(CancellationError(), taskIsCancelled: false))
        XCTAssertTrue(CaptureCancellation.isCancellation(URLError(.cancelled), taskIsCancelled: false))
        XCTAssertTrue(CaptureCancellation.isCancellation(URLError(.timedOut), taskIsCancelled: true))
        XCTAssertFalse(CaptureCancellation.isCancellation(URLError(.timedOut), taskIsCancelled: false))
    }

    // MARK: Toast actions

    @MainActor
    func testToastGrowsToFitAnActionButtonWithinItsOwnLimit() {
        let plain = ResultBarState(kind: .warning, title: "结果已就绪，但没有覆盖剪贴板", detail: "识别期间你复制了其他内容")
        let withAction = ResultBarState(
            kind: .warning,
            title: plain.title,
            detail: plain.detail,
            action: ResultBarAction(title: "仍然复制") {}
        )

        XCTAssertGreaterThan(
            ResultBarLayout.preferredWidth(for: withAction),
            ResultBarLayout.preferredWidth(for: plain)
        )
        XCTAssertLessThanOrEqual(
            ResultBarLayout.preferredWidth(for: withAction),
            ResultBarLayout.maximumWidthWithAction
        )
        XCTAssertLessThanOrEqual(ResultBarLayout.preferredWidth(for: plain), ResultBarLayout.maximumWidth)
    }

    @MainActor
    func testToastActionInvokesItsHandler() {
        var invocations = 0
        let state = ResultBarState(
            kind: .processing,
            title: "正在翻译文字…",
            detail: nil,
            action: ResultBarAction(title: "取消") { invocations += 1 }
        )

        state.action?.handler()

        XCTAssertEqual(invocations, 1)
    }

    // MARK: Hotkeys

    func testHotKeyFailureSummaryNamesEveryFailedShortcut() {
        XCTAssertNil(GlobalHotKeyRegistrationFailure.summary(for: []))

        let failures = [GlobalHotKeyAction.imageCapture, .pinCapture].map {
            GlobalHotKeyRegistrationFailure(
                action: $0,
                shortcut: $0.defaultShortcut,
                status: OSStatus(eventHotKeyExistsErr)
            )
        }
        let summary = GlobalHotKeyRegistrationFailure.summary(for: failures)

        XCTAssertEqual(summary?.contains(GlobalHotKeyAction.imageCapture.displayName), true)
        XCTAssertEqual(summary?.contains(GlobalHotKeyAction.pinCapture.displayName), true)
        XCTAssertEqual(summary?.contains(GlobalHotKeyAction.imageCapture.defaultShortcut.displayText), true)
    }

    // MARK: Selection overlay

    @MainActor
    func testClickingASecondWindowSelectsThatWindow() throws {
        let first = WindowSnapTarget(windowID: 1, frame: CGRect(x: 100, y: 100, width: 200, height: 200), zOrder: 0)
        let second = WindowSnapTarget(windowID: 2, frame: CGRect(x: 400, y: 100, width: 200, height: 200), zOrder: 1)
        let view = SelectionOverlayView(frame: CGRect(x: 0, y: 0, width: 800, height: 600))
        view.showsActionToolbar = true
        view.configureSnapTargets([first, second])
        var finished: CGRect?
        view.onFinish = { rect, _ in finished = rect }

        view.mouseMoved(with: try mouseEvent(type: .mouseMoved, point: CGPoint(x: 150, y: 150)))
        view.mouseDown(with: try mouseEvent(type: .leftMouseDown, point: CGPoint(x: 150, y: 150)))
        view.mouseUp(with: try mouseEvent(type: .leftMouseUp, point: CGPoint(x: 150, y: 150)))
        XCTAssertNil(finished, "The toolbar is showing; nothing is confirmed yet")

        // Hover tracking is paused while a selection exists, so this click
        // must resolve the window under the pointer itself.
        view.mouseDown(with: try mouseEvent(type: .leftMouseDown, point: CGPoint(x: 450, y: 150)))
        view.mouseUp(with: try mouseEvent(type: .leftMouseUp, point: CGPoint(x: 450, y: 150)))
        view.mouseDown(with: try mouseEvent(type: .leftMouseDown, point: CGPoint(x: 450, y: 150), clickCount: 2))

        XCTAssertEqual(finished, second.frame.integral)
    }

    @MainActor
    func testHoldingOptionRevealsTheToolbarInsteadOfRunningTheDefaultAction() throws {
        let view = SelectionOverlayView(frame: CGRect(x: 0, y: 0, width: 800, height: 600))
        view.showsActionToolbar = false
        view.optionRevealsActionToolbar = true
        var finishCount = 0
        view.onFinish = { _, _ in finishCount += 1 }

        view.mouseDown(with: try mouseEvent(type: .leftMouseDown, point: CGPoint(x: 100, y: 100)))
        view.mouseDragged(with: try mouseEvent(type: .leftMouseDragged, point: CGPoint(x: 300, y: 260)))
        view.mouseUp(with: try mouseEvent(
            type: .leftMouseUp,
            point: CGPoint(x: 300, y: 260),
            modifierFlags: [.option]
        ))

        XCTAssertEqual(finishCount, 0)
        XCTAssertTrue(view.showsActionToolbar)
    }

    @MainActor
    func testWithoutOptionTheDefaultActionStillRunsImmediately() throws {
        let view = SelectionOverlayView(frame: CGRect(x: 0, y: 0, width: 800, height: 600))
        view.showsActionToolbar = false
        view.optionRevealsActionToolbar = true
        var finishCount = 0
        view.onFinish = { _, _ in finishCount += 1 }

        view.mouseDown(with: try mouseEvent(type: .leftMouseDown, point: CGPoint(x: 100, y: 100)))
        view.mouseDragged(with: try mouseEvent(type: .leftMouseDragged, point: CGPoint(x: 300, y: 260)))
        view.mouseUp(with: try mouseEvent(type: .leftMouseUp, point: CGPoint(x: 300, y: 260)))

        XCTAssertEqual(finishCount, 1)
        XCTAssertFalse(view.showsActionToolbar)
    }

    // MARK: Helpers

    private func assertRect(
        _ actual: CGRect,
        _ expected: CGRect,
        _ message: String = "",
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        XCTAssertEqual(actual.minX, expected.minX, accuracy: 0.02, message, file: file, line: line)
        XCTAssertEqual(actual.minY, expected.minY, accuracy: 0.02, message, file: file, line: line)
        XCTAssertEqual(actual.width, expected.width, accuracy: 0.02, message, file: file, line: line)
        XCTAssertEqual(actual.height, expected.height, accuracy: 0.02, message, file: file, line: line)
    }

    private func makeDefaults() -> (UserDefaults, String) {
        let suiteName = "CaptureFlowFixTests.\(UUID().uuidString)"
        return (UserDefaults(suiteName: suiteName)!, suiteName)
    }

    /// 400 × 200 white image whose top-right quadrant is red.
    private func makeQuadrantImage() throws -> CGImage {
        let context = try XCTUnwrap(CGContext(
            data: nil,
            width: 400,
            height: 200,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ))
        context.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: 400, height: 200))
        context.setFillColor(CGColor(red: 1, green: 0, blue: 0, alpha: 1))
        // Core Graphics is y-up: the top-right quadrant is x ≥ 200, y ≥ 100.
        context.fill(CGRect(x: 200, y: 100, width: 200, height: 100))
        return try XCTUnwrap(context.makeImage())
    }

    /// Bounding box of the red pixels in AppKit view coordinates (origin
    /// bottom-left), as a pin view of the image's pixel size would show it.
    private func redQuadrant(of image: CGImage) throws -> CGRect? {
        let width = image.width
        let height = image.height
        let context = try XCTUnwrap(CGContext(
            data: nil,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: width * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ))
        context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        let pixels = try XCTUnwrap(context.data).bindMemory(to: UInt8.self, capacity: width * height * 4)

        var minX = Int.max, minRow = Int.max, maxX = Int.min, maxRow = Int.min
        for row in 0..<height {
            for column in 0..<width {
                let offset = (row * width + column) * 4
                let isRed = pixels[offset] > 200 && pixels[offset + 1] < 60 && pixels[offset + 2] < 60
                guard isRed else { continue }
                minX = min(minX, column)
                maxX = max(maxX, column)
                minRow = min(minRow, row)
                maxRow = max(maxRow, row)
            }
        }
        guard minX <= maxX, minRow <= maxRow else { return nil }
        // Bitmap rows run top to bottom; flip to a bottom-left origin.
        return CGRect(
            x: CGFloat(minX),
            y: CGFloat(height - 1 - maxRow),
            width: CGFloat(maxX - minX + 1),
            height: CGFloat(maxRow - minRow + 1)
        )
    }

    private func dpi(of data: Data) throws -> Double {
        let source = try XCTUnwrap(CGImageSourceCreateWithData(data as CFData, nil))
        let properties = try XCTUnwrap(
            CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any]
        )
        return try XCTUnwrap((properties[kCGImagePropertyDPIWidth] as? NSNumber)?.doubleValue)
    }

    @MainActor
    private func mouseEvent(
        type: NSEvent.EventType,
        point: CGPoint,
        clickCount: Int = 1,
        modifierFlags: NSEvent.ModifierFlags = []
    ) throws -> NSEvent {
        try XCTUnwrap(NSEvent.mouseEvent(
            with: type,
            location: point,
            modifierFlags: modifierFlags,
            timestamp: 0,
            windowNumber: 0,
            context: nil,
            eventNumber: 1,
            clickCount: clickCount,
            pressure: 1
        ))
    }
}
