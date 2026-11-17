import CoreGraphics
import Foundation

public enum ScrollingImageStitcherError: LocalizedError {
    case noFrames
    case invalidImage
    case outputTooLarge
    case couldNotCreateCanvas

    public var errorDescription: String? {
        switch self {
        case .noFrames:
            "没有采集到可用画面。"
        case .invalidImage:
            "滚动截图画面无效。"
        case .outputTooLarge:
            "长截图尺寸过大，请提前结束并分段保存。"
        case .couldNotCreateCanvas:
            "无法创建长截图画布。"
        }
    }
}

public enum ScrollingFrameDisposition: Equatable, Sendable {
    case firstFrame
    case appended(newPixelHeight: Int, confidence: Double)
    /// The viewport moved and the frame was tracked, but the new content is
    /// still smaller than `minimumCommitPixelHeight`. Nothing was added to the
    /// output yet; the frame is kept as the pending tail.
    case tracking(pendingPixelHeight: Int)
    case duplicate
    case rejected
}

public struct ScrollingStitchSegment: Identifiable, Equatable, Sendable {
    public let id: UUID
    public let direction: VerticalScrollDirection
    public let newPixelHeight: Int
    public let confidence: Double
    public let stableTopHeight: Int
    public let stableBottomHeight: Int

    public init(
        id: UUID = UUID(),
        direction: VerticalScrollDirection,
        newPixelHeight: Int,
        confidence: Double,
        stableTopHeight: Int,
        stableBottomHeight: Int
    ) {
        self.id = id
        self.direction = direction
        self.newPixelHeight = newPixelHeight
        self.confidence = confidence
        self.stableTopHeight = stableTopHeight
        self.stableBottomHeight = stableBottomHeight
    }
}

public enum ScrollingCaptureQualityIssue: Equatable, Sendable {
    case lowConfidence(segmentID: UUID, confidence: Double)
    case ultraLongOutput(recommendedPartCount: Int)
}

public final class ScrollingImageStitcher {
    private struct CapturedSegment {
        let image: CGImage
        let details: ScrollingStitchSegment
    }

    private struct ImageStrip {
        let image: CGImage
        let sourceY: Int
        let height: Int
    }

    /// A committed frame that later frames are registered against at full
    /// vertical resolution.
    private struct Anchor {
        let sample: GrayscaleFrame
        let fine: GrayscaleFrame
        let offset: Int
    }

    private struct PendingFrame {
        let image: CGImage
        let sample: GrayscaleFrame
        let confidence: Double
    }

    /// Dense capture produces many frames that each reveal only a few new
    /// rows. Committing every one of them would keep hundreds of full frames
    /// in memory and multiply seams, so frames are *tracked* until at least
    /// this many new pixels are visible, then committed as one segment.
    /// `0` commits every moved frame (the historical behaviour).
    public var minimumCommitPixelHeight = 0

    private let fineSampleWidth = 160
    private var anchorDown: Anchor?
    private var anchorUp: Anchor?
    private var pendingFrame: PendingFrame?
    private var lastSampleShift: Int?

    private let matcher: VerticalScrollMatcher
    private let sampleWidth: Int
    private let sampleHeight: Int
    private let maximumOutputPixels: Int
    private var firstImage: CGImage?
    private var previousSample: GrayscaleFrame?
    private var capturedSegments: [CapturedSegment] = []
    private var stitchSegments: [ScrollingStitchSegment] = []
    private var accumulatedHeight = 0
    private var viewportOffset = 0
    private var minimumViewportOffset = 0
    private var maximumViewportOffset = 0

    public init(
        matcher: VerticalScrollMatcher = VerticalScrollMatcher(),
        sampleWidth: Int = 144,
        sampleHeight: Int = 720,
        maximumOutputPixels: Int = 120_000_000
    ) {
        self.matcher = matcher
        self.sampleWidth = max(32, sampleWidth)
        self.sampleHeight = max(120, sampleHeight)
        self.maximumOutputPixels = maximumOutputPixels
    }

    public var frameCount: Int {
        firstImage == nil ? 0 : capturedSegments.count + 1
    }

    public var outputPixelHeight: Int { accumulatedHeight }

    public var segments: [ScrollingStitchSegment] { stitchSegments }

