import Foundation

public struct ContentClassifier: Sendable {
    public init() {}

    public func classify(_ text: String) -> CaptureContentType {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return .image }

        if looksLikeQRCodePayload(trimmed) {
            return .qrCode
        }

        if looksLikeTable(trimmed) {
            return .table
        }

        if looksLikeFormula(trimmed) {
            return .formula
        }

        if looksLikeCode(trimmed) {
            return .code
        }

        return .plainText
    }

    private func looksLikeQRCodePayload(_ text: String) -> Bool {
        guard !text.contains("\n") else { return false }
        return text.range(of: #"^(https?://|mailto:|tel:|otpauth://|WIFI:)"#, options: [.regularExpression, .caseInsensitive]) != nil
    }

    private func looksLikeTable(_ text: String) -> Bool {
        let rows = text.split(separator: "\n", omittingEmptySubsequences: true)
        guard rows.count >= 2 else { return false }

        let tabCounts = rows.map { $0.filter { $0 == "\t" }.count }
        if let first = tabCounts.first, first > 0,
           tabCounts.filter({ $0 == first }).count >= max(2, rows.count - 1) {
            return true
        }

        let pipeCounts = rows.map { $0.filter { $0 == "|" }.count }
        if let first = pipeCounts.first, first >= 2,
           pipeCounts.filter({ $0 == first }).count >= max(2, rows.count - 1) {
            return true
        }

        let multiColumnRows = rows.filter {
            $0.range(of: #"\S\s{2,}\S"#, options: .regularExpression) != nil
        }
        return multiColumnRows.count >= max(2, rows.count - 1)
    }

    // MARK: - Code

    /// Keywords that practically only occur in source code.
    private static let strongCodeKeywords: [String] = [
        "func", "def", "fn", "struct", "enum", "const", "var", "val", "void", "async", "await",
        "lambda", "elif", "typedef", "namespace", "extends", "implements", "println", "printf",
        "nil", "nullptr", "require", "console"
    ]

    /// Keywords that are also ordinary English words ("class", "return",
    /// "public", "this"). Together they count as a single token at most, so
    /// prose can never be classified as code on vocabulary alone.
    private static let weakCodeKeywords: [String] = [
        "class", "protocol", "interface", "import", "return", "let", "function", "static",
        "public", "private", "export", "null", "self", "this"
    ]

    private static let codeOperators: [String] = [
        "==", "!=", "=>", "->", "::", "&&", "||", "+=", "-=", ":=", "<=", ">=", "</", "/>", "++"
    ]

    private func looksLikeCode(_ text: String) -> Bool {
        let lines = text.components(separatedBy: "\n")

        if lines.first?.hasPrefix("#!/") == true { return true }

        let strongHits = Self.strongCodeKeywords.filter { containsWord($0, in: text) }.count
        let weakHit = Self.weakCodeKeywords.contains { containsWord($0, in: text) } ? 1 : 0
        let operatorHits = Self.codeOperators.filter { text.contains($0) }.count
        let tokens = strongHits + weakHit + operatorHits

        var structure = 0

        // Statement / block punctuation at line ends: `;`, `{`, `}`.
        let punctuatedLines = lines.filter { line in
            let stripped = line.trimmingCharacters(in: .whitespaces)
            guard let last = stripped.last else { return false }
            return last == ";" || last == "{" || last == "}" || stripped == "};" || stripped == "})"
        }.count
        if punctuatedLines >= 1 { structure += 1 }
        if punctuatedLines >= 3 { structure += 1 }

        // A call or declaration followed by a block / return type marker.
        if matches(text, #"\w+\s*\([^()\n]*\)\s*(\{|->|=>|:\s*(\n|$)|;\s*(\n|$))"#) {
            structure += 1
        }

        // A block opener followed by a deeper-indented line.
        if hasIndentedBlock(lines) { structure += 1 }

        // Several indented lines (OCR often keeps relative indentation).
        let indentedLines = lines.filter { $0.hasPrefix("  ") || $0.hasPrefix("\t") }.count
        if indentedLines >= 2 { structure += 1 }

        // Assignments: `name = value` (but not `==`).
        if matches(text, #"(^|\n)\s*([A-Za-z_][\w.\[\]]*\s+)?[A-Za-z_][\w.\[\]]*\s*(:\s*[\w<>\[\]?]+\s*)?=(?!=)\s*\S"#) {
            structure += 1
        }

        // Comments, preprocessor directives and markup tags.
        if matches(text, #"(^|\n)\s*(//|/\*|#include\b|#import\b|#define\b|<!--)"#)
            || matches(text, #"</?[A-Za-z][\w-]*(\s[^<>\n]*)?>"#) {
            structure += 1
        }

        if tokens >= 2 && structure >= 2 { return true }
        return structure >= 4
    }

    private func hasIndentedBlock(_ lines: [String]) -> Bool {
        guard lines.count >= 2 else { return false }
        for index in 0..<(lines.count - 1) {
            let opener = lines[index].trimmingCharacters(in: .whitespaces)
            let opensBlock = opener.hasSuffix("{") || opener.hasSuffix("):") || opener.hasSuffix("(")
                || (opener.hasSuffix(":") && matches(opener, #"^(if|elif|else|for|while|try|except|finally|with|class|def)\b"#))
            guard opensBlock else { continue }
            let next = lines[index + 1]
            guard !next.trimmingCharacters(in: .whitespaces).isEmpty else { continue }
            if indentation(of: next) >= indentation(of: lines[index]) + 2 { return true }
        }
        return false
    }

    private func indentation(of line: String) -> Int {
        var width = 0
        for character in line {
            if character == " " {
                width += 1
            } else if character == "\t" {
                width += 4
            } else {
                break
            }
        }
        return width
    }

    // MARK: - Formula

    private func looksLikeFormula(_ text: String) -> Bool {
        // Real LaTeX: a command plus grouping / script / equation structure.
        let latexCommands = countMatches(
            text,
            #"\\(frac|dfrac|sqrt|sum|prod|int|lim|begin|end|alpha|beta|gamma|delta|theta|lambda|sigma|omega|pi|infty|partial|cdot|times|leq|geq|neq|approx|mathbf|mathrm|left|right)\b"#
        )
        if latexCommands >= 2 { return true }
        if latexCommands == 1, text.contains("{") || text.contains("^") || text.contains("_") || text.contains("=") {
            return true
        }

        // Unicode math: a lone "≥" or "m²" in prose is not a formula. Require
        // several independent signals.
        let strongOperators = Set(text.unicodeScalars.filter { "∫∬∮∑∏√∛∂∇∞".unicodeScalars.contains($0) }).count
        let relationCount = text.unicodeScalars.filter { "≈≠≤≥±∓×÷∈∉⊂⊆∝→⇒⇔∀∃".unicodeScalars.contains($0) }.count
        let scriptCount = text.unicodeScalars.filter { "₀₁₂₃₄₅₆₇₈₉⁰¹²³⁴⁵⁶⁷⁸⁹ⁿⁱ₊₋⁺⁻".unicodeScalars.contains($0) }.count
        let operatorChains = countMatches(
            text,
            #"[A-Za-z0-9)\]}⁰¹²³⁴⁵⁶⁷⁸⁹₀₁₂₃₄₅₆₇₈₉]\s*[+\-*/^=<>]\s*[A-Za-z0-9(\[{√∑∫]"#
        )

        var score = strongOperators * 2
        if relationCount >= 1 { score += 1 }
        if relationCount >= 3 { score += 1 }
        if scriptCount >= 1 { score += 1 }
        if operatorChains >= 2 { score += 2 }
        guard score >= 3 else { return false }

        // Long prose that merely mentions a few symbols stays plain text.
        let mathCharacters = strongOperators + relationCount + scriptCount + operatorChains
        return text.count <= 200 || mathCharacters * 25 >= text.count
    }

    // MARK: - Regex helpers

    private func containsWord(_ word: String, in text: String) -> Bool {
        text.range(of: "\\b\(word)\\b", options: .regularExpression) != nil
    }

    private func matches(_ text: String, _ pattern: String) -> Bool {
        text.range(of: pattern, options: .regularExpression) != nil
    }

    private func countMatches(_ text: String, _ pattern: String) -> Int {
        guard let regex = try? NSRegularExpression(pattern: pattern) else { return 0 }
        return regex.numberOfMatches(in: text, range: NSRange(text.startIndex..<text.endIndex, in: text))
    }
}
