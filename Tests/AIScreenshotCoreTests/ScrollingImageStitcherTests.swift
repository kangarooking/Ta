import AppKit
import CoreGraphics
import XCTest
@testable import AIScreenshotCore

final class ScrollingImageStitcherTests: XCTestCase {
    func testSingleFrameOutputKeepsVisualTopAtTop() throws {
        let image = try makeDirectionalImage(width: 80, height: 120, splitY: 60)
        let stitcher = ScrollingImageStitcher(sampleWidth: 40)
        _ = try stitcher.append(image)

        let output = try stitcher.makeImage()

        assertRed(try pixel(in: output, x: 10, yFromTop: 10))
        assertBlue(try pixel(in: output, x: 10, yFromTop: 110))
    }

    func testDownwardStitchedOutputKeepsDocumentTopAtTop() throws {
        let first = try makeDirectionalImage(width: 120, height: 140, splitY: 110, offsetY: 0)
        let second = try makeDirectionalImage(width: 120, height: 140, splitY: 110, offsetY: 70)
        let stitcher = ScrollingImageStitcher(sampleWidth: 60)
        _ = try stitcher.append(first)
        let disposition = try stitcher.append(second)
        guard case .appended = disposition else {
            return XCTFail("Expected a stitched frame, got \(disposition)")
        }
        XCTAssertEqual(stitcher.segments.last?.direction, .down)

        let output = try stitcher.makeImage()

        assertRed(try pixel(in: output, x: 10, yFromTop: 10))
        assertBlue(try pixel(in: output, x: 10, yFromTop: output.height - 10))
    }

    func testSegmentedPreviewPartsKeepTopToBottomOrder() throws {
        let stitcher = ScrollingImageStitcher(sampleWidth: 60)
        for offset in [0, 70, 140] {
            _ = try stitcher.append(
                makeDirectionalImage(width: 120, height: 140, splitY: 110, offsetY: offset)
            )
        }

        let parts = try stitcher.makeImages(maximumPixelHeight: 100)

        XCTAssertGreaterThan(parts.count, 1)
        assertRed(try pixel(in: try XCTUnwrap(parts.first), x: 10, yFromTop: 10))
        let last = try XCTUnwrap(parts.last)
        assertBlue(try pixel(in: last, x: 10, yFromTop: last.height - 10))
    }

    func testStitcherAddsOnlyNewScrolledPixels() throws {
        let source = try makeStripedImage(width: 240, height: 600)
        let first = try XCTUnwrap(source.cropping(to: CGRect(x: 0, y: 0, width: 240, height: 300)))
        let second = try XCTUnwrap(source.cropping(to: CGRect(x: 0, y: 80, width: 240, height: 300)))
        let stitcher = ScrollingImageStitcher(sampleWidth: 80)

        XCTAssertEqual(try stitcher.append(first), .firstFrame)
        let outcome = try stitcher.append(second)
        guard case .appended(let newPixelHeight, _) = outcome else {
            return XCTFail("Expected appended frame, got \(outcome)")
        }
        XCTAssertEqual(newPixelHeight, 80, accuracy: 5)

        let output = try stitcher.makeImage()
        XCTAssertEqual(output.width, 240)
        XCTAssertEqual(output.height, 380, accuracy: 5)
    }

    func testWideCaptureKeepsPixelAccurateShiftSoTextRowsAreNotSwallowed() throws {
        let source = try makeStripedImage(width: 1_200, height: 800)
        let first = try XCTUnwrap(source.cropping(to: CGRect(x: 0, y: 0, width: 1_200, height: 360)))
        let second = try XCTUnwrap(source.cropping(to: CGRect(x: 0, y: 67, width: 1_200, height: 360)))
        let stitcher = ScrollingImageStitcher(sampleWidth: 96)

        XCTAssertEqual(try stitcher.append(first), .firstFrame)
        let outcome = try stitcher.append(second)

        guard case .appended(let newPixelHeight, _) = outcome else {
            return XCTFail("Expected appended frame, got \(outcome)")
        }
        XCTAssertEqual(newPixelHeight, 67, accuracy: 1)
        XCTAssertEqual(try stitcher.makeImage().height, 427, accuracy: 1)
    }