    public var qualityIssues: [ScrollingCaptureQualityIssue] {
        var issues = stitchSegments.compactMap { segment -> ScrollingCaptureQualityIssue? in
            guard segment.confidence < 0.58 else { return nil }
            return .lowConfidence(segmentID: segment.id, confidence: segment.confidence)
        }
        if let firstImage {
            let safeHeight = max(1, maximumOutputPixels / max(1, firstImage.width))
            if accumulatedHeight > safeHeight {
                issues.append(.ultraLongOutput(
                    recommendedPartCount: Int(ceil(Double(accumulatedHeight) / Double(safeHeight)))
                ))
            }
        }
        return issues
    }

    /// Lets the review UI nudge a detected seam without re-running capture.
    /// A negative delta removes duplicated rows; a positive delta restores rows.
    @discardableResult
    public func adjustSegment(id: UUID, pixelDelta: Int) -> Bool {
        guard let capturedIndex = capturedSegments.firstIndex(where: { $0.details.id == id }),
              let publicIndex = stitchSegments.firstIndex(where: { $0.id == id }) else {
            return false
        }
        let captured = capturedSegments[capturedIndex]
        let old = captured.details
        let newHeight = min(captured.image.height, max(1, old.newPixelHeight + pixelDelta))
        guard newHeight != old.newPixelHeight else { return false }
        let updated = ScrollingStitchSegment(
            id: old.id,
            direction: old.direction,
            newPixelHeight: newHeight,
            confidence: old.confidence,
            stableTopHeight: old.stableTopHeight,
            stableBottomHeight: old.stableBottomHeight
        )
        capturedSegments[capturedIndex] = CapturedSegment(image: captured.image, details: updated)
        stitchSegments[publicIndex] = updated
        accumulatedHeight += newHeight - old.newPixelHeight
        return true
    }

    public func reset() {
        firstImage = nil
        previousSample = nil
        capturedSegments.removeAll(keepingCapacity: true)
        stitchSegments.removeAll(keepingCapacity: true)
        accumulatedHeight = 0
        viewportOffset = 0
        minimumViewportOffset = 0
        maximumViewportOffset = 0
        anchorDown = nil
        anchorUp = nil
        pendingFrame = nil
        lastSampleShift = nil
    }

    /// Pixels that were tracked beyond the committed output but not committed
    /// yet (see `minimumCommitPixelHeight`).
    public var pendingPixelHeight: Int {
        guard pendingFrame != nil else { return 0 }
        if viewportOffset > maximumViewportOffset { return viewportOffset - maximumViewportOffset }
        if viewportOffset < minimumViewportOffset { return minimumViewportOffset - viewportOffset }
        return 0
    }

    /// Commits the tracked-but-uncommitted tail. Call before reading
    /// `segments` / rendering so the last partial step is not lost.
    @discardableResult
    public func commitPending() throws -> ScrollingFrameDisposition {
        guard let pending = pendingFrame else { return .duplicate }
        return try settle(
            image: pending.image,
            sample: pending.sample,
            confidence: pending.confidence,
            force: true,
            refine: true
        )
    }

    @discardableResult
    public func append(
        _ image: CGImage,
        constraint: VerticalScrollConstraint = .any,
        preferredPixelShift: Int? = nil
    ) throws -> ScrollingFrameDisposition {
        guard image.width > 0, image.height > 0 else {
            throw ScrollingImageStitcherError.invalidImage
        }
        let sample = try makeSample(from: image)

        guard let firstImage, let previousSample else {
            try installFirstFrame(image, sample: sample)
            return .firstFrame
        }
        guard image.width == firstImage.width, image.height == firstImage.height else {
            return .rejected
        }
        let scale = Double(image.height) / Double(sample.height)
        let preferredSampleShift = preferredPixelShift.map {
            Int((Double($0) / scale).rounded())
        }

        // Motion prior: consecutive frames of a dense capture move by a
        // similar amount. Searching near the previous displacement first is
        // faster and removes most repeated-content ambiguity; the full range
        // is only searched when the prediction fails.
        var match: VerticalScrollMatch?
        if let window = predictedSearchWindow(sampleHeight: sample.height) {
            match = matcher.match(
                previous: previousSample,
                current: sample,
                constraint: constraint,
                preferredSignedShift: preferredSampleShift,
                searchWindow: window
            )
        }
        if match == nil {
            match = matcher.match(
                previous: previousSample,
                current: sample,
                constraint: constraint,
                preferredSignedShift: preferredSampleShift
            )
        }
        guard let match else {
            lastSampleShift = nil
            return .rejected
        }
        return try appendMatched(
            image: image,
            sample: sample,
            match: match,
            scale: scale,
            force: false
        )
    }

