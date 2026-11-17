import AIScreenshotCore
import AppKit
import SwiftUI

enum ScrollingCaptureSessionOutcome {
  case cancelled
  case completed(
    images: [CGImage], acceptedFrames: Int, skippedFrames: Int, reviewedSeams: Int,
    outputNote: String?)
  case failed(String)
}

enum AutoScrollDirection: Equatable {
  case down
  case up

  var constraint: VerticalScrollConstraint { self == .down ? .downwardOnly : .upwardOnly }
  var sign: Int { self == .down ? 1 : -1 }
  var endName: String { self == .down ? "底部" : "顶部" }
}

@MainActor
final class ScrollingCaptureSessionController {
  private let captureService = ScreenCaptureService()
  private let stitcher = ScrollingImageStitcher()
  /// Compares each frame with the one captured immediately before it.
  private let frameMotionDetector = ViewportMotionDetector()
  private let seamReview = ScrollingSeamReviewWindowController()
  private var captureTask: Task<Void, Never>?
  private var panel: NSPanel?
  private var hudModel: ScrollingCaptureHUDModel?
  private var completion: ((ScrollingCaptureSessionOutcome) -> Void)?
  private var acceptedFrames = 0
  private var skippedFrames = 0
  private var isInRejectedStreak = false
  private var isPaused = false
  private var isFinishing = false
  private var isAutoScrolling = false
  private var autoScrollDirection = AutoScrollDirection.down
  private var activeSelection: CaptureSelection?
  private var scrollTarget: AccessibilityScrollTarget?
  private var pendingScrollProgress: AccessibilityScrollProgress?
  private var pendingSawMovement = false
  private var expectedScrollDistancePoints = 0
  private var autoScrollProgress = AutoScrollProgressTracker(maximumAttemptsWithoutProgress: 3)
  private var gesturesWithProgress = 0
  private var retargetCount = 0
  private var nextAutoScrollDate = Date.distantPast
  private var autoScrollSettleDeadline = Date.distantPast
  private var endStableSince: Date?
  private var targetRefreshDate: Date?
  private var stickyStatus: String?
  private var lastImage: CGImage?
  private var debugRecorder: LongCaptureDebugRecorder?
  static let standardAutoScrollDelayMilliseconds = 300
  static let standardAutoScrollDelay: TimeInterval = 0.30
  static let maximumAutoScrollSettleTime: TimeInterval = 1.20
  /// Extra time a gesture may keep animating before it is force-resolved.
  static let maximumAutoScrollAnimationTime: TimeInterval = 1.50
  /// Dense sampling is what lets a manual scroll be followed reliably: at
  /// this interval a normal wheel/trackpad scroll moves well under one
  /// viewport between frames, so consecutive frames always overlap.
  static let captureIntervalMilliseconds = 70
  /// The end of the content must stay visually stable this long before the
  /// capture is declared complete (covers lazy loading and bounce).
  static let endStabilityInterval: TimeInterval = 0.9
  static let maximumPartPixelHeight = 30_000

  enum NoProgressVerdict: Equatable {
    /// Content moved earlier and no longer does: the end was reached.
    case reachedEnd
    /// The tracked scrollbar says more content exists; lock the target again.
    case retarget
    /// No gesture ever moved the content. This is not "done".
    case notScrolling
  }

  func start(
    selection: CaptureSelection,
    completion: @escaping (ScrollingCaptureSessionOutcome) -> Void
  ) {
    cancel(notify: false)
    stitcher.reset()
    stitcher.minimumCommitPixelHeight = Self.minimumCommitPixelHeight(for: selection)
    frameMotionDetector.reset()
    acceptedFrames = 0
    skippedFrames = 0
    isInRejectedStreak = false
    isPaused = false
    isFinishing = false
    isAutoScrolling = false
    autoScrollDirection = .down
    autoScrollProgress.begin()
    activeSelection = selection
    scrollTarget = nil
    pendingScrollProgress = nil
    pendingSawMovement = false
    expectedScrollDistancePoints = Self.recommendedScrollDistance(for: selection)
    gesturesWithProgress = 0
    retargetCount = 0
    endStableSince = nil
    targetRefreshDate = nil
    stickyStatus = nil
    lastImage = nil
    nextAutoScrollDate = .distantPast
    autoScrollSettleDeadline = .distantPast
    self.completion = completion
    debugRecorder = LongCaptureDebugRecorder.makeIfEnabled(selection: selection)
    showHUD(near: selection)
    if AccessibilityAutoScrollService.isGranted {
      beginAutoScroll(selection: selection)
    } else {
      hudModel?.status = "直接滚动内容即可，拓会跟拍；也可点“自动滚动”并授权"
    }

    captureTask = Task { [weak self] in
      guard let self else { return }
      await captureLoop(selection: selection)
    }
  }