    func testStitcherIgnoresDuplicateImage() throws {
        let image = try makeStripedImage(width: 200, height: 260)
        let stitcher = ScrollingImageStitcher(sampleWidth: 72)
        _ = try stitcher.append(image)

        XCTAssertEqual(try stitcher.append(image), .duplicate)
        XCTAssertEqual(try stitcher.makeImage().height, 260)
    }

    func testStitcherPrependsUpwardScrollPixels() throws {
        let source = try makeStripedImage(width: 220, height: 520)
        let first = try XCTUnwrap(source.cropping(to: CGRect(x: 0, y: 120, width: 220, height: 260)))
        let upward = try XCTUnwrap(source.cropping(to: CGRect(x: 0, y: 75, width: 220, height: 260)))
        let stitcher = ScrollingImageStitcher(sampleWidth: 80)
        _ = try stitcher.append(first)

        let outcome = try stitcher.append(upward)
        guard case .appended(let newPixelHeight, _) = outcome else {
            return XCTFail("Expected appended frame, got \(outcome)")
        }
        XCTAssertEqual(newPixelHeight, 45, accuracy: 5)
        XCTAssertEqual(stitcher.segments.last?.direction, .up)
        XCTAssertEqual(try stitcher.makeImage().height, 305, accuracy: 5)
    }

    func testAutomaticDownwardStitcherNeverPrependsAnUpwardFrame() throws {
        let source = try makeStripedImage(width: 220, height: 520)
        let first = try XCTUnwrap(source.cropping(to: CGRect(x: 0, y: 120, width: 220, height: 260)))
        let upward = try XCTUnwrap(source.cropping(to: CGRect(x: 0, y: 75, width: 220, height: 260)))
        let stitcher = ScrollingImageStitcher(sampleWidth: 80)
        _ = try stitcher.append(first)

        XCTAssertEqual(
            try stitcher.append(upward, constraint: .downwardOnly, preferredPixelShift: 120),
            .rejected
        )
        XCTAssertTrue(stitcher.segments.isEmpty)
        XCTAssertEqual(try stitcher.makeImage().height, 260)
    }

    func testFallbackFrameAdvancesAnchorAfterConfirmedScrollMovement() throws {
        let source = try makeStripedImage(width: 220, height: 700)
        let first = try XCTUnwrap(source.cropping(to: CGRect(x: 0, y: 0, width: 220, height: 280)))
        let second = try XCTUnwrap(source.cropping(to: CGRect(x: 0, y: 90, width: 220, height: 280)))
        let third = try XCTUnwrap(source.cropping(to: CGRect(x: 0, y: 180, width: 220, height: 280)))
        let stitcher = ScrollingImageStitcher(sampleWidth: 80)

        XCTAssertEqual(try stitcher.append(first), .firstFrame)
        XCTAssertEqual(
            try stitcher.appendFallback(second, signedPixelShift: 90),
            .appended(newPixelHeight: 90, confidence: 0)
        )

        let thirdDisposition = try stitcher.append(
            third,
            constraint: .downwardOnly,
            preferredPixelShift: 90
        )
        guard case .appended(let newPixelHeight, _) = thirdDisposition else {
            return XCTFail("Expected normal matching to continue from fallback anchor")
        }
        XCTAssertEqual(newPixelHeight, 90, accuracy: 5)
        XCTAssertEqual(stitcher.outputPixelHeight, 460, accuracy: 5)
    }