    /// Commits the final, settled viewport after the tracked scrollbar reaches
    /// its end. The last scroll is commonly shorter than the regular step,
    /// and repeated chat rows can make that small seam look ambiguous. At the
    /// confirmed end it is safer to accept the best overlap at low
    /// confidence so the document tail is retained for seam review.
    @discardableResult
    public func appendTerminalFrame(
        _ image: CGImage,
        preferredPixelShift: Int? = nil,
        constraint: VerticalScrollConstraint = .downwardOnly
    ) throws -> ScrollingFrameDisposition {
        guard image.width > 0, image.height > 0 else {
            throw ScrollingImageStitcherError.invalidImage
        }
        let sample = try makeSample(from: image)
        guard let firstImage, let previousSample else {
            try installFirstFrame(image, sample: sample)
            return .firstFrame
        }
        guard image.width == firstImage.width, image.height == firstImage.height else {
            return .rejected
        }

        let scale = Double(image.height) / Double(sample.height)
        let preferredSampleShift = preferredPixelShift.map {
            Int((Double($0) / scale).rounded())
        }
        guard let match = matcher.match(
            previous: previousSample,
            current: sample,
            constraint: constraint,
            preferredSignedShift: preferredSampleShift,
            allowsAmbiguousMatch: true,
            minimumAcceptedConfidence: 0.05
        ) else {
            // Nothing new can be matched; still keep whatever was tracked.
            return try commitPending()
        }
        let terminalMatch = VerticalScrollMatch(
            signedShift: match.signedShift,
            meanAbsoluteDifference: match.meanAbsoluteDifference,
            confidence: min(match.confidence, 0.35)
        )
        let disposition = try appendMatched(
            image: image,
            sample: sample,
            match: terminalMatch,
            scale: scale,
            force: true
        )
        if case .duplicate = disposition, pendingFrame != nil {
            return try commitPending()
        }
        return disposition
    }

    private func installFirstFrame(_ image: CGImage, sample: GrayscaleFrame) throws {
        firstImage = image
        previousSample = sample
        accumulatedHeight = image.height
        let anchor = Anchor(sample: sample, fine: try makeFineSample(from: image), offset: 0)
        anchorDown = anchor
        anchorUp = anchor
        pendingFrame = nil
        lastSampleShift = nil
    }

    private func predictedSearchWindow(sampleHeight: Int) -> ClosedRange<Int>? {
        guard let lastSampleShift else { return nil }
        let slack = max(12, sampleHeight / 8)
        // Always include zero so a viewport that stopped moving is recognised
        // as stationary instead of being forced onto a moving candidate.
        return (min(lastSampleShift, 0) - slack)...(max(lastSampleShift, 0) + slack)
    }

    private func appendMatched(
        image: CGImage,
        sample: GrayscaleFrame,
        match: VerticalScrollMatch,
        scale: Double,
        force: Bool
    ) throws -> ScrollingFrameDisposition {
        guard firstImage != nil else { return .rejected }
        if match.isDuplicate {
            lastSampleShift = 0
            return .duplicate
        }

        lastSampleShift = match.signedShift
        let signedPixelShift = Int((Double(match.signedShift) * scale).rounded())
        viewportOffset += signedPixelShift
        previousSample = sample
        return try settle(
            image: image,
            sample: sample,
            confidence: match.confidence,
            force: force,
            refine: true
        )
    }

    /// Decides what a tracked frame at `viewportOffset` contributes: nothing
    /// (already represented), a pending tail, or a committed segment.
    private func settle(
        image: CGImage,
        sample: GrayscaleFrame,
        confidence: Double,
        force: Bool,
        refine: Bool
    ) throws -> ScrollingFrameDisposition {
        let direction: VerticalScrollDirection
        let overshoot: Int
        if viewportOffset > maximumViewportOffset {
            direction = .down
            overshoot = viewportOffset - maximumViewportOffset
        } else if viewportOffset < minimumViewportOffset {
            direction = .up
            overshoot = minimumViewportOffset - viewportOffset
        } else {
            // The user reversed into an area that is already represented.
            pendingFrame = nil
            return .duplicate
        }

        let combinedConfidence = min(pendingFrame?.confidence ?? 1, confidence)
        if !force, overshoot < max(2, minimumCommitPixelHeight) {
            if minimumCommitPixelHeight <= 0 {
                pendingFrame = nil
                return .duplicate
            }
            pendingFrame = PendingFrame(image: image, sample: sample, confidence: combinedConfidence)
            return .tracking(pendingPixelHeight: overshoot)
        }
        return try commit(
            image: image,
            sample: sample,
            direction: direction,
            confidence: combinedConfidence,
            refine: refine
        )
    }