  private func captureLoop(selection: CaptureSelection) async {
    var capturer: ScrollingRegionCapturer?
    var consecutiveFailures = 0
    while !Task.isCancelled, !isFinishing {
      if !isPaused {
        do {
          if capturer == nil || capturer?.isStale == true {
            capturer = try await captureService.makeRegionCapturer(for: selection)
          }
          guard let activeCapturer = capturer else { continue }
          let image: CGImage
          do {
            image = try await activeCapturer.capture()
            consecutiveFailures = 0
          } catch {
            // A single dropped frame (display sleep, Space switch) must not
            // throw away everything captured so far.
            consecutiveFailures += 1
            capturer = nil
            if consecutiveFailures >= 5 { throw error }
            try? await Task.sleep(for: .milliseconds(200))
            continue
          }
          guard !Task.isCancelled, !isFinishing else { return }
          try await process(image, selection: selection)
          updateHUD()
        } catch is CancellationError {
          return
        } catch {
          fail(error)
          return
        }
      }
      try? await Task.sleep(for: .milliseconds(Self.captureIntervalMilliseconds))
    }
  }

  private func process(_ image: CGImage, selection: CaptureSelection) async throws {
    lastImage = image
    let frameMotion = try frameMotionDetector.compare(image)
    try frameMotionDetector.commit(image)
    var disposition = try stitcher.append(
      image,
      constraint: isAutoScrolling ? autoScrollDirection.constraint : .any
    )
    if isAutoScrolling {
      disposition = try await driveAutoScroll(
        image: image,
        disposition: disposition,
        frameMotion: frameMotion,
        selection: selection
      )
    }
    record(disposition)
    debugRecorder?.record(
      image: image,
      disposition: disposition,
      outputPixelHeight: stitcher.outputPixelHeight + stitcher.pendingPixelHeight,
      isAutoScrolling: isAutoScrolling
    )
  }

  private func record(_ disposition: ScrollingFrameDisposition) {
    switch disposition {
    case .firstFrame:
      acceptedFrames = 1
      isInRejectedStreak = false
    case .appended:
      acceptedFrames += 1
      isInRejectedStreak = false
    case .tracking, .duplicate:
      isInRejectedStreak = false
    case .rejected:
      // Count interruptions, not frames: at this sampling rate one fast
      // flick produces many consecutive unmatched frames.
      if !isInRejectedStreak { skippedFrames += 1 }
      isInRejectedStreak = true
    }
  }

