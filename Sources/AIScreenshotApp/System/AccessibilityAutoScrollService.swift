import AppKit
@preconcurrency import ApplicationServices
import CoreGraphics

struct AccessibilityScrollProgress: Equatable {
    let value: Double
    let minimum: Double
    let maximum: Double

    var normalizedValue: Double {
        guard maximum > minimum else { return 0 }
        return min(1, max(0, (value - minimum) / (maximum - minimum)))
    }

    var isAtEnd: Bool {
        normalizedValue >= 0.9995
    }

    /// The same position measured from the other end, so that "end" means
    /// the top of the content. Lets the upward capture reuse every rule that
    /// was written for the downward one.
    var mirrored: AccessibilityScrollProgress {
        AccessibilityScrollProgress(
            value: maximum + minimum - value,
            minimum: minimum,
            maximum: maximum
        )
    }
}

struct AccessibilityScrollTarget {
    enum Mode: Equatable {
        case accessibilityTracked
        case eventFallback
    }

    let sourceProcessID: pid_t?
    let eventLocation: CGPoint
    let scrollContainer: AXUIElement?
    let verticalScrollBar: AXUIElement?

    var mode: Mode {
        // A scrollbar that cannot report its range/value would make the state
        // machine show "tracked" while every bottom check silently falls back
        // to image matching. Only advertise tracking when the contract works.
        verticalScrollBar == nil ? .eventFallback : .accessibilityTracked
    }
}

enum AccessibilityAutoScrollService {
    static let maximumPixelDeltaPerEvent = 32
    static let scrollEventIntervalMilliseconds = 14
    static var isGranted: Bool { AXIsProcessTrusted() }

    @discardableResult
    static func request() -> Bool {
        let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        return AXIsProcessTrustedWithOptions(options)
    }

    @discardableResult
    static func resolveTarget(for selection: CaptureSelection) -> AccessibilityScrollTarget {
        let eventLocation = scrollTargetLocation(for: selection)
        guard isGranted else {
            return AccessibilityScrollTarget(
                sourceProcessID: selection.sourceApplicationProcessID,
                eventLocation: eventLocation,
                scrollContainer: nil,
                verticalScrollBar: nil
            )
        }

        activateAccessibilityTree(for: selection.sourceApplicationProcessID)

        let systemWide = AXUIElementCreateSystemWide()
        var hitElement: AXUIElement?
        guard AXUIElementCopyElementAtPosition(
            systemWide,
            Float(eventLocation.x),
            Float(eventLocation.y),
            &hitElement
        ) == .success,
        let hitElement else {
            return AccessibilityScrollTarget(
                sourceProcessID: selection.sourceApplicationProcessID,
                eventLocation: eventLocation,
                scrollContainer: nil,
                verticalScrollBar: nil
            )
        }

        var hitPID: pid_t = 0
        AXUIElementGetPid(hitElement, &hitPID)
        guard selection.sourceApplicationProcessID == nil
                || selection.sourceApplicationProcessID == hitPID else {
            return AccessibilityScrollTarget(
                sourceProcessID: selection.sourceApplicationProcessID,
                eventLocation: eventLocation,
                scrollContainer: nil,
                verticalScrollBar: nil
            )
        }

        // Nested scroll areas are common (a code block or text field inside a
        // chat timeline). The nearest scrollable ancestor is often that inner
        // widget, which is already "at the end" and would finish the capture
        // after one frame. Collect every scrollable ancestor and lock the one
        // that covers the selection best.
        let selectionRect = selectionRectInEventSpace(for: selection)
        var best: (target: AccessibilityScrollTarget, coverage: CGFloat)?
        var current: AXUIElement? = hitElement
        var visited = 0
        while let element = current, visited < 32 {
            var scrollBar = copyElementAttribute(
                element,
                attribute: kAXVerticalScrollBarAttribute as String
            )
            if scrollBar == nil,
               copyStringAttribute(element, attribute: kAXRoleAttribute as String)
                == kAXScrollAreaRole as String {
                scrollBar = findVerticalScrollBar(in: element, maximumDepth: 2)
            }
            if let scrollBar {
                let candidate = AccessibilityScrollTarget(
                    sourceProcessID: selection.sourceApplicationProcessID,
                    eventLocation: eventLocation,
                    scrollContainer: element,
                    verticalScrollBar: scrollBar
                )
                if progress(of: candidate) != nil {
                    let coverage = coverageScore(
                        containerFrame: copyFrame(element),
                        selectionRect: selectionRect
                    )
                    if best == nil || coverage > (best?.coverage ?? 0) + 0.05 {
                        best = (candidate, coverage)
                    }
                }
            }

            current = copyElementAttribute(element, attribute: kAXParentAttribute as String)
            visited += 1
        }
        if let best { return best.target }

        return AccessibilityScrollTarget(
            sourceProcessID: selection.sourceApplicationProcessID,
            eventLocation: eventLocation,
            scrollContainer: hitElement,
            verticalScrollBar: nil
        )
    }

