import AppKit
import CoreImage

enum PinnedImageLayout {
    static func initialSize(
        imagePixels: CGSize,
        preferredLogicalSize: CGSize?,
        visibleFrame: CGRect
    ) -> CGSize {
        let preferred = preferredLogicalSize.flatMap { size -> CGSize? in
            guard size.width >= 1, size.height >= 1 else { return nil }
            return size
        }
        let base = preferred ?? CGSize(
            width: max(1, imagePixels.width),
            height: max(1, imagePixels.height)
        )
        let screenLimit = CGSize(
            width: max(1, visibleFrame.width - 24),
            height: max(1, visibleFrame.height - 24)
        )
        let limit = preferred == nil
            ? CGSize(width: min(640, screenLimit.width), height: min(480, screenLimit.height))
            : screenLimit
        let scale = min(1, limit.width / base.width, limit.height / base.height)
        return CGSize(width: base.width * scale, height: base.height * scale)
    }
}

/// Window sizing for pins. Every operation applies one uniform scale factor,
/// so a pin can never be stretched out of its aspect ratio.
enum PinnedImageZoom {
    static let minimumSize = CGSize(width: 120, height: 60)
    static let maximumSize = CGSize(width: 1200, height: 900)

    /// Size after zooming `current` by `factor`. Zooming in stops once either
    /// side would exceed `maximumSize`; zooming out stops once either side
    /// would drop below `minimumSize`. A pin that already lies outside those
    /// limits is never forced back, it just cannot move further away.
    static func scaledSize(current: CGSize, factor: CGFloat) -> CGSize {
        guard current.width > 0, current.height > 0, factor.isFinite, factor > 0 else {
            return current
        }
        let largestScale = min(maximumSize.width / current.width, maximumSize.height / current.height)
        let smallestScale = max(minimumSize.width / current.width, minimumSize.height / current.height)
        let upperBound = max(1, largestScale)
        let lowerBound = min(1, smallestScale)
        let scale = min(upperBound, max(lowerBound, factor))
        return CGSize(width: current.width * scale, height: current.height * scale)
    }

    /// Largest size with the aspect ratio of `content` that fits in `box`.
    static func aspectFit(_ content: CGSize, into box: CGSize) -> CGSize {
        guard content.width > 0, content.height > 0, box.width > 0, box.height > 0 else {
            return box
        }
        let scale = min(box.width / content.width, box.height / content.height)
        return CGSize(width: content.width * scale, height: content.height * scale)
    }

    /// Keeps `width` when possible and derives the height from the content's
    /// aspect ratio, shrinking uniformly if that would exceed `maximumSize`.
    static func size(forContent content: CGSize, preferredWidth: CGFloat) -> CGSize {
        guard content.width > 0, content.height > 0, preferredWidth > 0 else {
            return CGSize(width: max(1, preferredWidth), height: max(1, preferredWidth))
        }
        let proposed = CGSize(
            width: preferredWidth,
            height: preferredWidth * content.height / content.width
        )
        let scale = min(1, maximumSize.width / proposed.width, maximumSize.height / proposed.height)
        return CGSize(width: proposed.width * scale, height: proposed.height * scale)
    }
}

/// Rotation / mirroring of a pin, shared by on-screen drawing, "复制图片" and
/// crop mapping so that all three agree.
enum PinnedImageTransform {
    static func normalizedQuarterTurns(_ turns: Int) -> Int {
        ((turns % 4) + 4) % 4
    }

    static func isSideways(_ turns: Int) -> Bool {
        !normalizedQuarterTurns(turns).isMultiple(of: 2)
    }

    /// Pixel size of `size` once rotated by `turns` quarter turns.
    static func displayedSize(for size: CGSize, quarterTurns: Int) -> CGSize {
        isSideways(quarterTurns) ? CGSize(width: size.height, height: size.width) : size
    }

