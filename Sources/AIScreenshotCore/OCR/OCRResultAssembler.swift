import CoreGraphics
import Foundation

/// Builds an `OCRResult` from positioned text lines. Shared by Apple Vision
/// and the optional OCR packs so every engine produces the same structure:
/// all recognised text in reading order, tables rendered in place, barcode
/// payloads appended, plus a Markdown variant.
public struct OCRResultAssembler: Sendable {
    public init() {}

    public func assemble(
        lines: [OCRTextLine],
        layout: OCRDocumentLayout? = nil,
        barcodes: [DetectedBarcode] = [],
        languages: [String] = [],
        mergeWrappedLines: Bool = false,
        engine: OCREnginePreference = .appleVision,
        confidence: Float? = nil
    ) -> OCRResult {
        let document = layout ?? OCRDocumentLayoutAnalyzer().analyze(lines: lines)
        let plain = compose(
            lines: lines,
            tables: document.tables,
            mergeWrappedLines: mergeWrappedLines,
            markdownTables: false
        )
        let markdown = compose(
            lines: lines,
            tables: document.tables,
            mergeWrappedLines: mergeWrappedLines,
            markdownTables: true
        )

        let contentType: CaptureContentType
        if lines.isEmpty, !barcodes.isEmpty {
            contentType = .qrCode
        } else if plain.renderedTableCount > 0, plain.tableLineCount * 2 >= max(1, lines.count) {
            contentType = .table
        } else {
            contentType = ContentClassifier().classify(plain.text)
        }

        let averageConfidence: Float = lines.isEmpty
            ? 0
            : lines.map(\.confidence).reduce(0, +) / Float(lines.count)

        return OCRResult(
            text: appendingBarcodePayloads(barcodes, to: plain.text),
            contentType: contentType,
            confidence: confidence ?? averageConfidence,
            languages: languages,
            document: document,
            barcodes: barcodes,
            engine: engine,
            markdownText: appendingBarcodePayloads(barcodes, to: markdown.text)
        )
    }

    /// All recognised text in reading order with tables rendered in place
    /// (TSV, or Markdown when `markdownTables` is true).
    public func composeText(
        lines: [OCRTextLine],
        tables: [OCRTable],
        mergeWrappedLines: Bool = false,
        markdownTables: Bool = false
    ) -> String {
        compose(
            lines: lines,
            tables: tables,
            mergeWrappedLines: mergeWrappedLines,
            markdownTables: markdownTables
        ).text
    }

    private struct Composition {
        let text: String
        let renderedTableCount: Int
        let tableLineCount: Int
    }

    private func compose(
        lines: [OCRTextLine],
        tables: [OCRTable],
        mergeWrappedLines: Bool,
        markdownTables: Bool
    ) -> Composition {
        let ordered = OCRDocumentLayoutAnalyzer().orderedForReading(lines)
        let normalizer = OCRTextNormalizer()

        // Which table (if any) owns each line.
        var owners = [Int?](repeating: nil, count: ordered.count)
        for (tableIndex, table) in tables.enumerated() where table.rows.count >= 2 {
            if !table.sourceLines.isEmpty {
                for index in ordered.indices where owners[index] == nil {
                    if table.sourceLines.contains(ordered[index]) {
                        owners[index] = tableIndex
                    }
                }
            } else {
                let cells = table.rows.flatMap { $0 }.map(Self.compact).filter { !$0.isEmpty }
                for index in ordered.indices where owners[index] == nil {
                    let candidate = Self.compact(ordered[index].text)
                    guard !candidate.isEmpty else { continue }
                    let belongs = cells.contains { cell in
                        cell == candidate || (candidate.count >= 4 && cell.contains(candidate))
                    }
                    if belongs { owners[index] = tableIndex }
                }
            }
        }

        var pieces: [String] = []
        var pending: [String] = []
        var rendered = Set<Int>()

        func flushPending() {
            guard !pending.isEmpty else { return }
            let text = normalizer.normalize(pending.joined(separator: "\n"), mergeWrappedLines: mergeWrappedLines)
            if !text.isEmpty { pieces.append(text) }
            pending = []
        }

        func render(_ table: OCRTable) -> String {
            markdownTables ? table.markdown : table.tsv
        }

        for (index, line) in ordered.enumerated() {
            if let tableIndex = owners[index] {
                if rendered.insert(tableIndex).inserted {
                    flushPending()
                    let text = render(tables[tableIndex])
                    if !text.isEmpty { pieces.append(text) }
                }
            } else {
                pending.append(line.text)
            }
        }
        flushPending()

        // A table whose cells could not be matched to any line is appended
        // rather than dropped: duplicated text is better than lost text.
        for (tableIndex, table) in tables.enumerated()
        where table.rows.count >= 2 && !rendered.contains(tableIndex) {
            let text = render(table)
            if !text.isEmpty {
                pieces.append(text)
                rendered.insert(tableIndex)
            }
        }

        return Composition(
            text: pieces.joined(separator: markdownTables ? "\n\n" : "\n"),
            renderedTableCount: rendered.count,
            tableLineCount: owners.filter { $0 != nil }.count
        )
    }

    private func appendingBarcodePayloads(_ barcodes: [DetectedBarcode], to text: String) -> String {
        var result = text
        for barcode in barcodes {
            let payload = barcode.payload.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !payload.isEmpty, !result.contains(payload) else { continue }
            result = result.isEmpty ? payload : result + "\n" + payload
        }
        return result
    }

    private static func compact(_ value: String) -> String {
        var scalars = String.UnicodeScalarView()
        for scalar in value.unicodeScalars where !CharacterSet.whitespacesAndNewlines.contains(scalar) {
            scalars.append(scalar)
        }
        return String(scalars)
    }
}