    /// Fraction of the selection covered by a scroll container. Containers
    /// without a readable frame score zero, so the nearest one wins by order.
    static func coverageScore(containerFrame: CGRect?, selectionRect: CGRect) -> CGFloat {
        guard let containerFrame,
              selectionRect.width > 0,
              selectionRect.height > 0 else { return 0 }
        let overlap = containerFrame.intersection(selectionRect)
        guard !overlap.isNull else { return 0 }
        return (overlap.width * overlap.height) / (selectionRect.width * selectionRect.height)
    }

    /// Chromium and Electron do not build their accessibility tree until an
    /// assistive client asks for it, so Codex, VS Code, Feishu and similar
    /// apps expose no scroll area at all. `AXManualAccessibility` is the
    /// Electron-supported switch for non-VoiceOver clients; other apps reject
    /// the attribute harmlessly. The tree fills in asynchronously, so callers
    /// should resolve the target again a moment later.
    static func activateAccessibilityTree(for processID: pid_t?) {
        guard isGranted, let processID, processID > 0 else { return }
        let application = AXUIElementCreateApplication(processID)
        _ = AXUIElementSetAttributeValue(
            application,
            "AXManualAccessibility" as CFString,
            kCFBooleanTrue
        )
    }

    /// The selection rectangle in the top-left based global space used by
    /// CGEvent and the Accessibility API.
    static func selectionRectInEventSpace(for selection: CaptureSelection) -> CGRect {
        let displayBounds = CGDisplayBounds(selection.displayID)
        return CGRect(
            x: displayBounds.minX + selection.globalRect.minX - selection.screenFrame.minX,
            y: displayBounds.minY + selection.screenFrame.maxY - selection.globalRect.maxY,
            width: selection.globalRect.width,
            height: selection.globalRect.height
        )
    }

    static func cursorLocation() -> CGPoint? {
        CGEvent(source: nil)?.location
    }

    /// The window server routes synthetic wheel events to the window under
    /// the real pointer, not to `CGEvent.location`. Auto scroll therefore only
    /// works while the pointer rests inside the captured area.
    static func cursorIsInside(_ selection: CaptureSelection, inset: CGFloat = 4) -> Bool {
        guard let location = cursorLocation() else { return true }
        return pointIsInside(location, selectionRect: selectionRectInEventSpace(for: selection), inset: inset)
    }

    static func pointIsInside(_ point: CGPoint, selectionRect: CGRect, inset: CGFloat) -> Bool {
        let usable = selectionRect.width > inset * 4 && selectionRect.height > inset * 4
            ? selectionRect.insetBy(dx: inset, dy: inset)
            : selectionRect
        return usable.contains(point)
    }

    /// Parks the pointer on the scroll target once, when auto scroll starts.
    /// Returns true when the pointer had to be moved.
    @discardableResult
    static func movePointerIntoSelectionIfNeeded(
        _ selection: CaptureSelection,
        target: AccessibilityScrollTarget?
    ) -> Bool {
        guard !cursorIsInside(selection, inset: 12) else { return false }
        let location = target?.eventLocation ?? scrollTargetLocation(for: selection)
        CGWarpMouseCursorPosition(location)
        CGAssociateMouseAndMouseCursorPosition(1)
        return true
    }

    private static func copyFrame(_ element: AXUIElement) -> CGRect? {
        var positionValue: CFTypeRef?
        var sizeValue: CFTypeRef?
        guard AXUIElementCopyAttributeValue(
            element, kAXPositionAttribute as CFString, &positionValue) == .success,
              AXUIElementCopyAttributeValue(
                element, kAXSizeAttribute as CFString, &sizeValue) == .success,
              let positionValue,
              let sizeValue,
              CFGetTypeID(positionValue) == AXValueGetTypeID(),
              CFGetTypeID(sizeValue) == AXValueGetTypeID() else { return nil }
        var position = CGPoint.zero
        var size = CGSize.zero
        guard AXValueGetValue(
            unsafeDowncast(positionValue as AnyObject, to: AXValue.self), .cgPoint, &position),
              AXValueGetValue(
                unsafeDowncast(sizeValue as AnyObject, to: AXValue.self), .cgSize, &size)
        else { return nil }
        return CGRect(origin: position, size: size)
    }

    static func progress(of target: AccessibilityScrollTarget) -> AccessibilityScrollProgress? {
        guard let scrollBar = target.verticalScrollBar,
              let value = copyNumberAttribute(scrollBar, attribute: kAXValueAttribute as String) else {
            return nil
        }
        let minimum = copyNumberAttribute(scrollBar, attribute: kAXMinValueAttribute as String) ?? 0
        let maximum = copyNumberAttribute(scrollBar, attribute: kAXMaxValueAttribute as String) ?? 1
        guard maximum > minimum else { return nil }
        return AccessibilityScrollProgress(value: value, minimum: minimum, maximum: maximum)
    }

