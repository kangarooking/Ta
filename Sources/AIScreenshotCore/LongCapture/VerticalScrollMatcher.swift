import Foundation

public struct GrayscaleFrame: Equatable, Sendable {
    public let width: Int
    public let height: Int
    public let pixels: [UInt8]

    public init(width: Int, height: Int, pixels: [UInt8]) {
        precondition(width > 0 && height > 0, "Frame dimensions must be positive")
        precondition(pixels.count == width * height, "Pixel count must match dimensions")
        self.width = width
        self.height = height
        self.pixels = pixels
    }

    public subscript(x: Int, y: Int) -> UInt8 {
        pixels[y * width + x]
    }
}

public enum VerticalScrollDirection: String, Equatable, Sendable {
    case stationary
    case down
    case up
}

public enum VerticalScrollConstraint: Equatable, Sendable {
    case any
    case downwardOnly
    case upwardOnly
}

public struct StableEdgeRegions: Equatable, Sendable {
    public let topRows: Int
    public let bottomRows: Int

    public init(topRows: Int, bottomRows: Int) {
        self.topRows = topRows
        self.bottomRows = bottomRows
    }
}

public struct VerticalScrollMatch: Equatable, Sendable {
    /// Positive means that the viewport moved down; negative means it moved up.
    public let signedShift: Int
    public let meanAbsoluteDifference: Double
    public let confidence: Double

    public init(signedShift: Int, meanAbsoluteDifference: Double, confidence: Double) {
        self.signedShift = signedShift
        self.meanAbsoluteDifference = meanAbsoluteDifference
        self.confidence = confidence
    }

    public init(shift: Int, meanAbsoluteDifference: Double, confidence: Double) {
        self.init(
            signedShift: shift,
            meanAbsoluteDifference: meanAbsoluteDifference,
            confidence: confidence
        )
    }

    public var shift: Int { abs(signedShift) }

    public var direction: VerticalScrollDirection {
        if signedShift > 0 { return .down }
        if signedShift < 0 { return .up }
        return .stationary
    }

    public var isDuplicate: Bool {
        shift <= 1 && meanAbsoluteDifference <= 2.5
    }
}

public struct VerticalScrollMatcher: Sendable {
    public var maximumShiftFraction: Double
    public var ignoredTopFraction: Double
    public var ignoredBottomFraction: Double
    public var ignoredSideFraction: Double
    public var maximumMeanAbsoluteDifference: Double
    public var minimumConfidence: Double
    public var sampleStride: Int

    public init(
        maximumShiftFraction: Double = 0.82,
        ignoredTopFraction: Double = 0.10,
        ignoredBottomFraction: Double = 0.22,
        ignoredSideFraction: Double = 0.16,
        maximumMeanAbsoluteDifference: Double = 18,
        minimumConfidence: Double = 0.56,
        sampleStride: Int = 2
    ) {
        self.maximumShiftFraction = maximumShiftFraction
        self.ignoredTopFraction = ignoredTopFraction
        self.ignoredBottomFraction = ignoredBottomFraction
        self.ignoredSideFraction = ignoredSideFraction
        self.maximumMeanAbsoluteDifference = maximumMeanAbsoluteDifference
        self.minimumConfidence = min(1, max(0, minimumConfidence))
        self.sampleStride = max(1, sampleStride)
    }

