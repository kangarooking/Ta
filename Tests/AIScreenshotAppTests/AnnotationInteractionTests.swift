import AppKit
import XCTest
@testable import AIScreenshotApp

final class AnnotationInteractionTests: XCTestCase {
    // MARK: Hit geometry

    func testHollowRectangleIsHitOnItsStrokeButNotItsInterior() {
        let rect = CGRect(x: 50, y: 50, width: 200, height: 150)

        XCTAssertTrue(AnnotationHitGeometry.rectangleStrokeContains(
            CGPoint(x: 50, y: 120), rect: rect, lineWidth: 5, tolerance: 6
        ))
        XCTAssertTrue(AnnotationHitGeometry.rectangleStrokeContains(
            CGPoint(x: 254, y: 60), rect: rect, lineWidth: 5, tolerance: 6
        ))
        XCTAssertFalse(AnnotationHitGeometry.rectangleStrokeContains(
            CGPoint(x: 150, y: 120), rect: rect, lineWidth: 5, tolerance: 6
        ))
        XCTAssertFalse(AnnotationHitGeometry.rectangleStrokeContains(
            CGPoint(x: 20, y: 120), rect: rect, lineWidth: 5, tolerance: 6
        ))
    }

    func testHollowEllipseIsHitOnItsStrokeButNotItsInterior() {
        let rect = CGRect(x: 0, y: 0, width: 200, height: 100)

        XCTAssertTrue(AnnotationHitGeometry.ellipseStrokeContains(
            CGPoint(x: 200, y: 50), rect: rect, lineWidth: 4, tolerance: 6
        ))
        XCTAssertTrue(AnnotationHitGeometry.ellipseStrokeContains(
            CGPoint(x: 100, y: 0), rect: rect, lineWidth: 4, tolerance: 6
        ))
        XCTAssertFalse(AnnotationHitGeometry.ellipseStrokeContains(
            CGPoint(x: 100, y: 50), rect: rect, lineWidth: 4, tolerance: 6
        ))
        XCTAssertFalse(AnnotationHitGeometry.ellipseStrokeContains(
            CGPoint(x: 4, y: 4), rect: rect, lineWidth: 4, tolerance: 6
        ))
    }

    func testPolylineAndArrowHitsFollowTheStroke() {
        let points = [CGPoint(x: 0, y: 0), CGPoint(x: 100, y: 0), CGPoint(x: 100, y: 100)]

        XCTAssertTrue(AnnotationHitGeometry.polylineContains(
            CGPoint(x: 50, y: 4), points: points, lineWidth: 4, tolerance: 6
        ))
        XCTAssertFalse(AnnotationHitGeometry.polylineContains(
            CGPoint(x: 40, y: 60), points: points, lineWidth: 4, tolerance: 6
        ))
        XCTAssertTrue(AnnotationHitGeometry.arrowContains(
            CGPoint(x: 50, y: 52), start: CGPoint(x: 0, y: 50), end: CGPoint(x: 200, y: 50),
            lineWidth: 5, tolerance: 6
        ))
        XCTAssertFalse(AnnotationHitGeometry.arrowContains(
            CGPoint(x: 50, y: 120), start: CGPoint(x: 0, y: 50), end: CGPoint(x: 200, y: 50),
            lineWidth: 5, tolerance: 6
        ))
    }

    func testDistanceToSegmentClampsToEndpoints() {
        let distance = AnnotationHitGeometry.distance(
            from: CGPoint(x: 13, y: 4),
            toSegmentFrom: CGPoint(x: 0, y: 0),
            to: CGPoint(x: 10, y: 0)
        )
        XCTAssertEqual(distance, 5, accuracy: 0.0001)
    }

    // MARK: Degenerate shapes

