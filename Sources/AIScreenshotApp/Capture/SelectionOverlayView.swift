import AppKit
import AIScreenshotCore

enum CaptureActionToolbarLayout {
    static let actions: [(action: CaptureQuickAction, title: String, symbol: String)] = [
        (.beautify, "美化", "wand.and.stars"),
        (.multimodal, "AI 识图", "sparkles"),
        (.localOCR, "取字", "text.viewfinder"),
        (.translate, "翻译", "character.book.closed"),
        (.edit, "标注", "pencil.tip.crop.circle"),
        (.pin, "钉图", "pin.fill"),
        (.copyImage, "复制", "doc.on.doc"),
        (.save, "保存", "square.and.arrow.down")
    ]
}

enum SelectionResizeHandle: CaseIterable {
    case topLeft
    case top
    case topRight
    case right
    case bottomRight
    case bottom
    case bottomLeft
    case left

    func point(in rect: CGRect) -> CGPoint {
        switch self {
        case .topLeft: CGPoint(x: rect.minX, y: rect.maxY)
        case .top: CGPoint(x: rect.midX, y: rect.maxY)
        case .topRight: CGPoint(x: rect.maxX, y: rect.maxY)
        case .right: CGPoint(x: rect.maxX, y: rect.midY)
        case .bottomRight: CGPoint(x: rect.maxX, y: rect.minY)
        case .bottom: CGPoint(x: rect.midX, y: rect.minY)
        case .bottomLeft: CGPoint(x: rect.minX, y: rect.minY)
        case .left: CGPoint(x: rect.minX, y: rect.midY)
        }
    }

    func hitRect(in rect: CGRect, tolerance: CGFloat) -> CGRect {
        let diameter = tolerance * 2
        switch self {
        case .topLeft, .topRight, .bottomRight, .bottomLeft:
            let point = point(in: rect)
            return CGRect(
                x: point.x - tolerance,
                y: point.y - tolerance,
                width: diameter,
                height: diameter
            )
        case .top:
            return CGRect(
                x: rect.minX + tolerance,
                y: rect.maxY - tolerance,
                width: max(0, rect.width - diameter),
                height: diameter
            )
        case .right:
            return CGRect(
                x: rect.maxX - tolerance,
                y: rect.minY + tolerance,
                width: diameter,
                height: max(0, rect.height - diameter)
            )
        case .bottom:
            return CGRect(
                x: rect.minX + tolerance,
                y: rect.minY - tolerance,
                width: max(0, rect.width - diameter),
                height: diameter
            )
        case .left:
            return CGRect(
                x: rect.minX - tolerance,
                y: rect.minY + tolerance,
                width: diameter,
                height: max(0, rect.height - diameter)
            )
        }
    }

    var cursor: NSCursor {
        if #available(macOS 15.0, *) {
            return .frameResize(position: frameResizePosition, directions: .all)
        }
        switch self {
        case .top, .bottom:
            return .resizeUpDown
        case .left, .right, .topLeft, .topRight, .bottomRight, .bottomLeft:
            return .resizeLeftRight
        }
    }

    @available(macOS 15.0, *)
    private var frameResizePosition: NSCursor.FrameResizePosition {
        switch self {
        case .topLeft: .topLeft
        case .top: .top
        case .topRight: .topRight
        case .right: .right
        case .bottomRight: .bottomRight
        case .bottom: .bottom
        case .bottomLeft: .bottomLeft
        case .left: .left
        }
    }
}

enum SelectionDragMode {
    case creating
    case moving(original: CGRect)
    case resizing(handle: SelectionResizeHandle, original: CGRect)
}

enum SelectionRectEditor {
    static let resizeHitTolerance: CGFloat = 10

    static func resizeHandle(
        at point: CGPoint,
        for rect: CGRect,
        tolerance: CGFloat = resizeHitTolerance
    ) -> SelectionResizeHandle? {
        let corners: [SelectionResizeHandle] = [.topLeft, .topRight, .bottomRight, .bottomLeft]
        if let corner = corners.first(where: { $0.hitRect(in: rect, tolerance: tolerance).contains(point) }) {
            return corner
        }
        let edges: [SelectionResizeHandle] = [.top, .right, .bottom, .left]
        return edges.first(where: { $0.hitRect(in: rect, tolerance: tolerance).contains(point) })
    }

