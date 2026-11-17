import AIScreenshotCore
import CoreGraphics
import XCTest

@testable import AIScreenshotApp

@MainActor
final class ScrollingCaptureSessionControllerTests: XCTestCase {
  func testRecommendedScrollDistanceScalesWithSelectionAndStaysBounded() {
    XCTAssertEqual(
      ScrollingCaptureSessionController.recommendedScrollDistance(for: selection(height: 200)),
      120
    )
    XCTAssertEqual(
      ScrollingCaptureSessionController.recommendedScrollDistance(for: selection(height: 800)),
      336
    )
    XCTAssertEqual(
      ScrollingCaptureSessionController.recommendedScrollDistance(for: selection(height: 1600)),
      460
    )
  }

  func testAutoScrollSplitsLargeGestureIntoPortablePixelSteps() {
    XCTAssertEqual(
      AccessibilityAutoScrollService.pixelDeltas(for: 130),
      [-32, -32, -32, -32, -2]
    )
    XCTAssertEqual(AccessibilityAutoScrollService.pixelDeltas(for: 1), [-1])
    XCTAssertEqual(AccessibilityAutoScrollService.pixelDeltas(for: 96).reduce(0, +), -96)
    XCTAssertTrue(
      AccessibilityAutoScrollService.pixelDeltas(for: 520)
        .allSatisfy { abs($0) <= 32 }
    )
    XCTAssertEqual(AccessibilityAutoScrollService.pixelDeltas(for: -65), [32, 32, 1])
  }

  func testAutoScrollUsesStandardFastCaptureTiming() {
    XCTAssertEqual(ScrollingCaptureSessionController.standardAutoScrollDelayMilliseconds, 300)
    XCTAssertEqual(ScrollingCaptureSessionController.standardAutoScrollDelay, 0.30)
    XCTAssertEqual(ScrollingCaptureSessionController.maximumAutoScrollSettleTime, 1.20)
    XCTAssertEqual(AccessibilityAutoScrollService.scrollEventIntervalMilliseconds, 14)
  }

  func testPlacedFrameResolvesGestureAsProgress() {
    let now = Date()
    XCTAssertEqual(
      ScrollingCaptureSessionController.resolvePendingGesture(
        contentAdvanced: true, viewportMoved: true, sawMovement: false,
        now: now, settleDeadline: now.addingTimeInterval(1)),
      .progress
    )
  }

  func testMovingViewportWithoutSeamWaitsInsteadOfBlindStitching() {
    let now = Date()
    XCTAssertEqual(
      ScrollingCaptureSessionController.resolvePendingGesture(
        contentAdvanced: false, viewportMoved: true, sawMovement: true,
        now: now, settleDeadline: now.addingTimeInterval(-0.1)),
      .waiting
    )
  }

  func testSettledViewportThatMovedButNeverMatchedIsFlagged() {
    let now = Date()
    XCTAssertEqual(
      ScrollingCaptureSessionController.resolvePendingGesture(
        contentAdvanced: false, viewportMoved: false, sawMovement: true,
        now: now, settleDeadline: now.addingTimeInterval(-0.1)),
      .movedWithoutMatch
    )
  }

  func testSilentGestureIsNoMovementOnlyAfterDeadline() {
    let now = Date()
    XCTAssertEqual(
      ScrollingCaptureSessionController.resolvePendingGesture(
        contentAdvanced: false, viewportMoved: false, sawMovement: false,
        now: now, settleDeadline: now.addingTimeInterval(0.5)),
      .waiting
    )
    XCTAssertEqual(
      ScrollingCaptureSessionController.resolvePendingGesture(
        contentAdvanced: false, viewportMoved: false, sawMovement: false,
        now: now, settleDeadline: now.addingTimeInterval(-0.1)),
      .noMovement
    )
  }

  func testEndlesslyAnimatingViewportIsForceResolved() {
    let now = Date()
    let deadline = now.addingTimeInterval(
      -ScrollingCaptureSessionController.maximumAutoScrollAnimationTime - 0.1)
    XCTAssertEqual(
      ScrollingCaptureSessionController.resolvePendingGesture(
        contentAdvanced: false, viewportMoved: true, sawMovement: true,
        now: now, settleDeadline: deadline),
      .movedWithoutMatch
    )
  }

  func testNeverScrollingIsNotReportedAsReachedEnd() {
    XCTAssertEqual(
      ScrollingCaptureSessionController.noProgressVerdict(
        hasTrackedProgress: false, gesturesWithProgress: 0, retargetCount: 0),
      .notScrolling
    )
    XCTAssertEqual(
      ScrollingCaptureSessionController.noProgressVerdict(
        hasTrackedProgress: false, gesturesWithProgress: 3, retargetCount: 0),
      .reachedEnd
    )
  }