    /// Renders `image` exactly as the pin shows it (same transform order as
    /// `PinnedImageView.draw`): mirror first, then rotate counter-clockwise by
    /// `quarterTurns` × 90° in a y-up coordinate space.
    static func apply(
        to image: CGImage,
        quarterTurns: Int,
        mirroredHorizontally: Bool,
        mirroredVertically: Bool
    ) -> CGImage? {
        let turns = normalizedQuarterTurns(quarterTurns)
        guard turns != 0 || mirroredHorizontally || mirroredVertically else { return image }
        let source = CGSize(width: image.width, height: image.height)
        let output = displayedSize(for: source, quarterTurns: turns)
        guard let context = CGContext(
            data: nil,
            width: Int(output.width),
            height: Int(output.height),
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: CGColorSpace(name: CGColorSpace.sRGB) ?? CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else {
            return nil
        }
        context.interpolationQuality = .high
        context.translateBy(x: output.width / 2, y: output.height / 2)
        context.rotate(by: CGFloat(turns) * .pi / 2)
        context.scaleBy(x: mirroredHorizontally ? -1 : 1, y: mirroredVertically ? -1 : 1)
        context.draw(
            image,
            in: CGRect(
                x: -source.width / 2,
                y: -source.height / 2,
                width: source.width,
                height: source.height
            )
        )
        return context.makeImage()
    }

    /// Maps a rectangle in the pin view (AppKit coordinates, origin bottom-
    /// left) to the matching region of the *untransformed* image, expressed
    /// in unit coordinates with the origin at the image's top-left corner
    /// (the convention of `CGImage.cropping(to:)`).
    static func imageUnitRect(
        forViewRect rect: CGRect,
        viewSize: CGSize,
        quarterTurns: Int,
        mirroredHorizontally: Bool,
        mirroredVertically: Bool
    ) -> CGRect {
        guard viewSize.width > 0, viewSize.height > 0 else { return .zero }
        let turns = normalizedQuarterTurns(quarterTurns)
        let cosine: CGFloat = [1, 0, -1, 0][turns]
        let sine: CGFloat = [0, 1, 0, -1][turns]
        // The image is drawn into this size before rotation.
        let drawSize = displayedSize(for: viewSize, quarterTurns: turns)

        func unitPoint(_ point: CGPoint) -> CGPoint {
            let dx = point.x - viewSize.width / 2
            let dy = point.y - viewSize.height / 2
            // Undo the rotation, then the mirroring.
            var localX = dx * cosine + dy * sine
            var localY = -dx * sine + dy * cosine
            if mirroredHorizontally { localX = -localX }
            if mirroredVertically { localY = -localY }
            let unitX = localX / drawSize.width + 0.5
            let unitYFromBottom = localY / drawSize.height + 0.5
            return CGPoint(x: unitX, y: 1 - unitYFromBottom)
        }

        let first = unitPoint(CGPoint(x: rect.minX, y: rect.minY))
        let second = unitPoint(CGPoint(x: rect.maxX, y: rect.maxY))
        let mapped = CGRect(
            x: min(first.x, second.x),
            y: min(first.y, second.y),
            width: abs(first.x - second.x),
            height: abs(first.y - second.y)
        )
        return mapped.intersection(CGRect(x: 0, y: 0, width: 1, height: 1))
    }
}

@MainActor
final class PinnedImageWindowController {
    private struct PinRecord {
        let id: UUID
        let panel: NSPanel
        let sourceImage: CGImage
        let pixelScale: CGFloat
        var groupID: UUID?
    }

    private struct ClosedPin {
        let image: CGImage
        let frame: CGRect
        let pixelScale: CGFloat
    }

    private var records: [PinRecord] = []
    private var closedPins: [ClosedPin] = []

    func pin(_ image: CGImage, near selection: CaptureSelection) {
        _ = createPin(
            image,
            near: selection,
            preferredFrame: nil,
            preferredLogicalSize: selection.globalRect.size,
            pixelScale: selection.backingScaleFactor
        )
    }