    static func moved(_ rect: CGRect, by offset: CGPoint, inside bounds: CGRect) -> CGRect {
        let x = min(max(bounds.minX, rect.minX + offset.x), bounds.maxX - rect.width)
        let y = min(max(bounds.minY, rect.minY + offset.y), bounds.maxY - rect.height)
        return CGRect(origin: CGPoint(x: x, y: y), size: rect.size).integral
    }

    static func resized(
        _ rect: CGRect,
        handle: SelectionResizeHandle,
        to point: CGPoint,
        inside bounds: CGRect
    ) -> CGRect {
        let point = CGPoint(
            x: min(max(point.x, bounds.minX), bounds.maxX),
            y: min(max(point.y, bounds.minY), bounds.maxY)
        )
        let x1: CGFloat
        let x2: CGFloat
        let y1: CGFloat
        let y2: CGFloat
        switch handle {
        case .topLeft:
            (x1, x2, y1, y2) = (point.x, rect.maxX, rect.minY, point.y)
        case .top:
            (x1, x2, y1, y2) = (rect.minX, rect.maxX, rect.minY, point.y)
        case .topRight:
            (x1, x2, y1, y2) = (rect.minX, point.x, rect.minY, point.y)
        case .right:
            (x1, x2, y1, y2) = (rect.minX, point.x, rect.minY, rect.maxY)
        case .bottomRight:
            (x1, x2, y1, y2) = (rect.minX, point.x, point.y, rect.maxY)
        case .bottom:
            (x1, x2, y1, y2) = (rect.minX, rect.maxX, point.y, rect.maxY)
        case .bottomLeft:
            (x1, x2, y1, y2) = (point.x, rect.maxX, point.y, rect.maxY)
        case .left:
            (x1, x2, y1, y2) = (point.x, rect.maxX, rect.minY, rect.maxY)
        }
        return CGRect(
            x: min(x1, x2),
            y: min(y1, y2),
            width: abs(x2 - x1),
            height: abs(y2 - y1)
        ).intersection(bounds).integral
    }
}

final class CaptureActionButton: NSButton {
    var onHoverChange: ((Bool) -> Void)?
    private var hoverTrackingArea: NSTrackingArea?

    override func resetCursorRects() {
        guard window != nil, !isHiddenOrHasHiddenAncestor, !bounds.isEmpty else { return }
        addCursorRect(bounds, cursor: .pointingHand)
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let hoverTrackingArea {
            removeTrackingArea(hoverTrackingArea)
        }
        let area = NSTrackingArea(
            rect: bounds,
            options: [.activeAlways, .mouseEnteredAndExited, .inVisibleRect],
            owner: self,
            userInfo: nil
        )
        addTrackingArea(area)
        hoverTrackingArea = area
    }

    override func mouseEntered(with event: NSEvent) {
        super.mouseEntered(with: event)
        NSCursor.pointingHand.set()
        onHoverChange?(true)
    }

    override func mouseExited(with event: NSEvent) {
        super.mouseExited(with: event)
        onHoverChange?(false)
    }
}

final class SelectionOverlayView: NSView {
    var onFinish: ((CGRect?, CaptureQuickAction?) -> Void)?
    var showsActionToolbar = false
    /// When a default / remembered action would run immediately, holding
    /// Option while confirming the selection shows the action toolbar instead.
    var optionRevealsActionToolbar = false

    private var dragStart: CGPoint?
    private var dragMode: SelectionDragMode = .creating
    private var selectionRect: CGRect?
    private var pendingSnapRect: CGRect?
    private var hoveredSnapTarget: WindowSnapTarget?
    private var snapTargets: [WindowSnapTarget] = []
    private var didDrag = false
    private var isTransitioning = false
    private var actionToolbar: NSVisualEffectView?
    private var actionTooltip: NSView?
    private var showsPresetFixture = false
    private var trackingArea: NSTrackingArea?
    private var frozenDisplayImage: NSImage?

    override var acceptsFirstResponder: Bool { true }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool {
        true
    }