    private func commit(
        image: CGImage,
        sample: GrayscaleFrame,
        direction: VerticalScrollDirection,
        confidence: Double,
        refine: Bool
    ) throws -> ScrollingFrameDisposition {
        guard let firstImage else { return .rejected }
        let scale = Double(image.height) / Double(sample.height)
        let fine = try makeFineSample(from: image)
        let anchor = direction == .down ? anchorDown : anchorUp
        var stable = StableEdgeRegions(topRows: 0, bottomRows: 0)
        if let anchor {
            stable = matcher.stableEdges(previous: anchor.sample, current: sample)
            let displacement = viewportOffset - anchor.offset
            if refine,
               anchor.fine.width == fine.width,
               anchor.fine.height == fine.height,
               displacement != 0,
               abs(displacement) < Int(Double(image.height) * 0.75) {
                // The matcher works on a vertically reduced sample, so its
                // shift is quantised to `scale` pixels and tracking drift adds
                // up. Re-register against the last committed frame at full
                // vertical resolution to land on the exact pixel row.
                let radius = min(16, Int(scale.rounded(.up)) * 3 + 2)
                let refined = Self.refinedDisplacement(
                    anchor: anchor.fine,
                    current: fine,
                    approximate: displacement,
                    radius: radius,
                    topMargin: max(
                        Int((Double(stable.topRows) * scale).rounded(.up)),
                        image.height / 20
                    ),
                    bottomMargin: max(
                        Int((Double(stable.bottomRows) * scale).rounded(.up)),
                        image.height / 10
                    )
                )
                viewportOffset += refined - displacement
            }
        }

        let newPixelHeight: Int
        switch direction {
        case .down:
            newPixelHeight = viewportOffset - maximumViewportOffset
        case .up:
            newPixelHeight = minimumViewportOffset - viewportOffset
        case .stationary:
            newPixelHeight = 0
        }
        guard newPixelHeight >= 2 else {
            pendingFrame = nil
            return .duplicate
        }
        if direction == .down {
            maximumViewportOffset = viewportOffset
        } else {
            minimumViewportOffset = viewportOffset
        }

        let appendedHeight = min(image.height, newPixelHeight)
        let details = ScrollingStitchSegment(
            direction: direction,
            newPixelHeight: appendedHeight,
            confidence: confidence,
            stableTopHeight: min(image.height / 3, Int((Double(stable.topRows) * scale).rounded())),
            stableBottomHeight: min(image.height / 3, Int((Double(stable.bottomRows) * scale).rounded()))
        )
        capturedSegments.append(CapturedSegment(image: image, details: details))
        stitchSegments.append(details)
        accumulatedHeight = firstImage.height + maximumViewportOffset - minimumViewportOffset
        let newAnchor = Anchor(sample: sample, fine: fine, offset: viewportOffset)
        if direction == .down {
            anchorDown = newAnchor
        } else {
            anchorUp = newAnchor
        }
        pendingFrame = nil
        return .appended(newPixelHeight: appendedHeight, confidence: confidence)
    }

    /// Finds the displacement (in full-resolution rows) near `approximate`
    /// that best aligns `current` with `anchor`. Positive means `current`
    /// shows content further down than `anchor`.
    static func refinedDisplacement(
        anchor: GrayscaleFrame,
        current: GrayscaleFrame,
        approximate: Int,
        radius: Int,
        topMargin: Int,
        bottomMargin: Int
    ) -> Int {
        let height = anchor.height
        let width = anchor.width
        let sign = approximate >= 0 ? 1 : -1
        let largestShift = abs(approximate) + radius
        let startY = max(0, topMargin)
        let endY = height - largestShift - max(0, bottomMargin)
        guard radius > 0, endY - startY >= 16 else { return approximate }
        let startX = width / 12
        let endX = width - width / 12

        var bestShift = approximate
        var bestScore = Double.greatestFiniteMagnitude
        for magnitude in max(1, abs(approximate) - radius)...(abs(approximate) + radius) {
            var total = 0
            var count = 0
            anchor.pixels.withUnsafeBufferPointer { anchorPixels in
                current.pixels.withUnsafeBufferPointer { currentPixels in
                    var y = startY
                    while y < endY {
                        let anchorRow = (sign > 0 ? y + magnitude : y) * width
                        let currentRow = (sign > 0 ? y : y + magnitude) * width
                        var x = startX
                        while x < endX {
                            total += abs(Int(anchorPixels[anchorRow + x]) - Int(currentPixels[currentRow + x]))
                            count += 1
                            x += 2
                        }
                        y += 1
                    }
                }
            }
            guard count > 0 else { continue }
            let score = Double(total) / Double(count)
            let candidate = sign * magnitude
            // Prefer the candidate closest to the estimate on (near) ties so
            // flat or periodic content cannot drag the seam away.
            if score < bestScore - 0.01
                || (abs(score - bestScore) <= 0.01
                    && abs(candidate - approximate) < abs(bestShift - approximate)) {
                bestScore = score
                bestShift = candidate
            }
        }
        return bestShift
    }

