@preconcurrency import Vision
import CoreGraphics
import Foundation

public struct VisionOCRService: Sendable {
    public init() {}

    public func recognize(
        image: CGImage,
        languages: [String],
        mergeWrappedLines: Bool
    ) async throws -> OCRResult {
        if #available(macOS 26.0, *) {
            return try await VisionOCRFallbackRunner().run(
                document: {
                    try await recognizeDocument(
                        image: image,
                        languages: languages,
                        mergeWrappedLines: mergeWrappedLines
                    )
                },
                legacy: {
                    try await recognizeLegacy(
                        image: image,
                        languages: languages,
                        mergeWrappedLines: mergeWrappedLines
                    )
                }
            )
        }
        return try await recognizeLegacy(
            image: image,
            languages: languages,
            mergeWrappedLines: mergeWrappedLines
        )
    }

    private func recognizeLegacy(
        image: CGImage,
        languages: [String],
        mergeWrappedLines: Bool
    ) async throws -> OCRResult {
        return try await Task.detached(priority: .userInitiated) {
            let request = VNRecognizeTextRequest()
            request.recognitionLevel = .accurate
            request.usesLanguageCorrection = true
            request.automaticallyDetectsLanguage = true
            if !languages.isEmpty {
                request.recognitionLanguages = languages
            }

            let barcodeRequest = VNDetectBarcodesRequest()
            let handler = VNImageRequestHandler(cgImage: image, options: [:])
            try handler.perform([request, barcodeRequest])

            let lines = (request.results ?? [])
                .compactMap { observation -> OCRTextLine? in
                    guard let candidate = observation.topCandidates(1).first else { return nil }
                    return OCRTextLine(
                        text: candidate.string,
                        confidence: candidate.confidence,
                        boundingBox: observation.boundingBox
                    )
                }
                .sorted { lhs, rhs in
                    let verticalDistance = abs(lhs.boundingBox.midY - rhs.boundingBox.midY)
                    if verticalDistance < 0.015 {
                        return lhs.boundingBox.minX < rhs.boundingBox.minX
                    }
                    return lhs.boundingBox.midY > rhs.boundingBox.midY
                }

            let barcodes = (barcodeRequest.results ?? []).compactMap { observation -> DetectedBarcode? in
                guard let payload = observation.payloadStringValue, !payload.isEmpty else { return nil }
                return DetectedBarcode(
                    payload: payload,
                    symbology: observation.symbology.rawValue,
                    boundingBox: observation.boundingBox
                )
            }
            return OCRResultAssembler().assemble(
                lines: lines,
                barcodes: barcodes,
                languages: languages,
                mergeWrappedLines: mergeWrappedLines
            )
        }.value
    }

    @available(macOS 26.0, *)
    private func recognizeDocument(
        image: CGImage,
        languages: [String],
        mergeWrappedLines: Bool
    ) async throws -> OCRResult {
        var request = RecognizeDocumentsRequest()
        request.textRecognitionOptions.automaticallyDetectLanguage = true
        request.textRecognitionOptions.useLanguageCorrection = true
        request.textRecognitionOptions.maximumCandidateCount = 1
        if !languages.isEmpty {
            request.textRecognitionOptions.recognitionLanguages = languages.map(Locale.Language.init(identifier:))
        }
        request.barcodeDetectionOptions.enabled = true
        let observations = try await request.perform(on: image)

        let lines = observations.flatMap { observation in
            observation.document.text.lines.map { line in
                OCRTextLine(
                    text: line.transcript,
                    confidence: line.confidence,
                    boundingBox: line.boundingRegion.normalizedPath.boundingBox
                )
            }
        }.sorted { lhs, rhs in
            let verticalDistance = abs(lhs.boundingBox.midY - rhs.boundingBox.midY)
            if verticalDistance < 0.015 { return lhs.boundingBox.minX < rhs.boundingBox.minX }
            return lhs.boundingBox.midY > rhs.boundingBox.midY
        }

        let nativeBlocks = observations.flatMap { observation in
            observation.document.paragraphs.map { paragraph in
                OCRDocumentBlock(lines: paragraph.lines.map { line in
                    OCRTextLine(
                        text: line.transcript,
                        confidence: line.confidence,
                        boundingBox: line.boundingRegion.normalizedPath.boundingBox
                    )
                })
            }
        }
        let nativeTables = observations.flatMap { observation in
            observation.document.tables.compactMap { table -> OCRTable? in
                let rows = table.rows.map { row in row.map { $0.content.text.transcript } }
                return rows.isEmpty ? nil : OCRTable(rows: rows)
            }
        }
        let fallbackLayout = OCRDocumentLayoutAnalyzer().analyze(lines: lines)
        let layout = OCRDocumentLayout(
            blocks: nativeBlocks.isEmpty ? fallbackLayout.blocks : nativeBlocks,
            tables: nativeTables.isEmpty ? fallbackLayout.tables : nativeTables
        )
        let barcodes = observations.flatMap { observation in
            observation.document.barcodes.compactMap { barcode -> DetectedBarcode? in
                guard let payload = barcode.payloadString, !payload.isEmpty else { return nil }
                return DetectedBarcode(
                    payload: payload,
                    symbology: String(describing: barcode.symbology),
                    boundingBox: barcode.boundingRegion.normalizedPath.boundingBox
                )
            }
        }

        return OCRResultAssembler().assemble(
            lines: lines,
            layout: layout,
            barcodes: barcodes,
            languages: languages,
            mergeWrappedLines: mergeWrappedLines
        )
    }
}

struct VisionOCRFallbackRunner {
    func run(
        document: () async throws -> OCRResult,
        legacy: () async throws -> OCRResult
    ) async throws -> OCRResult {
        do {
            return try await document()
        } catch let error as CancellationError {
            throw error
        } catch {
            try Task.checkCancellation()
            return try await legacy()
        }
    }
}
