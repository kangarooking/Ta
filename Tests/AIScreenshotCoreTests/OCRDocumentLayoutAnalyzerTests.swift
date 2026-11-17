import CoreGraphics
import XCTest
@testable import AIScreenshotCore

final class OCRDocumentLayoutAnalyzerTests: XCTestCase {
    func testDetectsRowsAndCellsAsTable() {
        let lines = [
            line("Name", x: 0.1, y: 0.8), line("Score", x: 0.55, y: 0.8),
            line("Alice", x: 0.1, y: 0.7), line("98", x: 0.55, y: 0.7),
            line("Bob", x: 0.1, y: 0.6), line("95", x: 0.55, y: 0.6)
        ]

        let document = OCRDocumentLayoutAnalyzer().analyze(lines: lines)

        XCTAssertEqual(document.tables.first?.rows, [["Name", "Score"], ["Alice", "98"], ["Bob", "95"]])
        XCTAssertTrue(document.tables.first?.markdown.contains("| Name | Score |") == true)
    }

    func testSeparatesParagraphsAcrossLargeVerticalGap() {
        let lines = [
            line("Heading", x: 0.1, y: 0.85),
            line("First paragraph", x: 0.1, y: 0.78),
            line("Second section", x: 0.1, y: 0.42)
        ]

        let document = OCRDocumentLayoutAnalyzer().analyze(lines: lines)

        XCTAssertEqual(document.blocks.count, 2)
        XCTAssertEqual(document.blocks.last?.text, "Second section")
    }

    func testSplitsWhitespaceSeparatedCells() {
        let lines = [
            line("Product   Price   Qty", x: 0.1, y: 0.8),
            line("Tea       12      2", x: 0.1, y: 0.7),
            line("Coffee    18      1", x: 0.1, y: 0.6)
        ]

        let document = OCRDocumentLayoutAnalyzer().analyze(lines: lines)

        XCTAssertEqual(document.tables.first?.rows.first, ["Product", "Price", "Qty"])
    }

    func testTwoFragmentedRowsAreNotATable() {
        let lines = [
            line("Home", x: 0.1, y: 0.9), line("Docs", x: 0.5, y: 0.9),
            line("Updated", x: 0.1, y: 0.8), line("10:32", x: 0.5, y: 0.8)
        ]
        XCTAssertTrue(OCRDocumentLayoutAnalyzer().analyze(lines: lines).tables.isEmpty)
    }

    func testMisalignedFragmentRowsAreNotATable() {
        let lines = [
            line("Home", x: 0.05, y: 0.9, width: 0.1), line("Docs", x: 0.30, y: 0.9, width: 0.1),
            line("Posted", x: 0.12, y: 0.8, width: 0.1), line("10:32", x: 0.62, y: 0.8, width: 0.1),
            line("Share", x: 0.20, y: 0.7, width: 0.1), line("Reply", x: 0.84, y: 0.7, width: 0.1)
        ]
        XCTAssertTrue(OCRDocumentLayoutAnalyzer().analyze(lines: lines).tables.isEmpty)
    }

    func testArticleWithNavigationBarKeepsAllTextAndIsNotATable() {
        let lines = [
            line("Home", x: 0.05, y: 0.92, width: 0.08), line("Docs", x: 0.2, y: 0.92, width: 0.08),
            line("Blog", x: 0.35, y: 0.92, width: 0.08), line("About", x: 0.5, y: 0.92, width: 0.08),
            line("Article heading", x: 0.05, y: 0.8, width: 0.6),
            line("First paragraph line", x: 0.05, y: 0.72, width: 0.8),
            line("Updated", x: 0.05, y: 0.6, width: 0.12), line("10:32", x: 0.8, y: 0.6, width: 0.1)
        ]

        let result = OCRResultAssembler().assemble(lines: lines.shuffled())

        XCTAssertTrue(result.document.tables.isEmpty)
        XCTAssertEqual(result.contentType, .plainText)
        XCTAssertEqual(
            result.text,
            "Home\nDocs\nBlog\nAbout\nArticle heading\nFirst paragraph line\nUpdated\n10:32"
        )
        XCTAssertEqual(result.markdownText, result.text)
        XCTAssertFalse(result.hasMarkdownAlternative)
    }