  private func driveAutoScroll(
    image: CGImage,
    disposition initialDisposition: ScrollingFrameDisposition,
    frameMotion: ViewportMotionMeasurement,
    selection: CaptureSelection
  ) async throws -> ScrollingFrameDisposition {
    var disposition = initialDisposition
    let now = Date()
    let direction = autoScrollDirection

    // Electron/Chromium build their accessibility tree only after being
    // asked; look for the scroll area once more when it has had time to fill.
    if let refreshDate = targetRefreshDate, now >= refreshDate {
      targetRefreshDate = nil
      if scrollTarget?.mode != .accessibilityTracked {
        scrollTarget = AccessibilityAutoScrollService.resolveTarget(for: selection)
      }
    }

    let rawProgress = scrollTarget.flatMap { AccessibilityAutoScrollService.progress(of: $0) }
    let progress = direction == .up ? rawProgress?.mirrored : rawProgress
    let requestedPixelShift =
      direction.sign
      * Int((Double(max(1, expectedScrollDistancePoints)) * selection.backingScaleFactor).rounded())

    let contentAdvanced: Bool
    switch disposition {
    case .appended, .tracking: contentAdvanced = true
    case .firstFrame, .duplicate, .rejected: contentAdvanced = false
    }
    let viewportMoved = frameMotion.hasReference && !frameMotion.isStationary
    let trackedAdvanced = AccessibilityAutoScrollService.progressAdvanced(
      from: pendingScrollProgress, to: progress)

    if autoScrollProgress.hasPendingScroll {
      let resolution = Self.resolvePendingGesture(
        contentAdvanced: contentAdvanced,
        viewportMoved: viewportMoved,
        sawMovement: pendingSawMovement || trackedAdvanced,
        now: now,
        settleDeadline: autoScrollSettleDeadline
      )
      switch resolution {
      case .waiting:
        if viewportMoved || trackedAdvanced { pendingSawMovement = true }
      case .progress:
        closePendingGesture(withProgress: true, now: now, selection: selection)
      case .movedWithoutMatch:
        // The content demonstrably moved but no seam could be measured.
        // Keep a clearly flagged low-confidence frame for seam review
        // rather than silently dropping a viewport.
        if progress?.isAtEnd == true {
          disposition = try stitcher.appendTerminalFrame(
            image, preferredPixelShift: requestedPixelShift, constraint: direction.constraint)
        } else {
          disposition = try stitcher.appendFallback(image, signedPixelShift: requestedPixelShift)
        }
        closePendingGesture(withProgress: true, now: now, selection: selection)
      case .noMovement:
        closePendingGesture(withProgress: false, now: now, selection: selection)
      }
    }

    let stationaryNow = frameMotion.hasReference && frameMotion.isStationary
    if progress?.isAtEnd == true, stationaryNow, !autoScrollProgress.hasPendingScroll {
      let since = endStableSince ?? now
      endStableSince = since
      if now.timeIntervalSince(since) >= Self.endStabilityInterval {
        try stopAutoScroll(.reachedEnd, lastImage: image)
        return disposition
      }
    } else {
      endStableSince = nil
    }

    guard isAutoScrolling,
      !autoScrollProgress.hasPendingScroll,
      now >= nextAutoScrollDate,
      progress?.isAtEnd != true
    else { return disposition }

    if autoScrollProgress.shouldStop {
      switch Self.noProgressVerdict(
        hasTrackedProgress: progress != nil,
        gesturesWithProgress: gesturesWithProgress,
        retargetCount: retargetCount
      ) {
      case .reachedEnd:
        try stopAutoScroll(.reachedEnd, lastImage: image)
      case .retarget:
        // The scrollbar still reports content beyond the viewport. Lock the
        // scroll area again and retry with a smaller step.
        retargetCount += 1
        scrollTarget = AccessibilityAutoScrollService.resolveTarget(for: selection)
        autoScrollProgress.begin()
        pendingScrollProgress = nil
        expectedScrollDistancePoints = max(96, Self.recommendedScrollDistance(for: selection) / 2)
        nextAutoScrollDate = now.addingTimeInterval(Self.standardAutoScrollDelay)
        stickyStatus = nil
        hudModel?.status = "滚动区域仍未到\(direction.endName)，正在重新锁定并继续"
      case .notScrolling:
        try stopAutoScroll(.notScrolling, lastImage: image)
      }
      return disposition
    }

    guard AccessibilityAutoScrollService.cursorIsInside(selection) else {
      // Synthetic wheel events go to whatever is under the real pointer.
      // Sending them now would scroll some other window (or nothing) and be
      // mistaken for the end of the content.
      stickyStatus = "鼠标不在截图区域内，自动滚动已暂停；把鼠标移回内容区即可继续"
      return disposition
    }
    if stickyStatus?.hasPrefix("鼠标不在截图区域内") == true { stickyStatus = nil }

    let distance = min(
      Self.recommendedScrollDistance(for: selection), max(96, expectedScrollDistancePoints))
    if await AccessibilityAutoScrollService.postVerticalScroll(
      in: selection,
      target: scrollTarget,
      pixelsDown: direction.sign * distance
    ) {
      autoScrollProgress.didSendScroll()
      pendingScrollProgress = progress
      pendingSawMovement = false
      expectedScrollDistancePoints = distance
      nextAutoScrollDate = now.addingTimeInterval(Self.standardAutoScrollDelay)
      autoScrollSettleDeadline = now.addingTimeInterval(Self.maximumAutoScrollSettleTime)
      debugRecorder?.note("scroll \(direction.sign * distance)pt")
    } else {
      isAutoScrolling = false
      hudModel?.isAutoScrolling = false
      stickyStatus = "无法发送滚动事件，请检查辅助功能权限；仍可手动滚动跟拍"
    }
    return disposition
  }