    func testTerminalFrameKeepsShortAmbiguousDocumentTail() throws {
        let source = try makeRepeatingStripedImage(width: 220, height: 520, period: 24)
        let first = try XCTUnwrap(source.cropping(to: CGRect(x: 0, y: 0, width: 220, height: 280)))
        let terminal = try XCTUnwrap(source.cropping(to: CGRect(x: 0, y: 75, width: 220, height: 280)))
        let stitcher = ScrollingImageStitcher(sampleWidth: 80)

        XCTAssertEqual(try stitcher.append(first), .firstFrame)
        XCTAssertEqual(
            try stitcher.append(
                terminal,
                constraint: .downwardOnly,
                preferredPixelShift: 75
            ),
            .rejected
        )

        let disposition = try stitcher.appendTerminalFrame(
            terminal,
            preferredPixelShift: 75
        )
        guard case .appended(let newPixelHeight, let confidence) = disposition else {
            return XCTFail("Expected the final short tail to be appended, got \(disposition)")
        }
        XCTAssertEqual(newPixelHeight, 75, accuracy: 2)
        XCTAssertLessThan(confidence, 0.9)
        XCTAssertEqual(stitcher.outputPixelHeight, 355, accuracy: 2)
    }

    func testDenseFramesAreTrackedThenCommittedWithoutLosingRows() throws {
        let source = try makeStripedImage(width: 240, height: 900)
        let stitcher = ScrollingImageStitcher(sampleWidth: 80)
        stitcher.minimumCommitPixelHeight = 100
        var sawTracking = false
        var appendedCount = 0
        // 13 px per frame: far below the commit threshold, like a slow
        // manual scroll sampled at a high frame rate.
        for y in stride(from: 0, through: 390, by: 13) {
            let frame = try XCTUnwrap(source.cropping(to: CGRect(x: 0, y: y, width: 240, height: 300)))
            switch try stitcher.append(frame) {
            case .tracking: sawTracking = true
            case .appended: appendedCount += 1
            case .rejected: XCTFail("Dense frame at \(y) must be matched")
            case .firstFrame, .duplicate: break
            }
        }
        XCTAssertTrue(sawTracking)
        XCTAssertLessThanOrEqual(appendedCount, 4)
        XCTAssertGreaterThan(stitcher.pendingPixelHeight, 0)

        _ = try stitcher.commitPending()

        XCTAssertEqual(stitcher.pendingPixelHeight, 0)
        XCTAssertEqual(stitcher.outputPixelHeight, 690, accuracy: 3)
        XCTAssertEqual(try stitcher.makeImage().height, 690, accuracy: 3)
    }

    func testTallRetinaFrameSeamIsRefinedToExactPixelRow() throws {
        // Taller than the 720-row matching sample, so the coarse shift is
        // quantised to ~2.5 px; the seam must still land on the exact row.
        let source = try makeStripedImage(width: 300, height: 2_600)
        let first = try XCTUnwrap(source.cropping(to: CGRect(x: 0, y: 0, width: 300, height: 1_800)))
        let second = try XCTUnwrap(source.cropping(to: CGRect(x: 0, y: 517, width: 300, height: 1_800)))
        let stitcher = ScrollingImageStitcher(sampleWidth: 96)

        _ = try stitcher.append(first)
        guard case .appended(let newPixelHeight, _) = try stitcher.append(second) else {
            return XCTFail("Expected appended frame")
        }
        XCTAssertEqual(newPixelHeight, 517, accuracy: 1)
    }

    func testUpwardTrackingCommitsPendingRowsAboveTheStart() throws {
        let source = try makeStripedImage(width: 220, height: 700)
        let stitcher = ScrollingImageStitcher(sampleWidth: 80)
        stitcher.minimumCommitPixelHeight = 80
        for y in stride(from: 300, through: 180, by: -15) {
            let frame = try XCTUnwrap(source.cropping(to: CGRect(x: 0, y: y, width: 220, height: 260)))
            XCTAssertNotEqual(try stitcher.append(frame, constraint: .upwardOnly), .rejected)
        }
        _ = try stitcher.commitPending()

        XCTAssertTrue(stitcher.segments.allSatisfy { $0.direction == .up })
        XCTAssertEqual(stitcher.outputPixelHeight, 380, accuracy: 3)
    }