    func testClickWithoutDragIsNotADrawableShape() {
        let point = CGPoint(x: 40, y: 40)

        XCTAssertFalse(AnnotationDragValidity.isDrawableArrow(from: point, to: point))
        XCTAssertFalse(AnnotationDragValidity.isDrawableArrow(from: point, to: CGPoint(x: 41, y: 41)))
        XCTAssertTrue(AnnotationDragValidity.isDrawableArrow(from: point, to: CGPoint(x: 60, y: 40)))
        XCTAssertFalse(AnnotationDragValidity.isDrawableRect(.zero, minimumSide: 2))
        XCTAssertTrue(AnnotationDragValidity.isDrawableRect(CGRect(x: 0, y: 0, width: 5, height: 5), minimumSide: 2))
        XCTAssertFalse(AnnotationDragValidity.isDrawableStroke([point, point]))
        XCTAssertFalse(AnnotationDragValidity.isDrawableStroke([point]))
        XCTAssertTrue(AnnotationDragValidity.isDrawableStroke([point, CGPoint(x: 44, y: 40)]))
    }

    // MARK: Display / export size parity

    func testTextAndNumberSizesAreNotFlooredOnScreen() {
        // Export renders at scale 1; a zoomed-out editor must show the same
        // proportion instead of clamping to an 11 pt / 18 pt minimum.
        XCTAssertEqual(AnnotationTextMetrics.renderedSize(for: 24, scale: 1), 24)
        XCTAssertEqual(AnnotationTextMetrics.renderedSize(for: 24, scale: 0.25), 6)
        XCTAssertEqual(AnnotationTextMetrics.renderedSize(for: 30, scale: 0.5), 15)
    }

    // MARK: Canvas behaviour

    @MainActor
    func testDrawingToolDrawsInsideAnExistingRectangle() throws {
        let canvas = try makeCanvas()
        try drag(canvas, from: CGPoint(x: 50, y: 50), to: CGPoint(x: 250, y: 200))
        XCTAssertEqual(canvas.elementCount, 1)

        canvas.setTool(.arrow)
        canvas.mouseDown(with: try mouseEvent(type: .leftMouseDown, point: CGPoint(x: 100, y: 100)))
        XCTAssertFalse(canvas.isManipulatingElement, "The rectangle's interior must not grab the press")
        canvas.mouseDragged(with: try mouseEvent(type: .leftMouseDragged, point: CGPoint(x: 200, y: 150)))
        canvas.mouseUp(with: try mouseEvent(type: .leftMouseUp, point: CGPoint(x: 200, y: 150)))

        XCTAssertEqual(canvas.elementCount, 2)
    }

    @MainActor
    func testPressOnTheStrokeStillGrabsTheElement() throws {
        let canvas = try makeCanvas()
        try drag(canvas, from: CGPoint(x: 50, y: 50), to: CGPoint(x: 250, y: 200))
        canvas.setTool(.arrow)

        canvas.mouseDown(with: try mouseEvent(type: .leftMouseDown, point: CGPoint(x: 50, y: 120)))
        XCTAssertTrue(canvas.isManipulatingElement)
        canvas.mouseUp(with: try mouseEvent(type: .leftMouseUp, point: CGPoint(x: 50, y: 120)))

        XCTAssertEqual(canvas.elementCount, 1, "Grabbing must not draw a new arrow")
    }

    @MainActor
    func testSelectToolStillGrabsHollowShapesByTheirInterior() throws {
        let canvas = try makeCanvas()
        try drag(canvas, from: CGPoint(x: 50, y: 50), to: CGPoint(x: 250, y: 200))
        canvas.setTool(.select)

        canvas.mouseDown(with: try mouseEvent(type: .leftMouseDown, point: CGPoint(x: 150, y: 120)))
        XCTAssertTrue(canvas.isManipulatingElement)
        canvas.mouseUp(with: try mouseEvent(type: .leftMouseUp, point: CGPoint(x: 150, y: 120)))
        XCTAssertTrue(canvas.hasSelection)
    }