  private func closePendingGesture(
    withProgress: Bool, now: Date, selection: CaptureSelection
  ) {
    if withProgress {
      autoScrollProgress.finishPendingWithProgress()
      gesturesWithProgress += 1
      expectedScrollDistancePoints = Self.recommendedScrollDistance(for: selection)
      nextAutoScrollDate = max(nextAutoScrollDate, now)
    } else {
      autoScrollProgress.finishPendingWithoutProgress()
    }
    pendingScrollProgress = nil
    pendingSawMovement = false
  }

  enum PendingGestureResolution: Equatable {
    case waiting
    case progress
    case movedWithoutMatch
    case noMovement
  }

  /// Resolves one scroll gesture. Only a frame the stitcher could place is
  /// progress; movement without a measurable seam waits until the viewport
  /// settles, and silence until the deadline is "no movement".
  static func resolvePendingGesture(
    contentAdvanced: Bool,
    viewportMoved: Bool,
    sawMovement: Bool,
    now: Date,
    settleDeadline: Date
  ) -> PendingGestureResolution {
    if contentAdvanced { return .progress }
    if now >= settleDeadline, !viewportMoved {
      return sawMovement ? .movedWithoutMatch : .noMovement
    }
    if now >= settleDeadline.addingTimeInterval(maximumAutoScrollAnimationTime) {
      return .movedWithoutMatch
    }
    return .waiting
  }

  static func noProgressVerdict(
    hasTrackedProgress: Bool,
    gesturesWithProgress: Int,
    retargetCount: Int
  ) -> NoProgressVerdict {
    if hasTrackedProgress {
      // Reached only while the scrollbar is *not* at its end.
      return retargetCount < 2 ? .retarget : .notScrolling
    }
    return gesturesWithProgress > 0 ? .reachedEnd : .notScrolling
  }

  static func recommendedScrollDistance(for selection: CaptureSelection) -> Int {
    min(460, max(120, Int((selection.globalRect.height * 0.42).rounded())))
  }

  /// New content is committed in steps of roughly a third of the viewport:
  /// large enough to keep memory and seam count low, small enough that every
  /// committed frame overlaps the previous one generously.
  static func minimumCommitPixelHeight(for selection: CaptureSelection) -> Int {
    max(48, Int((selection.globalRect.height * selection.backingScaleFactor * 0.34).rounded()))
  }

  private enum AutoScrollStopReason {
    case reachedEnd
    case notScrolling
  }

  private func stopAutoScroll(_ reason: AutoScrollStopReason, lastImage: CGImage) throws {
    let direction = autoScrollDirection
    isAutoScrolling = false
    autoScrollProgress.begin()
    pendingScrollProgress = nil
    pendingSawMovement = false
    endStableSince = nil
    hudModel?.isAutoScrolling = false
    switch reason {
    case .reachedEnd:
      // Keep the short final step that is smaller than a regular commit.
      _ = try stitcher.appendTerminalFrame(lastImage, constraint: direction.constraint)
      stickyStatus = "已到达滚动区域\(direction.endName)，内容采集完成"
    case .notScrolling:
      _ = try stitcher.commitPending()
      stickyStatus =
        "没有检测到滚动：内容可能已在\(direction.endName)，或该应用不接收自动滚动。可切换方向，或直接手动滚动，拓会跟拍"
    }
    debugRecorder?.note("auto-scroll stopped: \(reason)")
  }

