import AppKit
import CoreGraphics
import AIScreenshotCore

struct CaptureSelection: @unchecked Sendable {
    let globalRect: CGRect
    let screenFrame: CGRect
    let displayID: CGDirectDisplayID
    let backingScaleFactor: CGFloat
    let excludedWindowIDs: [CGWindowID]
    let sourceApplicationProcessID: pid_t?
    let frozenDisplayImage: CGImage?

    init(
        globalRect: CGRect,
        screenFrame: CGRect,
        displayID: CGDirectDisplayID,
        backingScaleFactor: CGFloat,
        excludedWindowIDs: [CGWindowID] = [],
        sourceApplicationProcessID: pid_t? = nil,
        frozenDisplayImage: CGImage? = nil
    ) {
        self.globalRect = globalRect
        self.screenFrame = screenFrame
        self.displayID = displayID
        self.backingScaleFactor = backingScaleFactor
        self.excludedWindowIDs = excludedWindowIDs
        self.sourceApplicationProcessID = sourceApplicationProcessID
        self.frozenDisplayImage = frozenDisplayImage
    }
}

@MainActor
struct SelectionOverlayStartContext {
    let screen: NSScreen
    let displayID: CGDirectDisplayID
    let sourceProcessID: pid_t?
    let snapTargets: [WindowSnapTarget]
}

@MainActor
final class SelectionOverlayController {
    private var panel: SelectionOverlayPanel?
    private var eventMonitor: Any?
    private var isStarting = false
    private var isFinishing = false

    func prepareStartContext() -> SelectionOverlayStartContext? {
        guard let screen = Self.screenUnderPointer() else { return nil }
        let screenNumber = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber
        let displayID = CGDirectDisplayID(screenNumber?.uint32Value ?? 0)
        let sourceProcessID = NSWorkspace.shared.frontmostApplication?.processIdentifier
        let targets = WindowSnapService().targets(
            on: screen,
            frontmostProcessID: sourceProcessID
        )
        return SelectionOverlayStartContext(
            screen: screen,
            displayID: displayID,
            sourceProcessID: sourceProcessID,
            snapTargets: targets
        )
    }

    func begin(
        showsActionToolbar: Bool,
        presetRect: CGRect? = nil,
        completion: @escaping (CaptureSelection?, CaptureQuickAction?) -> Void
    ) {
        guard panel == nil, !isStarting else { return }
        guard let context = prepareStartContext() else {
            completion(nil, nil)
            return
        }
        begin(
            context: context,
            frozenDisplayImage: nil,
            showsActionToolbar: showsActionToolbar,
            presetRect: presetRect,
            completion: completion
        )
    }

    func begin(
        context: SelectionOverlayStartContext,
        frozenDisplayImage: CGImage?,
        showsActionToolbar: Bool,
        optionRevealsActionToolbar: Bool = false,
        presetRect: CGRect? = nil,
        completion: @escaping (CaptureSelection?, CaptureQuickAction?) -> Void
    ) {
        guard panel == nil, !isStarting else { return }
        isStarting = true
        isFinishing = false
        NSCursor.crosshair.set()

        showPanel(
            screen: context.screen,
            displayID: context.displayID,
            sourceProcessID: context.sourceProcessID,
            snapTargets: context.snapTargets,
            frozenDisplayImage: frozenDisplayImage,
            showsActionToolbar: showsActionToolbar,
            optionRevealsActionToolbar: optionRevealsActionToolbar,
            presetRect: presetRect,
            completion: completion
        )
    }

    private func showPanel(
        screen: NSScreen,
        displayID: CGDirectDisplayID,
        sourceProcessID: pid_t?,
        snapTargets: [WindowSnapTarget],
        frozenDisplayImage: CGImage?,
        showsActionToolbar: Bool,
        optionRevealsActionToolbar: Bool,
        presetRect: CGRect?,
        completion: @escaping (CaptureSelection?, CaptureQuickAction?) -> Void
    ) {
        guard panel == nil else { return }
        isStarting = false

        let panel = SelectionOverlayPanel(
            contentRect: screen.frame,
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        panel.level = .screenSaver
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = false
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.animationBehavior = .none
        panel.hidesOnDeactivate = false
        panel.acceptsMouseMovedEvents = true
        panel.ignoresMouseEvents = false

        let overlay = SelectionOverlayView(frame: CGRect(origin: .zero, size: screen.frame.size))
        overlay.showsActionToolbar = showsActionToolbar
        overlay.optionRevealsActionToolbar = optionRevealsActionToolbar
        overlay.setFrozenDisplayImage(frozenDisplayImage)
        overlay.onFinish = { [weak self, weak panel, weak overlay] localRect, action in
            guard let self, let panel else { return }
            let globalRect = localRect.map { panel.convertToScreen($0) }
            if let globalRect {
                let selection = CaptureSelection(
                    globalRect: globalRect,
                    screenFrame: screen.frame,
                    displayID: displayID,
                    backingScaleFactor: screen.backingScaleFactor,
                    excludedWindowIDs: [CGWindowID(panel.windowNumber)],
                    sourceApplicationProcessID: sourceProcessID,
                    frozenDisplayImage: frozenDisplayImage
                )
                if action == .edit {
                    overlay?.prepareForDeferredDismissal()
                } else {
                    finish()
                }
                completion(selection, action)
            } else {
                finish()
                completion(nil, nil)
            }
        }
        panel.contentView = overlay
        self.panel = panel
        isFinishing = false

        let targets = snapTargets.map { target in
            WindowSnapTarget(
                windowID: target.windowID,
                frame: panel.convertFromScreen(target.frame),
                zOrder: target.zOrder
            )
        }
        overlay.configureSnapTargets(targets)

        if let presetRect {
            overlay.showPresetSelection(presetRect.intersection(overlay.bounds))
        }

        eventMonitor = NSEvent.addLocalMonitorForEvents(matching: [.rightMouseDown]) { [weak overlay, weak panel] event in
            guard event.window === panel else { return event }
            overlay?.cancelSelection()
            return nil
        }

        // Keep the current app and Space untouched. This panel captures input
        // without making Ta the active application.
        panel.orderFrontRegardless()
        panel.makeKeyAndOrderFront(nil)
        panel.makeFirstResponder(overlay)
        panel.invalidateCursorRects(for: overlay)
        overlay.activateInitialCursor()
    }

    func dismiss() {
        finish()
    }

    private func finish() {
        guard !isFinishing else { return }
        isFinishing = true
        isStarting = false
        if let eventMonitor {
            NSEvent.removeMonitor(eventMonitor)
            self.eventMonitor = nil
        }
        panel?.orderOut(nil)
        panel?.close()
        panel = nil
        NSCursor.arrow.set()
    }

    private static func screenUnderPointer() -> NSScreen? {
        let mouseLocation = NSEvent.mouseLocation
        return NSScreen.screens.first { NSMouseInRect(mouseLocation, $0.frame, false) }
            ?? NSScreen.main
            ?? NSScreen.screens.first
    }
}

private final class SelectionOverlayPanel: NSPanel {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }
}
