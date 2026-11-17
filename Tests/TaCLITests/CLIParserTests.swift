import Foundation
import Testing
@testable import TaCLI
import TaAgentContracts

@Suite("ta CLI parser")
struct CLIParserTests {
    @Test("parses status with global JSON output")
    func status() throws {
        let invocation = try CLIParser.parse(["status", "--json"])

        #expect(invocation.action == .request(.systemStatus, [:]))
        #expect(invocation.outputFormat == .json)
        #expect(invocation.timeout == 10)
    }

    @Test("parses a region capture and requested output path")
    func regionCapture() throws {
        let invocation = try CLIParser.parse([
            "capture", "region",
            "--display", "2",
            "--x", "100", "--y", "120",
            "--width", "800", "--height", "600",
            "--output", "/tmp/ta-region.png",
            "--cloud", "deny"
        ])

        #expect(invocation.action == .request(.captureRegion, [
            "displayId": .integer(2),
            "x": .number(100), "y": .number(120),
            "width": .number(800), "height": .number(600),
            "cloud": .string("deny")
        ]))
        #expect(invocation.outputPath == "/tmp/ta-region.png")
    }

    @Test("image input paths use inputPath and never API key arguments")
    func ocrPath() throws {
        let invocation = try CLIParser.parse(["ocr", "./shot.png", "--json"])
        let expectedPath = URL(fileURLWithPath: "./shot.png").standardizedFileURL.path

        #expect(invocation.action == .request(.recognizeOCR, ["inputPath": .string(expectedPath)]))
        #expect(!CLIParser.usage.localizedCaseInsensitiveContains("api-key"))
    }

    @Test("missing required window ID is actionable")
    func missingWindowID() {
        #expect(throws: CLIParseError.self) {
            _ = try CLIParser.parse(["capture", "window"])
        }
    }

    @Test("save forwards the required destination path")
    func savePath() throws {
        let invocation = try CLIParser.parse(["save", "last", "--output", "/tmp/ta-save.png"])

        #expect(invocation.action == .request(.deliverSave, [
            "path": .string("/tmp/ta-save.png")
        ]))
        #expect(invocation.outputPath == "/tmp/ta-save.png")
    }

    @Test("save rejects a missing destination")
    func saveRequiresOutput() {
        #expect(throws: CLIParseError.self) {
            _ = try CLIParser.parse(["save", "last"])
        }
    }

    @Test("transform parses last image, recipe, and durable output")
    func transformLast() throws {
        let invocation = try CLIParser.parse([
            "transform", "last",
            "--recipe", "./annotations.json",
            "--output", "/tmp/ta-marked.png",
            "--json"
        ])

        #expect(invocation.action == .request(.transformImage, [
            "action": .string("apply"),
            "recipePath": .string(URL(fileURLWithPath: "./annotations.json").standardizedFileURL.path)
        ]))
        #expect(invocation.outputPath == "/tmp/ta-marked.png")
    }

    @Test("transform accepts explicit image paths")
    func transformImagePath() throws {
        let invocation = try CLIParser.parse([
            "transform", "./input.png", "--recipe", "./annotations.json"
        ])

        #expect(invocation.action == .request(.transformImage, [
            "action": .string("apply"),
            "inputPath": .string(URL(fileURLWithPath: "./input.png").standardizedFileURL.path),
            "recipePath": .string(URL(fileURLWithPath: "./annotations.json").standardizedFileURL.path)
        ]))
    }

    @Test("transform undo and redo map to history actions")
    func transformHistory() throws {
        #expect(try CLIParser.parse(["transform", "undo"]).action == .request(
            .transformImage,
            ["action": .string("undo")]
        ))
        #expect(try CLIParser.parse(["transform", "redo"]).action == .request(
            .transformImage,
            ["action": .string("redo")]
        ))
    }

    @Test("cloud-capable commands get a long default timeout; explicit --timeout wins")
    func defaultTimeouts() throws {
        #expect(try CLIParser.parse(["status"]).timeout == 10)
        #expect(try CLIParser.parse(["window", "list"]).timeout == 10)
        #expect(try CLIParser.parse(["capture", "frontmost"]).timeout == 30)
        #expect(try CLIParser.parse(["analyze", "last"]).timeout == 180)
        #expect(try CLIParser.parse(["translate", "last", "--mode", "image"]).timeout == 180)
        #expect(try CLIParser.parse(["translate", "last"]).timeout == 180)
        #expect(try CLIParser.parse(["translate", "text", "--text", "hi"]).timeout == 180)
        #expect(try CLIParser.parse(["ocr", "last"]).timeout == 180)
        #expect(try CLIParser.parse(["ocr", "last", "--cloud", "deny"]).timeout == 60)
        #expect(try CLIParser.parse(["analyze", "last", "--timeout", "7.5"]).timeout == 7.5)
        #expect(throws: CLIParseError.self) {
            _ = try CLIParser.parse(["status", "--timeout", "0"])
        }
    }

    @Test("omitting --cloud sends no cloud parameter so the user's Ta setting applies")
    func cloudIsOmittedByDefault() throws {
        #expect(try CLIParser.parse(["analyze", "last"]).action == .request(.analyzeImage, [:]))
        #expect(try CLIParser.parse(["translate", "last"]).action == .ocrThenTranslate([:]))
    }

    @Test("capture app and window accept an app name or title fragment")
    func captureByApp() throws {
        #expect(try CLIParser.parse(["capture", "app", "Safari"]).action == .request(
            .captureWindow,
            ["app": .string("Safari")]
        ))
        #expect(try CLIParser.parse(["capture", "app", "Safari", "--title", "Docs", "--json"]).action == .request(
            .captureWindow,
            ["app": .string("Safari"), "title": .string("Docs")]
        ))
        #expect(try CLIParser.parse(["capture", "window", "--title", "Inbox"]).action == .request(
            .captureWindow,
            ["title": .string("Inbox")]
        ))
        #expect(try CLIParser.parse(["capture", "window", "--window-id", "7"]).action == .request(
            .captureWindow,
            ["windowId": .integer(7)]
        ))
        #expect(throws: CLIParseError.self) { _ = try CLIParser.parse(["capture", "app"]) }
        #expect(throws: CLIParseError.self) {
            _ = try CLIParser.parse(["capture", "window", "--window-id", "7", "--app", "Safari"])
        }
    }

    @Test("--max-size maps to maxDimension and is range checked")
    func maxSize() throws {
        #expect(try CLIParser.parse(["capture", "frontmost", "--max-size", "1568"]).action == .request(
            .captureFrontmost,
            ["maxDimension": .integer(1_568)]
        ))
        #expect(throws: CLIParseError.self) {
            _ = try CLIParser.parse(["capture", "frontmost", "--max-size", "4"])
        }
        #expect(throws: CLIParseError.self) {
            _ = try CLIParser.parse(["capture", "frontmost", "--max-size", "big"])
        }
    }

    @Test("--boxes and --overwrite are forwarded")
    func boxesAndOverwrite() throws {
        #expect(try CLIParser.parse(["ocr", "last", "--boxes"]).action == .request(
            .recognizeOCR,
            ["boxes": .bool(true)]
        ))
        let save = try CLIParser.parse(["save", "last", "--output", "/tmp/ta-save.png", "--overwrite"])
        #expect(save.action == .request(.deliverSave, [
            "path": .string("/tmp/ta-save.png"),
            "overwrite": .bool(true)
        ]))
        #expect(save.overwrite)
        let capture = try CLIParser.parse(["capture", "frontmost", "--output", "/tmp/ta.png"])
        #expect(!capture.overwrite)
        #expect(capture.action == .request(.captureFrontmost, [:]))
    }

    @Test("transform apply requires a recipe")
    func transformRequiresRecipe() {
        #expect(throws: CLIParseError.self) {
            _ = try CLIParser.parse(["transform", "last"])
        }
    }
}
