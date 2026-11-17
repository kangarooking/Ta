@preconcurrency import ScreenCaptureKit
import AppKit
import CoreGraphics

enum ScreenCaptureError: LocalizedError {
    case displayUnavailable
    case windowUnavailable
    case invalidSelection

    var errorDescription: String? {
        switch self {
        case .displayUnavailable:
            return "找不到对应的显示器，请重新选择。"
        case .windowUnavailable:
            return "找不到对应的窗口，请重新选择。"
        case .invalidSelection:
            return "选区无效，请重新框选。"
        }
    }
}

enum FrozenDisplayCropper {
    static func pixelRect(
        for selection: CaptureSelection,
        imageWidth: Int,
        imageHeight: Int
    ) -> CGRect {
        let clipped = selection.globalRect.intersection(selection.screenFrame)
        guard clipped.width >= 1,
              clipped.height >= 1,
              selection.screenFrame.width > 0,
              selection.screenFrame.height > 0,
              imageWidth > 0,
              imageHeight > 0 else {
            return .null
        }

        let scaleX = CGFloat(imageWidth) / selection.screenFrame.width
        let scaleY = CGFloat(imageHeight) / selection.screenFrame.height
        let localX = clipped.minX - selection.screenFrame.minX
        let localTop = selection.screenFrame.maxY - clipped.maxY
        let pixels = CGRect(
            x: localX * scaleX,
            y: localTop * scaleY,
            width: clipped.width * scaleX,
            height: clipped.height * scaleY
        ).integral
        return pixels.intersection(
            CGRect(x: 0, y: 0, width: imageWidth, height: imageHeight)
        )
    }

    static func crop(_ image: CGImage, to selection: CaptureSelection) throws -> CGImage {
        let pixelRect = pixelRect(
            for: selection,
            imageWidth: image.width,
            imageHeight: image.height
        )
        guard !pixelRect.isNull,
              pixelRect.width >= 1,
              pixelRect.height >= 1,
              let cropped = image.cropping(to: pixelRect) else {
            throw ScreenCaptureError.invalidSelection
        }
        return cropped
    }
}

/// Ta's own transient chrome (result toast, long-capture HUD, …) must never be
/// baked into a screenshot. Windows registered here are excluded from every
/// display capture. Pinned images are deliberately *not* registered: users
/// expect to be able to capture them.
final class CaptureExclusionRegistry: @unchecked Sendable {
    static let shared = CaptureExclusionRegistry()

    private let lock = NSLock()
    private var identifiers = Set<CGWindowID>()

    @MainActor
    func register(_ window: NSWindow) {
        let number = window.windowNumber
        guard number > 0 else { return }
        lock.lock()
        identifiers.insert(CGWindowID(number))
        lock.unlock()
    }

    @MainActor
    func unregister(_ window: NSWindow) {
        let number = window.windowNumber
        guard number > 0 else { return }
        lock.lock()
        identifiers.remove(CGWindowID(number))
        lock.unlock()
    }

    var windowIDs: Set<CGWindowID> {
        lock.lock()
        defer { lock.unlock() }
        return identifiers
    }
}

/// Captures the same display region repeatedly without re-enumerating
/// shareable content for every frame, which is what made dense long-capture
/// sampling too slow to follow a manual scroll.
final class ScrollingRegionCapturer: @unchecked Sendable {
    private let filter: SCContentFilter
    private let configuration: SCStreamConfiguration
    private let excludedSnapshot: Set<CGWindowID>

    init(
        filter: SCContentFilter,
        configuration: SCStreamConfiguration,
        excludedSnapshot: Set<CGWindowID>
    ) {
        self.filter = filter
        self.configuration = configuration
        self.excludedSnapshot = excludedSnapshot
    }

    /// True when Ta opened or closed one of its own overlay windows since the
    /// filter was built; the caller should build a new capturer.
    var isStale: Bool {
        CaptureExclusionRegistry.shared.windowIDs != excludedSnapshot
    }

    func capture() async throws -> CGImage {
        try await SCScreenshotManager.captureImage(
            contentFilter: filter,
            configuration: configuration
        )
    }
}