    override func resetCursorRects() {
        guard window != nil, !isHiddenOrHasHiddenAncestor, !bounds.isEmpty else { return }
        addValidCursorRect(bounds, cursor: .crosshair)
        if let actionToolbar {
            addValidCursorRect(actionToolbar.frame, cursor: .pointingHand)
        }
        guard dragStart == nil, showsActionToolbar, let selectionRect else { return }
        addValidCursorRect(selectionRect.insetBy(dx: 4, dy: 4), cursor: .openHand)
        for handle in SelectionResizeHandle.allCases {
            addValidCursorRect(
                handle.hitRect(in: selectionRect, tolerance: SelectionRectEditor.resizeHitTolerance),
                cursor: handle.cursor
            )
        }
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let trackingArea { removeTrackingArea(trackingArea) }
        let area = NSTrackingArea(
            rect: bounds,
            options: [.activeAlways, .mouseMoved, .mouseEnteredAndExited, .inVisibleRect],
            owner: self,
            userInfo: nil
        )
        addTrackingArea(area)
        trackingArea = area
    }

    func setFrozenDisplayImage(_ image: CGImage?) {
        frozenDisplayImage = image.map { NSImage(cgImage: $0, size: bounds.size) }
        needsDisplay = true
    }

    func activateInitialCursor() {
        NSCursor.crosshair.set()
        invalidateCursorRects()
    }

    func configureSnapTargets(_ targets: [WindowSnapTarget]) {
        snapTargets = targets
        updateHoveredSnapTarget(at: convert(window?.mouseLocationOutsideOfEventStream ?? .zero, from: nil))
    }

    func showPresetSelection(_ rect: CGRect) {
        guard rect.width >= 4, rect.height >= 4 else { return }
        showsPresetFixture = true
        selectionRect = rect.integral
        dragStart = nil
        invalidateCursorRects()
        needsDisplay = true
        if showsActionToolbar {
            showActionToolbar(for: rect.integral)
        }
    }

    override func mouseDown(with event: NSEvent) {
        guard !isTransitioning else { return }
        if showsActionToolbar,
           event.clickCount == 2,
           let selectionRect,
           selectionRect.contains(convert(event.locationInWindow, from: nil)) {
            onFinish?(selectionRect, .copyImage)
            return
        }

        let point = convert(event.locationInWindow, from: nil)
        removeActionToolbar()
        dragStart = point
        if showsActionToolbar, let selectionRect,
           let handle = resizeHandle(at: point, for: selectionRect) {
            dragMode = .resizing(handle: handle, original: selectionRect)
            pendingSnapRect = nil
            handle.cursor.set()
        } else if showsActionToolbar, let selectionRect, selectionRect.contains(point) {
            dragMode = .moving(original: selectionRect)
            pendingSnapRect = nil
            NSCursor.closedHand.set()
        } else {
            dragMode = .creating
            // Hover tracking pauses while a selection exists, so resolve the
            // window under the pointer now instead of reusing a stale target
            // (otherwise clicking window B re-selected window A).
            hoveredSnapTarget = WindowSnapTargetSelector.target(at: point, from: snapTargets)
            pendingSnapRect = hoveredSnapTarget?.frame
            selectionRect = pendingSnapRect
            NSCursor.crosshair.set()
        }
        didDrag = false
        needsDisplay = true
    }

    override func mouseDragged(with event: NSEvent) {
        guard let dragStart else { return }
        let current = convert(event.locationInWindow, from: nil)
        guard didDrag || hypot(current.x - dragStart.x, current.y - dragStart.y) >= 3 else { return }
        didDrag = true
        switch dragMode {
        case .creating:
            pendingSnapRect = nil
            hoveredSnapTarget = nil
            selectionRect = normalizedRect(from: dragStart, to: current).intersection(bounds)
        case .moving(let original):
            NSCursor.closedHand.set()
            selectionRect = SelectionRectEditor.moved(
                original,
                by: CGPoint(x: current.x - dragStart.x, y: current.y - dragStart.y),
                inside: bounds
            )
        case .resizing(let handle, let original):
            handle.cursor.set()
            selectionRect = SelectionRectEditor.resized(
                original,
                handle: handle,
                to: current,
                inside: bounds
            )
        }
        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        guard let dragStart else {
            return
        }
        let current = convert(event.locationInWindow, from: nil)
        let rect: CGRect
        switch dragMode {
        case .creating:
            rect = pendingSnapRect ?? normalizedRect(from: dragStart, to: current).intersection(bounds)
        case .moving(let original):
            rect = didDrag ? (selectionRect ?? original) : original
        case .resizing(_, let original):
            rect = didDrag ? (selectionRect ?? original) : original
        }
        if rect.width >= 4, rect.height >= 4 {
            selectionRect = rect.integral
            self.dragStart = nil
            pendingSnapRect = nil
            dragMode = .creating
            didDrag = false
            invalidateCursorRects()
            needsDisplay = true
            if !showsActionToolbar,
               optionRevealsActionToolbar,
               event.modifierFlags.contains(.option) {
                showsActionToolbar = true
            }
            if showsActionToolbar {
                showActionToolbar(for: rect.integral)
            } else {
                onFinish?(rect.integral, nil)
            }
        } else {
            self.dragStart = nil
            pendingSnapRect = nil
            dragMode = .creating
            selectionRect = nil
            didDrag = false
            invalidateCursorRects()
            needsDisplay = true
        }
    }