    func pinFromPasteboard() -> Bool {
        let pasteboard = NSPasteboard.general
        if let data = pasteboard.data(forType: .png) ?? pasteboard.data(forType: .tiff),
           let image = NSImage(data: data),
           let cgImage = image.cgImage(forProposedRect: nil, context: nil, hints: nil) {
            pinPasteboardImage(cgImage)
            return true
        }
        if let urls = pasteboard.readObjects(forClasses: [NSURL.self], options: nil) as? [URL],
           let url = urls.first {
            if let image = NSImage(contentsOf: url),
               let cgImage = image.cgImage(forProposedRect: nil, context: nil, hints: nil) {
                pinPasteboardImage(cgImage)
            } else if let card = makeTextCard(title: url.lastPathComponent, detail: url.path, icon: NSWorkspace.shared.icon(forFile: url.path)) {
                pinPasteboardImage(card)
            } else { return false }
            return true
        }
        if let html = pasteboard.string(forType: .html),
           let data = html.data(using: .utf8),
           let attributed = try? NSAttributedString(
               data: data,
               options: [.documentType: NSAttributedString.DocumentType.html, .characterEncoding: String.Encoding.utf8.rawValue],
               documentAttributes: nil
           ), let image = renderText(attributed) {
            pinPasteboardImage(image)
            return true
        }
        if let text = pasteboard.string(forType: .string), !text.isEmpty {
            let attributed = NSAttributedString(string: text, attributes: [
                .font: NSFont.systemFont(ofSize: 18),
                .foregroundColor: NSColor.labelColor
            ])
            guard let image = renderText(attributed) else { return false }
            pinPasteboardImage(image)
            return true
        }
        return false
    }

    func hideAll() {
        records.forEach { $0.panel.orderOut(nil) }
    }

    func showAll() {
        records.forEach { $0.panel.orderFrontRegardless() }
    }

    func enableInteractionForAll() {
        records.forEach { $0.panel.ignoresMouseEvents = false }
    }

    @discardableResult
    func restoreLastClosed() -> Bool {
        guard let closed = closedPins.popLast() else { return false }
        _ = createPin(
            closed.image,
            near: selectionAtMouse(),
            preferredFrame: closed.frame,
            preferredLogicalSize: nil,
            pixelScale: closed.pixelScale
        )
        return true
    }

    private func pinPasteboardImage(_ image: CGImage) {
        _ = createPin(
            image,
            near: selectionAtMouse(),
            preferredFrame: nil,
            preferredLogicalSize: nil,
            pixelScale: 1
        )
    }