struct ScreenCaptureService: Sendable {
    func makeRegionCapturer(
        for selection: CaptureSelection,
        pixelScale: CGFloat? = nil
    ) async throws -> ScrollingRegionCapturer {
        let screenFrame = selection.screenFrame
        let clipped = selection.globalRect.intersection(screenFrame)
        guard clipped.width >= 1, clipped.height >= 1 else {
            throw ScreenCaptureError.invalidSelection
        }
        let localRect = CGRect(
            x: clipped.minX - screenFrame.minX,
            y: screenFrame.maxY - clipped.maxY,
            width: clipped.width,
            height: clipped.height
        )
        let content = try await SCShareableContent.excludingDesktopWindows(
            false,
            onScreenWindowsOnly: true
        )
        guard let display = content.displays.first(where: { $0.displayID == selection.displayID })
        else {
            throw ScreenCaptureError.displayUnavailable
        }
        let registered = CaptureExclusionRegistry.shared.windowIDs
        let excludedIDs = Set(selection.excludedWindowIDs).union(registered)
        let excludedWindows = content.windows.filter { excludedIDs.contains($0.windowID) }
        let filter = SCContentFilter(display: display, excludingWindows: excludedWindows)
        let configuration = Self.configuration(
            size: localRect.size,
            sourceRect: localRect,
            pixelScale: pixelScale ?? selection.backingScaleFactor,
            showsCursor: false,
            ignoreWindowShadows: true
        )
        return ScrollingRegionCapturer(
            filter: filter,
            configuration: configuration,
            excludedSnapshot: registered
        )
    }

    func capture(_ selection: CaptureSelection) async throws -> CGImage {
        if let frozenDisplayImage = selection.frozenDisplayImage {
            return try FrozenDisplayCropper.crop(frozenDisplayImage, to: selection)
        }

        let screenFrame = selection.screenFrame
        let clipped = selection.globalRect.intersection(screenFrame)
        guard clipped.width >= 1, clipped.height >= 1 else {
            throw ScreenCaptureError.invalidSelection
        }

        let localRect = CGRect(
            x: clipped.minX - screenFrame.minX,
            y: screenFrame.maxY - clipped.maxY,
            width: clipped.width,
            height: clipped.height
        )
        let scale = selection.backingScaleFactor

        return try await captureDisplay(
            displayID: selection.displayID,
            sourceRect: localRect,
            pixelScale: scale,
            excludedWindowIDs: selection.excludedWindowIDs,
            showsCursor: false
        )
    }

    func captureDisplay(
        displayID: CGDirectDisplayID,
        sourceRect: CGRect? = nil,
        pixelScale: CGFloat,
        excludedWindowIDs: [CGWindowID] = [],
        showsCursor: Bool = false
    ) async throws -> CGImage {
        let content = try await SCShareableContent.excludingDesktopWindows(
            false,
            onScreenWindowsOnly: true
        )
        guard let display = content.displays.first(where: { $0.displayID == displayID }) else {
            throw ScreenCaptureError.displayUnavailable
        }
        let excludedIDs = Set(excludedWindowIDs)
            .union(CaptureExclusionRegistry.shared.windowIDs)
        let excludedWindows = content.windows.filter { excludedIDs.contains($0.windowID) }
        let filter = SCContentFilter(display: display, excludingWindows: excludedWindows)
        let captureRect = sourceRect ?? CGRect(origin: .zero, size: display.frame.size)
        guard captureRect.width >= 1, captureRect.height >= 1 else {
            throw ScreenCaptureError.invalidSelection
        }
        let configuration = Self.configuration(
            size: captureRect.size,
            sourceRect: sourceRect,
            pixelScale: pixelScale,
            showsCursor: showsCursor,
            ignoreWindowShadows: true
        )

        return try await SCScreenshotManager.captureImage(
            contentFilter: filter,
            configuration: configuration
        )
    }

    func captureWindow(
        windowID: CGWindowID,
        pixelScale: CGFloat,
        showsCursor: Bool = false
    ) async throws -> CGImage {
        let content = try await SCShareableContent.excludingDesktopWindows(
            false,
            onScreenWindowsOnly: true
        )
        guard let window = content.windows.first(where: { $0.windowID == windowID }) else {
            throw ScreenCaptureError.windowUnavailable
        }
        let filter = SCContentFilter(desktopIndependentWindow: window)
        let configuration = Self.configuration(
            size: window.frame.size,
            sourceRect: nil,
            pixelScale: max(1, pixelScale),
            showsCursor: showsCursor,
            ignoreWindowShadows: true
        )

        return try await SCScreenshotManager.captureImage(
            contentFilter: filter,
            configuration: configuration
        )
    }

    private static func configuration(
        size: CGSize,
        sourceRect: CGRect?,
        pixelScale: CGFloat,
        showsCursor: Bool,
        ignoreWindowShadows: Bool
    ) -> SCStreamConfiguration {
        let configuration = SCStreamConfiguration()
        if let sourceRect { configuration.sourceRect = sourceRect }
        configuration.width = max(1, Int((size.width * pixelScale).rounded()))
        configuration.height = max(1, Int((size.height * pixelScale).rounded()))
        configuration.scalesToFit = false
        configuration.showsCursor = showsCursor
        configuration.ignoreShadowsSingleWindow = ignoreWindowShadows
        return configuration
    }
}