    /// Advances the stitching anchor after the viewport demonstrably moved but
    /// the visual matcher could not find a unique seam. Chat timelines often
    /// contain repeated cards, blank areas, animated cursors, and sticky
    /// composers; those conditions may make a correct seam ambiguous even
    /// though the scroll itself succeeded. Keeping this low-confidence frame
    /// is safer than blocking all subsequent capture or silently dropping an
    /// entire viewport.
    @discardableResult
    public func appendFallback(
        _ image: CGImage,
        signedPixelShift requestedSignedPixelShift: Int
    ) throws -> ScrollingFrameDisposition {
        guard image.width > 0, image.height > 0 else {
            throw ScrollingImageStitcherError.invalidImage
        }
        let sample = try makeSample(from: image)
        guard let firstImage, previousSample != nil else {
            try installFirstFrame(image, sample: sample)
            return .firstFrame
        }
        guard image.width == firstImage.width, image.height == firstImage.height else {
            return .rejected
        }

        let maximumSafeShift = max(2, Int((Double(image.height) * 0.55).rounded(.down)))
        let magnitude = min(maximumSafeShift, max(2, abs(requestedSignedPixelShift)))
        let signedPixelShift = requestedSignedPixelShift < 0 ? -magnitude : magnitude
        viewportOffset += signedPixelShift
        previousSample = sample
        lastSampleShift = nil
        return try settle(
            image: image,
            sample: sample,
            confidence: 0,
            force: true,
            refine: false
        )
    }

    public func makeImage() throws -> CGImage {
        guard let firstImage else { throw ScrollingImageStitcherError.noFrames }
        guard firstImage.width * accumulatedHeight <= maximumOutputPixels else {
            throw ScrollingImageStitcherError.outputTooLarge
        }
        return try render(strips: makeStrips(), startY: 0, height: accumulatedHeight)
    }

    /// Renders an ultra-long capture into ordered, clipboard/save-friendly parts.
    public func makeImages(maximumPixelHeight: Int = 30_000) throws -> [CGImage] {
        guard let firstImage else { throw ScrollingImageStitcherError.noFrames }
        let pixelSafeHeight = max(1, maximumOutputPixels / max(1, firstImage.width))
        let partHeight = max(1, min(maximumPixelHeight, pixelSafeHeight))
        let strips = makeStrips()
        var images: [CGImage] = []
        var startY = 0
        while startY < accumulatedHeight {
            let height = min(partHeight, accumulatedHeight - startY)
            images.append(try render(strips: strips, startY: startY, height: height))
            startY += height
        }
        return images
    }

    private func makeStrips() -> [ImageStrip] {
        guard let firstImage else { return [] }
        let upward = capturedSegments.filter { $0.details.direction == .up }
        let downward = capturedSegments.filter { $0.details.direction == .down }

        // Sticky regions must be present consistently. Taking the maximum lets
        // one blank/repetitive frame crop that many rows from every seam, which
        // is especially destructive in chat apps with a large composer.
        let stableTop = conservativeStableHeight(upward.map(\.details.stableTopHeight))
        let stableBottom = conservativeStableHeight(downward.map(\.details.stableBottomHeight))
        let safeTop = min(stableTop, firstImage.height / 3)
        let safeBottom = min(stableBottom, firstImage.height / 3)
        var strips: [ImageStrip] = []

        if let earliest = upward.last, safeTop > 0 {
            strips.append(ImageStrip(image: earliest.image, sourceY: 0, height: safeTop))
        }
        for segment in upward.reversed() {
            let sourceY = min(safeTop, max(0, segment.image.height - segment.details.newPixelHeight))
            let height = min(segment.details.newPixelHeight, segment.image.height - sourceY)
            if height > 0 {
                strips.append(ImageStrip(image: segment.image, sourceY: sourceY, height: height))
            }
        }

        let baseTop = upward.isEmpty ? 0 : safeTop
        let baseBottom = downward.isEmpty ? 0 : safeBottom
        let baseHeight = max(0, firstImage.height - baseTop - baseBottom)
        if baseHeight > 0 {
            strips.append(ImageStrip(image: firstImage, sourceY: baseTop, height: baseHeight))
        }

        for segment in downward {
            let height = min(segment.details.newPixelHeight, segment.image.height - safeBottom)
            let sourceY = max(0, segment.image.height - safeBottom - height)
            if height > 0 {
                strips.append(ImageStrip(image: segment.image, sourceY: sourceY, height: height))
            }
        }
        if let latest = downward.last, safeBottom > 0 {
            strips.append(ImageStrip(
                image: latest.image,
                sourceY: latest.image.height - safeBottom,
                height: safeBottom
            ))
        }
        return strips
    }