    func testStitcherSplitsUltraLongOutputIntoBoundedParts() throws {
        let source = try makeStripedImage(width: 200, height: 760)
        let stitcher = ScrollingImageStitcher(sampleWidth: 80)
        for y in stride(from: 0, through: 360, by: 90) {
            let frame = try XCTUnwrap(source.cropping(to: CGRect(x: 0, y: y, width: 200, height: 280)))
            _ = try stitcher.append(frame)
        }

        let parts = try stitcher.makeImages(maximumPixelHeight: 240)

        XCTAssertGreaterThan(parts.count, 1)
        XCTAssertTrue(parts.allSatisfy { $0.height <= 240 })
        XCTAssertEqual(parts.reduce(0) { $0 + $1.height }, stitcher.outputPixelHeight)
    }

    func testManualSeamAdjustmentChangesOutputHeight() throws {
        let source = try makeStripedImage(width: 200, height: 520)
        let first = try XCTUnwrap(source.cropping(to: CGRect(x: 0, y: 0, width: 200, height: 260)))
        let second = try XCTUnwrap(source.cropping(to: CGRect(x: 0, y: 70, width: 200, height: 260)))
        let stitcher = ScrollingImageStitcher(sampleWidth: 80)
        _ = try stitcher.append(first)
        _ = try stitcher.append(second)
        let segment = try XCTUnwrap(stitcher.segments.first)
        let originalHeight = stitcher.outputPixelHeight

        XCTAssertTrue(stitcher.adjustSegment(id: segment.id, pixelDelta: -5))
        XCTAssertEqual(stitcher.outputPixelHeight, originalHeight - 5)
        XCTAssertEqual(try stitcher.makeImage().height, originalHeight - 5)
    }

    func testCodexLikeFixedSidebarAndComposerDoNotDropMiddleContent() throws {
        let stitcher = ScrollingImageStitcher(sampleWidth: 120)
        let firstFrame = try makeCodexLikeFrame(offsetY: 0)
        let firstExpected = codexContentColor(x: 160, documentY: 20)
        let firstActual = try pixel(in: firstFrame, x: 160, yFromTop: 20)
        XCTAssertEqual(firstActual.red, firstExpected.red, accuracy: 24)
        XCTAssertEqual(firstActual.green, firstExpected.green, accuracy: 24)
        XCTAssertEqual(firstActual.blue, firstExpected.blue, accuracy: 24)
        let secondFrame = try makeCodexLikeFrame(offsetY: 90)
        let seamExpected = codexContentColor(x: 160, documentY: 240)
        let seamInFrame = try pixel(in: secondFrame, x: 160, yFromTop: 150)
        XCTAssertLessThan(colorDistance(seamInFrame, seamExpected), 80)
        let seamCrop = try XCTUnwrap(secondFrame.cropping(to: CGRect(x: 0, y: 150, width: 260, height: 90)))
        let seamInCrop = try pixel(in: seamCrop, x: 160, yFromTop: 0)
        XCTAssertLessThan(colorDistance(seamInCrop, seamExpected), 80)
        for offset in [0, 90, 180, 270] {
            let disposition = try stitcher.append(makeCodexLikeFrame(offsetY: offset))
            XCTAssertNotEqual(disposition, .rejected)
        }

        let output = try stitcher.makeImage()

        for segment in stitcher.segments {
            XCTAssertEqual(segment.direction, .down)
            XCTAssertEqual(segment.newPixelHeight, 90, accuracy: 6)
            XCTAssertEqual(segment.stableBottomHeight, 60, accuracy: 6)
        }
        XCTAssertEqual(output.height, 570, accuracy: 6)
        for documentY in [20, 130, 220, 260, 350, 440, 495] {
            let actual = try pixel(in: output, x: 160, yFromTop: documentY)
            let localDistance = ((documentY - 8)...(documentY + 8))
                .map { colorDistance(actual, codexContentColor(x: 160, documentY: $0)) }
                .min() ?? .max
            XCTAssertLessThan(localDistance, 80, "Missing content near document row \(documentY)")
        }
    }