  private func showHUD(near selection: CaptureSelection) {
    let model = ScrollingCaptureHUDModel()
    model.onTogglePause = { [weak self] in self?.togglePause() }
    model.onToggleAutoScroll = { [weak self] in self?.toggleAutoScroll() }
    model.onToggleDirection = { [weak self] in self?.toggleDirection() }
    model.onFinish = { [weak self] in self?.finish() }
    model.onCancel = { [weak self] in self?.cancel(notify: true) }
    hudModel = model

    let size = CGSize(width: 620, height: 132)
    let screen =
      NSScreen.screens.first(where: { $0.frame.intersects(selection.globalRect) })
      ?? NSScreen.main
      ?? NSScreen.screens[0]
    let visible = screen.visibleFrame
    let x = min(
      max(visible.minX + 12, selection.globalRect.midX - size.width / 2),
      visible.maxX - size.width - 12)
    let below = selection.globalRect.minY - size.height - 12
    let y =
      below >= visible.minY + 8
      ? below
      : min(visible.maxY - size.height - 8, selection.globalRect.maxY + 12)

    let panel = ScrollingCaptureHUDPanel(
      contentRect: CGRect(origin: CGPoint(x: x, y: y), size: size),
      styleMask: [.borderless, .nonactivatingPanel],
      backing: .buffered,
      defer: false
    )
    panel.level = .screenSaver
    panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .transient]
    panel.isOpaque = false
    panel.backgroundColor = .clear
    panel.hasShadow = true
    panel.hidesOnDeactivate = false
    panel.contentView = NSHostingView(rootView: ScrollingCaptureHUDView(model: model))
    panel.orderFrontRegardless()
    // The HUD floats above the captured app and may overlap the selection;
    // it must never end up inside the stitched image.
    CaptureExclusionRegistry.shared.register(panel)
    self.panel = panel
  }

  private func updateHUD() {
    hudModel?.acceptedFrames = acceptedFrames
    hudModel?.skippedFrames = skippedFrames
    hudModel?.pixelHeight = stitcher.outputPixelHeight + stitcher.pendingPixelHeight
    hudModel?.isUpward = autoScrollDirection == .up
    if let stickyStatus {
      hudModel?.status = stickyStatus
      return
    }
    if isPaused {
      hudModel?.status = "已暂停，可检查内容"
    } else if isInRejectedStreak {
      hudModel?.status =
        isAutoScrolling
        ? "画面变化较大，正在重新对齐"
        : "滚动太快或内容变化，稍微往回滚一点即可重新对齐"
    } else if isAutoScrolling, endStableSince != nil {
      hudModel?.status = "已到\(autoScrollDirection.endName)，正在确认最终画面"
    } else if isAutoScrolling, autoScrollProgress.attemptsWithoutProgress > 0 {
      hudModel?.status =
        "等待页面响应 · 已重试 \(autoScrollProgress.attemptsWithoutProgress) / \(autoScrollProgress.maximumAttemptsWithoutProgress) 次"
    } else if isAutoScrolling {
      hudModel?.status =
        scrollTarget?.mode == .accessibilityTracked
        ? "自动滚动中 · 已锁定滚动区域"
        : "自动滚动中 · 通用模式（请保持鼠标在内容区内）"
    } else if acceptedFrames <= 1, stitcher.pendingPixelHeight == 0 {
      hudModel?.status = "直接滚动内容即可，拓会跟拍；也可开启自动滚动"
    } else {
      hudModel?.status = "正在跟拍 · 上下滚动都可以，滚完点“完成并保存”"
    }
  }

  private func togglePause() {
    isPaused.toggle()
    if !isPaused {
      // The content may have been scrolled while paused.
      frameMotionDetector.reset()
      if isAutoScrolling {
        autoScrollProgress.begin()
        pendingScrollProgress = nil
        nextAutoScrollDate = Date()
        autoScrollSettleDeadline = .distantPast
      }
    }
    hudModel?.isPaused = isPaused
    updateHUD()
  }

  private func toggleAutoScroll() {
    if isAutoScrolling {
      isAutoScrolling = false
      autoScrollProgress.begin()
      pendingScrollProgress = nil
      hudModel?.isAutoScrolling = false
      stickyStatus = nil
      _ = try? stitcher.commitPending()
      updateHUD()
      return
    }
    guard AccessibilityAutoScrollService.isGranted || AccessibilityAutoScrollService.request()
    else {
      stickyStatus = "自动滚动需要辅助功能权限；授权后再次点击。手动滚动不需要权限"
      updateHUD()
      return
    }
    guard let activeSelection else {
      stickyStatus = "截图区域已经失效，请重新开始长截图"
      updateHUD()
      return
    }
    beginAutoScroll(selection: activeSelection)
    updateHUD()
  }

  private func toggleDirection() {
    autoScrollDirection = autoScrollDirection == .down ? .up : .down
    stickyStatus = nil
    if isAutoScrolling, let activeSelection {
      _ = try? stitcher.commitPending()
      beginAutoScroll(selection: activeSelection)
    }
    updateHUD()
  }

  private func beginAutoScroll(selection: CaptureSelection) {
    let resolvedTarget = AccessibilityAutoScrollService.resolveTarget(for: selection)
    scrollTarget = resolvedTarget
    // Wheel events are delivered to the window under the real pointer, so
    // park it on the content once. It is not moved again afterwards.
    AccessibilityAutoScrollService.movePointerIntoSelectionIfNeeded(
      selection, target: resolvedTarget)
    isAutoScrolling = true
    autoScrollProgress.begin()
    pendingScrollProgress = nil
    pendingSawMovement = false
    gesturesWithProgress = 0
    retargetCount = 0
    endStableSince = nil
    expectedScrollDistancePoints = Self.recommendedScrollDistance(for: selection)
    stickyStatus = nil
    // Give the first frame time to land before the first gesture.
    nextAutoScrollDate = Date().addingTimeInterval(Self.standardAutoScrollDelay)
    autoScrollSettleDeadline = .distantPast
    targetRefreshDate =
      resolvedTarget.mode == .accessibilityTracked ? nil : Date().addingTimeInterval(1.6)
    hudModel?.isAutoScrolling = true
  }

  private func finish() {
    guard !isFinishing else { return }
    isFinishing = true
    captureTask?.cancel()
    captureTask = nil
    closeHUD()
    // Frames tracked since the last commit are part of the capture too.
    _ = try? stitcher.commitPending()
    guard !stitcher.segments.isEmpty else {
      do {
        complete(
          images: try stitcher.makeImages(maximumPixelHeight: Self.maximumPartPixelHeight),
          reviewedSeams: 0)
      } catch {
        fail(error)
      }
      return
    }
    seamReview.present(
      stitcher: stitcher,
      onComplete: { [weak self] images, reviewedSeams in
        self?.complete(images: images, reviewedSeams: reviewedSeams)
      },
      onCancel: { [weak self] in
        guard let self else { return }
        stitcher.reset()
        let completion = self.completion
        self.completion = nil
        completion?(.cancelled)
      }
    )
  }

  private func complete(images: [CGImage], reviewedSeams: Int) {
    let completion = self.completion
    self.completion = nil
    var images = images
    var outputNote: String?
    // A capture that had to be split is what users experience as "the long
    // screenshot is incomplete": only the first part reaches the clipboard.
    // When halving a Retina capture makes it fit one image, prefer that.
    if images.count > 1,
      (activeSelection?.backingScaleFactor ?? 1) >= 2,
      let merged = Self.mergedAtHalfResolution(
        images, maximumPixelHeight: Self.maximumPartPixelHeight)
    {
      images = [merged]
      outputNote = "内容超长，已按 1x 清晰度合并为单张"
    }
    debugRecorder?.finish(outputHeights: images.map(\.height))
    debugRecorder = nil
    let outcome = ScrollingCaptureSessionOutcome.completed(
      images: images,
      acceptedFrames: acceptedFrames,
      skippedFrames: skippedFrames,
      reviewedSeams: reviewedSeams,
      outputNote: outputNote
    )
    releaseCapturedFrames()
    completion?(outcome)
  }

  /// Draws ordered parts of one tall capture into a single half-size image.
  /// Returns nil when the result would still exceed the per-image limit.
  static func mergedAtHalfResolution(
    _ parts: [CGImage], maximumPixelHeight: Int
  ) -> CGImage? {
    guard let first = parts.first, parts.allSatisfy({ $0.width == first.width }) else {
      return nil
    }
    let totalHeight = parts.reduce(0) { $0 + $1.height }
    let width = max(1, first.width / 2)
    let height = max(1, totalHeight / 2)
    guard height <= maximumPixelHeight,
      let context = CGContext(
        data: nil,
        width: width,
        height: height,
        bitsPerComponent: 8,
        bytesPerRow: 0,
        space: CGColorSpace(name: CGColorSpace.sRGB) ?? CGColorSpaceCreateDeviceRGB(),
        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
      )
    else { return nil }
    context.interpolationQuality = .high
    var consumed = 0
    for part in parts {
      let top = Int((Double(consumed) / 2).rounded())
      let bottom = min(height, Int((Double(consumed + part.height) / 2).rounded()))
      if bottom > top {
        context.draw(
          part,
          in: CGRect(x: 0, y: height - bottom, width: width, height: bottom - top))
      }
      consumed += part.height
    }
    return context.makeImage()
  }

  private func fail(_ error: Error) {
    captureTask?.cancel()
    captureTask = nil
    closeHUD()
    let message = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
    let completion = self.completion
    self.completion = nil
    releaseCapturedFrames()
    completion?(.failed(message))
  }

  private func cancel(notify: Bool) {
    captureTask?.cancel()
    captureTask = nil
    closeHUD()
    seamReview.close()
    releaseCapturedFrames()
    if notify {
      let completion = self.completion
      self.completion = nil
      completion?(.cancelled)
    } else {
      completion = nil
    }
  }

  private func closeHUD() {
    if let panel { CaptureExclusionRegistry.shared.unregister(panel) }
    panel?.orderOut(nil)
    panel?.close()
    panel = nil
    hudModel = nil
  }

  private func releaseCapturedFrames() {
    stitcher.reset()
    frameMotionDetector.reset()
    lastImage = nil
    debugRecorder = nil
    activeSelection = nil
    scrollTarget = nil
    pendingScrollProgress = nil
  }
}