    @discardableResult
    private func createPin(
        _ image: CGImage,
        near selection: CaptureSelection,
        preferredFrame: CGRect?,
        preferredLogicalSize: CGSize?,
        pixelScale: CGFloat
    ) -> NSPanel {
        let imageSize = CGSize(width: image.width, height: image.height)
        let visibleFrame = NSScreen.screens
            .first(where: { $0.frame.intersects(selection.globalRect) })?
            .visibleFrame ?? NSScreen.main?.visibleFrame ?? selection.screenFrame
        let initialSize = PinnedImageLayout.initialSize(
            imagePixels: imageSize,
            preferredLogicalSize: preferredLogicalSize,
            visibleFrame: visibleFrame
        )
        let width = initialSize.width
        let height = initialSize.height
        let origin = CGPoint(
            x: min(max(visibleFrame.minX + 12, selection.globalRect.minX), visibleFrame.maxX - width - 12),
            y: min(max(visibleFrame.minY + 12, selection.globalRect.minY), visibleFrame.maxY - height - 12)
        )

        let panel = PinnedImagePanel(
            contentRect: preferredFrame ?? CGRect(origin: origin, size: CGSize(width: width, height: height)),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        panel.level = .floating
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.isMovableByWindowBackground = false

        let contentView = PinnedImageView(image: image)
        contentView.onCopy = { copiedImage in
            guard let payload = ClipboardImageEncoder.payload(for: copiedImage, scale: pixelScale) else {
                return
            }
            let pasteboard = NSPasteboard.general
            pasteboard.clearContents()
            pasteboard.setData(payload.png, forType: .png)
            if let tiff = payload.tiff {
                pasteboard.setData(tiff, forType: .tiff)
            }
        }
        let id = UUID()
        contentView.onClose = { [weak self, weak panel] in
            guard let self, let panel,
                  let record = records.first(where: { $0.id == id }) else { return }
            closedPins.append(
                ClosedPin(image: record.sourceImage, frame: panel.frame, pixelScale: record.pixelScale)
            )
            if closedPins.count > 3 { closedPins.removeFirst() }
            panel.orderOut(nil)
            records.removeAll { $0.id == id }
        }
        contentView.onEnableClickThrough = { [weak panel] in panel?.ignoresMouseEvents = true }
        contentView.onGroupVisiblePins = { [weak self] in self?.groupVisiblePins(containing: id) }
        contentView.onHideGroup = { [weak self] in self?.hideGroup(containing: id) }
        panel.contentView = contentView
        panel.orderFrontRegardless()
        records.append(
            PinRecord(id: id, panel: panel, sourceImage: image, pixelScale: pixelScale, groupID: nil)
        )
        return panel
    }

    private func groupVisiblePins(containing id: UUID) {
        let groupID = UUID()
        for index in records.indices where records[index].panel.isVisible {
            records[index].groupID = groupID
        }
    }

    private func hideGroup(containing id: UUID) {
        guard let groupID = records.first(where: { $0.id == id })?.groupID else { return }
        records.filter { $0.groupID == groupID }.forEach { $0.panel.orderOut(nil) }
    }

    private func selectionAtMouse() -> CaptureSelection {
        let point = NSEvent.mouseLocation
        let screen = NSScreen.screens.first { NSMouseInRect(point, $0.frame, false) }
            ?? NSScreen.main ?? NSScreen.screens[0]
        let number = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber
        return CaptureSelection(
            globalRect: CGRect(origin: point, size: CGSize(width: 1, height: 1)),
            screenFrame: screen.frame,
            displayID: CGDirectDisplayID(number?.uint32Value ?? 0),
            backingScaleFactor: screen.backingScaleFactor
        )
    }

    private func renderText(_ attributed: NSAttributedString) -> CGImage? {
        let width: CGFloat = 520
        let textRect = attributed.boundingRect(
            with: CGSize(width: width - 48, height: 1200),
            options: [.usesLineFragmentOrigin, .usesFontLeading]
        )
        let height = min(1200, max(120, textRect.height + 48))
        guard let rep = NSBitmapImageRep(
            bitmapDataPlanes: nil, pixelsWide: Int(width), pixelsHigh: Int(height),
            bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
            colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0
        ) else { return nil }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        NSColor.textBackgroundColor.setFill()
        CGRect(x: 0, y: 0, width: width, height: height).fill()
        attributed.draw(with: CGRect(x: 24, y: 24, width: width - 48, height: height - 48), options: [.usesLineFragmentOrigin])
        NSGraphicsContext.restoreGraphicsState()
        return rep.cgImage
    }

    private func makeTextCard(title: String, detail: String, icon: NSImage) -> CGImage? {
        let text = NSMutableAttributedString()
        text.append(NSAttributedString(string: "\(title)\n", attributes: [.font: NSFont.systemFont(ofSize: 20, weight: .semibold)]))
        text.append(NSAttributedString(string: detail, attributes: [.font: NSFont.systemFont(ofSize: 13), .foregroundColor: NSColor.secondaryLabelColor]))
        return renderText(text)
    }
}

final class PinnedImagePanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }

    override func constrainFrameRect(_ frameRect: NSRect, to screen: NSScreen?) -> NSRect {
        // NSWindow normally clamps borderless windows below the menu bar. A pin
        // behaves like a free canvas object, so preserve the user's exact drag.
        frameRect
    }
}

enum PinnedImageInteraction {
    static func shouldClose(clickCount: Int) -> Bool {
        clickCount >= 2
    }
}

struct PinnedImageDecorationState: Equatable {
    var showsBorder = true
    var showsShadow = true

    mutating func toggleBorder() {
        showsBorder.toggle()
    }

    mutating func toggleShadow() {
        showsShadow.toggle()
    }
}

enum PinnedImageDecoration {
    static let borderTitle = "显示边框"
    static let shadowTitle = "窗口阴影"
    static let visibleBorderWidth: CGFloat = 1
}