    private func makeStripedImage(width: Int, height: Int) throws -> CGImage {
        var pixels = [UInt8](repeating: 0, count: width * height * 4)
        for y in 0..<height {
            for x in 0..<width {
                let offset = (y * width + x) * 4
                pixels[offset] = UInt8((y + (x / 16) * 13 + (y / 17) * 31) % 256)
                pixels[offset + 1] = UInt8((y * 2 + (x / 24) * 29 + (y / 23) * 17) % 256)
                pixels[offset + 2] = UInt8((y * 3 + (x / 12) * 7 + (y / 31) * 43) % 256)
                pixels[offset + 3] = 255
            }
        }
        let data = Data(pixels) as CFData
        guard let provider = CGDataProvider(data: data),
              let image = CGImage(
                width: width,
                height: height,
                bitsPerComponent: 8,
                bitsPerPixel: 32,
                bytesPerRow: width * 4,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue)
                    .union(.byteOrder32Big),
                provider: provider,
                decode: nil,
                shouldInterpolate: false,
                intent: .defaultIntent
              ) else {
            throw StitcherTestError.couldNotCreateImage
        }
        return image
    }

    private func makeRepeatingStripedImage(
        width: Int,
        height: Int,
        period: Int
    ) throws -> CGImage {
        var pixels = [UInt8](repeating: 0, count: width * height * 4)
        for y in 0..<height {
            let phase = y % max(2, period)
            let value = UInt8((phase * 211 / max(1, period - 1)) + 20)
            for x in 0..<width {
                let offset = (y * width + x) * 4
                pixels[offset] = value
                pixels[offset + 1] = UInt8((Int(value) + x % 17) % 255)
                pixels[offset + 2] = UInt8((Int(value) + x % 29) % 255)
                pixels[offset + 3] = 255
            }
        }
        let data = Data(pixels) as CFData
        guard let provider = CGDataProvider(data: data),
              let image = CGImage(
                width: width,
                height: height,
                bitsPerComponent: 8,
                bitsPerPixel: 32,
                bytesPerRow: width * 4,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue)
                    .union(.byteOrder32Big),
                provider: provider,
                decode: nil,
                shouldInterpolate: false,
                intent: .defaultIntent
              ) else {
            throw StitcherTestError.couldNotCreateImage
        }
        return image
    }

    private func makeCodexLikeFrame(offsetY: Int) throws -> CGImage {
        let width = 260
        let height = 300
        let sidebarWidth = 72
        let composerHeight = 60
        var pixels = [UInt8](repeating: 0, count: width * height * 4)
        for y in 0..<height {
            for x in 0..<width {
                let offset = (y * width + x) * 4
                let color: (red: UInt8, green: UInt8, blue: UInt8)
                if x < sidebarWidth {
                    color = (
                        UInt8((x * 3 + y / 8 * 11 + 44) % 180),
                        UInt8((x * 7 + y / 10 * 13 + 38) % 180),
                        UInt8((x * 5 + y / 12 * 17 + 52) % 180)
                    )
                } else if y >= height - composerHeight {
                    color = (
                        UInt8((x * 2 + y * 3 + 60) % 200),
                        UInt8((x * 3 + y * 2 + 70) % 200),
                        UInt8((x * 5 + y + 80) % 200)
                    )
                } else {
                    color = codexContentColor(x: x, documentY: y + offsetY)
                }
                pixels[offset] = color.red
                pixels[offset + 1] = color.green
                pixels[offset + 2] = color.blue
                pixels[offset + 3] = 255
            }
        }
        let data = Data(pixels) as CFData
        guard let provider = CGDataProvider(data: data),
              let image = CGImage(
                width: width,
                height: height,
                bitsPerComponent: 8,
                bitsPerPixel: 32,
                bytesPerRow: width * 4,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue)
                    .union(.byteOrder32Big),
                provider: provider,
                decode: nil,
                shouldInterpolate: false,
                intent: .defaultIntent
              ) else {
            throw StitcherTestError.couldNotCreateImage
        }
        return image
    }

    private func codexContentColor(x: Int, documentY: Int) -> (red: UInt8, green: UInt8, blue: UInt8) {
        var value = UInt64(bitPattern: Int64(documentY &* 1_103_515_245 &+ x &* 12_345))
        value ^= value >> 17
        value &*= 0x9E3779B185EBCA87
        value ^= value >> 29
        return (
            UInt8(truncatingIfNeeded: value),
            UInt8(truncatingIfNeeded: value >> 16),
            UInt8(truncatingIfNeeded: value >> 32)
        )
    }

    private func colorDistance(
        _ lhs: (red: UInt8, green: UInt8, blue: UInt8),
        _ rhs: (red: UInt8, green: UInt8, blue: UInt8)
    ) -> Int {
        abs(Int(lhs.red) - Int(rhs.red))
            + abs(Int(lhs.green) - Int(rhs.green))
            + abs(Int(lhs.blue) - Int(rhs.blue))
    }

    private func makeDirectionalImage(
        width: Int,
        height: Int,
        splitY: Int,
        offsetY: Int = 0
    ) throws -> CGImage {
        var pixels = [UInt8](repeating: 0, count: width * height * 4)
        for y in 0..<height {
            for x in 0..<width {
                let offset = (y * width + x) * 4
                let globalY = y + offsetY
                let isTop = globalY < splitY
                let texture = UInt8((globalY * 17 + x * 13 + (globalY / 5) * 29) % 36)
                pixels[offset] = isTop ? 210 + texture : 20 + texture
                pixels[offset + 1] = 35 + texture
                pixels[offset + 2] = isTop ? 20 + texture : 210 + texture
                pixels[offset + 3] = 255
            }
        }
        let data = Data(pixels) as CFData
        guard let provider = CGDataProvider(data: data),
              let image = CGImage(
                width: width,
                height: height,
                bitsPerComponent: 8,
                bitsPerPixel: 32,
                bytesPerRow: width * 4,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue)
                    .union(.byteOrder32Big),
                provider: provider,
                decode: nil,
                shouldInterpolate: false,
                intent: .defaultIntent
              ) else {
            throw StitcherTestError.couldNotCreateImage
        }
        return image
    }

    private func pixel(in image: CGImage, x: Int, yFromTop: Int) throws -> (red: UInt8, green: UInt8, blue: UInt8) {
        // Encode and decode through the same AppKit path used by preview/save so
        // the assertion validates visual orientation instead of CGContext space.
        let encoded = try XCTUnwrap(
            NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:])
        )
        let decoded = try XCTUnwrap(NSBitmapImageRep(data: encoded))
        let color = try XCTUnwrap(
            decoded.colorAt(x: x, y: yFromTop)?.usingColorSpace(.deviceRGB)
        )
        return (
            UInt8((color.redComponent * 255).rounded()),
            UInt8((color.greenComponent * 255).rounded()),
            UInt8((color.blueComponent * 255).rounded())
        )
    }

    private func assertRed(
        _ pixel: (red: UInt8, green: UInt8, blue: UInt8),
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        XCTAssertGreaterThan(pixel.red, pixel.blue, file: file, line: line)
    }

    private func assertBlue(
        _ pixel: (red: UInt8, green: UInt8, blue: UInt8),
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        XCTAssertGreaterThan(pixel.blue, pixel.red, file: file, line: line)
    }
}

private enum StitcherTestError: Error {
    case couldNotCreateImage
}
