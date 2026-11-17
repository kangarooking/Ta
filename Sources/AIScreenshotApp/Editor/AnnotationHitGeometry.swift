import CoreGraphics

/// Hit-testing for annotations. Hollow shapes are only hit on their stroke so
/// that a drawing tool can still be used inside an existing rectangle/ellipse.
enum AnnotationHitGeometry {
    static func distance(from point: CGPoint, toSegmentFrom start: CGPoint, to end: CGPoint) -> CGFloat {
        let dx = end.x - start.x
        let dy = end.y - start.y
        let lengthSquared = dx * dx + dy * dy
        guard lengthSquared > 0.000_001 else {
            return hypot(point.x - start.x, point.y - start.y)
        }
        let projection = ((point.x - start.x) * dx + (point.y - start.y) * dy) / lengthSquared
        let clamped = min(1, max(0, projection))
        return hypot(
            point.x - (start.x + dx * clamped),
            point.y - (start.y + dy * clamped)
        )
    }

    /// `true` when `point` lies on the outline of `rect` drawn with
    /// `lineWidth`, allowing `tolerance` on either side. The interior is not a
    /// hit.
    static func rectangleStrokeContains(
        _ point: CGPoint,
        rect: CGRect,
        lineWidth: CGFloat,
        tolerance: CGFloat
    ) -> Bool {
        let reach = max(0, lineWidth / 2) + max(0, tolerance)
        guard rect.insetBy(dx: -reach, dy: -reach).contains(point) else { return false }
        let inner = rect.insetBy(dx: reach, dy: reach)
        // A shape thinner than its own hit band has no interior left.
        guard !inner.isNull, inner.width > 0, inner.height > 0 else { return true }
        return !inner.contains(point)
    }

    static func ellipseStrokeContains(
        _ point: CGPoint,
        rect: CGRect,
        lineWidth: CGFloat,
        tolerance: CGFloat
    ) -> Bool {
        let reach = max(0, lineWidth / 2) + max(0, tolerance)
        let center = CGPoint(x: rect.midX, y: rect.midY)
        let dx = point.x - center.x
        let dy = point.y - center.y

        func isInside(radiusX: CGFloat, radiusY: CGFloat) -> Bool {
            guard radiusX > 0, radiusY > 0 else { return false }
            let nx = dx / radiusX
            let ny = dy / radiusY
            return nx * nx + ny * ny <= 1
        }

        guard isInside(radiusX: rect.width / 2 + reach, radiusY: rect.height / 2 + reach) else {
            return false
        }
        return !isInside(radiusX: rect.width / 2 - reach, radiusY: rect.height / 2 - reach)
    }

    static func polylineContains(
        _ point: CGPoint,
        points: [CGPoint],
        lineWidth: CGFloat,
        tolerance: CGFloat
    ) -> Bool {
        guard let first = points.first else { return false }
        let reach = max(0, lineWidth / 2) + max(0, tolerance)
        if points.count == 1 {
            return hypot(point.x - first.x, point.y - first.y) <= reach
        }
        var previous = first
        for current in points.dropFirst() {
            if distance(from: point, toSegmentFrom: previous, to: current) <= reach {
                return true
            }
            previous = current
        }
        return false
    }

    static func polygonContains(_ point: CGPoint, polygon: [CGPoint]) -> Bool {
        guard polygon.count >= 3 else { return false }
        var isInside = false
        var previous = polygon[polygon.count - 1]
        for current in polygon {
            if (current.y > point.y) != (previous.y > point.y) {
                let crossingX = (previous.x - current.x) * (point.y - current.y)
                    / (previous.y - current.y) + current.x
                if point.x < crossingX { isInside.toggle() }
            }
            previous = current
        }
        return isInside
    }

    static func arrowContains(
        _ point: CGPoint,
        start: CGPoint,
        end: CGPoint,
        lineWidth: CGFloat,
        tolerance: CGFloat
    ) -> Bool {
        if distance(from: point, toSegmentFrom: start, to: end) <= max(0, lineWidth / 2) + max(0, tolerance) {
            return true
        }
        return polygonContains(
            point,
            polygon: TaperedArrowGeometry.polygon(from: start, to: end, width: lineWidth)
        )
    }
}

/// Decides whether a finished drag produced something visible. A plain click
/// with a drag tool must not leave an invisible zero-size element behind.
enum AnnotationDragValidity {
    static let minimumArrowLength: CGFloat = 4
    static let minimumStrokeExtent: CGFloat = 1

    static func isDrawableArrow(from start: CGPoint, to end: CGPoint) -> Bool {
        hypot(end.x - start.x, end.y - start.y) >= minimumArrowLength
    }

    static func isDrawableRect(_ rect: CGRect, minimumSide: CGFloat) -> Bool {
        !rect.isNull && rect.width >= minimumSide && rect.height >= minimumSide
    }

    /// A freehand stroke needs at least two points that are not all stacked
    /// on the same spot.
    static func isDrawableStroke(_ points: [CGPoint]) -> Bool {
        guard points.count > 1, let first = points.first else { return false }
        return points.contains {
            hypot($0.x - first.x, $0.y - first.y) >= minimumStrokeExtent
        }
    }
}