    @MainActor
    func testClickingAnElementWithoutMovingItDoesNotTouchUndoHistory() throws {
        let canvas = try makeCanvas()
        try drag(canvas, from: CGPoint(x: 50, y: 50), to: CGPoint(x: 250, y: 200))
        try drag(canvas, from: CGPoint(x: 300, y: 220), to: CGPoint(x: 380, y: 280))
        canvas.undo(nil)
        XCTAssertEqual(canvas.undoDepth, 1)
        XCTAssertEqual(canvas.redoDepth, 1)

        canvas.mouseDown(with: try mouseEvent(type: .leftMouseDown, point: CGPoint(x: 50, y: 120)))
        canvas.mouseUp(with: try mouseEvent(type: .leftMouseUp, point: CGPoint(x: 50, y: 120)))

        XCTAssertEqual(canvas.undoDepth, 1, "A click that moves nothing is not an undo step")
        XCTAssertEqual(canvas.redoDepth, 1, "A click that moves nothing must not clear redo")
    }

    @MainActor
    func testMovingAnElementRecordsExactlyOneUndoStep() throws {
        let canvas = try makeCanvas()
        try drag(canvas, from: CGPoint(x: 50, y: 50), to: CGPoint(x: 250, y: 200))
        XCTAssertEqual(canvas.undoDepth, 1)

        try drag(canvas, from: CGPoint(x: 50, y: 120), to: CGPoint(x: 90, y: 140))

        XCTAssertEqual(canvas.elementCount, 1)
        XCTAssertEqual(canvas.undoDepth, 2)
    }

    @MainActor
    func testArrowClickWithoutDragAddsNothing() throws {
        let canvas = try makeCanvas()
        canvas.setTool(.arrow)

        canvas.mouseDown(with: try mouseEvent(type: .leftMouseDown, point: CGPoint(x: 120, y: 120)))
        canvas.mouseUp(with: try mouseEvent(type: .leftMouseUp, point: CGPoint(x: 120, y: 120)))

        XCTAssertEqual(canvas.elementCount, 0)
        XCTAssertEqual(canvas.undoDepth, 0)
    }

    @MainActor
    func testExportedImageKeepsSourcePixelSize() throws {
        let canvas = try makeCanvas()
        try drag(canvas, from: CGPoint(x: 50, y: 50), to: CGPoint(x: 250, y: 200))

        let rendered = canvas.renderedImage()

        XCTAssertEqual(rendered.width, 400)
        XCTAssertEqual(rendered.height, 300)
    }

    // MARK: Helpers

    @MainActor
    private func makeCanvas() throws -> AnnotationCanvasView {
        let context = try XCTUnwrap(CGContext(
            data: nil,
            width: 400,
            height: 300,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ))
        context.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: 400, height: 300))
        let image = try XCTUnwrap(context.makeImage())
        // No inset and a frame equal to the image size: view points map 1:1
        // to image pixels.
        let canvas = AnnotationCanvasView(image: image, contentInset: 0)
        canvas.frame = CGRect(x: 0, y: 0, width: 400, height: 300)
        return canvas
    }

    @MainActor
    private func drag(_ canvas: AnnotationCanvasView, from start: CGPoint, to end: CGPoint) throws {
        canvas.mouseDown(with: try mouseEvent(type: .leftMouseDown, point: start))
        canvas.mouseDragged(with: try mouseEvent(type: .leftMouseDragged, point: end))
        canvas.mouseUp(with: try mouseEvent(type: .leftMouseUp, point: end))
    }

    @MainActor
    private func mouseEvent(
        type: NSEvent.EventType,
        point: CGPoint,
        clickCount: Int = 1
    ) throws -> NSEvent {
        try XCTUnwrap(NSEvent.mouseEvent(
            with: type,
            location: point,
            modifierFlags: [],
            timestamp: 0,
            windowNumber: 0,
            context: nil,
            eventNumber: 1,
            clickCount: clickCount,
            pressure: 1
        ))
    }
}
