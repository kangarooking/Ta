@preconcurrency import ScreenCaptureKit
import AppKit
import CoreGraphics
import Foundation

enum TaAgentCaptureError: Error, Equatable, Sendable {
    case screenPermissionRequired
    case targetNotFound
    case targetChanged
    case invalidRegion
    case captureFailed(String)
}

enum TaAgentCaptureTarget: Equatable, Sendable {
    case display(id: CGDirectDisplayID? = nil)
    case frontmost
    case window(id: CGWindowID)
    case region(displayID: CGDirectDisplayID, globalRect: CGRect)
}

struct TaAgentDisplayTarget: Equatable, Sendable {
    let id: CGDirectDisplayID
    let frame: CGRect
    let pixelScale: CGFloat
    let isMain: Bool
}

struct TaAgentWindowTarget: Equatable, Sendable {
    let id: CGWindowID
    let ownerProcessID: pid_t
    let appName: String
    let bundleIdentifier: String?
    let title: String?
    let frame: CGRect
    let zOrder: Int
}

struct TaAgentCaptureSnapshot: Equatable, Sendable {
    let displays: [TaAgentDisplayTarget]
    let windows: [TaAgentWindowTarget]
    let frontmostProcessID: pid_t?
}

struct TaAgentResolvedCaptureRequest: Equatable, Sendable {
    enum Kind: String, Equatable, Sendable {
        case display
        case window
        case region
    }

    let kind: Kind
    let displayID: CGDirectDisplayID?
    let windowID: CGWindowID?
    let sourceRect: CGRect?
    let pixelScale: CGFloat
    let showsCursor: Bool
}

struct TaAgentResolvedTarget: Equatable, Sendable {
    let kind: TaAgentResolvedCaptureRequest.Kind
    let displayID: CGDirectDisplayID?
    let windowID: CGWindowID?
    let ownerProcessID: pid_t?
    let appName: String?
    let bundleIdentifier: String?
    let title: String?
    let frame: CGRect
}

struct TaAgentCaptureResult: @unchecked Sendable {
    let image: CGImage
    let target: TaAgentResolvedTarget
}

struct TaAgentPreparedCapture: Equatable, Sendable {
    let request: TaAgentResolvedCaptureRequest
    let target: TaAgentResolvedTarget
    let visibleBundleIdentifiers: Set<String>
}

protocol TaAgentCaptureBackend: Sendable {
    func snapshot(frontmostProcessID: pid_t?) async throws -> TaAgentCaptureSnapshot
    func capture(_ request: TaAgentResolvedCaptureRequest) async throws -> CGImage
}

struct TaAgentCaptureService: Sendable {
    private let backend: any TaAgentCaptureBackend
    private let frontmostProcessID: @Sendable () async -> pid_t?

    init(
        backend: any TaAgentCaptureBackend = ScreenCaptureAgentBackend(),
        frontmostProcessID: @escaping @Sendable () async -> pid_t? = {
            await MainActor.run { NSWorkspace.shared.frontmostApplication?.processIdentifier }
        }
    ) {
        self.backend = backend
        self.frontmostProcessID = frontmostProcessID
    }

    func snapshot() async throws -> TaAgentCaptureSnapshot {
        let frozenProcessID = await frontmostProcessID()
        return try await backend.snapshot(frontmostProcessID: frozenProcessID)
    }

    func capture(_ target: TaAgentCaptureTarget) async throws -> TaAgentCaptureResult {
        let prepared = try await prepare(target)
        return try await capture(prepared)
    }

    func prepare(_ target: TaAgentCaptureTarget) async throws -> TaAgentPreparedCapture {
        // Freeze the foreground PID before the first ScreenCaptureKit await. This
        // prevents Ta or another app transition from changing the requested target.
        let frozenProcessID = await frontmostProcessID()
        let snapshot = try await backend.snapshot(frontmostProcessID: frozenProcessID)
        let resolution = try resolve(target, snapshot: snapshot)
        let visibleBundleIdentifiers: Set<String>
        if let bundleIdentifier = resolution.target.bundleIdentifier {
            visibleBundleIdentifiers = [bundleIdentifier]
        } else {
            visibleBundleIdentifiers = Set(snapshot.windows.compactMap { window in
                // Status-bar items and other sliver windows show no content
                // worth protecting; counting them would make a menu-bar
                // password manager block every display capture.
                guard window.frame.height > 40,
                      window.frame.intersects(resolution.target.frame) else { return nil }
                return window.bundleIdentifier
            })
        }
        return TaAgentPreparedCapture(
            request: resolution.request,
            target: resolution.target,
            visibleBundleIdentifiers: visibleBundleIdentifiers
        )
    }

    func capture(_ prepared: TaAgentPreparedCapture) async throws -> TaAgentCaptureResult {
        do {
            let image = try await backend.capture(prepared.request)
            return TaAgentCaptureResult(image: image, target: prepared.target)
        } catch let error as TaAgentCaptureError {
            throw error
        } catch let error as ScreenCaptureError {
            switch error {
            case .displayUnavailable, .windowUnavailable:
                throw TaAgentCaptureError.targetChanged
            case .invalidSelection:
                throw TaAgentCaptureError.invalidRegion
            }
        } catch {
            throw TaAgentCaptureError.captureFailed(error.localizedDescription)
        }
    }