  func testTrackedScrollbarBelowEndRetargetsBeforeGivingUp() {
    XCTAssertEqual(
      ScrollingCaptureSessionController.noProgressVerdict(
        hasTrackedProgress: true, gesturesWithProgress: 5, retargetCount: 0),
      .retarget
    )
    XCTAssertEqual(
      ScrollingCaptureSessionController.noProgressVerdict(
        hasTrackedProgress: true, gesturesWithProgress: 5, retargetCount: 2),
      .notScrolling
    )
  }

  func testScrollProgressNormalizesNonUnitRanges() {
    let progress = AccessibilityScrollProgress(value: 150, minimum: 100, maximum: 300)
    XCTAssertEqual(progress.normalizedValue, 0.25, accuracy: 0.000_1)
    XCTAssertFalse(progress.isAtEnd)
  }

  func testMirroredProgressTreatsTopAsEnd() {
    let top = AccessibilityScrollProgress(value: 0, minimum: 0, maximum: 1)
    XCTAssertFalse(top.isAtEnd)
    XCTAssertTrue(top.mirrored.isAtEnd)
    let nonUnit = AccessibilityScrollProgress(value: 120, minimum: 100, maximum: 300)
    XCTAssertEqual(nonUnit.mirrored.normalizedValue, 0.9, accuracy: 0.000_1)
  }

  func testPointerOutsideSelectionIsDetected() {
    let rect = CGRect(x: 100, y: 100, width: 400, height: 300)
    XCTAssertTrue(
      AccessibilityAutoScrollService.pointIsInside(
        CGPoint(x: 300, y: 250), selectionRect: rect, inset: 4))
    XCTAssertFalse(
      AccessibilityAutoScrollService.pointIsInside(
        CGPoint(x: 300, y: 420), selectionRect: rect, inset: 4))
    XCTAssertFalse(
      AccessibilityAutoScrollService.pointIsInside(
        CGPoint(x: 101, y: 250), selectionRect: rect, inset: 4))
  }

  func testScrollContainerCoveringSelectionBeatsSmallInnerOne() {
    let selection = CGRect(x: 0, y: 0, width: 600, height: 800)
    let inner = AccessibilityAutoScrollService.coverageScore(
      containerFrame: CGRect(x: 50, y: 300, width: 500, height: 120), selectionRect: selection)
    let outer = AccessibilityAutoScrollService.coverageScore(
      containerFrame: CGRect(x: 0, y: -40, width: 600, height: 900), selectionRect: selection)
    XCTAssertLessThan(inner, 0.2)
    XCTAssertEqual(outer, 1, accuracy: 0.001)
    XCTAssertEqual(
      AccessibilityAutoScrollService.coverageScore(containerFrame: nil, selectionRect: selection),
      0)
  }

  func testOverlongRetinaPartsMergeIntoOneHalfResolutionImage() throws {
    let parts = [
      try makeImage(width: 200, height: 300),
      try makeImage(width: 200, height: 300),
      try makeImage(width: 200, height: 101),
    ]
    let merged = try XCTUnwrap(
      ScrollingCaptureSessionController.mergedAtHalfResolution(parts, maximumPixelHeight: 400))
    XCTAssertEqual(merged.width, 100)
    XCTAssertEqual(merged.height, 350)
    XCTAssertNil(
      ScrollingCaptureSessionController.mergedAtHalfResolution(parts, maximumPixelHeight: 300))
  }

  func testClosingSeamReviewWindowCancelsCaptureSession() throws {
    let stitcher = ScrollingImageStitcher()
    _ = try stitcher.append(makeImage(width: 120, height: 80))
    let controller = ScrollingSeamReviewWindowController()
    var cancellationCount = 0

    controller.present(
      stitcher: stitcher,
      onComplete: { _, _ in XCTFail("Closing the review window must not complete the capture") },
      onCancel: { cancellationCount += 1 }
    )
    let window = try XCTUnwrap(NSApp.windows.first(where: { $0.title == "检查长截图接缝" }))
    window.performClose(nil)

    XCTAssertEqual(cancellationCount, 1)
  }

  private func selection(height: CGFloat) -> CaptureSelection {
    CaptureSelection(
      globalRect: CGRect(x: 100, y: 100, width: 600, height: height),
      screenFrame: CGRect(x: 0, y: 0, width: 1920, height: 1080),
      displayID: 1,
      backingScaleFactor: 2
    )
  }

  private func makeImage(width: Int, height: Int) throws -> CGImage {
    guard
      let context = CGContext(
        data: nil,
        width: width,
        height: height,
        bitsPerComponent: 8,
        bytesPerRow: 0,
        space: CGColorSpaceCreateDeviceRGB(),
        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
      ), let image = context.makeImage()
    else {
      throw NSError(domain: "ScrollingCaptureSessionControllerTests", code: 1)
    }
    return image
  }
}