    public func match(
        previous: GrayscaleFrame,
        current: GrayscaleFrame,
        constraint: VerticalScrollConstraint = .any,
        preferredSignedShift: Int? = nil,
        allowsAmbiguousMatch: Bool = false,
        minimumAcceptedConfidence: Double? = nil,
        searchWindow: ClosedRange<Int>? = nil
    ) -> VerticalScrollMatch? {
        guard previous.width == current.width,
              previous.height == current.height,
              previous.width >= 8,
              previous.height >= 24 else {
            return nil
        }

        let height = previous.height
        let fixedEdges = stableEdges(
            previous: previous,
            current: current,
            maximumFraction: 0.28,
            maximumRowDifference: 3.0
        )
        let topMargin = max(
            max(1, Int(Double(height) * ignoredTopFraction)),
            fixedEdges.topRows
        )
        let bottomMargin = max(
            max(1, Int(Double(height) * ignoredBottomFraction)),
            fixedEdges.bottomRows
        )
        let sideMargin = min(
            max(1, Int(Double(previous.width) * ignoredSideFraction)),
            max(1, previous.width / 3)
        )
        let startX = sideMargin
        let endX = previous.width - sideMargin
        guard endX - startX >= 8 else { return nil }
        let maximumShift = min(height - topMargin - bottomMargin - 12,
                               Int(Double(height) * maximumShiftFraction))
        guard maximumShift >= 0 else { return nil }

        let shiftRange: ClosedRange<Int>
        switch constraint {
        case .any:
            shiftRange = (-maximumShift)...maximumShift
        case .downwardOnly:
            shiftRange = 0...maximumShift
        case .upwardOnly:
            shiftRange = (-maximumShift)...0
        }
        var candidateShifts = Array(shiftRange)
        if let searchWindow {
            candidateShifts = candidateShifts.filter { searchWindow.contains($0) }
            guard !candidateShifts.isEmpty else { return nil }
        }
        if let preferredSignedShift {
            candidateShifts.sort {
                let lhsDistance = abs($0 - preferredSignedShift)
                let rhsDistance = abs($1 - preferredSignedShift)
                return lhsDistance == rhsDistance ? abs($0) < abs($1) : lhsDistance < rhsDistance
            }
        }

        func unweightedDifference(
            for signedShift: Int,
            fromX: Int,
            toX: Int
        ) -> Double? {
            let shift = abs(signedShift)
            let startY = topMargin
            let endY = height - shift - bottomMargin
            guard endY - startY >= 12 else { return nil }

            var totalDifference = 0
            var sampleCount = 0
            var y = startY
            while y < endY {
                var x = fromX
                while x < toX {
                    let previousY = signedShift >= 0 ? y + shift : y
                    let currentY = signedShift >= 0 ? y : y + shift
                    totalDifference += abs(Int(previous[x, previousY]) - Int(current[x, currentY]))
                    sampleCount += 1
                    x += sampleStride
                }
                y += sampleStride
            }

            guard sampleCount > 0 else { return nil }
            return Double(totalDifference) / Double(sampleCount)
        }

        func difference(for signedShift: Int) -> Double? {
            let shift = abs(signedShift)
            let startY = topMargin
            let endY = height - shift - bottomMargin
            guard endY - startY >= 12 else { return nil }

            // Vote across independent vertical content bands. White space and
            // a fixed sidebar can no longer dominate the entire score, while
            // text/code edges still provide strong alignment evidence.
            let bandCount = 5
            let contentWidth = endX - startX
            let bandWidth = max(2, contentWidth / bandCount)
            var bandScores: [Double] = []
            for band in 0..<bandCount {
                let bandStart = startX + band * bandWidth
                let bandEnd = band == bandCount - 1
                    ? endX
                    : min(endX, bandStart + bandWidth)
                guard bandEnd - bandStart >= 2 else { continue }

                var edgeDifference = 0
                var edgeSamples = 0
                var y = max(startY, 1)
                while y < endY {
                    var x = max(bandStart, 1)
                    while x < bandEnd {
                        let previousY = signedShift >= 0 ? y + shift : y
                        let currentY = signedShift >= 0 ? y : y + shift
                        let previousValue = Int(previous[x, previousY])
                        let currentValue = Int(current[x, currentY])
                        let previousGradient = abs(previousValue - Int(previous[x, previousY - 1]))
                            + abs(previousValue - Int(previous[x - 1, previousY]))
                        let currentGradient = abs(currentValue - Int(current[x, currentY - 1]))
                            + abs(currentValue - Int(current[x - 1, currentY]))
                        if max(previousGradient, currentGradient) >= 10 {
                            edgeDifference += abs(previousValue - currentValue)
                            edgeSamples += 1
                        }
                        x += sampleStride
                    }
                    y += sampleStride
                }

                let minimumEvidence = max(8, (endY - startY) / 14)
                if edgeSamples >= minimumEvidence {
                    bandScores.append(Double(edgeDifference) / Double(edgeSamples))
                }
            }

            if bandScores.count >= 2 {
                bandScores.sort()
                if bandScores.count >= 4 {
                    // Trim the worst band (usually a sidebar, animation, or
                    // caret) and aggregate the rest conservatively.
                    bandScores.removeLast()
                }
                // Use the lower median so a minority of two dynamic/fixed
                // bands cannot veto three independently aligned content bands.
                return bandScores[(bandScores.count - 1) / 2]
            }
            return unweightedDifference(for: signedShift, fromX: startX, toX: endX)
        }

        var bestShift = 0
        var bestDifference = Double.greatestFiniteMagnitude
        var secondBestDifference = Double.greatestFiniteMagnitude
        var scoredCandidates: [(shift: Int, difference: Double)] = []

        for signedShift in candidateShifts {
            guard let candidateDifference = difference(for: signedShift) else { continue }
            scoredCandidates.append((signedShift, candidateDifference))
            if candidateDifference < bestDifference {
                secondBestDifference = bestDifference
                bestDifference = candidateDifference
                bestShift = signedShift
            } else if candidateDifference < secondBestDifference {
                secondBestDifference = candidateDifference
            }
        }

        guard bestDifference <= maximumMeanAbsoluteDifference else { return nil }
        // Multi-band voting finds the displacement, then a broader overlap
        // check rejects coincidences where only a few narrow bands happened to
        // align. The generous ceiling still tolerates animated browser panels.
        guard let overlapVerification = unweightedDifference(
            for: bestShift,
            fromX: startX,
            toX: endX
        ), overlapVerification <= max(36, maximumMeanAbsoluteDifference * 2.5) else {
            return nil
        }

        if constraint != .any {
            // Direction constraints alone are insufficient on repeated chat
            // cards: an upward frame can still have a plausible positive
            // offset. Cross-check the forbidden direction and reject whenever
            // it explains the frame materially better.
            let oppositeShifts: ClosedRange<Int>
            switch constraint {
            case .downwardOnly:
                oppositeShifts = (-maximumShift)...(-2)
            case .upwardOnly:
                oppositeShifts = 2...maximumShift
            case .any:
                oppositeShifts = 0...0
            }
            let oppositeBest = oppositeShifts.compactMap(difference(for:)).min()
            if let oppositeBest, oppositeBest + 0.5 < bestDifference {
                return nil
            }

            // Repeated chat cards can produce two equally plausible offsets.
            // Accepting either one is how an automatic downward capture jumps
            // to an earlier conversation. Mature stitchers reject this case
            // unless one location is uniquely better.
            let farDistance = max(8, height / 18)
            let hasFarAmbiguity = scoredCandidates.contains { candidate in
                abs(candidate.shift - bestShift) >= farDistance
                    && candidate.difference <= bestDifference + 0.15
            }
            guard allowsAmbiguousMatch || !hasFarAmbiguity else { return nil }

            // A small under-estimate removes pixels forever, while a tiny
            // over-estimate only leaves a reviewable duplicate. Within the
            // measurement uncertainty, bias downward by at most two sample
            // rows so text is never swallowed at the seam.
            if constraint == .downwardOnly,
               let safer = scoredCandidates
                .filter({ $0.shift >= bestShift && $0.shift <= bestShift + 2 })
                .filter({ $0.difference <= bestDifference + 0.35 })
                .max(by: { $0.shift < $1.shift }) {
                bestShift = safer.shift
                bestDifference = safer.difference
            }
        }

        let quality = max(0, 1 - bestDifference / maximumMeanAbsoluteDifference)
        let separation: Double
        if secondBestDifference.isFinite, secondBestDifference > 0 {
            separation = min(1, max(0, (secondBestDifference - bestDifference) / secondBestDifference * 6))
        } else {
            separation = 1
        }
        let confidence = min(1, quality * 0.8 + separation * 0.2)
        guard confidence >= (minimumAcceptedConfidence ?? minimumConfidence) else { return nil }
        return VerticalScrollMatch(
            signedShift: bestShift,
            meanAbsoluteDifference: bestDifference,
            confidence: confidence
        )
    }