    private func resolve(
        _ target: TaAgentCaptureTarget,
        snapshot: TaAgentCaptureSnapshot
    ) throws -> (request: TaAgentResolvedCaptureRequest, target: TaAgentResolvedTarget) {
        switch target {
        case .display(let requestedID):
            let display = requestedID.flatMap { id in snapshot.displays.first { $0.id == id } }
                ?? (requestedID == nil ? snapshot.displays.first(where: \.isMain) : nil)
            guard let display else { throw TaAgentCaptureError.targetNotFound }
            return displayResolution(display, sourceRect: nil, kind: .display)

        case .frontmost:
            guard let processID = snapshot.frontmostProcessID,
                  let window = snapshot.windows
                    .filter({ $0.ownerProcessID == processID })
                    .min(by: { $0.zOrder < $1.zOrder }) else {
                throw TaAgentCaptureError.targetNotFound
            }
            return windowResolution(window, displays: snapshot.displays)

        case .window(let id):
            guard let window = snapshot.windows.first(where: { $0.id == id }) else {
                throw TaAgentCaptureError.targetNotFound
            }
            return windowResolution(window, displays: snapshot.displays)

        case .region(let displayID, let globalRect):
            guard let display = snapshot.displays.first(where: { $0.id == displayID }) else {
                throw TaAgentCaptureError.targetNotFound
            }
            let clipped = globalRect.standardized.intersection(display.frame)
            guard clipped.width >= 1, clipped.height >= 1 else {
                throw TaAgentCaptureError.invalidRegion
            }
            let localRect = CGRect(
                x: clipped.minX - display.frame.minX,
                y: clipped.minY - display.frame.minY,
                width: clipped.width,
                height: clipped.height
            )
            return displayResolution(display, sourceRect: localRect, kind: .region)
        }
    }

    private func displayResolution(
        _ display: TaAgentDisplayTarget,
        sourceRect: CGRect?,
        kind: TaAgentResolvedCaptureRequest.Kind
    ) -> (request: TaAgentResolvedCaptureRequest, target: TaAgentResolvedTarget) {
        let globalFrame = sourceRect.map {
            CGRect(
                x: display.frame.minX + $0.minX,
                y: display.frame.minY + $0.minY,
                width: $0.width,
                height: $0.height
            )
        } ?? display.frame
        return (
            TaAgentResolvedCaptureRequest(
                kind: kind,
                displayID: display.id,
                windowID: nil,
                sourceRect: sourceRect,
                pixelScale: display.pixelScale,
                showsCursor: false
            ),
            TaAgentResolvedTarget(
                kind: kind,
                displayID: display.id,
                windowID: nil,
                ownerProcessID: nil,
                appName: nil,
                bundleIdentifier: nil,
                title: nil,
                frame: globalFrame
            )
        )
    }

    private func windowResolution(
        _ window: TaAgentWindowTarget,
        displays: [TaAgentDisplayTarget]
    ) -> (request: TaAgentResolvedCaptureRequest, target: TaAgentResolvedTarget) {
        let pixelScale = displays
            .filter { $0.frame.intersects(window.frame) }
            .map(\.pixelScale)
            .max() ?? 1
        return (
            TaAgentResolvedCaptureRequest(
                kind: .window,
                displayID: nil,
                windowID: window.id,
                sourceRect: nil,
                pixelScale: max(1, pixelScale),
                showsCursor: false
            ),
            TaAgentResolvedTarget(
                kind: .window,
                displayID: nil,
                windowID: window.id,
                ownerProcessID: window.ownerProcessID,
                appName: window.appName,
                bundleIdentifier: window.bundleIdentifier,
                title: window.title,
                frame: window.frame
            )
        )
    }
}

private struct ScreenCaptureAgentBackend: TaAgentCaptureBackend {
    private let captureService = ScreenCaptureService()

    func snapshot(frontmostProcessID: pid_t?) async throws -> TaAgentCaptureSnapshot {
        let screenScales: [CGDirectDisplayID: CGFloat] = await MainActor.run {
            var scales: [CGDirectDisplayID: CGFloat] = [:]
            for screen in NSScreen.screens {
                guard let displayID = screen.deviceDescription[
                    NSDeviceDescriptionKey("NSScreenNumber")
                ] as? CGDirectDisplayID else { continue }
                scales[displayID] = screen.backingScaleFactor
            }
            return scales
        }
        let content = try await SCShareableContent.excludingDesktopWindows(
            false,
            onScreenWindowsOnly: true
        )
        let displays = content.displays.map { display in
            let fallbackScale = display.frame.width > 0
                ? CGFloat(display.width) / display.frame.width
                : 1
            return TaAgentDisplayTarget(
                id: display.displayID,
                frame: display.frame,
                pixelScale: max(1, screenScales[display.displayID] ?? fallbackScale),
                isMain: display.displayID == CGMainDisplayID()
            )
        }
        let windows = content.windows.enumerated().compactMap { index, window -> TaAgentWindowTarget? in
            guard let application = window.owningApplication,
                  window.frame.width >= 1,
                  window.frame.height >= 1 else { return nil }
            return TaAgentWindowTarget(
                id: window.windowID,
                ownerProcessID: application.processID,
                appName: application.applicationName,
                bundleIdentifier: application.bundleIdentifier,
                title: window.title,
                frame: window.frame,
                zOrder: index
            )
        }
        return TaAgentCaptureSnapshot(
            displays: displays,
            windows: windows,
            frontmostProcessID: frontmostProcessID
        )
    }

    func capture(_ request: TaAgentResolvedCaptureRequest) async throws -> CGImage {
        switch request.kind {
        case .display, .region:
            guard let displayID = request.displayID else {
                throw TaAgentCaptureError.targetChanged
            }
            return try await captureService.captureDisplay(
                displayID: displayID,
                sourceRect: request.sourceRect,
                pixelScale: request.pixelScale,
                showsCursor: request.showsCursor
            )
        case .window:
            guard let windowID = request.windowID else {
                throw TaAgentCaptureError.targetChanged
            }
            return try await captureService.captureWindow(
                windowID: windowID,
                pixelScale: request.pixelScale,
                showsCursor: request.showsCursor
            )
        }
    }
}
