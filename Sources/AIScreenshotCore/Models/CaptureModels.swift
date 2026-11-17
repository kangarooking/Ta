import Foundation

public enum CaptureMode: String, CaseIterable, Codable, Sendable {
    case interactive
    case intelligent
    case translation
    case image
    case pin
    case long
}

public enum PostCaptureAction: String, CaseIterable, Codable, Sendable {
    case choose
    case recognize
    case translateText
    case copyImage
    case pin
    case edit
    case rememberLast
}

public enum CaptureQuickAction: String, CaseIterable, Codable, Sendable {
    case localOCR
    case multimodal
    case translate
    case copyImage
    case pin
    case edit
    case beautify
    case save
    case translateText
}

public enum RecognitionRoute: String, CaseIterable, Codable, Sendable {
    case localOCR
    case multimodal
    case smart
}

public enum OCREnginePreference: String, CaseIterable, Codable, Sendable {
    case appleVision
    case rapidOCR
    case paddleOCR
    case deepSeekOCR2

    public var displayName: String {
        switch self {
        case .appleVision: "Apple Vision（内置）"
        case .rapidOCR: "RapidOCR 增强包"
        case .paddleOCR: "PaddleOCR 增强包"
        case .deepSeekOCR2: "DeepSeek-OCR-2（最新）"
        }
    }

    public var isLocalEngine: Bool {
        self != .deepSeekOCR2
    }

    public var usesOptionalPack: Bool {
        self == .rapidOCR || self == .paddleOCR
    }
}

public extension CaptureMode {
    var directAction: CaptureQuickAction? {
        switch self {
        case .interactive, .long:
            nil
        case .intelligent:
            .localOCR
        case .translation:
            .translateText
        case .image:
            .copyImage
        case .pin:
            .pin
        }
    }
}

public enum CaptureContentType: String, CaseIterable, Codable, Sendable {
    case plainText
    case code
    case table
    case qrCode
    case formula
    case image
}

public struct OCRTextLine: Equatable, Sendable {
    public let text: String
    public let confidence: Float
    public let boundingBox: CGRect

    public init(text: String, confidence: Float, boundingBox: CGRect) {
        self.text = text
        self.confidence = confidence
        self.boundingBox = boundingBox
    }
}

public struct OCRDocumentBlock: Equatable, Sendable {
    public let lines: [OCRTextLine]
    public var text: String { lines.map(\.text).joined(separator: "\n") }
    public init(lines: [OCRTextLine]) { self.lines = lines }
}

public struct OCRTable: Equatable, Sendable {
    public let rows: [[String]]
    /// The recognised lines this table was built from. Empty when the table
    /// came from an engine that only reports cell text; consumers then fall
    /// back to matching cell text against the recognised lines.
    public let sourceLines: [OCRTextLine]

    public init(rows: [[String]], sourceLines: [OCRTextLine] = []) {
        self.rows = rows
        self.sourceLines = sourceLines
    }

    public var columnCount: Int { rows.map(\.count).max() ?? 0 }

    public var markdown: String {
        let columnCount = self.columnCount
        guard columnCount > 0 else { return "" }
        let normalized = rows.map { row in
            (row + Array(repeating: "", count: max(0, columnCount - row.count))).map(Self.markdownCell)
        }
        let header = "| " + normalized[0].joined(separator: " | ") + " |"
        let divider = "| " + Array(repeating: "---", count: columnCount).joined(separator: " | ") + " |"
        let body = normalized.dropFirst().map { "| " + $0.joined(separator: " | ") + " |" }
        return ([header, divider] + body).joined(separator: "\n")
    }

    public var tsv: String {
        rows.map { row in row.map(Self.tsvCell).joined(separator: "\t") }.joined(separator: "\n")
    }

    private static func markdownCell(_ value: String) -> String {
        value
            .replacingOccurrences(of: "\r\n", with: " ")
            .replacingOccurrences(of: "\n", with: " ")
            .replacingOccurrences(of: "|", with: "\\|")
    }

