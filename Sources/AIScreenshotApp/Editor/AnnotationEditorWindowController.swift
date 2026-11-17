import AppKit
import CoreImage

enum AnnotationEditorWindowLayout {
    static let styleMask: NSWindow.StyleMask = [.titled, .closable, .miniaturizable, .resizable]
}

enum AnnotationEditorToolbarMetrics {
    static let rowCount = 2
    static let height: CGFloat = 96
    static let horizontalInset: CGFloat = 16
    static let verticalInset: CGFloat = 8
    static let rowSpacing: CGFloat = 6
    static let groupSpacing: CGFloat = 8
    static let groupCornerRadius: CGFloat = 8
    static let iconButtonSide: CGFloat = 30
    static let toolPickerWidth: CGFloat = 184
    static let widthSliderWidth: CGFloat = 104
    static let opacitySliderWidth: CGFloat = 84
}

@MainActor
final class AnnotationEditorWindowController: NSObject, NSWindowDelegate {
    private var windows: [NSWindow] = []
    private var canvases: [ObjectIdentifier: AnnotationCanvasView] = [:]
    private let exportService = ImageExportService()

    /// Called after "完成" / Return copied the annotated image to the
    /// clipboard, right before the editor window closes.
    var onDelivered: ((CGImage) -> Void)?