    override func mouseMoved(with event: NSEvent) {
        guard dragStart == nil, !isTransitioning else { return }
        let point = convert(event.locationInWindow, from: nil)
        cursor(at: point).set()
        guard selectionRect == nil, actionToolbar == nil else { return }
        updateHoveredSnapTarget(at: point)
    }

    override func mouseExited(with event: NSEvent) {
        guard dragStart == nil, actionToolbar == nil else { return }
        hoveredSnapTarget = nil
        needsDisplay = true
    }

    override func rightMouseDown(with event: NSEvent) {
        cancelSelection()
    }

    func cancelSelection() {
        guard !isTransitioning else { return }
        removeActionToolbar()
        dragStart = nil
        dragMode = .creating
        pendingSnapRect = nil
        hoveredSnapTarget = nil
        selectionRect = nil
        invalidateCursorRects()
        needsDisplay = true
        onFinish?(nil, nil)
    }

    func prepareForDeferredDismissal() {
        isTransitioning = true
        removeActionToolbar()
        needsDisplay = true
    }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 {
            cancelSelection()
        } else if showsActionToolbar,
                  (event.keyCode == 36 || event.keyCode == 76),
                  let selectionRect {
            onFinish?(selectionRect, .copyImage)
        } else {
            super.keyDown(with: event)
        }
    }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)
        guard let context = NSGraphicsContext.current?.cgContext else { return }

        drawFrozenDisplayImage()

        context.setFillColor(NSColor.black.withAlphaComponent(0.42).cgColor)
        context.fill(bounds)

        guard let displayRect = selectionRect ?? hoveredSnapTarget?.frame else {
            drawHint(
                optionRevealsActionToolbar && !showsActionToolbar
                    ? "拖动选择区域 · 按住 ⌥ 松开可选择其他操作 · 右键 / Esc 取消"
                    : "拖动选择区域 · 右键 / Esc 取消",
                at: CGPoint(x: bounds.midX, y: bounds.midY)
            )
            return
        }

        if showsPresetFixture {
            context.setFillColor(NSColor.white.cgColor)
            context.fill(displayRect)
            drawPresetFixture(in: displayRect)
        } else if frozenDisplayImage != nil {
            context.saveGState()
            context.clip(to: displayRect)
            drawFrozenDisplayImage()
            context.restoreGState()
        } else {
            context.saveGState()
            context.setBlendMode(.clear)
            context.fill(displayRect)
            context.restoreGState()
        }

        context.setStrokeColor(NSColor.controlAccentColor.cgColor)
        context.setLineWidth(selectionRect == nil ? 1.5 : 2)
        context.stroke(displayRect.insetBy(dx: 1, dy: 1))

        if selectionRect != nil, showsActionToolbar {
            drawResizeHandles(around: displayRect, in: context)
        }

        let dimensions = "\(Int(displayRect.width)) × \(Int(displayRect.height))"
        drawHint(dimensions, at: CGPoint(x: displayRect.midX, y: max(26, displayRect.minY - 18)))
    }

    private func drawFrozenDisplayImage() {
        guard let frozenDisplayImage else { return }
        NSGraphicsContext.current?.imageInterpolation = .none
        frozenDisplayImage.draw(
            in: bounds,
            from: CGRect(origin: .zero, size: frozenDisplayImage.size),
            operation: .copy,
            fraction: 1,
            respectFlipped: true,
            hints: nil
        )
    }

    private func updateHoveredSnapTarget(at point: CGPoint) {
        let candidate = WindowSnapTargetSelector.target(at: point, from: snapTargets)
        guard candidate != hoveredSnapTarget else { return }
        hoveredSnapTarget = candidate
        needsDisplay = true
    }

    private func showActionToolbar(for selectionRect: CGRect) {
        removeActionToolbar()

        let stack = NSStackView()
        stack.orientation = .horizontal
        stack.alignment = .centerY
        stack.spacing = 3
        stack.edgeInsets = NSEdgeInsets(top: 6, left: 7, bottom: 6, right: 7)

        for (index, item) in CaptureActionToolbarLayout.actions.enumerated() {
            if index == 4 || index == 6 {
                let separator = NSBox()
                separator.boxType = .separator
                separator.widthAnchor.constraint(equalToConstant: 1).isActive = true
                separator.heightAnchor.constraint(equalToConstant: 22).isActive = true
                stack.addArrangedSubview(separator)
            }
            let image = NSImage(systemSymbolName: item.symbol, accessibilityDescription: item.title) ?? NSImage()
            let configured = image.withSymbolConfiguration(.init(pointSize: 13, weight: .medium)) ?? image
            let button = CaptureActionButton(
                image: configured,
                target: self,
                action: #selector(handleAction(_:))
            )
            button.tag = CaptureQuickAction.allCases.firstIndex(of: item.action) ?? index
            button.bezelStyle = .recessed
            button.controlSize = .regular
            button.imageScaling = .scaleProportionallyDown
            button.setAccessibilityLabel(item.title)
            button.identifier = NSUserInterfaceItemIdentifier("capture-action-\(item.action.rawValue)")
            button.onHoverChange = { [weak self, weak button] isHovering in
                guard let self else { return }
                if isHovering, let button {
                    self.showActionTooltip(item.title, for: button)
                } else {
                    self.hideActionTooltip()
                }
            }
            button.widthAnchor.constraint(equalToConstant: 36).isActive = true
            button.heightAnchor.constraint(equalToConstant: 32).isActive = true
            stack.addArrangedSubview(button)
        }

        let toolbar = NSVisualEffectView()
        toolbar.material = .hudWindow
        toolbar.blendingMode = .withinWindow
        toolbar.state = .active
        toolbar.wantsLayer = true
        toolbar.layer?.cornerRadius = 12
        toolbar.layer?.masksToBounds = true
        toolbar.identifier = NSUserInterfaceItemIdentifier("capture-action-toolbar")
        toolbar.addSubview(stack)

        let fittingSize = stack.fittingSize
        let toolbarSize = CGSize(width: fittingSize.width, height: max(44, fittingSize.height))
        let proposedBelow = selectionRect.minY - toolbarSize.height - 10
        let y = proposedBelow >= 8
            ? proposedBelow
            : min(bounds.height - toolbarSize.height - 8, selectionRect.maxY + 10)
        let x = min(
            max(8, selectionRect.maxX - toolbarSize.width),
            bounds.width - toolbarSize.width - 8
        )
        toolbar.frame = CGRect(origin: CGPoint(x: x, y: y), size: toolbarSize)
        stack.frame = toolbar.bounds
        addSubview(toolbar)
        actionToolbar = toolbar
        invalidateCursorRects()
    }

    @objc
    private func handleAction(_ sender: NSButton) {
        guard let selectionRect,
              CaptureQuickAction.allCases.indices.contains(sender.tag) else {
            return
        }
        onFinish?(selectionRect, CaptureQuickAction.allCases[sender.tag])
    }

    private func removeActionToolbar() {
        hideActionTooltip()
        actionToolbar?.removeFromSuperview()
        actionToolbar = nil
        invalidateCursorRects()
    }

    private func normalizedRect(from start: CGPoint, to end: CGPoint) -> CGRect {
        CGRect(
            x: min(start.x, end.x),
            y: min(start.y, end.y),
            width: abs(end.x - start.x),
            height: abs(end.y - start.y)
        )
    }

    private func resizeHandle(at point: CGPoint, for rect: CGRect) -> SelectionResizeHandle? {
        SelectionRectEditor.resizeHandle(at: point, for: rect)
    }

    func cursor(at point: CGPoint) -> NSCursor {
        if let actionToolbar, actionToolbar.frame.contains(point) {
            return .pointingHand
        }
        guard showsActionToolbar, let selectionRect else { return .crosshair }
        if let handle = resizeHandle(at: point, for: selectionRect) {
            return handle.cursor
        }
        return selectionRect.contains(point) ? .openHand : .crosshair
    }

    private func invalidateCursorRects() {
        window?.invalidateCursorRects(for: self)
    }

    private func addValidCursorRect(_ rect: CGRect, cursor: NSCursor) {
        let clippedRect = rect.intersection(bounds)
        guard !clippedRect.isNull,
              !clippedRect.isInfinite,
              clippedRect.width > 0,
              clippedRect.height > 0 else {
            return
        }
        addCursorRect(clippedRect, cursor: cursor)
    }

    private func showActionTooltip(_ title: String, for button: NSButton) {
        hideActionTooltip()

        let label = NSTextField(labelWithString: title)
        label.font = .systemFont(ofSize: 12, weight: .medium)
        label.textColor = .white
        label.alignment = .center
        label.sizeToFit()

        let horizontalPadding: CGFloat = 10
        let verticalPadding: CGFloat = 6
        let bubbleSize = CGSize(
            width: ceil(label.frame.width) + horizontalPadding * 2,
            height: ceil(label.frame.height) + verticalPadding * 2
        )
        let buttonFrame = button.convert(button.bounds, to: self)
        let x = min(
            max(6, buttonFrame.midX - bubbleSize.width / 2),
            bounds.maxX - bubbleSize.width - 6
        )
        let proposedAbove = buttonFrame.maxY + 7
        let y = proposedAbove + bubbleSize.height <= bounds.maxY - 6
            ? proposedAbove
            : max(6, buttonFrame.minY - bubbleSize.height - 7)

        let bubble = NSView(frame: CGRect(origin: CGPoint(x: x, y: y), size: bubbleSize))
        bubble.identifier = NSUserInterfaceItemIdentifier("capture-action-tooltip")
        bubble.wantsLayer = true
        bubble.layer?.backgroundColor = NSColor.black.withAlphaComponent(0.90).cgColor
        bubble.layer?.cornerRadius = 7
        bubble.layer?.shadowColor = NSColor.black.cgColor
        bubble.layer?.shadowOpacity = 0.35
        bubble.layer?.shadowRadius = 6
        bubble.layer?.shadowOffset = CGSize(width: 0, height: -2)
        bubble.layer?.zPosition = 10_000

        label.frame = CGRect(
            x: horizontalPadding,
            y: verticalPadding,
            width: bubbleSize.width - horizontalPadding * 2,
            height: bubbleSize.height - verticalPadding * 2
        )
        bubble.addSubview(label)
        addSubview(bubble, positioned: .above, relativeTo: nil)
        actionTooltip = bubble
    }

    private func hideActionTooltip() {
        actionTooltip?.removeFromSuperview()
        actionTooltip = nil
    }

    private func drawResizeHandles(around rect: CGRect, in context: CGContext) {
        context.saveGState()
        defer { context.restoreGState() }
        context.setLineWidth(1.5)
        context.setFillColor(NSColor.white.cgColor)
        context.setStrokeColor(NSColor.controlAccentColor.cgColor)
        for handle in SelectionResizeHandle.allCases {
            let point = handle.point(in: rect)
            let handleRect = CGRect(x: point.x - 4, y: point.y - 4, width: 8, height: 8)
            context.fillEllipse(in: handleRect)
            context.strokeEllipse(in: handleRect)
        }
    }

    private func drawHint(_ text: String, at point: CGPoint) {
        let attributes: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 13, weight: .medium),
            .foregroundColor: NSColor.white,
            .backgroundColor: NSColor.black.withAlphaComponent(0.7)
        ]
        let size = text.size(withAttributes: attributes)
        let origin = CGPoint(x: point.x - size.width / 2, y: point.y - size.height / 2)
        text.draw(at: origin, withAttributes: attributes)
    }

    private func drawPresetFixture(in rect: CGRect) {
        let title = "Ta · 一次截图，多种下一步"
        title.draw(at: CGPoint(x: rect.minX + 42, y: rect.maxY - 82), withAttributes: [
            .font: NSFont.systemFont(ofSize: 30, weight: .bold),
            .foregroundColor: NSColor.labelColor
        ])
        "取字 · AI 识图 · 翻译 · 钉图 · 标注 · 美化".draw(
            at: CGPoint(x: rect.minX + 42, y: rect.maxY - 130),
            withAttributes: [
                .font: NSFont.systemFont(ofSize: 18, weight: .medium),
                .foregroundColor: NSColor.secondaryLabelColor
            ]
        )
        NSColor(calibratedRed: 0.91, green: 0.25, blue: 0.18, alpha: 0.10).setFill()
        NSBezierPath(
            roundedRect: CGRect(
                x: rect.minX + 42,
                y: rect.minY + 48,
                width: rect.width - 84,
                height: max(58, rect.height - 220)
            ),
            xRadius: 18,
            yRadius: 18
        ).fill()
    }
}