    private static func tsvCell(_ value: String) -> String {
        value
            .replacingOccurrences(of: "\r\n", with: " ")
            .replacingOccurrences(of: "\n", with: " ")
            .replacingOccurrences(of: "\t", with: " ")
    }
}

public struct OCRDocumentLayout: Equatable, Sendable {
    public let blocks: [OCRDocumentBlock]
    public let tables: [OCRTable]
    public init(blocks: [OCRDocumentBlock], tables: [OCRTable]) {
        self.blocks = blocks
        self.tables = tables
    }
}

public struct DetectedBarcode: Equatable, Sendable {
    public let payload: String
    public let symbology: String
    public let boundingBox: CGRect
    public init(payload: String, symbology: String, boundingBox: CGRect) {
        self.payload = payload
        self.symbology = symbology
        self.boundingBox = boundingBox
    }
}

public enum CaptureJobPhase: String, Codable, Sendable {
    case idle
    case selecting
    case captured
    case localProcessing
    case clipboardCommitted
    case resultShown
    case editing
    case exporting
    case cancelled
    case failed
}

public struct CaptureJob: Identifiable, Equatable, Sendable {
    public let id: UUID
    public let mode: CaptureMode
    public private(set) var phase: CaptureJobPhase
    public let createdAt: Date

    public init(
        id: UUID = UUID(),
        mode: CaptureMode,
        phase: CaptureJobPhase = .idle,
        createdAt: Date = Date()
    ) {
        self.id = id
        self.mode = mode
        self.phase = phase
        self.createdAt = createdAt
    }

    public mutating func transition(to next: CaptureJobPhase) throws {
        guard Self.allowedTransitions[phase, default: []].contains(next) else {
            throw CaptureJobError.invalidTransition(from: phase, to: next)
        }
        phase = next
    }

    private static let allowedTransitions: [CaptureJobPhase: Set<CaptureJobPhase>] = [
        .idle: [.selecting, .cancelled],
        .selecting: [.captured, .cancelled, .failed],
        .captured: [.localProcessing, .clipboardCommitted, .failed],
        .localProcessing: [.clipboardCommitted, .resultShown, .cancelled, .failed],
        .clipboardCommitted: [.resultShown, .editing, .exporting],
        .resultShown: [.editing, .exporting],
        .editing: [.exporting, .resultShown],
        .exporting: [.resultShown, .failed],
        .cancelled: [],
        .failed: []
    ]
}

public enum CaptureJobError: Error, Equatable, Sendable {
    case invalidTransition(from: CaptureJobPhase, to: CaptureJobPhase)
}

public struct OCRResult: Equatable, Sendable {
    public let text: String
    public let contentType: CaptureContentType
    public let confidence: Float
    public let languages: [String]
    public let document: OCRDocumentLayout
    public let barcodes: [DetectedBarcode]
    public let engine: OCREnginePreference
    /// Same content as `text`, but with detected tables rendered as Markdown
    /// tables instead of TSV. Equal to `text` when there is no table.
    public let markdownText: String

    public init(
        text: String,
        contentType: CaptureContentType,
        confidence: Float,
        languages: [String] = [],
        document: OCRDocumentLayout = OCRDocumentLayout(blocks: [], tables: []),
        barcodes: [DetectedBarcode] = [],
        engine: OCREnginePreference = .appleVision,
        markdownText: String? = nil
    ) {
        self.text = text
        self.contentType = contentType
        self.confidence = confidence
        self.languages = languages
        self.document = document
        self.barcodes = barcodes
        self.engine = engine
        self.markdownText = markdownText ?? text
    }

    /// True when `markdownText` offers a different (Markdown table) rendering.
    public var hasMarkdownAlternative: Bool { markdownText != text }

    public var isLowConfidence: Bool {
        confidence < 0.72
    }
}