    /// - Parameter scale: backing scale of the display the image was captured
    ///   from; used for the DPI metadata of copied images.
    func open(image: CGImage, scale: CGFloat? = nil) {
        let canvas = AnnotationCanvasView(image: image)
        canvas.exportScale = scale
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 1080, height: 760),
            styleMask: AnnotationEditorWindowLayout.styleMask,
            backing: .buffered,
            defer: false
        )
        window.title = "标注截图"
        window.titlebarAppearsTransparent = false
        window.minSize = NSSize(width: 860, height: 600)
        window.isReleasedWhenClosed = false
        window.acceptsMouseMovedEvents = true
        window.delegate = self
        window.contentView = makeEditorContent(canvas: canvas, window: window)
        window.center()
        window.makeKeyAndOrderFront(nil)
        NSApplication.shared.activate(ignoringOtherApps: true)
        windows.append(window)
        canvases[ObjectIdentifier(window)] = canvas
        // Return on the canvas behaves like the "完成" button.
        canvas.onCommit = { [weak self, weak window] in
            guard let self, let window else { return }
            self.deliverAndClose(window)
        }
    }

    /// "完成": like the inline flow, hand the annotated image to the
    /// clipboard instead of silently discarding the annotations.
    private func deliverAndClose(_ window: NSWindow) {
        guard let canvas = canvases[ObjectIdentifier(window)] else {
            window.performClose(nil)
            return
        }
        guard let image = canvas.copyRenderedImageToPasteboard() else {
            // Keep the window (and the annotations) when the copy failed.
            NSSound.beep()
            return
        }
        onDelivered?(image)
        window.performClose(nil)
    }

    @objc
    private func finishEditing(_ sender: NSButton) {
        guard let window = sender.window else { return }
        deliverAndClose(window)
    }

    func windowWillClose(_ notification: Notification) {
        guard let closingWindow = notification.object as? NSWindow else { return }
        closingWindow.undoManager?.removeAllActions()
        canvases[ObjectIdentifier(closingWindow)]?.onCommit = nil
        canvases.removeValue(forKey: ObjectIdentifier(closingWindow))
        windows.removeAll { $0 === closingWindow }
    }

    private func makeEditorContent(canvas: AnnotationCanvasView, window: NSWindow) -> NSView {
        let root = NSView()
        root.wantsLayer = true
        root.layer?.backgroundColor = NSColor.windowBackgroundColor.cgColor

        let toolbar = NSVisualEffectView()
        toolbar.material = .headerView
        toolbar.blendingMode = .withinWindow
        toolbar.state = .active
        toolbar.identifier = NSUserInterfaceItemIdentifier("annotation-editor-toolbar")

        let tools = NSPopUpButton(frame: .zero, pullsDown: false)
        for tool in AnnotationTool.allCases {
            tools.addItem(withTitle: tool.displayName)
            tools.lastItem?.tag = tool.rawValue
        }
        tools.selectItem(withTag: AnnotationTool.rectangle.rawValue)
        tools.toolTip = "标注工具"
        tools.target = canvas
        tools.action = #selector(AnnotationCanvasView.selectTool(_:))
        tools.controlSize = .regular
        tools.widthAnchor.constraint(equalToConstant: AnnotationEditorToolbarMetrics.toolPickerWidth).isActive = true

        let colorWell = NSColorWell()
        colorWell.color = .systemRed
        colorWell.target = canvas
        colorWell.action = #selector(AnnotationCanvasView.changeColor(_:))
        colorWell.toolTip = "标注颜色"
        colorWell.widthAnchor.constraint(equalToConstant: 36).isActive = true
        colorWell.heightAnchor.constraint(equalToConstant: 24).isActive = true

        let widthSlider = NSSlider(value: 5, minValue: 1, maxValue: 22, target: canvas, action: #selector(AnnotationCanvasView.changeWidth(_:)))
        widthSlider.toolTip = "线条粗细"
        widthSlider.controlSize = .small
        widthSlider.widthAnchor.constraint(equalToConstant: AnnotationEditorToolbarMetrics.widthSliderWidth).isActive = true

        let textSize = NSPopUpButton()
        for size in AnnotationTextMetrics.availableSizes {
            textSize.addItem(withTitle: String(Int(size)))
            textSize.lastItem?.tag = Int(size)
        }
        textSize.selectItem(withTag: Int(AnnotationTextMetrics.defaultSize))
        textSize.target = canvas
        textSize.action = #selector(AnnotationCanvasView.changeTextSize(_:))
        textSize.toolTip = "文字字号"
        textSize.controlSize = .small
        textSize.widthAnchor.constraint(equalToConstant: 62).isActive = true

        let opacitySlider = NSSlider(value: 1, minValue: 0.15, maxValue: 1, target: canvas, action: #selector(AnnotationCanvasView.changeOpacity(_:)))
        opacitySlider.toolTip = "透明度"
        opacitySlider.controlSize = .small
        opacitySlider.widthAnchor.constraint(equalToConstant: AnnotationEditorToolbarMetrics.opacitySliderWidth).isActive = true

        let dashed = NSButton(checkboxWithTitle: "虚线", target: canvas, action: #selector(AnnotationCanvasView.changeDashed(_:)))
        dashed.toolTip = "形状和箭头使用虚线"
        dashed.controlSize = .small

        let undo = toolbarIconButton(symbol: "arrow.uturn.backward", tooltip: "撤销", target: canvas, action: #selector(AnnotationCanvasView.undo(_:)))
        let redo = toolbarIconButton(symbol: "arrow.uturn.forward", tooltip: "重做", target: canvas, action: #selector(AnnotationCanvasView.redo(_:)))
        let rotate = toolbarIconButton(symbol: "rotate.right", tooltip: "所选对象顺时针旋转 90°", target: canvas, action: #selector(AnnotationCanvasView.rotateSelected))
        let shrink = toolbarIconButton(symbol: "minus.magnifyingglass", tooltip: "缩小所选对象", target: canvas, action: #selector(AnnotationCanvasView.shrinkSelected))
        let grow = toolbarIconButton(symbol: "plus.magnifyingglass", tooltip: "放大所选对象", target: canvas, action: #selector(AnnotationCanvasView.growSelected))

        let copy = toolbarActionButton(title: "复制", symbol: "doc.on.doc", target: canvas, action: #selector(AnnotationCanvasView.copyRenderedImage))
        let save = toolbarActionButton(title: "保存", symbol: "square.and.arrow.down", target: self, action: #selector(saveEditorImage(_:)))
        save.identifier = NSUserInterfaceItemIdentifier("saveEditorImage")

        let done = NSButton(title: "完成", target: self, action: #selector(finishEditing(_:)))
        done.toolTip = "完成并复制标注后的图片"
        done.identifier = NSUserInterfaceItemIdentifier("finishEditorImage")
        done.bezelStyle = .rounded
        done.bezelColor = NSColor(
            calibratedRed: 214 / 255,
            green: 64 / 255,
            blue: 47 / 255,
            alpha: 1
        )
        done.contentTintColor = .white
        done.font = .systemFont(ofSize: NSFont.systemFontSize, weight: .semibold)
        done.keyEquivalent = "\r"

        let toolGroup = toolbarGroup([
            toolbarSymbolLabel("pencil.tip", accessibilityDescription: "工具"),
            tools
        ])
        let historyGroup = toolbarGroup([undo, redo])
        let outputGroup = toolbarGroup([copy, save, done])

        let strokeGroup = toolbarGroup([
            colorWell,
            toolbarSymbolLabel("lineweight", accessibilityDescription: "线条粗细"),
            widthSlider
        ])
        let textGroup = toolbarGroup([
            toolbarSymbolLabel("textformat.size", accessibilityDescription: "文字字号"),
            textSize
        ])
        let appearanceGroup = toolbarGroup([
            toolbarSymbolLabel("circle.lefthalf.filled", accessibilityDescription: "透明度"),
            opacitySlider,
            dashed
        ])
        let transformGroup = toolbarGroup([rotate, shrink, grow])

        let topSpacer = flexibleSpacer()
        let topRow = toolbarRow([toolGroup, topSpacer, historyGroup, outputGroup])
        topRow.identifier = NSUserInterfaceItemIdentifier("annotation-editor-toolbar-primary-row")

        let bottomSpacer = flexibleSpacer()
        let bottomRow = toolbarRow([strokeGroup, textGroup, appearanceGroup, bottomSpacer, transformGroup])
        bottomRow.identifier = NSUserInterfaceItemIdentifier("annotation-editor-toolbar-style-row")

        let stack = NSStackView(views: [topRow, bottomRow])
        stack.orientation = .vertical
        stack.alignment = .width
        stack.distribution = .fillEqually
        stack.spacing = AnnotationEditorToolbarMetrics.rowSpacing
        stack.edgeInsets = NSEdgeInsets(
            top: AnnotationEditorToolbarMetrics.verticalInset,
            left: AnnotationEditorToolbarMetrics.horizontalInset,
            bottom: AnnotationEditorToolbarMetrics.verticalInset,
            right: AnnotationEditorToolbarMetrics.horizontalInset
        )

        toolbar.addSubview(stack)
        root.addSubview(toolbar)
        root.addSubview(canvas)
        toolbar.translatesAutoresizingMaskIntoConstraints = false
        stack.translatesAutoresizingMaskIntoConstraints = false
        canvas.translatesAutoresizingMaskIntoConstraints = false

        NSLayoutConstraint.activate([
            toolbar.leadingAnchor.constraint(equalTo: root.leadingAnchor),
            toolbar.trailingAnchor.constraint(equalTo: root.trailingAnchor),
            toolbar.topAnchor.constraint(equalTo: root.topAnchor),
            toolbar.heightAnchor.constraint(equalToConstant: AnnotationEditorToolbarMetrics.height),
            stack.leadingAnchor.constraint(equalTo: toolbar.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: toolbar.trailingAnchor),
            stack.topAnchor.constraint(equalTo: toolbar.topAnchor),
            stack.bottomAnchor.constraint(equalTo: toolbar.bottomAnchor),
            canvas.leadingAnchor.constraint(equalTo: root.leadingAnchor),
            canvas.trailingAnchor.constraint(equalTo: root.trailingAnchor),
            canvas.topAnchor.constraint(equalTo: toolbar.bottomAnchor),
            canvas.bottomAnchor.constraint(equalTo: root.bottomAnchor)
        ])

        objc_setAssociatedObject(save, &AssociatedKeys.canvas, canvas, .OBJC_ASSOCIATION_RETAIN_NONATOMIC)
        return root
    }

    private func toolbarRow(_ views: [NSView]) -> NSStackView {
        let row = NSStackView(views: views)
        row.orientation = .horizontal
        row.alignment = .centerY
        row.spacing = AnnotationEditorToolbarMetrics.groupSpacing
        return row
    }

    private func toolbarGroup(_ views: [NSView]) -> NSVisualEffectView {
        let group = NSVisualEffectView()
        group.material = .contentBackground
        group.blendingMode = .withinWindow
        group.state = .active
        group.wantsLayer = true
        group.layer?.cornerRadius = AnnotationEditorToolbarMetrics.groupCornerRadius
        group.layer?.masksToBounds = true
        group.layer?.borderWidth = 0.5
        group.layer?.borderColor = NSColor.separatorColor.withAlphaComponent(0.52).cgColor

        let stack = NSStackView(views: views)
        stack.orientation = .horizontal
        stack.alignment = .centerY
        stack.spacing = 6
        stack.edgeInsets = NSEdgeInsets(top: 3, left: 7, bottom: 3, right: 7)
        group.addSubview(stack)
        stack.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: group.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: group.trailingAnchor),
            stack.topAnchor.constraint(equalTo: group.topAnchor),
            stack.bottomAnchor.constraint(equalTo: group.bottomAnchor)
        ])
        return group
    }

    private func toolbarIconButton(
        symbol: String,
        tooltip: String,
        target: AnyObject,
        action: Selector
    ) -> NSButton {
        let image = NSImage(systemSymbolName: symbol, accessibilityDescription: tooltip)?
            .withSymbolConfiguration(.init(pointSize: 12, weight: .medium)) ?? NSImage()
        let button = NSButton(image: image, target: target, action: action)
        button.bezelStyle = .texturedRounded
        button.toolTip = tooltip
        button.widthAnchor.constraint(equalToConstant: AnnotationEditorToolbarMetrics.iconButtonSide).isActive = true
        button.heightAnchor.constraint(equalToConstant: AnnotationEditorToolbarMetrics.iconButtonSide).isActive = true
        return button
    }

    private func toolbarActionButton(
        title: String,
        symbol: String,
        target: AnyObject,
        action: Selector
    ) -> NSButton {
        let button = NSButton(title: title, target: target, action: action)
        button.image = NSImage(systemSymbolName: symbol, accessibilityDescription: title)?
            .withSymbolConfiguration(.init(pointSize: 11, weight: .medium))
        button.imagePosition = .imageLeading
        button.bezelStyle = .rounded
        button.toolTip = title
        return button
    }

    private func toolbarSymbolLabel(_ symbol: String, accessibilityDescription: String) -> NSImageView {
        let image = NSImage(systemSymbolName: symbol, accessibilityDescription: accessibilityDescription)?
            .withSymbolConfiguration(.init(pointSize: 11, weight: .medium))
        let imageView = NSImageView(image: image ?? NSImage())
        imageView.toolTip = accessibilityDescription
        imageView.contentTintColor = .secondaryLabelColor
        return imageView
    }

    private func flexibleSpacer() -> NSView {
        let spacer = NSView()
        spacer.setContentHuggingPriority(.defaultLow, for: .horizontal)
        spacer.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        return spacer
    }

    @objc
    private func saveEditorImage(_ sender: NSButton) {
        guard let canvas = objc_getAssociatedObject(sender, &AssociatedKeys.canvas) as? AnnotationCanvasView else { return }
        do {
            _ = try exportService.save(canvas.renderedImage())
        } catch {
            let alert = NSAlert(error: error)
            alert.runModal()
        }
    }
}

@MainActor
private enum AssociatedKeys {
    static var canvas: UInt8 = 0
}

enum AnnotationTool: Int, CaseIterable {
    case select
    case crop
    case rectangle
    case ellipse
    case arrow
    case pen
    case highlighter
    case text
    case number
    case mosaic
    case mosaicBrush
    case blur
    case eraser
    case magnify

    var displayName: String {
        switch self {
        case .select: "选择/移动已有标注"
        case .crop: "裁剪"
        case .rectangle: "矩形"
        case .ellipse: "椭圆"
        case .arrow: "箭头"
        case .pen: "画笔"
        case .highlighter: "高亮笔"
        case .text: "文字"
        case .number: "编号"
        case .mosaic: "框选马赛克"
        case .mosaicBrush: "涂抹马赛克"
        case .blur: "模糊"
        case .eraser: "橡皮"
        case .magnify: "局部放大"
        }
    }
}

struct TaperedArrowGeometry {
    static func polygon(from start: CGPoint, to end: CGPoint, width: CGFloat) -> [CGPoint] {
        let dx = end.x - start.x
        let dy = end.y - start.y
        let length = hypot(dx, dy)
        guard length > 0.5 else { return Array(repeating: start, count: 7) }

        let direction = CGPoint(x: dx / length, y: dy / length)
        let normal = CGPoint(x: -direction.y, y: direction.x)
        let baseWidth = max(2, width)
        let tailHalfWidth = max(1, baseWidth * 0.28)
        let neckHalfWidth = max(tailHalfWidth + 0.8, baseWidth * 0.72)
        let headHalfWidth = max(7, baseWidth * 2.2)
        let headLength = min(max(12, baseWidth * 4), length * 0.55)
        let neck = CGPoint(
            x: end.x - direction.x * headLength,
            y: end.y - direction.y * headLength
        )

        func offset(_ point: CGPoint, by amount: CGFloat) -> CGPoint {
            CGPoint(x: point.x + normal.x * amount, y: point.y + normal.y * amount)
        }

        return [
            offset(start, by: -tailHalfWidth),
            offset(neck, by: -neckHalfWidth),
            offset(neck, by: -headHalfWidth),
            end,
            offset(neck, by: headHalfWidth),
            offset(neck, by: neckHalfWidth),
            offset(start, by: tailHalfWidth)
        ]
    }
}

enum AnnotationResizeHandle: CaseIterable {
    case bottomLeft
    case bottomRight
    case topLeft
    case topRight

    func point(in rect: CGRect) -> CGPoint {
        switch self {
        case .bottomLeft: CGPoint(x: rect.minX, y: rect.minY)
        case .bottomRight: CGPoint(x: rect.maxX, y: rect.minY)
        case .topLeft: CGPoint(x: rect.minX, y: rect.maxY)
        case .topRight: CGPoint(x: rect.maxX, y: rect.maxY)
        }
    }

    func anchor(in rect: CGRect) -> CGPoint {
        switch self {
        case .bottomLeft: CGPoint(x: rect.maxX, y: rect.maxY)
        case .bottomRight: CGPoint(x: rect.minX, y: rect.maxY)
        case .topLeft: CGPoint(x: rect.maxX, y: rect.minY)
        case .topRight: CGPoint(x: rect.minX, y: rect.minY)
        }
    }
}

struct AnnotationSelectionGeometry {
    static func uniformScale(
        anchor: CGPoint,
        originalHandle: CGPoint,
        draggedHandle: CGPoint,
        minimum: CGFloat = 0.1,
        maximum: CGFloat = 20
    ) -> CGFloat {
        let original = CGPoint(x: originalHandle.x - anchor.x, y: originalHandle.y - anchor.y)
        let dragged = CGPoint(x: draggedHandle.x - anchor.x, y: draggedHandle.y - anchor.y)
        let denominator = original.x * original.x + original.y * original.y
        guard denominator > 0.001 else { return 1 }
        let projected = (dragged.x * original.x + dragged.y * original.y) / denominator
        return min(maximum, max(minimum, projected))
    }
}

struct AnnotationTextMetrics {
    static let availableSizes: [CGFloat] = [12, 14, 16, 18, 20, 24, 28, 32, 40, 48, 64, 72]
    static let defaultSize: CGFloat = 24

    static func clamped(_ size: CGFloat) -> CGFloat {
        min(availableSizes.last ?? 72, max(availableSizes.first ?? 12, size))
    }

    /// Size at which an annotation of image-space `size` is drawn when the
    /// image is shown at `scale`. Export uses `scale == 1`. There is
    /// deliberately no on-screen minimum: a floor that only applied on screen
    /// made text and number badges look larger in the editor than in the
    /// exported image.
    static func renderedSize(for size: CGFloat, scale: CGFloat) -> CGFloat {
        max(1, size * scale)
    }

    static func stepped(from size: CGFloat, direction: Int) -> CGFloat {
        let current = clamped(size)
        if direction > 0 {
            return availableSizes.first(where: { $0 > current }) ?? (availableSizes.last ?? current)
        }
        if direction < 0 {
            return availableSizes.last(where: { $0 < current }) ?? (availableSizes.first ?? current)
        }
        return current
    }

    static func boundingSize(
        for text: String,
        font: NSFont,
        maximumWidth: CGFloat
    ) -> CGSize {
        let content = text.isEmpty ? "文字" : text
        let attributes: [NSAttributedString.Key: Any] = [.font: font]
        let unconstrained = (content as NSString).boundingRect(
            with: CGSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude),
            options: [.usesLineFragmentOrigin, .usesFontLeading],
            attributes: attributes
        )
        let width = min(maximumWidth, max(font.pointSize * 1.5, ceil(unconstrained.width) + 6))
        let constrained = (content as NSString).boundingRect(
            with: CGSize(width: width, height: CGFloat.greatestFiniteMagnitude),
            options: [.usesLineFragmentOrigin, .usesFontLeading],
            attributes: attributes
        )
        return CGSize(
            width: max(font.pointSize * 1.5, ceil(constrained.width) + 6),
            height: max(font.pointSize * 1.35, ceil(constrained.height) + 6)
        )
    }
}

struct AnnotationStrokeSmoother {
    static func points(from start: CGPoint, to end: CGPoint, maximumSpacing: CGFloat) -> [CGPoint] {
        let distance = hypot(end.x - start.x, end.y - start.y)
        guard distance > 0 else { return [] }
        let spacing = max(0.5, maximumSpacing)
        let steps = max(1, Int(ceil(distance / spacing)))
        return (1...steps).map { step in
            let progress = CGFloat(step) / CGFloat(steps)
            return CGPoint(
                x: start.x + (end.x - start.x) * progress,
                y: start.y + (end.y - start.y) * progress
            )
        }
    }
}

private final class InlineAnnotationTextView: NSTextView {
    var onFinish: (() -> Void)?
    var onAdjustSize: ((Int) -> Void)?

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 {
            onFinish?()
        } else {
            super.keyDown(with: event)
        }
    }

    override func rightMouseDown(with event: NSEvent) {
        onFinish?()
    }

    override func scrollWheel(with event: NSEvent) {
        let delta = event.scrollingDeltaY
        guard abs(delta) > 0.01 else {
            super.scrollWheel(with: event)
            return
        }
        onAdjustSize?(delta > 0 ? 1 : -1)
    }
}

private enum AnnotationElement {
    case rectangle(CGRect, NSColor, CGFloat, Bool)
    case ellipse(CGRect, NSColor, CGFloat, Bool)
    case arrow(CGPoint, CGPoint, NSColor, CGFloat, Bool)
    case pen([CGPoint], NSColor, CGFloat, Bool)
    case highlighter([CGPoint], NSColor, CGFloat)
    case text(String, CGPoint, NSColor, CGFloat)
    case number(Int, CGPoint, NSColor, CGFloat)
    case mosaic(CGRect)
    case mosaicStroke([CGPoint], CGFloat)
    case blur(CGRect)
    case magnify(CGRect, CGFloat)
}

private struct AnnotationSnapshot {
    let elements: [AnnotationElement]
    let cropRect: CGRect?
}

private enum DirectManipulationMode {
    case move
    case resize(handle: AnnotationResizeHandle, anchor: CGPoint, originalHandle: CGPoint)
}

final class AnnotationCanvasView: NSView, NSTextViewDelegate {
    var onCancel: (() -> Void)?
    var onCommit: (() -> Void)?
    var onElementsChanged: ((Bool) -> Void)?
    var onWidthChanged: ((CGFloat) -> Void)?
    var onTextSizeChanged: ((CGFloat) -> Void)?
    /// Backing scale of the captured display, for DPI metadata on copy.
    var exportScale: CGFloat?

    /// Read-only state for tests.
    var elementCount: Int { elements.count }
    var undoDepth: Int { undoSnapshots.count }
    var redoDepth: Int { redoSnapshots.count }
    var hasSelection: Bool { selectedElementIndex != nil }
    var isManipulatingElement: Bool { directManipulationMode != nil }

    private let sourceCGImage: CGImage
    private let sourceImage: NSImage
    private let contentInset: CGFloat
    private lazy var mosaicImage = filteredImage(name: "CIPixellate", parameters: [kCIInputScaleKey: 14])
    private lazy var blurImage = filteredImage(name: "CIGaussianBlur", parameters: [kCIInputRadiusKey: 12])
    private var elements: [AnnotationElement] = []
    private var undoSnapshots: [AnnotationSnapshot] = []
    private var redoSnapshots: [AnnotationSnapshot] = []
    private var cropRect: CGRect?
    private var selectedTool: AnnotationTool = .rectangle
    private var selectedColor: NSColor = .systemRed
    private var selectedWidth: CGFloat = 5
    private var selectedTextSize = AnnotationTextMetrics.defaultSize
    private var selectedOpacity: CGFloat = 1
    private var selectedDashed = false
    private var selectedElementIndex: Int?
    private var selectedElementBeforeDrag: AnnotationElement?
    private var directManipulationMode: DirectManipulationMode?
    private var snapshotBeforeManipulation: AnnotationSnapshot?
    private var didManipulateElement = false
    private var dragStart: CGPoint?
    private var dragCurrent: CGPoint?
    private var penPoints: [CGPoint] = []
    private var activeTextView: InlineAnnotationTextView?
    private var activeTextOrigin: CGPoint?
    private var activeTextElementIndex: Int?
    private var activeTextSize: CGFloat?
    private var isFinishingTextEntry = false
    private var hoverImagePoint: CGPoint?
    private var trackingAreaReference: NSTrackingArea?

    init(image: CGImage, contentInset: CGFloat = 24) {
        sourceCGImage = image
        sourceImage = NSImage(cgImage: image, size: NSSize(width: image.width, height: image.height))
        self.contentInset = contentInset
        super.init(frame: .zero)
        wantsLayer = true
        layer?.backgroundColor = NSColor.black.withAlphaComponent(0.82).cgColor
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override var acceptsFirstResponder: Bool { true }
    var isTextEntryActive: Bool { activeTextView != nil }

    override func resetCursorRects() {
        super.resetCursorRects()
        guard window != nil, !isHiddenOrHasHiddenAncestor, !imageFrame.isEmpty else { return }
        let cursor: NSCursor
        switch selectedTool {
        case .select: cursor = .openHand
        case .text: cursor = .iBeam
        default: cursor = .crosshair
        }
        addCursorRect(imageFrame, cursor: cursor)
    }

    override func updateTrackingAreas() {
        if let trackingAreaReference {
            removeTrackingArea(trackingAreaReference)
        }
        let tracking = NSTrackingArea(
            rect: imageFrame,
            options: [.activeInKeyWindow, .mouseMoved, .mouseEnteredAndExited],
            owner: self,
            userInfo: nil
        )
        addTrackingArea(tracking)
        trackingAreaReference = tracking
        super.updateTrackingAreas()
    }

    override func mouseMoved(with event: NSEvent) {
        let point = convert(event.locationInWindow, from: nil)
        hoverImagePoint = imageFrame.contains(point) ? imagePoint(from: point) : nil
        if selectedTool == .mosaicBrush { needsDisplay = true }
    }

    override func mouseExited(with event: NSEvent) {
        hoverImagePoint = nil
        if selectedTool == .mosaicBrush { needsDisplay = true }
    }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)
        let destination = imageFrame
        let visibleSource = visibleImageRect
        NSColor.black.withAlphaComponent(0.22).setFill()
        bounds.fill()
        sourceImage.draw(in: destination, from: visibleSource, operation: .sourceOver, fraction: 1)
        for (index, element) in elements.enumerated() {
            if index == activeTextElementIndex { continue }
            draw(element, in: destination, sourceRect: visibleSource)
            if index == selectedElementIndex {
                drawSelection(for: element, in: destination, sourceRect: visibleSource)
            }
        }
        if let preview = previewElement {
            draw(preview, in: destination, sourceRect: visibleSource)
        }
        if directManipulationMode == nil, selectedTool == .crop, let start = dragStart, let current = dragCurrent {
            let rect = normalizedRect(from: start, to: current)
            let mapped = CGRect(
                x: destination.minX + (rect.minX - visibleSource.minX) / visibleSource.width * destination.width,
                y: destination.minY + (rect.minY - visibleSource.minY) / visibleSource.height * destination.height,
                width: rect.width / visibleSource.width * destination.width,
                height: rect.height / visibleSource.height * destination.height
            )
            NSColor.controlAccentColor.setStroke()
            let cropPath = NSBezierPath(rect: mapped)
            let pattern: [CGFloat] = [7, 5]
            cropPath.setLineDash(pattern, count: pattern.count, phase: 0)
            cropPath.lineWidth = 2
            cropPath.stroke()
        }
        if selectedTool == .mosaicBrush,
           dragStart == nil,
           activeTextView == nil,
           let hoverImagePoint {
            let center = viewPoint(from: hoverImagePoint)
            let diameter = mosaicBrushDiameter * destination.width / visibleSource.width
            let cursorRect = CGRect(
                x: center.x - diameter / 2,
                y: center.y - diameter / 2,
                width: diameter,
                height: diameter
            )
            NSColor.black.withAlphaComponent(0.72).setStroke()
            let outside = NSBezierPath(ovalIn: cursorRect.insetBy(dx: -1, dy: -1))
            outside.lineWidth = 2.5
            outside.stroke()
            NSColor.white.withAlphaComponent(0.92).setStroke()
            let inside = NSBezierPath(ovalIn: cursorRect)
            inside.lineWidth = 1
            inside.stroke()
        }
        NSColor.white.withAlphaComponent(0.18).setStroke()
        let border = NSBezierPath(roundedRect: destination, xRadius: 3, yRadius: 3)
        border.lineWidth = 1
        border.stroke()
    }

    override func mouseDown(with event: NSEvent) {
        let viewPoint = convert(event.locationInWindow, from: nil)
        guard imageFrame.contains(viewPoint) else { return }
        finishTextEntry(commit: true)
        let point = imagePoint(from: viewPoint)

        if selectedTool == .eraser {
            if let index = hitTestElement(at: point, allowsInterior: false) {
                registerUndo()
                elements.remove(at: index)
                selectedElementIndex = nil
                notifyElementsChanged()
                needsDisplay = true
            }
            return
        }

        if let index = selectedElementIndex,
           elements.indices.contains(index),
           let handle = resizeHandle(at: viewPoint, for: elements[index]) {
            beginDirectManipulation(
                index: index,
                start: point,
                mode: .resize(
                    handle: handle,
                    anchor: handle.anchor(in: boundingRect(of: elements[index])),
                    originalHandle: handle.point(in: boundingRect(of: elements[index]))
                )
            )
            return
        }

        // The select tool grabs anything under the pointer, including the
        // interior of hollow shapes. With a drawing tool only the element's
        // actual stroke (or filled body) grabs it; otherwise the tool draws,
        // so you can annotate inside an existing rectangle or ellipse.
        if let index = hitTestElement(at: point, allowsInterior: selectedTool == .select) {
            selectedElementIndex = index
            if event.clickCount >= 2 {
                editElement(at: index)
                needsDisplay = true
                return
            }
            beginDirectManipulation(index: index, start: point, mode: .move)
            return
        }

        selectedElementIndex = nil
        if selectedTool == .select {
            selectedElementBeforeDrag = nil
            directManipulationMode = nil
            needsDisplay = true
            return
        }
        if selectedTool == .text {
            beginTextEntry(at: point)
            return
        }
        if selectedTool == .number {
            registerUndo()
            let next = elements.compactMap { element -> Int? in
                if case .number(let number, _, _, _) = element { return number }
                return nil
            }.max().map { $0 + 1 } ?? 1
            elements.append(.number(next, point, effectiveColor, max(22, selectedWidth * 6)))
            selectedElementIndex = elements.count - 1
            notifyElementsChanged()
            needsDisplay = true
            return
        }
        dragStart = point
        dragCurrent = point
        penPoints = [point]
        needsDisplay = true
    }

    override func mouseDragged(with event: NSEvent) {
        guard dragStart != nil else { return }
        let point = imagePoint(from: convert(event.locationInWindow, from: nil))
        dragCurrent = point
        if let mode = directManipulationMode,
           let index = selectedElementIndex,
           elements.indices.contains(index),
           let original = selectedElementBeforeDrag,
           let start = dragStart {
            // Ignore pointer jitter during a plain click so that selecting an
            // element neither nudges it nor creates an undo step.
            let viewScale = imageFrame.width / max(1, visibleImageRect.width)
            let travelled = hypot(point.x - start.x, point.y - start.y) * viewScale
            guard didManipulateElement || travelled >= 2 else { return }
            didManipulateElement = true
            switch mode {
            case .move:
                elements[index] = translated(original, dx: point.x - start.x, dy: point.y - start.y)
            case .resize(_, let anchor, let originalHandle):
                let scale = AnnotationSelectionGeometry.uniformScale(
                    anchor: anchor,
                    originalHandle: originalHandle,
                    draggedHandle: point
                )
                elements[index] = scaled(original, around: anchor, by: scale)
            }
        } else if selectedTool == .mosaicBrush, let previous = penPoints.last {
            penPoints.append(contentsOf: AnnotationStrokeSmoother.points(
                from: previous,
                to: point,
                maximumSpacing: max(1.5, mosaicBrushDiameter * 0.08)
            ))
        } else if selectedTool == .pen || selectedTool == .highlighter {
            penPoints.append(point)
        }
        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        guard let start = dragStart else { return }
        let end = imagePoint(from: convert(event.locationInWindow, from: nil))
        if directManipulationMode != nil {
            // Record undo only when the element really moved or resized.
            if didManipulateElement, let snapshot = snapshotBeforeManipulation {
                registerUndo(snapshot)
                notifyElementsChanged()
            }
            endDirectManipulation()
            needsDisplay = true
            return
        }
        let rect = normalizedRect(from: start, to: end)
        let element: AnnotationElement?
        switch selectedTool {
        case .rectangle where AnnotationDragValidity.isDrawableRect(rect, minimumSide: 2):
            element = .rectangle(rect, effectiveColor, selectedWidth, selectedDashed)
        case .ellipse where AnnotationDragValidity.isDrawableRect(rect, minimumSide: 2):
            element = .ellipse(rect, effectiveColor, selectedWidth, selectedDashed)
        case .arrow where AnnotationDragValidity.isDrawableArrow(from: start, to: end):
            element = .arrow(start, end, effectiveColor, selectedWidth, selectedDashed)
        case .pen where AnnotationDragValidity.isDrawableStroke(penPoints + [end]):
            penPoints.append(end)
            element = .pen(penPoints, effectiveColor, selectedWidth, selectedDashed)
        case .highlighter where AnnotationDragValidity.isDrawableStroke(penPoints + [end]):
            penPoints.append(end)
            element = .highlighter(penPoints, selectedColor.withAlphaComponent(min(0.45, selectedOpacity)), selectedWidth * 4)
        case .mosaic where AnnotationDragValidity.isDrawableRect(rect, minimumSide: 3):
            element = .mosaic(rect)
        case .mosaicBrush where !penPoints.isEmpty:
            if let previous = penPoints.last {
                penPoints.append(contentsOf: AnnotationStrokeSmoother.points(
                    from: previous,
                    to: end,
                    maximumSpacing: max(1.5, mosaicBrushDiameter * 0.08)
                ))
            }
            element = .mosaicStroke(penPoints, mosaicBrushDiameter)
        case .blur where AnnotationDragValidity.isDrawableRect(rect, minimumSide: 3):
            element = .blur(rect)
        case .magnify where AnnotationDragValidity.isDrawableRect(rect, minimumSide: 12):
            element = .magnify(rect, 2)
        case .crop where AnnotationDragValidity.isDrawableRect(rect, minimumSide: 12):
            registerUndo()
            cropRect = rect.intersection(visibleImageRect)
            element = nil
        default:
            element = nil
        }
        if let element {
            registerUndo()
            elements.append(element)
            selectedElementIndex = elements.count - 1
            notifyElementsChanged()
        }
        dragStart = nil
        dragCurrent = nil
        penPoints = []
        needsDisplay = true
    }

    override func keyDown(with event: NSEvent) {
        let character = event.charactersIgnoringModifiers
        if character == "[" || character == "]" {
            adjustActiveToolSize(direction: character == "]" ? 1 : -1)
        } else if event.keyCode == 53, let onCancel {
            onCancel()
        } else if [36, 76].contains(event.keyCode), let onCommit {
            onCommit()
        } else if event.modifierFlags.contains(.command), event.charactersIgnoringModifiers == "z" {
            event.modifierFlags.contains(.shift) ? performRedoStep() : performUndoStep()
        } else if [51, 117].contains(event.keyCode),
                  let index = selectedElementIndex,
                  elements.indices.contains(index) {
            registerUndo()
            elements.remove(at: index)
            selectedElementIndex = nil
            notifyElementsChanged()
            needsDisplay = true
        } else {
            super.keyDown(with: event)
        }
    }

    override func scrollWheel(with event: NSEvent) {
        guard selectedTool == .text || selectedTool == .mosaicBrush || selectedTool == .pen || selectedTool == .highlighter else {
            super.scrollWheel(with: event)
            return
        }
        let delta = event.scrollingDeltaY
        guard abs(delta) > 0.01 else { return }
        adjustActiveToolSize(direction: delta > 0 ? 1 : -1)
    }

    override func rightMouseDown(with event: NSEvent) {
        if activeTextView != nil {
            finishTextEntry(commit: true)
            return
        }
        if dragStart != nil {
            // Cancelling a move/resize puts the element back where it was.
            if directManipulationMode != nil,
               let index = selectedElementIndex,
               elements.indices.contains(index),
               let original = selectedElementBeforeDrag {
                elements[index] = original
            }
            penPoints = []
            endDirectManipulation()
            needsDisplay = true
            return
        }
        if let onCancel {
            onCancel()
        } else {
            super.rightMouseDown(with: event)
        }
    }

    @objc func selectTool(_ sender: NSPopUpButton) {
        setTool(AnnotationTool(rawValue: sender.selectedTag()) ?? .rectangle)
    }

    func setTool(_ tool: AnnotationTool) {
        finishTextEntry(commit: true)
        selectedTool = tool
        selectedElementIndex = selectedTool == .select ? selectedElementIndex : nil
        needsDisplay = true
        window?.invalidateCursorRects(for: self)
    }

    @objc func changeColor(_ sender: NSColorWell) {
        selectedColor = sender.color
        if let activeTextView {
            activeTextView.textColor = effectiveColor
            activeTextView.insertionPointColor = effectiveColor
            updateTextEditorFontAndFrame()
        }
    }

    @objc func changeWidth(_ sender: NSSlider) {
        selectedWidth = CGFloat(sender.doubleValue)
        onWidthChanged?(selectedWidth)
        needsDisplay = true
    }

    @objc func changeTextSize(_ sender: NSPopUpButton) {
        let value = CGFloat(sender.selectedTag())
        setTextSize(value > 0 ? value : CGFloat(Double(sender.titleOfSelectedItem ?? "24") ?? 24))
    }

    @objc func changeOpacity(_ sender: NSSlider) {
        selectedOpacity = CGFloat(sender.doubleValue)
    }

    @objc func changeDashed(_ sender: NSButton) {
        selectedDashed = sender.state == .on
    }

    @objc func rotateSelected() { transformSelected(scale: 1, rotate90: true) }
    @objc func shrinkSelected() { transformSelected(scale: 0.9, rotate90: false) }
    @objc func growSelected() { transformSelected(scale: 1.1, rotate90: false) }

    @objc func undo(_ sender: Any?) {
        performUndoStep()
    }

    @objc func redo(_ sender: Any?) {
        performRedoStep()
    }

    /// Consolidated handler for toolbar actions and the system
    /// `undo:`/`redo:` responder actions; Cmd+Z therefore always resolves to
    /// the snapshot history instead of stale NSTextView undo registrations.
    private func performUndoStep() {
        finishTextEntry(commit: true)
        guard let snapshot = undoSnapshots.popLast() else { return }
        redoSnapshots.append(currentSnapshot)
        restore(snapshot)
        needsDisplay = true
    }

    private func performRedoStep() {
        finishTextEntry(commit: true)
        guard let snapshot = redoSnapshots.popLast() else { return }
        undoSnapshots.append(currentSnapshot)
        restore(snapshot)
        needsDisplay = true
    }

    @objc func copyRenderedImage() {
        _ = copyRenderedImageToPasteboard()
    }

    /// Copies the annotated image (with scale-aware DPI metadata) and returns
    /// it, or `nil` when it could not be encoded or written.
    @discardableResult
    func copyRenderedImageToPasteboard() -> CGImage? {
        let image = renderedImage()
        guard let payload = ClipboardImageEncoder.payload(
            for: image,
            scale: exportScale ?? ClipboardService.defaultImageScale
        ) else {
            return nil
        }
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        guard pasteboard.setData(payload.png, forType: .png) else { return nil }
        if let tiff = payload.tiff {
            pasteboard.setData(tiff, forType: .tiff)
        }
        return image
    }

    func renderedImage() -> CGImage {
        finishTextEntry(commit: true)
        let visibleSource = visibleImageRect.integral
        guard let representation = NSBitmapImageRep(
            bitmapDataPlanes: nil,
            pixelsWide: max(1, Int(visibleSource.width)),
            pixelsHigh: max(1, Int(visibleSource.height)),
            bitsPerSample: 8,
            samplesPerPixel: 4,
            hasAlpha: true,
            isPlanar: false,
            colorSpaceName: .deviceRGB,
            bytesPerRow: 0,
            bitsPerPixel: 0
        ) else { return sourceCGImage }

        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: representation)
        let destination = CGRect(x: 0, y: 0, width: visibleSource.width, height: visibleSource.height)
        sourceImage.draw(in: destination, from: visibleSource, operation: .copy, fraction: 1)
        for element in elements {
            draw(element, in: destination, sourceRect: visibleSource)
        }
        NSGraphicsContext.restoreGraphicsState()
        return representation.cgImage ?? sourceCGImage
    }

    var displayedImageFrame: CGRect { imageFrame }

    private var imageFrame: CGRect {
        let insetBounds = bounds.insetBy(dx: contentInset, dy: contentInset)
        let visible = visibleImageRect
        let scale = min(insetBounds.width / visible.width,
                        insetBounds.height / visible.height)
        let size = CGSize(width: visible.width * scale,
                          height: visible.height * scale)
        return CGRect(x: insetBounds.midX - size.width / 2,
                      y: insetBounds.midY - size.height / 2,
                      width: size.width,
                      height: size.height)
    }

    private var previewElement: AnnotationElement? {
        guard directManipulationMode == nil else { return nil }
        guard let start = dragStart, let end = dragCurrent else { return nil }
        let rect = normalizedRect(from: start, to: end)
        let element: AnnotationElement? = switch selectedTool {
        case .rectangle: .rectangle(rect, effectiveColor, selectedWidth, selectedDashed)
        case .ellipse: .ellipse(rect, effectiveColor, selectedWidth, selectedDashed)
        case .arrow: .arrow(start, end, effectiveColor, selectedWidth, selectedDashed)
        case .pen: .pen(penPoints, effectiveColor, selectedWidth, selectedDashed)
        case .highlighter: .highlighter(penPoints, selectedColor.withAlphaComponent(min(0.45, selectedOpacity)), selectedWidth * 4)
        case .mosaic: .mosaic(rect)
        case .mosaicBrush: .mosaicStroke(penPoints, mosaicBrushDiameter)
        case .blur: .blur(rect)
        case .magnify: .magnify(rect, 2)
        case .select, .crop, .text, .number, .eraser: nil
        }
        return element
    }

    private func editElement(at index: Int) {
        guard elements.indices.contains(index) else { return }
        guard case .text(_, let origin, _, _) = elements[index] else { return }
        beginTextEntry(at: origin, editingElementAt: index)
    }

    func beginTextEntry(at point: CGPoint, editingElementAt index: Int? = nil) {
        finishTextEntry(commit: true)

        var initialText = ""
        var textColor = effectiveColor
        var textSize = selectedTextSize
        var origin = point
        if let index, elements.indices.contains(index),
           case .text(let text, let existingOrigin, let color, let size) = elements[index] {
            initialText = text
            textColor = color
            textSize = size
            origin = existingOrigin
        }

        selectedTextSize = AnnotationTextMetrics.clamped(textSize)
        activeTextSize = selectedTextSize
        onTextSizeChanged?(selectedTextSize)

        let textView = InlineAnnotationTextView()
        textView.string = initialText
        textView.textColor = textColor
        textView.drawsBackground = false
        textView.isRichText = false
        textView.importsGraphics = false
        textView.allowsUndo = true
        textView.isHorizontallyResizable = false
        textView.isVerticallyResizable = true
        textView.textContainerInset = CGSize(width: 2, height: 2)
        textView.textContainer?.lineFragmentPadding = 0
        textView.textContainer?.widthTracksTextView = true
        textView.insertionPointColor = textColor
        textView.delegate = self
        textView.onFinish = { [weak self] in self?.finishTextEntry(commit: true) }
        textView.onAdjustSize = { [weak self] direction in self?.adjustActiveToolSize(direction: direction) }

        activeTextView = textView
        activeTextOrigin = origin
        activeTextElementIndex = index
        addSubview(textView)
        updateTextEditorFontAndFrame()
        window?.makeFirstResponder(textView)
        if !initialText.isEmpty {
            textView.selectAll(nil)
        }
    }

    func textDidChange(_ notification: Notification) {
        updateTextEditorFontAndFrame()
    }

    func textDidEndEditing(_ notification: Notification) {
        finishTextEntry(commit: true)
    }

    private func updateTextEditorFontAndFrame() {
        guard let textView = activeTextView, let origin = activeTextOrigin else { return }
        let scale = imageFrame.width / visibleImageRect.width
        // Same effective size as the committed element (and the export).
        let font = NSFont.systemFont(
            ofSize: AnnotationTextMetrics.renderedSize(for: activeTextSize ?? selectedTextSize, scale: scale),
            weight: .regular
        )
        textView.font = font
        textView.typingAttributes = [
            .font: font,
            .foregroundColor: textView.textColor ?? effectiveColor
        ]

        let anchor = viewPoint(from: origin)
        let maximumWidth = max(font.pointSize * 2, imageFrame.maxX - anchor.x)
        let size = AnnotationTextMetrics.boundingSize(
            for: textView.string,
            font: font,
            maximumWidth: maximumWidth
        )
        let x = min(max(anchor.x, imageFrame.minX), imageFrame.maxX - size.width)
        let y = min(max(anchor.y - size.height, imageFrame.minY), imageFrame.maxY - size.height)
        textView.frame = CGRect(origin: CGPoint(x: x, y: y), size: size)
        activeTextOrigin = imagePoint(from: CGPoint(x: x, y: y + size.height))
        textView.textContainer?.containerSize = CGSize(width: size.width, height: CGFloat.greatestFiniteMagnitude)
        textView.needsDisplay = true
    }

    private func finishTextEntry(commit: Bool) {
        guard !isFinishingTextEntry, let textView = activeTextView else { return }
        isFinishingTextEntry = true
        let text = textView.string.trimmingCharacters(in: .whitespacesAndNewlines)
        let origin = activeTextOrigin
        let editingIndex = activeTextElementIndex
        let textSize = activeTextSize ?? selectedTextSize
        let textColor = textView.textColor ?? effectiveColor
        textView.delegate = nil
        textView.onFinish = nil
        textView.onAdjustSize = nil
        // Drop any registered text-editing undo actions before the view is
        // released; otherwise a later Cmd+Z can invoke them on freed memory.
        textView.undoManager?.removeAllActions(withTarget: textView)
        window?.undoManager?.removeAllActions(withTarget: textView)
        activeTextView = nil
        activeTextOrigin = nil
        activeTextElementIndex = nil
        activeTextSize = nil
        textView.removeFromSuperview()
        window?.makeFirstResponder(self)

        if commit, !text.isEmpty, let origin {
            registerUndo()
            if let editingIndex, elements.indices.contains(editingIndex) {
                elements[editingIndex] = .text(text, origin, textColor, textSize)
                selectedElementIndex = editingIndex
            } else {
                elements.append(.text(text, origin, textColor, textSize))
                selectedElementIndex = elements.count - 1
            }
            notifyElementsChanged()
        }
        isFinishingTextEntry = false
        needsDisplay = true
    }

    private func imagePoint(from viewPoint: CGPoint) -> CGPoint {
        let frame = imageFrame
        let x = min(max(viewPoint.x, frame.minX), frame.maxX)
        let y = min(max(viewPoint.y, frame.minY), frame.maxY)
        return CGPoint(
            x: visibleImageRect.minX + (x - frame.minX) / frame.width * visibleImageRect.width,
            y: visibleImageRect.minY + (y - frame.minY) / frame.height * visibleImageRect.height
        )
    }

    private func viewPoint(from imagePoint: CGPoint) -> CGPoint {
        let frame = imageFrame
        return CGPoint(
            x: frame.minX + (imagePoint.x - visibleImageRect.minX) / visibleImageRect.width * frame.width,
            y: frame.minY + (imagePoint.y - visibleImageRect.minY) / visibleImageRect.height * frame.height
        )
    }

    private func normalizedRect(from start: CGPoint, to end: CGPoint) -> CGRect {
        CGRect(x: min(start.x, end.x), y: min(start.y, end.y),
               width: abs(end.x - start.x), height: abs(end.y - start.y))
    }

    private var visibleImageRect: CGRect {
        cropRect ?? CGRect(x: 0, y: 0, width: sourceCGImage.width, height: sourceCGImage.height)
    }

    private var effectiveColor: NSColor {
        selectedColor.withAlphaComponent(selectedOpacity)
    }

    private var mosaicBrushDiameter: CGFloat {
        max(12, selectedWidth * 4)
    }

    private func adjustActiveToolSize(direction: Int) {
        if selectedTool == .text || activeTextView != nil {
            setTextSize(AnnotationTextMetrics.stepped(from: activeTextSize ?? selectedTextSize, direction: direction))
        } else {
            selectedWidth = min(22, max(1, selectedWidth + CGFloat(direction)))
            onWidthChanged?(selectedWidth)
            needsDisplay = true
        }
    }

    private func setTextSize(_ size: CGFloat) {
        selectedTextSize = AnnotationTextMetrics.clamped(size)
        if activeTextView != nil {
            activeTextSize = selectedTextSize
            updateTextEditorFontAndFrame()
        }
        onTextSizeChanged?(selectedTextSize)
        needsDisplay = true
    }

    private var currentSnapshot: AnnotationSnapshot {
        AnnotationSnapshot(elements: elements, cropRect: cropRect)
    }

    private func registerUndo() {
        registerUndo(currentSnapshot)
    }

    private func registerUndo(_ snapshot: AnnotationSnapshot) {
        undoSnapshots.append(snapshot)
        if undoSnapshots.count > 100 { undoSnapshots.removeFirst() }
        redoSnapshots.removeAll()
    }

    private func restore(_ snapshot: AnnotationSnapshot) {
        elements = snapshot.elements
        cropRect = snapshot.cropRect
        selectedElementIndex = nil
        endDirectManipulation()
        notifyElementsChanged()
    }

    private func draw(_ element: AnnotationElement, in destination: CGRect, sourceRect: CGRect) {
        func map(_ point: CGPoint) -> CGPoint {
            CGPoint(
                x: destination.minX + (point.x - sourceRect.minX) / sourceRect.width * destination.width,
                y: destination.minY + (point.y - sourceRect.minY) / sourceRect.height * destination.height
            )
        }
        func map(_ rect: CGRect) -> CGRect {
            let origin = map(rect.origin)
            return CGRect(
                x: origin.x,
                y: origin.y,
                width: rect.width / sourceRect.width * destination.width,
                height: rect.height / sourceRect.height * destination.height
            )
        }
        let strokeScale = destination.width / sourceRect.width

        func applyDash(_ path: NSBezierPath, dashed: Bool, width: CGFloat) {
            guard dashed else { return }
            let pattern: [CGFloat] = [max(4, width * 2), max(3, width * 1.4)]
            path.setLineDash(pattern, count: pattern.count, phase: 0)
        }

        switch element {
        case .rectangle(let rect, let color, let width, let dashed):
            color.setStroke()
            let path = NSBezierPath(roundedRect: map(rect), xRadius: 5, yRadius: 5)
            path.lineWidth = max(1, width * strokeScale)
            applyDash(path, dashed: dashed, width: path.lineWidth)
            path.stroke()
        case .ellipse(let rect, let color, let width, let dashed):
            color.setStroke()
            let path = NSBezierPath(ovalIn: map(rect))
            path.lineWidth = max(1, width * strokeScale)
            applyDash(path, dashed: dashed, width: path.lineWidth)
            path.stroke()
        case .arrow(let start, let end, let color, let width, let dashed):
            drawArrow(from: map(start), to: map(end), color: color, width: max(1, width * strokeScale), dashed: dashed)
        case .pen(let points, let color, let width, let dashed):
            guard let first = points.first else { return }
            color.setStroke()
            let path = NSBezierPath()
            path.lineCapStyle = .round
            path.lineJoinStyle = .round
            path.lineWidth = max(1, width * strokeScale)
            path.move(to: map(first))
            points.dropFirst().forEach { path.line(to: map($0)) }
            applyDash(path, dashed: dashed, width: path.lineWidth)
            path.stroke()
        case .highlighter(let points, let color, let width):
            guard let first = points.first else { return }
            color.setStroke()
            let path = NSBezierPath()
            path.lineCapStyle = .round
            path.lineJoinStyle = .round
            path.lineWidth = max(3, width * strokeScale)
            path.move(to: map(first))
            points.dropFirst().forEach { path.line(to: map($0)) }
            path.stroke()
        case .text(let text, let origin, let color, let size):
            let mappedOrigin = map(origin)
            let font = NSFont.systemFont(
                ofSize: AnnotationTextMetrics.renderedSize(for: size, scale: strokeScale),
                weight: .regular
            )
            let maximumWidth = max(font.pointSize * 2, destination.maxX - mappedOrigin.x)
            let textSize = AnnotationTextMetrics.boundingSize(for: text, font: font, maximumWidth: maximumWidth)
            let textRect = CGRect(
                x: mappedOrigin.x,
                y: mappedOrigin.y - textSize.height,
                width: textSize.width,
                height: textSize.height
            )
            (text as NSString).draw(
                with: textRect,
                options: [.usesLineFragmentOrigin, .usesFontLeading],
                attributes: [.font: font, .foregroundColor: color]
            )
        case .number(let number, let center, let color, let size):
            let mappedCenter = map(center)
            let diameter = AnnotationTextMetrics.renderedSize(for: size, scale: strokeScale)
            let circle = CGRect(x: mappedCenter.x - diameter / 2, y: mappedCenter.y - diameter / 2, width: diameter, height: diameter)
            color.setFill()
            NSBezierPath(ovalIn: circle).fill()
            let string = "\(number)" as NSString
            let attributes: [NSAttributedString.Key: Any] = [
                .font: NSFont.systemFont(ofSize: diameter * 0.56, weight: .bold),
                .foregroundColor: NSColor.white
            ]
            let textSize = string.size(withAttributes: attributes)
            string.draw(at: CGPoint(x: circle.midX - textSize.width / 2, y: circle.midY - textSize.height / 2), withAttributes: attributes)
        case .mosaic(let rect):
            mosaicImage?.draw(in: map(rect), from: rect, operation: .copy, fraction: 1)
        case .mosaicStroke(let points, let width):
            guard let first = points.first,
                  let context = NSGraphicsContext.current?.cgContext else { return }
            context.saveGState()
            let mappedWidth = max(4, width * strokeScale)
            if points.count == 1 {
                let center = map(first)
                context.addEllipse(in: CGRect(
                    x: center.x - mappedWidth / 2,
                    y: center.y - mappedWidth / 2,
                    width: mappedWidth,
                    height: mappedWidth
                ))
                context.clip()
            } else {
                context.beginPath()
                context.move(to: map(first))
                points.dropFirst().forEach { context.addLine(to: map($0)) }
                context.setLineWidth(mappedWidth)
                context.setLineCap(.round)
                context.setLineJoin(.round)
                context.replacePathWithStrokedPath()
                context.clip()
            }
            mosaicImage?.draw(in: destination, from: sourceRect, operation: .copy, fraction: 1)
            context.restoreGState()
        case .blur(let rect):
            blurImage?.draw(in: map(rect), from: rect, operation: .copy, fraction: 1)
        case .magnify(let rect, let factor):
            let target = map(rect)
            let sourceSize = CGSize(width: rect.width / factor, height: rect.height / factor)
            let source = CGRect(
                x: rect.midX - sourceSize.width / 2,
                y: rect.midY - sourceSize.height / 2,
                width: sourceSize.width,
                height: sourceSize.height
            ).intersection(CGRect(x: 0, y: 0, width: sourceCGImage.width, height: sourceCGImage.height))
            NSGraphicsContext.saveGraphicsState()
            NSBezierPath(ovalIn: target).addClip()
            sourceImage.draw(in: target, from: source, operation: .copy, fraction: 1)
            NSGraphicsContext.restoreGraphicsState()
            NSColor.white.setStroke()
            let border = NSBezierPath(ovalIn: target)
            border.lineWidth = max(2, 3 * strokeScale)
            border.stroke()
        }
    }

    /// Hit tolerance in image coordinates, equal to ~6 points on screen.
    private var hitTolerance: CGFloat {
        let viewScale = imageFrame.width / max(1, visibleImageRect.width)
        guard viewScale.isFinite, viewScale > 0 else { return 6 }
        return 6 / viewScale
    }

    /// Topmost element under `point`.
    /// - Parameter allowsInterior: `true` for the select tool, where the
    ///   (padded) bounding box of any element counts. `false` for drawing
    ///   tools: only the visible stroke or filled body counts, and the
    ///   already-selected element gets a slightly more forgiving band.
    private func hitTestElement(at point: CGPoint, allowsInterior: Bool) -> Int? {
        let tolerance = hitTolerance
        for index in elements.indices.reversed() {
            let band = index == selectedElementIndex ? tolerance * 1.6 : tolerance
            if isPoint(point, onVisiblePartOf: elements[index], tolerance: band) {
                return index
            }
        }
        guard allowsInterior else { return nil }
        for index in elements.indices.reversed() {
            if boundingRect(of: elements[index]).insetBy(dx: -tolerance, dy: -tolerance).contains(point) {
                return index
            }
        }
        return nil
    }

    private func isPoint(
        _ point: CGPoint,
        onVisiblePartOf element: AnnotationElement,
        tolerance: CGFloat
    ) -> Bool {
        switch element {
        case .rectangle(let rect, _, let width, _):
            return AnnotationHitGeometry.rectangleStrokeContains(
                point, rect: rect, lineWidth: width, tolerance: tolerance
            )
        case .ellipse(let rect, _, let width, _):
            return AnnotationHitGeometry.ellipseStrokeContains(
                point, rect: rect, lineWidth: width, tolerance: tolerance
            )
        case .arrow(let start, let end, _, let width, _):
            return AnnotationHitGeometry.arrowContains(
                point, start: start, end: end, lineWidth: width, tolerance: tolerance
            )
        case .pen(let points, _, let width, _), .highlighter(let points, _, let width):
            return AnnotationHitGeometry.polylineContains(
                point, points: points, lineWidth: width, tolerance: tolerance
            )
        case .mosaicStroke(let points, let width):
            return AnnotationHitGeometry.polylineContains(
                point, points: points, lineWidth: width, tolerance: tolerance
            )
        case .text, .number:
            return boundingRect(of: element).insetBy(dx: -tolerance, dy: -tolerance).contains(point)
        case .mosaic, .blur, .magnify:
            // Filled regions: their whole body is visible.
            return boundingRect(of: element).contains(point)
        }
    }

    private func beginDirectManipulation(index: Int, start: CGPoint, mode: DirectManipulationMode) {
        guard elements.indices.contains(index) else { return }
        // No undo step yet: a click that only selects must not pollute the
        // undo stack or clear redo. See `mouseUp`.
        snapshotBeforeManipulation = currentSnapshot
        didManipulateElement = false
        selectedElementIndex = index
        selectedElementBeforeDrag = elements[index]
        directManipulationMode = mode
        dragStart = start
        dragCurrent = start
        needsDisplay = true
    }

    private func endDirectManipulation() {
        dragStart = nil
        dragCurrent = nil
        selectedElementBeforeDrag = nil
        snapshotBeforeManipulation = nil
        didManipulateElement = false
        directManipulationMode = nil
    }

    private func resizeHandle(at viewPoint: CGPoint, for element: AnnotationElement) -> AnnotationResizeHandle? {
        let mappedRect = viewRect(from: boundingRect(of: element))
        return AnnotationResizeHandle.allCases.first { handle in
            let point = handle.point(in: mappedRect)
            return hypot(viewPoint.x - point.x, viewPoint.y - point.y) <= 9
        }
    }

    private func viewRect(from imageRect: CGRect) -> CGRect {
        let origin = viewPoint(from: imageRect.origin)
        return CGRect(
            x: origin.x,
            y: origin.y,
            width: imageRect.width / visibleImageRect.width * imageFrame.width,
            height: imageRect.height / visibleImageRect.height * imageFrame.height
        )
    }

    private func boundingRect(of element: AnnotationElement) -> CGRect {
        func pointsRect(_ points: [CGPoint]) -> CGRect {
            guard let first = points.first else { return .zero }
            return points.dropFirst().reduce(CGRect(origin: first, size: .zero)) { partial, point in
                partial.union(CGRect(origin: point, size: .zero))
            }
        }
        switch element {
        case .rectangle(let rect, _, _, _), .ellipse(let rect, _, _, _),
             .mosaic(let rect), .blur(let rect), .magnify(let rect, _):
            return rect
        case .arrow(let start, let end, _, _, _):
            return normalizedRect(from: start, to: end)
        case .pen(let points, _, let width, _), .highlighter(let points, _, let width):
            return pointsRect(points).insetBy(dx: -width / 2, dy: -width / 2)
        case .mosaicStroke(let points, let width):
            return pointsRect(points).insetBy(dx: -width / 2, dy: -width / 2)
        case .text(let text, let origin, _, let size):
            let font = NSFont.systemFont(ofSize: size, weight: .regular)
            let textSize = AnnotationTextMetrics.boundingSize(
                for: text,
                font: font,
                maximumWidth: max(font.pointSize * 2, CGFloat(sourceCGImage.width) - origin.x)
            )
            return CGRect(x: origin.x, y: origin.y - textSize.height, width: textSize.width, height: textSize.height)
        case .number(_, let center, _, let size):
            return CGRect(x: center.x - size / 2, y: center.y - size / 2, width: size, height: size)
        }
    }

    private func translated(_ element: AnnotationElement, dx: CGFloat, dy: CGFloat) -> AnnotationElement {
        func move(_ point: CGPoint) -> CGPoint { CGPoint(x: point.x + dx, y: point.y + dy) }
        func move(_ rect: CGRect) -> CGRect { rect.offsetBy(dx: dx, dy: dy) }
        return switch element {
        case .rectangle(let rect, let color, let width, let dashed): .rectangle(move(rect), color, width, dashed)
        case .ellipse(let rect, let color, let width, let dashed): .ellipse(move(rect), color, width, dashed)
        case .arrow(let start, let end, let color, let width, let dashed): .arrow(move(start), move(end), color, width, dashed)
        case .pen(let points, let color, let width, let dashed): .pen(points.map(move), color, width, dashed)
        case .highlighter(let points, let color, let width): .highlighter(points.map(move), color, width)
        case .text(let text, let origin, let color, let size): .text(text, move(origin), color, size)
        case .number(let number, let center, let color, let size): .number(number, move(center), color, size)
        case .mosaic(let rect): .mosaic(move(rect))
        case .mosaicStroke(let points, let width): .mosaicStroke(points.map(move), width)
        case .blur(let rect): .blur(move(rect))
        case .magnify(let rect, let factor): .magnify(move(rect), factor)
        }
    }

    private func scaled(_ element: AnnotationElement, around anchor: CGPoint, by scale: CGFloat) -> AnnotationElement {
        func point(_ value: CGPoint) -> CGPoint {
            CGPoint(
                x: anchor.x + (value.x - anchor.x) * scale,
                y: anchor.y + (value.y - anchor.y) * scale
            )
        }
        func rect(_ value: CGRect) -> CGRect {
            let first = point(CGPoint(x: value.minX, y: value.minY))
            let second = point(CGPoint(x: value.maxX, y: value.maxY))
            return normalizedRect(from: first, to: second)
        }
        return switch element {
        case .rectangle(let value, let color, let width, let dashed): .rectangle(rect(value), color, width * scale, dashed)
        case .ellipse(let value, let color, let width, let dashed): .ellipse(rect(value), color, width * scale, dashed)
        case .arrow(let start, let end, let color, let width, let dashed): .arrow(point(start), point(end), color, width * scale, dashed)
        case .pen(let points, let color, let width, let dashed): .pen(points.map(point), color, width * scale, dashed)
        case .highlighter(let points, let color, let width): .highlighter(points.map(point), color, width * scale)
        case .text(let text, let origin, let color, let size): .text(text, point(origin), color, size * scale)
        case .number(let number, let center, let color, let size): .number(number, point(center), color, size * scale)
        case .mosaic(let value): .mosaic(rect(value))
        case .mosaicStroke(let points, let width): .mosaicStroke(points.map(point), width * scale)
        case .blur(let value): .blur(rect(value))
        case .magnify(let value, let factor): .magnify(rect(value), factor)
        }
    }

    private func transformSelected(scale: CGFloat, rotate90: Bool) {
        guard let index = selectedElementIndex, elements.indices.contains(index) else { return }
        registerUndo()
        elements[index] = transformed(elements[index], scale: scale, rotate90: rotate90)
        needsDisplay = true
    }

    private func transformed(_ element: AnnotationElement, scale: CGFloat, rotate90: Bool) -> AnnotationElement {
        let bounds = boundingRect(of: element)
        let center = CGPoint(x: bounds.midX, y: bounds.midY)
        func point(_ value: CGPoint) -> CGPoint {
            var dx = (value.x - center.x) * scale
            var dy = (value.y - center.y) * scale
            if rotate90 { (dx, dy) = (dy, -dx) }
            return CGPoint(x: center.x + dx, y: center.y + dy)
        }
        func rect(_ value: CGRect) -> CGRect {
            let newCenter = point(CGPoint(x: value.midX, y: value.midY))
            let width = (rotate90 ? value.height : value.width) * scale
            let height = (rotate90 ? value.width : value.height) * scale
            return CGRect(x: newCenter.x - width / 2, y: newCenter.y - height / 2, width: width, height: height)
        }
        return switch element {
        case .rectangle(let value, let color, let width, let dashed): .rectangle(rect(value), color, width * scale, dashed)
        case .ellipse(let value, let color, let width, let dashed): .ellipse(rect(value), color, width * scale, dashed)
        case .arrow(let start, let end, let color, let width, let dashed): .arrow(point(start), point(end), color, width * scale, dashed)
        case .pen(let points, let color, let width, let dashed): .pen(points.map(point), color, width * scale, dashed)
        case .highlighter(let points, let color, let width): .highlighter(points.map(point), color, width * scale)
        case .text(let text, let origin, let color, let size): .text(text, point(origin), color, size * scale)
        case .number(let number, let value, let color, let size): .number(number, point(value), color, size * scale)
        case .mosaic(let value): .mosaic(rect(value))
        case .mosaicStroke(let points, let width): .mosaicStroke(points.map(point), width * scale)
        case .blur(let value): .blur(rect(value))
        case .magnify(let value, let factor): .magnify(rect(value), factor)
        }
    }

    private func drawSelection(for element: AnnotationElement, in destination: CGRect, sourceRect: CGRect) {
        let rect = boundingRect(of: element)
        let mapped = CGRect(
            x: destination.minX + (rect.minX - sourceRect.minX) / sourceRect.width * destination.width,
            y: destination.minY + (rect.minY - sourceRect.minY) / sourceRect.height * destination.height,
            width: rect.width / sourceRect.width * destination.width,
            height: rect.height / sourceRect.height * destination.height
        ).insetBy(dx: -4, dy: -4)
        NSColor.controlAccentColor.setStroke()
        let selection = NSBezierPath(rect: mapped)
        let pattern: [CGFloat] = [5, 4]
        selection.setLineDash(pattern, count: pattern.count, phase: 0)
        selection.lineWidth = 1.5
        selection.stroke()
        for point in [
            CGPoint(x: mapped.minX, y: mapped.minY), CGPoint(x: mapped.maxX, y: mapped.minY),
            CGPoint(x: mapped.minX, y: mapped.maxY), CGPoint(x: mapped.maxX, y: mapped.maxY)
        ] {
            NSColor.white.setFill()
            NSColor.controlAccentColor.setStroke()
            let handle = NSBezierPath(ovalIn: CGRect(x: point.x - 4, y: point.y - 4, width: 8, height: 8))
            handle.fill()
            handle.stroke()
        }
    }

    private func drawArrow(from start: CGPoint, to end: CGPoint, color: NSColor, width: CGFloat, dashed: Bool) {
        color.setFill()
        let points = TaperedArrowGeometry.polygon(from: start, to: end, width: width)
        guard let first = points.first else { return }
        if dashed, points.count == 7, hypot(end.x - start.x, end.y - start.y) > 0.5 {
            // Dashed arrows keep a solid head and get a dashed shaft; a
            // tapered filled polygon cannot carry a dash pattern.
            let neck = CGPoint(
                x: (points[1].x + points[5].x) / 2,
                y: (points[1].y + points[5].y) / 2
            )
            color.setStroke()
            let shaft = NSBezierPath()
            shaft.lineWidth = max(1.5, width * 0.9)
            shaft.lineCapStyle = .butt
            let pattern: [CGFloat] = [max(4, shaft.lineWidth * 2), max(3, shaft.lineWidth * 1.4)]
            shaft.setLineDash(pattern, count: pattern.count, phase: 0)
            shaft.move(to: start)
            shaft.line(to: neck)
            shaft.stroke()

            let head = NSBezierPath()
            head.move(to: points[2])
            head.line(to: points[3])
            head.line(to: points[4])
            head.close()
            head.fill()
            return
        }
        let path = NSBezierPath()
        path.move(to: first)
        points.dropFirst().forEach { path.line(to: $0) }
        path.close()
        path.fill()
    }

    private func notifyElementsChanged() {
        onElementsChanged?(!elements.isEmpty)
    }

    private func filteredImage(name: String, parameters: [String: Any]) -> NSImage? {
        let input = CIImage(cgImage: sourceCGImage)
        guard let filter = CIFilter(name: name) else { return nil }
        filter.setValue(input, forKey: kCIInputImageKey)
        parameters.forEach { filter.setValue($0.value, forKey: $0.key) }
        guard let output = filter.outputImage?.cropped(to: input.extent),
              let image = CIContext(options: [.useSoftwareRenderer: false]).createCGImage(output, from: input.extent) else {
            return nil
        }
        return NSImage(cgImage: image, size: sourceImage.size)
    }
}