private final class PinnedImageView: NSView {
    var onCopy: ((CGImage) -> Void)?
    var onClose: (() -> Void)?
    var onEnableClickThrough: (() -> Void)?
    var onGroupVisiblePins: (() -> Void)?
    var onHideGroup: (() -> Void)?

    private let image: NSImage
    private let sourceCGImage: CGImage
    private var dragStartOnScreen: CGPoint?
    private var windowStartOrigin: CGPoint?
    private var rotationQuarterTurns = 0
    private var isMirroredHorizontally = false
    private var isMirroredVertically = false
    private var filteredMode: FilteredMode = .none
    private var cropRect: CGRect?
    private var isCropping = false
    private var cropDragStart: CGPoint?
    private var cropDragCurrent: CGPoint?
    private var thumbnailPreviousFrame: CGRect?
    private var isClosing = false
    private var decoration = PinnedImageDecorationState()

    private enum FilteredMode {
        case none
        case grayscale
        case inverted
    }

    init(image: CGImage) {
        self.sourceCGImage = image
        self.image = NSImage(cgImage: image, size: .zero)
        super.init(frame: .zero)
        wantsLayer = true
        layer?.cornerRadius = 10
        layer?.masksToBounds = true
        layer?.borderColor = NSColor.white.withAlphaComponent(0.28).cgColor
        layer?.borderWidth = PinnedImageDecoration.visibleBorderWidth

        let menu = NSMenu()
        menu.addItem(withTitle: "复制图片", action: #selector(copyImage), keyEquivalent: "c")
        menu.addItem(withTitle: "裁剪…", action: #selector(beginCrop), keyEquivalent: "")
        menu.addItem(withTitle: "重置裁剪", action: #selector(resetCrop), keyEquivalent: "")
        menu.addItem(.separator())
        menu.addItem(withTitle: "向左旋转", action: #selector(rotateLeft), keyEquivalent: "[")
        menu.addItem(withTitle: "向右旋转", action: #selector(rotateRight), keyEquivalent: "]")
        menu.addItem(withTitle: "水平翻转", action: #selector(mirrorHorizontally), keyEquivalent: "")
        menu.addItem(withTitle: "垂直翻转", action: #selector(mirrorVertically), keyEquivalent: "")
        menu.addItem(.separator())
        menu.addItem(withTitle: "灰度显示", action: #selector(toggleGrayscale(_:)), keyEquivalent: "")
        menu.addItem(withTitle: "反色显示", action: #selector(toggleInversion(_:)), keyEquivalent: "")
        menu.addItem(withTitle: PinnedImageDecoration.borderTitle, action: #selector(toggleBorder(_:)), keyEquivalent: "")
        menu.items.last?.state = .on
        menu.addItem(withTitle: PinnedImageDecoration.shadowTitle, action: #selector(toggleShadow(_:)), keyEquivalent: "")
        menu.items.last?.state = .on
        menu.addItem(withTitle: "保持最前", action: #selector(toggleTopmost(_:)), keyEquivalent: "")
        menu.items.last?.state = .on
        menu.addItem(withTitle: "恢复显示", action: #selector(resetAppearance), keyEquivalent: "0")
        menu.addItem(.separator())
        menu.addItem(withTitle: "缩略图模式", action: #selector(toggleThumbnail(_:)), keyEquivalent: "")
        menu.addItem(withTitle: "鼠标穿透", action: #selector(enableClickThrough), keyEquivalent: "")
        menu.addItem(withTitle: "将可见钉图编为一组", action: #selector(groupVisiblePins), keyEquivalent: "")
        menu.addItem(withTitle: "隐藏本组", action: #selector(hideGroup), keyEquivalent: "")
        menu.addItem(.separator())
        menu.addItem(withTitle: "关闭钉图", action: #selector(closePin), keyEquivalent: "w")
        menu.items.forEach { $0.target = self }
        self.menu = menu
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)
        NSColor.black.setFill()
        bounds.fill()
        guard let context = NSGraphicsContext.current else { return }
        context.saveGraphicsState()
        let cgContext = context.cgContext
        cgContext.translateBy(x: bounds.midX, y: bounds.midY)
        cgContext.rotate(by: CGFloat(rotationQuarterTurns) * .pi / 2)
        cgContext.scaleBy(
            x: isMirroredHorizontally ? -1 : 1,
            y: isMirroredVertically ? -1 : 1
        )
        let isSideways = rotationQuarterTurns.isMultiple(of: 2) == false
        let drawSize = CGSize(
            width: isSideways ? bounds.height : bounds.width,
            height: isSideways ? bounds.width : bounds.height
        )
        displayedImage.draw(
            in: CGRect(x: -drawSize.width / 2, y: -drawSize.height / 2,
                       width: drawSize.width, height: drawSize.height),
            from: .zero,
            operation: .sourceOver,
            fraction: 1,
            respectFlipped: true,
            hints: [.interpolation: NSImageInterpolation.high]
        )
        context.restoreGraphicsState()
        if isCropping, let start = cropDragStart, let current = cropDragCurrent {
            let rect = normalizedRect(start, current).intersection(bounds)
            // Dim everything except the selection; the picture underneath is
            // already drawn with the current rotation / mirroring.
            NSColor.black.withAlphaComponent(0.45).setFill()
            let dimming = NSBezierPath(rect: bounds)
            if !rect.isNull, rect.width > 0, rect.height > 0 {
                dimming.append(NSBezierPath(rect: rect))
                dimming.windingRule = .evenOdd
            }
            dimming.fill()
            NSColor.controlAccentColor.setStroke()
            let border = NSBezierPath(rect: rect)
            let pattern: [CGFloat] = [6, 4]
            border.setLineDash(pattern, count: pattern.count, phase: 0)
            border.lineWidth = 2
            border.stroke()
        }
    }

    override func mouseDown(with event: NSEvent) {
        if closeForDoubleClickIfNeeded(event) { return }
        if isCropping {
            let point = convert(event.locationInWindow, from: nil)
            cropDragStart = point
            cropDragCurrent = point
            needsDisplay = true
            return
        }
        dragStartOnScreen = NSEvent.mouseLocation
        windowStartOrigin = window?.frame.origin
    }

    override func mouseDragged(with event: NSEvent) {
        if isCropping, cropDragStart != nil {
            cropDragCurrent = convert(event.locationInWindow, from: nil)
            needsDisplay = true
            return
        }
        guard let dragStartOnScreen, let windowStartOrigin else { return }
        let current = NSEvent.mouseLocation
        window?.setFrameOrigin(CGPoint(
            x: windowStartOrigin.x + current.x - dragStartOnScreen.x,
            y: windowStartOrigin.y + current.y - dragStartOnScreen.y
        ))
    }

    override func mouseUp(with event: NSEvent) {
        if closeForDoubleClickIfNeeded(event) { return }
        guard isCropping, let start = cropDragStart else { return }
        let end = convert(event.locationInWindow, from: nil)
        let selected = normalizedRect(start, end).intersection(bounds)
        defer {
            isCropping = false
            cropDragStart = nil
            cropDragCurrent = nil
            needsDisplay = true
        }
        guard selected.width >= 12, selected.height >= 12 else { return }
        let current = cropRect ?? CGRect(x: 0, y: 0, width: sourceCGImage.width, height: sourceCGImage.height)
        // The selection is made on the rotated / mirrored picture; map it back
        // to the untransformed image before cropping.
        let unit = PinnedImageTransform.imageUnitRect(
            forViewRect: selected,
            viewSize: bounds.size,
            quarterTurns: rotationQuarterTurns,
            mirroredHorizontally: isMirroredHorizontally,
            mirroredVertically: isMirroredVertically
        )
        let newCrop = CGRect(
            x: current.minX + unit.minX * current.width,
            y: current.minY + unit.minY * current.height,
            width: unit.width * current.width,
            height: unit.height * current.height
        ).integral.intersection(CGRect(x: 0, y: 0, width: sourceCGImage.width, height: sourceCGImage.height))
        guard newCrop.width >= 2, newCrop.height >= 2 else { return }
        cropRect = newCrop
        resizeWindowForCurrentAspect()
    }

    override func scrollWheel(with event: NSEvent) {
        guard let window else { return }
        if event.modifierFlags.contains(.command) {
            window.alphaValue = min(1, max(0.2, window.alphaValue + event.scrollingDeltaY * 0.015))
            return
        }

        let factor = event.scrollingDeltaY >= 0 ? 1.06 : 0.94
        var frame = window.frame
        // One scale factor for both axes: the aspect ratio never changes.
        let newSize = PinnedImageZoom.scaledSize(current: frame.size, factor: factor)
        frame.origin.x -= (newSize.width - frame.width) / 2
        frame.origin.y -= (newSize.height - frame.height) / 2
        frame.size = newSize
        window.setFrame(frame, display: true)
    }

    @objc
    private func copyImage() {
        onCopy?(renderedCGImage)
    }

    @objc private func beginCrop() {
        isCropping = true
        cropDragStart = nil
        cropDragCurrent = nil
        needsDisplay = true
    }

    @objc private func resetCrop() {
        cropRect = nil
        resizeWindowForCurrentAspect()
        needsDisplay = true
    }

    @objc private func rotateLeft() { rotate(by: -1) }
    @objc private func rotateRight() { rotate(by: 1) }

    private func rotate(by delta: Int) {
        let wasSideways = !rotationQuarterTurns.isMultiple(of: 2)
        rotationQuarterTurns = (rotationQuarterTurns + delta + 4) % 4
        let isSideways = !rotationQuarterTurns.isMultiple(of: 2)
        if wasSideways != isSideways, let window {
            var frame = window.frame
            let center = CGPoint(x: frame.midX, y: frame.midY)
            frame.size = CGSize(width: frame.height, height: frame.width)
            frame.origin = CGPoint(x: center.x - frame.width / 2, y: center.y - frame.height / 2)
            window.setFrame(frame, display: true, animate: true)
        }
        needsDisplay = true
    }

    @objc private func mirrorHorizontally() {
        isMirroredHorizontally.toggle()
        needsDisplay = true
    }

    @objc private func mirrorVertically() {
        isMirroredVertically.toggle()
        needsDisplay = true
    }

    @objc private func toggleGrayscale(_ sender: NSMenuItem) {
        filteredMode = filteredMode == .grayscale ? .none : .grayscale
        sender.state = filteredMode == .grayscale ? .on : .off
        menu?.item(withTitle: "反色显示")?.state = .off
        needsDisplay = true
    }

    @objc private func toggleInversion(_ sender: NSMenuItem) {
        filteredMode = filteredMode == .inverted ? .none : .inverted
        sender.state = filteredMode == .inverted ? .on : .off
        menu?.item(withTitle: "灰度显示")?.state = .off
        needsDisplay = true
    }

    @objc private func toggleBorder(_ sender: NSMenuItem) {
        decoration.toggleBorder()
        applyDecoration()
    }

    @objc private func toggleShadow(_ sender: NSMenuItem) {
        decoration.toggleShadow()
        applyDecoration()
    }

    @objc private func toggleTopmost(_ sender: NSMenuItem) {
        guard let window else { return }
        let shouldFloat = window.level != .floating
        window.level = shouldFloat ? .floating : .normal
        sender.state = shouldFloat ? .on : .off
    }

    @objc private func resetAppearance() {
        let wasSideways = !rotationQuarterTurns.isMultiple(of: 2)
        rotationQuarterTurns = 0
        isMirroredHorizontally = false
        isMirroredVertically = false
        filteredMode = .none
        decoration = PinnedImageDecorationState()
        menu?.item(withTitle: "灰度显示")?.state = .off
        menu?.item(withTitle: "反色显示")?.state = .off
        applyDecoration()
        if wasSideways, let window {
            var frame = window.frame
            let center = CGPoint(x: frame.midX, y: frame.midY)
            frame.size = CGSize(width: frame.height, height: frame.width)
            frame.origin = CGPoint(x: center.x - frame.width / 2, y: center.y - frame.height / 2)
            window.setFrame(frame, display: true, animate: true)
        }
        needsDisplay = true
    }

    private func applyDecoration() {
        layer?.borderWidth = decoration.showsBorder
            ? PinnedImageDecoration.visibleBorderWidth
            : 0
        window?.hasShadow = decoration.showsShadow
        window?.invalidateShadow()
        menu?.item(withTitle: PinnedImageDecoration.borderTitle)?.state = decoration.showsBorder ? .on : .off
        menu?.item(withTitle: PinnedImageDecoration.shadowTitle)?.state = decoration.showsShadow ? .on : .off
    }

    @objc private func toggleThumbnail(_ sender: NSMenuItem) {
        guard let window else { return }
        if let previous = thumbnailPreviousFrame {
            thumbnailPreviousFrame = nil
            window.setFrame(previous, display: true, animate: true)
            sender.state = .off
        } else {
            thumbnailPreviousFrame = window.frame
            var frame = window.frame
            let center = CGPoint(x: frame.midX, y: frame.midY)
            frame.size = PinnedImageZoom.aspectFit(displayedPixelSize, into: CGSize(width: 160, height: 220))
            frame.origin = CGPoint(x: center.x - frame.width / 2, y: center.y - frame.height / 2)
            window.setFrame(frame, display: true, animate: true)
            sender.state = .on
        }
    }

    @objc private func enableClickThrough() { onEnableClickThrough?() }
    @objc private func groupVisiblePins() { onGroupVisiblePins?() }
    @objc private func hideGroup() { onHideGroup?() }

    private var displayedImage: NSImage {
        NSImage(cgImage: filteredCGImage, size: image.size)
    }

    private var currentCGImage: CGImage {
        guard let cropRect, let cropped = sourceCGImage.cropping(to: cropRect) else { return sourceCGImage }
        return cropped
    }

    /// Cropped image with the display filter applied (not yet rotated).
    private var filteredCGImage: CGImage {
        switch filteredMode {
        case .none:
            currentCGImage
        case .grayscale:
            filteredImage(filterName: "CIPhotoEffectMono") ?? currentCGImage
        case .inverted:
            filteredImage(filterName: "CIColorInvert") ?? currentCGImage
        }
    }

    /// Exactly what the pin shows: crop, filter, rotation and mirroring.
    private var renderedCGImage: CGImage {
        let filtered = filteredCGImage
        return PinnedImageTransform.apply(
            to: filtered,
            quarterTurns: rotationQuarterTurns,
            mirroredHorizontally: isMirroredHorizontally,
            mirroredVertically: isMirroredVertically
        ) ?? filtered
    }

    /// Pixel size of the picture as displayed (after crop and rotation).
    private var displayedPixelSize: CGSize {
        PinnedImageTransform.displayedSize(
            for: CGSize(width: currentCGImage.width, height: currentCGImage.height),
            quarterTurns: rotationQuarterTurns
        )
    }

    private func filteredImage(filterName: String) -> CGImage? {
        let input = CIImage(cgImage: currentCGImage)
        guard let filter = CIFilter(name: filterName) else { return nil }
        filter.setValue(input, forKey: kCIInputImageKey)
        guard let output = filter.outputImage else { return nil }
        return CIContext().createCGImage(output, from: input.extent)
    }

    private func resizeWindowForCurrentAspect() {
        guard let window else { return }
        var frame = window.frame
        let center = CGPoint(x: frame.midX, y: frame.midY)
        frame.size = PinnedImageZoom.size(forContent: displayedPixelSize, preferredWidth: frame.width)
        frame.origin = CGPoint(x: center.x - frame.width / 2, y: center.y - frame.height / 2)
        window.setFrame(frame, display: true, animate: true)
    }

    private func normalizedRect(_ first: CGPoint, _ second: CGPoint) -> CGRect {
        CGRect(x: min(first.x, second.x), y: min(first.y, second.y), width: abs(first.x - second.x), height: abs(first.y - second.y))
    }

    private func closeForDoubleClickIfNeeded(_ event: NSEvent) -> Bool {
        guard PinnedImageInteraction.shouldClose(clickCount: event.clickCount) else { return false }
        guard !isClosing else { return true }
        isClosing = true
        onClose?()
        return true
    }

    @objc
    private func closePin() {
        guard !isClosing else { return }
        isClosing = true
        onClose?()
    }
}