    func testTableIsRenderedInPlaceBetweenSurroundingText() {
        let lines = [
            line("Quarterly report", x: 0.1, y: 0.9, width: 0.5),
            line("Name", x: 0.1, y: 0.8), line("Score", x: 0.55, y: 0.8),
            line("Alice", x: 0.1, y: 0.7), line("98", x: 0.55, y: 0.7),
            line("Bob", x: 0.1, y: 0.6), line("95", x: 0.55, y: 0.6),
            line("Source: internal", x: 0.1, y: 0.5, width: 0.5)
        ]

        let result = OCRResultAssembler().assemble(lines: lines)

        XCTAssertEqual(
            result.text,
            "Quarterly report\nName\tScore\nAlice\t98\nBob\t95\nSource: internal"
        )
        XCTAssertEqual(
            result.markdownText,
            "Quarterly report\n\n| Name | Score |\n| --- | --- |\n| Alice | 98 |\n| Bob | 95 |\n\nSource: internal"
        )
        XCTAssertTrue(result.hasMarkdownAlternative)
        XCTAssertEqual(result.contentType, .table)
        XCTAssertEqual(result.document.tables.count, 1)
    }

    func testSmallTableInsideLongDocumentDoesNotMakeTheWholeResultATable() {
        var lines = [
            line("Name", x: 0.1, y: 0.95, height: 0.02), line("Score", x: 0.55, y: 0.95, height: 0.02),
            line("Alice", x: 0.1, y: 0.92, height: 0.02), line("98", x: 0.55, y: 0.92, height: 0.02),
            line("Bob", x: 0.1, y: 0.89, height: 0.02), line("95", x: 0.55, y: 0.89, height: 0.02)
        ]
        for index in 0..<10 {
            lines.append(line(
                "Paragraph sentence number \(index) continues here.",
                x: 0.1,
                y: 0.8 - CGFloat(index) * 0.05,
                width: 0.8,
                height: 0.02
            ))
        }

        let result = OCRResultAssembler().assemble(lines: lines)

        XCTAssertEqual(result.document.tables.count, 1)
        XCTAssertNotEqual(result.contentType, .table)
        XCTAssertTrue(result.text.contains("Alice\t98"))
        XCTAssertTrue(result.text.contains("Paragraph sentence number 9 continues here."))
    }

    func testNativeTableWithoutGeometryIsMatchedByCellText() {
        let lines = [
            line("Intro", x: 0.1, y: 0.9),
            line("Name", x: 0.1, y: 0.8), line("Score", x: 0.55, y: 0.8),
            line("Alice", x: 0.1, y: 0.7), line("98", x: 0.55, y: 0.7),
            line("Outro", x: 0.1, y: 0.5)
        ]
        let layout = OCRDocumentLayout(
            blocks: [OCRDocumentBlock(lines: lines)],
            tables: [OCRTable(rows: [["Name", "Score"], ["Alice", "98"]])]
        )

        let result = OCRResultAssembler().assemble(lines: lines, layout: layout)

        XCTAssertEqual(result.text, "Intro\nName\tScore\nAlice\t98\nOutro")
    }

    func testBarcodePayloadIsReturnedAlongsideText() {
        let barcode = DetectedBarcode(
            payload: "https://example.com/pay",
            symbology: "QR",
            boundingBox: CGRect(x: 0.6, y: 0.2, width: 0.2, height: 0.2)
        )

        let withText = OCRResultAssembler().assemble(
            lines: [line("Scan to pay", x: 0.1, y: 0.8)],
            barcodes: [barcode]
        )
        XCTAssertEqual(withText.text, "Scan to pay\nhttps://example.com/pay")
        XCTAssertEqual(withText.barcodes, [barcode])

        let onlyCode = OCRResultAssembler().assemble(lines: [], barcodes: [barcode])
        XCTAssertEqual(onlyCode.text, "https://example.com/pay")
        XCTAssertEqual(onlyCode.contentType, .qrCode)

        let alreadyVisible = OCRResultAssembler().assemble(
            lines: [line("https://example.com/pay", x: 0.1, y: 0.8, width: 0.6)],
            barcodes: [barcode]
        )
        XCTAssertEqual(alreadyVisible.text, "https://example.com/pay")
    }

    func testMarkdownEscapesPipesAndPadsShortRows() {
        let table = OCRTable(rows: [["a|b", "c"], ["only"]])
        XCTAssertEqual(table.markdown, "| a\\|b | c |\n| --- | --- |\n| only |  |")
        XCTAssertEqual(OCRResult(text: "plain", contentType: .plainText, confidence: 1).markdownText, "plain")
    }

    private func line(
        _ text: String,
        x: CGFloat,
        y: CGFloat,
        width: CGFloat = 0.25,
        height: CGFloat = 0.04
    ) -> OCRTextLine {
        OCRTextLine(text: text, confidence: 0.95, boundingBox: CGRect(x: x, y: y, width: width, height: height))
    }
}