@MainActor
private final class ScrollingCaptureHUDModel: ObservableObject {
  @Published var status = "正在采集第一帧…"
  @Published var acceptedFrames = 0
  @Published var skippedFrames = 0
  @Published var pixelHeight = 0
  @Published var isPaused = false
  @Published var isAutoScrolling = false
  @Published var isUpward = false
  var onToggleDirection: (() -> Void)?
  var onTogglePause: (() -> Void)?
  var onToggleAutoScroll: (() -> Void)?
  var onFinish: (() -> Void)?
  var onCancel: (() -> Void)?
}

private struct ScrollingCaptureHUDView: View {
  @ObservedObject var model: ScrollingCaptureHUDModel

  var body: some View {
    VStack(alignment: .leading, spacing: 10) {
      HStack(spacing: 9) {
        Image(systemName: model.isPaused ? "pause.circle.fill" : "arrow.up.arrow.down.circle.fill")
          .font(.title2)
          .foregroundStyle(model.isPaused ? .orange : .blue)
        VStack(alignment: .leading, spacing: 2) {
          Text("长截图采集中")
            .font(.headline)
          Text(model.status)
            .font(.caption)
            .lineLimit(2)
            .fixedSize(horizontal: false, vertical: true)
            .foregroundStyle(.secondary)
        }
        Spacer()
        Text("\(model.acceptedFrames) 帧 · \(model.pixelHeight) px")
          .font(.system(.caption, design: .rounded))
          .foregroundStyle(.secondary)
      }

      HStack(spacing: 8) {
        Text(model.skippedFrames > 0 ? "有 \(model.skippedFrames) 处画面不连续" : "手动滚动随时可用")
          .font(.caption2)
          .foregroundStyle(model.skippedFrames > 0 ? .orange : .secondary)
        Spacer()
        Button(model.isUpward ? "方向：向上" : "方向：向下") { model.onToggleDirection?() }
          .help("自动滚动的方向。聊天记录通常需要向上")
        Button(model.isAutoScrolling ? "停止自动滚动" : "自动滚动") { model.onToggleAutoScroll?() }
        Button(model.isPaused ? "继续" : "暂停") { model.onTogglePause?() }
        Button("取消") { model.onCancel?() }
        Button("完成并保存") { model.onFinish?() }
          .buttonStyle(.borderedProminent)
          .disabled(model.acceptedFrames == 0)
      }
    }
    .padding(14)
    .frame(width: 620, height: 132)
    .background(.ultraThickMaterial, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
    .overlay {
      RoundedRectangle(cornerRadius: 16, style: .continuous)
        .stroke(.white.opacity(0.18))
    }
  }
}

private final class ScrollingCaptureHUDPanel: NSPanel {
  override var canBecomeKey: Bool { true }
  override var canBecomeMain: Bool { false }
}