    /// Finds same-position rows that stayed fixed between frames, such as a
    /// chat title bar or composer. The result is deliberately conservative;
    /// callers should still surface low-confidence seams for review.
    public func stableEdges(
        previous: GrayscaleFrame,
        current: GrayscaleFrame,
        maximumFraction: Double = 0.25,
        maximumRowDifference: Double = 3.0
    ) -> StableEdgeRegions {
        guard previous.width == current.width,
              previous.height == current.height else {
            return StableEdgeRegions(topRows: 0, bottomRows: 0)
        }

        let limit = max(0, min(previous.height / 3, Int(Double(previous.height) * maximumFraction)))
        guard limit > 0 else { return StableEdgeRegions(topRows: 0, bottomRows: 0) }

        func rowDifference(_ row: Int) -> Double {
            var total = 0
            var count = 0
            let sideMargin = min(
                max(0, Int(Double(previous.width) * ignoredSideFraction)),
                max(0, previous.width / 3)
            )
            var x = sideMargin
            let endX = max(x + 1, previous.width - sideMargin)
            while x < endX {
                total += abs(Int(previous[x, row]) - Int(current[x, row]))
                count += 1
                x += sampleStride
            }
            return count == 0 ? .greatestFiniteMagnitude : Double(total) / Double(count)
        }

        var top = 0
        while top < limit, rowDifference(top) <= maximumRowDifference { top += 1 }

        var bottom = 0
        while bottom < limit,
              rowDifference(previous.height - 1 - bottom) <= maximumRowDifference {
            bottom += 1
        }

        // One or two coincidentally equal rows are not a stable application region.
        return StableEdgeRegions(
            topRows: top >= 3 ? top : 0,
            bottomRows: bottom >= 3 ? bottom : 0
        )
    }
}
