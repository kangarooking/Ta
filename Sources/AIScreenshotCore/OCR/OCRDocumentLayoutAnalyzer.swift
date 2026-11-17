import CoreGraphics
import Foundation

public struct OCRDocumentLayoutAnalyzer: Sendable {
    /// A table needs at least this many consecutive, column-aligned rows.
    static let minimumTableRows = 3
    /// Maximum horizontal drift (normalised image width) for two cells to
    /// count as the same column.
    static let columnTolerance: CGFloat = 0.03
    /// Looser tolerance for cell positions estimated from character offsets.
    static let estimatedColumnTolerance: CGFloat = 0.06

    public init() {}

    public func analyze(lines: [OCRTextLine]) -> OCRDocumentLayout {
        let rows = groupIntoRows(lines)
        return OCRDocumentLayout(
            blocks: makeBlocks(from: rows.flatMap { $0 }),
            tables: makeTables(from: rows)
        )
    }

    /// Lines in reading order: visual rows top to bottom, fragments of the
    /// same row left to right.
    public func orderedForReading(_ lines: [OCRTextLine]) -> [OCRTextLine] {
        groupIntoRows(lines).flatMap { $0 }
    }

    private func makeBlocks(from lines: [OCRTextLine]) -> [OCRDocumentBlock] {
        guard let first = lines.first else { return [] }
        var blocks: [[OCRTextLine]] = [[first]]
        for line in lines.dropFirst() {
            let previous = blocks[blocks.count - 1].last!
            let verticalGap = previous.boundingBox.minY - line.boundingBox.maxY
            let typicalHeight = max(0.01, (previous.boundingBox.height + line.boundingBox.height) / 2)
            let largeGap = verticalGap > typicalHeight * 2.4
            let majorIndentChange = abs(previous.boundingBox.minX - line.boundingBox.minX) > 0.28
            if largeGap || majorIndentChange {
                blocks.append([line])
            } else {
                blocks[blocks.count - 1].append(line)
            }
        }
        return blocks.map(OCRDocumentBlock.init(lines:))
    }

    // MARK: - Tables

    private struct Cell {
        let text: String
        let minX: CGFloat
        let midX: CGFloat
        let maxX: CGFloat
        let isEstimated: Bool
    }

    private struct CandidateRow {
        let lines: [OCRTextLine]
        let cells: [Cell]
        let midY: CGFloat
        let height: CGFloat
    }

    /// Detects tables conservatively: at least `minimumTableRows` consecutive
    /// visual rows whose cells line up in the same columns. Two rows that
    /// merely happen to be split into fragments (a navigation bar, a
    /// timestamp next to a headline) are not a table.
    private func makeTables(from visualRows: [[OCRTextLine]]) -> [OCRTable] {
        let candidates = visualRows.map(candidateRow)
        var tables: [OCRTable] = []
        var run: [CandidateRow] = []
        var mappings: [[Int]] = []

        func flush() {
            defer {
                run = []
                mappings = []
            }
            guard let reference = run.first, run.count >= Self.minimumTableRows else { return }
            let columnCount = reference.cells.count
            let fullRows = run.filter { $0.cells.count == columnCount }.count
            guard fullRows >= 2, fullRows * 2 >= run.count else { return }
            var rows: [[String]] = []
            for (row, mapping) in zip(run, mappings) {
                var values = Array(repeating: "", count: columnCount)
                for (cell, column) in zip(row.cells, mapping) {
                    values[column] = cell.text
                }
                rows.append(values)
            }
            tables.append(OCRTable(rows: rows, sourceLines: run.flatMap(\.lines)))
        }

        for candidate in candidates {
            guard candidate.cells.count >= 2 else {
                flush()
                continue
            }
            if let reference = run.first, let previous = run.last,
               isVerticallyAdjacent(previous, candidate),
               let mapping = columnMapping(of: candidate, onto: reference) {
                run.append(candidate)
                mappings.append(mapping)
            } else {
                flush()
                run = [candidate]
                mappings = [Array(0..<candidate.cells.count)]
            }
        }
        flush()
        return tables
    }