    static func postDownwardScroll(
        in selection: CaptureSelection,
        target: AccessibilityScrollTarget? = nil,
        pixels: Int = 260
    ) async -> Bool {
        await postVerticalScroll(in: selection, target: target, pixelsDown: abs(pixels))
    }

    @discardableResult
    static func postVerticalScroll(
        in selection: CaptureSelection,
        target: AccessibilityScrollTarget? = nil,
        pixelsDown: Int
    ) async -> Bool {
        guard isGranted else { return false }
        // AX scrollbars are observation-only. Their value is frequently a
        // normalized 0...1 fraction rather than a pixel offset, so adding a
        // 300-pixel capture step jumps straight to the maximum in Chromium and
        // Electron. Use AX only to track progress/bottom and deliver the actual
        // motion through a normal wheel gesture at the locked hit point.
        let location = target?.eventLocation ?? scrollTargetLocation(for: selection)
        let source = CGEventSource(stateID: .hidSystemState)
        let deltas = pixelDeltas(for: pixelsDown)
        for (index, pixelDelta) in deltas.enumerated() {
            guard let event = CGEvent(
                scrollWheelEvent2Source: source,
                units: .pixel,
                wheelCount: 1,
                wheel1: pixelDelta,
                wheel2: 0,
                wheel3: 0
            ) else { return false }
            event.location = location
            event.post(tap: .cghidEventTap)
            if index < deltas.count - 1 {
                try? await Task.sleep(for: .milliseconds(scrollEventIntervalMilliseconds))
            }
        }
        return true
    }

    static func progressAdvanced(
        from before: AccessibilityScrollProgress?,
        to after: AccessibilityScrollProgress?,
        tolerance: Double = 0.000_05
    ) -> Bool {
        guard let before, let after else { return false }
        return after.normalizedValue > before.normalizedValue + tolerance
    }

    static func pixelDeltas(for requestedPixels: Int) -> [Int32] {
        var remaining = max(1, abs(requestedPixels))
        var deltas: [Int32] = []
        let wheelSign: Int32 = requestedPixels >= 0 ? -1 : 1
        while remaining > 0 {
            let step = min(maximumPixelDeltaPerEvent, remaining)
            deltas.append(wheelSign * Int32(step))
            remaining -= step
        }
        return deltas
    }

    static func scrollTargetLocation(for selection: CaptureSelection) -> CGPoint {
        let displayBounds = CGDisplayBounds(selection.displayID)
        return CGPoint(
            x: displayBounds.minX + selection.globalRect.midX - selection.screenFrame.minX,
            y: displayBounds.minY + selection.screenFrame.maxY - selection.globalRect.midY
        )
    }

    private static func findVerticalScrollBar(
        in element: AXUIElement,
        maximumDepth: Int
    ) -> AXUIElement? {
        guard maximumDepth >= 0 else { return nil }
        if copyStringAttribute(element, attribute: kAXRoleAttribute as String) == kAXScrollBarRole as String {
            let orientation = copyStringAttribute(element, attribute: kAXOrientationAttribute as String)
            if orientation == kAXVerticalOrientationValue as String || orientation == nil {
                return element
            }
        }
        guard maximumDepth > 0,
              let children = copyElementArrayAttribute(
                element,
                attribute: kAXChildrenAttribute as String
              ) else { return nil }
        for child in children {
            if let result = findVerticalScrollBar(in: child, maximumDepth: maximumDepth - 1) {
                return result
            }
        }
        return nil
    }

    private static func copyElementAttribute(
        _ element: AXUIElement,
        attribute: String
    ) -> AXUIElement? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, attribute as CFString, &value) == .success,
              let value,
              CFGetTypeID(value) == AXUIElementGetTypeID() else { return nil }
        return unsafeDowncast(value as AnyObject, to: AXUIElement.self)
    }

    private static func copyElementArrayAttribute(
        _ element: AXUIElement,
        attribute: String
    ) -> [AXUIElement]? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, attribute as CFString, &value) == .success,
              let array = value as? [Any] else { return nil }
        return array.compactMap { item in
            let object = item as CFTypeRef
            guard CFGetTypeID(object) == AXUIElementGetTypeID() else { return nil }
            return unsafeDowncast(object as AnyObject, to: AXUIElement.self)
        }
    }

    private static func copyStringAttribute(
        _ element: AXUIElement,
        attribute: String
    ) -> String? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, attribute as CFString, &value) == .success else {
            return nil
        }
        return value as? String
    }

    private static func copyNumberAttribute(
        _ element: AXUIElement,
        attribute: String
    ) -> Double? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, attribute as CFString, &value) == .success else {
            return nil
        }
        return (value as? NSNumber)?.doubleValue
    }

    static func openSystemSettings() {
        guard let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") else { return }
        NSWorkspace.shared.open(url)
    }
}