    private func conservativeStableHeight(_ values: [Int]) -> Int {
        guard !values.isEmpty else { return 0 }
        let sorted = values.sorted()
        // Lower median: one unusually large detection can never dominate a
        // two-frame capture, while a stable region seen across frames survives.
        return sorted[(sorted.count - 1) / 2]
    }

    private func render(strips: [ImageStrip], startY: Int, height: Int) throws -> CGImage {
        guard let firstImage else { throw ScrollingImageStitcherError.noFrames }
        let width = firstImage.width
        guard let context = CGContext(
            data: nil,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: CGColorSpace(name: CGColorSpace.sRGB) ?? CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else {
            throw ScrollingImageStitcherError.couldNotCreateCanvas
        }

        context.interpolationQuality = .high

        let requested = startY..<(startY + height)
        var globalY = 0
        for strip in strips {
            let stripRange = globalY..<(globalY + strip.height)
            let lower = max(requested.lowerBound, stripRange.lowerBound)
            let upper = min(requested.upperBound, stripRange.upperBound)
            if lower < upper {
                let offset = lower - stripRange.lowerBound
                let cropRect = CGRect(
                    x: 0,
                    y: strip.sourceY + offset,
                    width: strip.image.width,
                    height: upper - lower
                )
                if let cropped = strip.image.cropping(to: cropRect) {
                    let destinationY = height - (upper - startY)
                    context.draw(cropped, in: CGRect(
                        x: 0,
                        y: destinationY,
                        width: width,
                        height: upper - lower
                    ))
                }
            }
            globalY += strip.height
        }

        guard let image = context.makeImage() else {
            throw ScrollingImageStitcherError.couldNotCreateCanvas
        }
        return image
    }

    /// Full vertical resolution, reduced width: used only to refine a seam to
    /// the exact pixel row when a segment is committed.
    func makeFineSample(from image: CGImage) throws -> GrayscaleFrame {
        let height = image.height
        var pixels = [UInt8](repeating: 0, count: fineSampleWidth * height)
        guard let context = CGContext(
            data: &pixels,
            width: fineSampleWidth,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: fineSampleWidth,
            space: CGColorSpaceCreateDeviceGray(),
            bitmapInfo: CGImageAlphaInfo.none.rawValue
        ) else {
            throw ScrollingImageStitcherError.invalidImage
        }
        context.interpolationQuality = .low
        context.draw(image, in: CGRect(x: 0, y: 0, width: fineSampleWidth, height: height))
        return GrayscaleFrame(width: fineSampleWidth, height: height, pixels: pixels)
    }

    func makeSample(from image: CGImage) throws -> GrayscaleFrame {
        // Horizontal and vertical matching have different needs. Deriving the
        // sample height from the (small) sample width made a wide Retina frame
        // only a few dozen rows tall, so one matcher row could represent
        // 10–20 real pixels and cut straight through a line of text. Keep a
        // high, independent vertical resolution while still compressing x.
        let height = min(image.height, sampleHeight)
        var pixels = [UInt8](repeating: 0, count: sampleWidth * height)
        guard let context = CGContext(
            data: &pixels,
            width: sampleWidth,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: sampleWidth,
            space: CGColorSpaceCreateDeviceGray(),
            bitmapInfo: CGImageAlphaInfo.none.rawValue
        ) else {
            throw ScrollingImageStitcherError.invalidImage
        }
        context.interpolationQuality = .medium
        context.draw(image, in: CGRect(x: 0, y: 0, width: sampleWidth, height: height))
        return GrayscaleFrame(width: sampleWidth, height: height, pixels: pixels)
    }
}