    private func candidateRow(_ row: [OCRTextLine]) -> CandidateRow {
        let midY = row.first?.boundingBox.midY ?? 0
        let height = row.map(\.boundingBox.height).max() ?? 0
        let cells: [Cell]
        if row.count > 1 {
            cells = row.map { line in
                Cell(
                    text: line.text.trimmingCharacters(in: .whitespacesAndNewlines),
                    minX: line.boundingBox.minX,
                    midX: line.boundingBox.midX,
                    maxX: line.boundingBox.maxX,
                    isEstimated: false
                )
            }
        } else if let line = row.first {
            cells = whitespaceSeparatedCells(of: line)
        } else {
            cells = []
        }
        return CandidateRow(lines: row, cells: cells, midY: midY, height: height)
    }

    /// Splits one recognised line on runs of two or more spaces (or tabs) and
    /// estimates each cell's horizontal position from its character offset.
    private func whitespaceSeparatedCells(of line: OCRTextLine) -> [Cell] {
        let text = line.text as NSString
        guard text.length > 0,
              let regex = try? NSRegularExpression(pattern: "\\s{2,}|\\t") else { return [] }
        var ranges: [NSRange] = []
        var cursor = 0
        for match in regex.matches(in: line.text, range: NSRange(location: 0, length: text.length)) {
            ranges.append(NSRange(location: cursor, length: match.range.location - cursor))
            cursor = match.range.location + match.range.length
        }
        ranges.append(NSRange(location: cursor, length: text.length - cursor))

        let box = line.boundingBox
        let total = CGFloat(text.length)
        return ranges.compactMap { range -> Cell? in
            guard range.length > 0 else { return nil }
            let value = text.substring(with: range).trimmingCharacters(in: .whitespacesAndNewlines)
            guard !value.isEmpty else { return nil }
            let start = box.minX + box.width * CGFloat(range.location) / total
            let end = box.minX + box.width * CGFloat(range.location + range.length) / total
            return Cell(text: value, minX: start, midX: (start + end) / 2, maxX: end, isEstimated: true)
        }
    }

    private func isVerticallyAdjacent(_ previous: CandidateRow, _ next: CandidateRow) -> Bool {
        let typicalHeight = max(0.01, max(previous.height, next.height))
        return abs(previous.midY - next.midY) <= typicalHeight * 5
    }

    /// Maps every cell of `row` onto a distinct column of `reference`, in
    /// order. Returns nil when a cell does not line up with any column.
    private func columnMapping(of row: CandidateRow, onto reference: CandidateRow) -> [Int]? {
        guard row.cells.count <= reference.cells.count else { return nil }
        var mapping: [Int] = []
        var nextColumn = 0
        for cell in row.cells {
            var matched: Int?
            var column = nextColumn
            while column < reference.cells.count {
                if isAligned(cell, reference.cells[column]) {
                    matched = column
                    break
                }
                column += 1
            }
            guard let matched else { return nil }
            mapping.append(matched)
            nextColumn = matched + 1
        }
        return mapping
    }

    private func isAligned(_ lhs: Cell, _ rhs: Cell) -> Bool {
        let tolerance = (lhs.isEstimated || rhs.isEstimated)
            ? Self.estimatedColumnTolerance
            : Self.columnTolerance
        return abs(lhs.minX - rhs.minX) <= tolerance
            || abs(lhs.midX - rhs.midX) <= tolerance
            || abs(lhs.maxX - rhs.maxX) <= tolerance
    }

    // MARK: - Rows

    /// Groups lines into visual rows (top to bottom); each row is sorted
    /// left to right.
    private func groupIntoRows(_ lines: [OCRTextLine]) -> [[OCRTextLine]] {
        var rows: [[OCRTextLine]] = []
        for line in lines {
            if let index = rows.firstIndex(where: { row in
                guard let first = row.first else { return false }
                let tolerance = max(0.012, max(first.boundingBox.height, line.boundingBox.height) * 0.65)
                return abs(first.boundingBox.midY - line.boundingBox.midY) <= tolerance
            }) {
                rows[index].append(line)
            } else {
                rows.append([line])
            }
        }
        return rows
            .map { row in row.sorted { $0.boundingBox.minX < $1.boundingBox.minX } }
            .sorted { ($0.first?.boundingBox.midY ?? 0) > ($1.first?.boundingBox.midY ?? 0) }
    }
}
