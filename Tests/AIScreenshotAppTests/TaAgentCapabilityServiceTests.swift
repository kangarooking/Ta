import CoreGraphics
import Foundation
import Testing
import AIScreenshotCore
@testable import AIScreenshotApp
import TaAgentContracts

@Suite("Ta Agent capability service")
struct TaAgentCapabilityServiceTests {
    @Test("status and permissions never expose provider secrets")
    func statusAndPermissions() async throws {
        let fixture = try CapabilityFixture()

        let status = await fixture.service.handle(request(.systemStatus))
        let permissions = await fixture.service.handle(request(.systemPermissions))
        let encoded = try AgentJSONCoding.encoder().encode([status, permissions])
        let json = String(decoding: encoded, as: UTF8.self)

        #expect(status.ok)
        #expect(permissions.ok)
        #expect(json.contains("screenRecording"))
        #expect(!json.localizedCaseInsensitiveContains("apiKey"))
        #expect(!json.contains("test-secret"))
    }

    @Test("capture creates an artifact used by OCR, analyze, translate, and save")
    func completeCapabilityFlow() async throws {
        let fixture = try CapabilityFixture()

        let capture = await fixture.service.handle(request(.captureFrontmost))
        #expect(capture.ok)
        #expect(capture.meta?.cloudUploaded == false)
        let artifact = try #require(capture.artifacts.first)
        #expect(FileManager.default.fileExists(atPath: artifact.path))

        let ocr = await fixture.service.handle(request(.recognizeOCR))
        #expect(ocr.data?.objectValue?["text"] == .string("hello from OCR"))

        let analyze = await fixture.service.handle(request(.analyzeImage))
        #expect(analyze.data?.objectValue?["text"] == .string("image summary"))
        #expect(analyze.meta?.cloudUploaded == true)

        let translation = await fixture.service.handle(request(
            .translateText,
            params: ["text": .string("hello")]
        ))
        #expect(translation.data?.objectValue?["text"] == .string("你好"))

        let outputURL = fixture.root.appendingPathComponent("saved.png")
        let saved = await fixture.service.handle(request(
            .deliverSave,
            params: ["path": .string(outputURL.path)]
        ))
        #expect(saved.ok)
        #expect(FileManager.default.fileExists(atPath: outputURL.path))
    }

    @Test("capabilities advertise local image transforms")
    func capabilitiesAdvertiseTransform() async throws {
        let fixture = try CapabilityFixture()

        let response = await fixture.service.handle(request(.systemCapabilities))

        #expect(response.ok)
        #expect(response.data?.objectValue?["methods"]?.arrayValue?.contains(.string("transform.image")) == true)
    }

    @Test("transform applies recipes and returns local artifacts with undo and redo")
    func transformApplyUndoRedo() async throws {
        let fixture = try CapabilityFixture()
        _ = await fixture.service.handle(request(.captureFrontmost))
        let recipe = AnnotationRecipe(version: 1, operations: [
            .rectangle(.init(id: "focus", rect: .init(x: 0, y: 0, width: 3, height: 2), lineWidth: 1))
        ])
        let recipeJSON = String(decoding: try AgentJSONCoding.encoder().encode(recipe), as: UTF8.self)

        let applied = await fixture.service.handle(request(
            .transformImage,
            params: ["action": .string("apply"), "recipe": .string(recipeJSON)]
        ))
        #expect(applied.ok)
        #expect(applied.meta?.cloudUploaded == false)
        #expect(applied.artifacts.first?.mimeType == "image/png")
        #expect(applied.data?.objectValue?["elementCount"] == .integer(1))
        #expect(applied.data?.objectValue?["canUndo"] == .bool(true))

        let undone = await fixture.service.handle(request(
            .transformImage,
            params: ["action": .string("undo")]
        ))
        #expect(undone.ok)
        #expect(undone.data?.objectValue?["elementCount"] == .integer(0))
        #expect(undone.data?.objectValue?["canRedo"] == .bool(true))

        let redone = await fixture.service.handle(request(
            .transformImage,
            params: ["action": .string("redo")]
        ))
        #expect(redone.ok)
        #expect(redone.data?.objectValue?["elementCount"] == .integer(1))
    }

    @Test("invalid transform recipes do not mutate the current edit session")
    func invalidTransformIsAtomic() async throws {
        let fixture = try CapabilityFixture()
        _ = await fixture.service.handle(request(.captureFrontmost))
        let valid = AnnotationRecipe(version: 1, operations: [
            .rectangle(.init(id: "focus", rect: .init(x: 0, y: 0, width: 3, height: 2), lineWidth: 1))
        ])
        let validJSON = String(decoding: try AgentJSONCoding.encoder().encode(valid), as: UTF8.self)
        _ = await fixture.service.handle(request(
            .transformImage,
            params: ["action": .string("apply"), "recipe": .string(validJSON)]
        ))

        let invalid = await fixture.service.handle(request(
            .transformImage,
            params: ["action": .string("apply"), "recipe": .string(#"{"version":1,"operations":[{"type":"eraser","targetIds":["missing"]}]}"#)]
        ))
        #expect(invalid.error?.code == .invalidRequest)

        let undone = await fixture.service.handle(request(
            .transformImage,
            params: ["action": .string("undo")]
        ))
        #expect(undone.ok)
        #expect(undone.data?.objectValue?["elementCount"] == .integer(0))
    }

    @Test("privacy denylist blocks capture before pixels are requested")
    func privacyDenylist() async throws {
        let fixture = try CapabilityFixture(
            privacyPolicy: TaAgentPrivacyPolicy(
                isEnabled: true,
                automaticCaptureAllowed: true,
                cloudPolicy: .allow,
                blockedBundleIdentifiers: ["com.apple.Safari"],
                allowCaptureTa: true
            )
        )

        let response = await fixture.service.handle(request(.captureFrontmost))

        #expect(response.error?.code == .targetBlockedByPrivacyPolicy)
        #expect(await fixture.backend.captureCount == 0)
    }

    @Test("display capture is blocked when it intersects a denylisted app")
    func displayCaptureHonorsDenylist() async throws {
        let fixture = try CapabilityFixture(
            privacyPolicy: TaAgentPrivacyPolicy(
                isEnabled: true,
                automaticCaptureAllowed: true,
                cloudPolicy: .allow,
                blockedBundleIdentifiers: ["com.apple.Safari"],
                allowCaptureTa: true
            )
        )

        let response = await fixture.service.handle(request(
            .captureDisplay,
            params: ["displayId": .integer(1)]
        ))

        #expect(response.error?.code == .targetBlockedByPrivacyPolicy)
        #expect(await fixture.backend.captureCount == 0)
    }

    @Test("cloud deny prevents model calls")
    func cloudDeny() async throws {
        let fixture = try CapabilityFixture()
        _ = await fixture.service.handle(request(.captureFrontmost))

        let response = await fixture.service.handle(request(
            .analyzeImage,
            params: ["cloud": .string("deny")]
        ))

        #expect(response.error?.code == .cloudUploadNotAllowed)
        #expect(await fixture.dependenciesProbe.analyzeCount == 0)
    }

    @Test("the user's deny setting cannot be overridden by cloud=allow or cloud=auto")
    func userDenyBeatsRequestParameter() async throws {
        let fixture = try CapabilityFixture(privacyPolicy: .testing(cloud: .deny))
        _ = await fixture.service.handle(request(.captureFrontmost))

        for requested in ["allow", "auto"] {
            let analyze = await fixture.service.handle(request(
                .analyzeImage,
                params: ["cloud": .string(requested)]
            ))
            let translate = await fixture.service.handle(request(
                .translateText,
                params: ["text": .string("hello"), "cloud": .string(requested)]
            ))
            let translateImage = await fixture.service.handle(request(
                .translateImage,
                params: ["cloud": .string(requested)]
            ))
            #expect(analyze.error?.code == .cloudUploadNotAllowed)
            #expect(translate.error?.code == .cloudUploadNotAllowed)
            #expect(translateImage.error?.code == .cloudUploadNotAllowed)
            #expect(analyze.meta?.cloudUploaded == false)
        }
        #expect(await fixture.dependenciesProbe.analyzeCount == 0)
        #expect(await fixture.dependenciesProbe.translateCount == 0)
    }

    @Test("cloud OCR honours the user's deny setting")
    func cloudOCRHonoursDeny() async throws {
        let fixture = try CapabilityFixture(privacyPolicy: .testing(cloud: .deny), ocrEngine: .deepSeekOCR2)
        _ = await fixture.service.handle(request(.captureFrontmost))

        let response = await fixture.service.handle(request(
            .recognizeOCR,
            params: ["cloud": .string("allow")]
        ))

        #expect(response.error?.code == .cloudUploadNotAllowed)
    }

    @Test("cloudUploaded is true when the upload happened but the call then failed")
    func cloudUploadedReportedOnFailure() async throws {
        let fixture = try CapabilityFixture(cloudCallsFail: true)
        _ = await fixture.service.handle(request(.captureFrontmost))

        let analyze = await fixture.service.handle(request(.analyzeImage))
        let translate = await fixture.service.handle(request(
            .translateText,
            params: ["text": .string("hello")]
        ))

        #expect(!analyze.ok)
        #expect(analyze.meta?.cloudUploaded == true)
        #expect(!translate.ok)
        #expect(translate.meta?.cloudUploaded == true)
        // A request rejected before any upload still reports false.
        let invalid = await fixture.service.handle(request(
            .analyzeImage,
            params: ["task": .string("no-such-task")]
        ))
        #expect(invalid.error?.code == .invalidRequest)
        #expect(invalid.meta?.cloudUploaded == false)
    }

    @Test("deliver.save only writes PNG files to safe, non-existing destinations")
    func saveDestinationRestrictions() async throws {
        let fixture = try CapabilityFixture()
        _ = await fixture.service.handle(request(.captureFrontmost))
        func save(_ path: String, overwrite: Bool? = nil) async -> AgentResponseEnvelope {
            var params: [String: JSONValue] = ["path": .string(path)]
            if let overwrite { params["overwrite"] = .bool(overwrite) }
            return await fixture.service.handle(request(.deliverSave, params: params))
        }

        let hidden = await save(fixture.root.appendingPathComponent(".zshrc.png").path)
        let hiddenDirectory = await save(fixture.root.appendingPathComponent(".ssh/key.png").path)
        let wrongExtension = await save(fixture.root.appendingPathComponent("script.sh").path)
        let system = await save("/usr/local/bin/ta-shot.png")
        let relative = await save("relative.png")
        for response in [hidden, hiddenDirectory, wrongExtension, system, relative] {
            #expect(response.error?.code == .invalidRequest)
        }
        #expect(!FileManager.default.fileExists(atPath: fixture.root.appendingPathComponent("script.sh").path))

        // A missing extension is completed, and the response reports the real path.
        let bare = await save(fixture.root.appendingPathComponent("shot").path)
        #expect(bare.ok)
        let barePath = try #require(bare.data?.objectValue?["path"]?.stringValue)
        #expect(barePath.hasSuffix("/shot.png"))
        #expect(FileManager.default.fileExists(atPath: barePath))

        // Existing files are protected unless overwrite is explicit.
        let again = await save(barePath)
        #expect(again.error?.code == .invalidRequest)
        #expect(again.error?.message.contains("overwrite") == true)
        let explicitNoOverwrite = await save(barePath, overwrite: false)
        #expect(explicitNoOverwrite.error?.code == .invalidRequest)
        let overwritten = await save(barePath, overwrite: true)
        #expect(overwritten.ok)
    }

    @Test("inputPath only accepts regular image files")
    func inputPathRestrictions() async throws {
        let fixture = try CapabilityFixture()
        let secret = fixture.root.appendingPathComponent("secret.txt")
        try Data("token".utf8).write(to: secret)
        let directory = fixture.root.appendingPathComponent("folder.png", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let link = fixture.root.appendingPathComponent("link.png")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: secret)

        for path in [secret.path, directory.path, link.path, fixture.root.appendingPathComponent("missing.png").path, "relative.png"] {
            let response = await fixture.service.handle(request(
                .recognizeOCR,
                params: ["inputPath": .string(path)]
            ))
            #expect(response.error?.code == .invalidRequest, "accepted \(path)")
        }

        // A real artifact produced by capture is accepted by explicit path.
        let capture = await fixture.service.handle(request(.captureFrontmost))
        let artifact = try #require(capture.artifacts.first)
        let ocr = await fixture.service.handle(request(
            .recognizeOCR,
            params: ["inputPath": .string(artifact.path)]
        ))
        #expect(ocr.ok)
    }

    @Test("capture reports pixel size and scale, and validates maxDimension")
    func captureImageMetadataAndMaxDimension() async throws {
        let fixture = try CapabilityFixture()

        let capture = await fixture.service.handle(request(
            .captureFrontmost,
            params: ["maxDimension": .integer(1_568)]
        ))
        let image = try #require(capture.data?.objectValue?["image"]?.objectValue)
        #expect(image["width"] == .integer(4))
        #expect(image["height"] == .integer(3))
        #expect(image["originalWidth"] == .integer(4))
        #expect(image["downscaled"] == .bool(false))
        #expect(image["pixelsPerPoint"] == .number(4.0 / 400.0))

        for bad in [JSONValue.integer(0), .integer(8), .integer(100_000), .string("big")] {
            let response = await fixture.service.handle(request(
                .captureFrontmost,
                params: ["maxDimension": bad]
            ))
            #expect(response.error?.code == .invalidRequest)
        }
    }

    @Test("downscaling bounds the longer side and keeps the aspect ratio")
    func downscaleHelper() throws {
        let context = try #require(CGContext(
            data: nil, width: 400, height: 200, bitsPerComponent: 8, bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ))
        let source = try #require(context.makeImage())

        let scaled = try TaAgentCapabilityService.downscaled(source, maxDimension: 100)
        #expect(scaled.width == 100)
        #expect(scaled.height == 50)
        let untouched = try TaAgentCapabilityService.downscaled(source, maxDimension: 1_568)
        #expect(untouched.width == 400)
        #expect(untouched.height == 200)
    }

    @Test("capture.window resolves an app name or title fragment in one call")
    func captureByAppOrTitle() async throws {
        let fixture = try CapabilityFixture()

        let byApp = await fixture.service.handle(request(.captureWindow, params: ["app": .string("safari")]))
        let byBundle = await fixture.service.handle(request(
            .captureWindow,
            params: ["app": .string("com.apple.Safari")]
        ))
        let byTitle = await fixture.service.handle(request(.captureWindow, params: ["title": .string("exam")]))
        let missing = await fixture.service.handle(request(.captureWindow, params: ["app": .string("Finder")]))
        let wrongTitle = await fixture.service.handle(request(
            .captureWindow,
            params: ["app": .string("Safari"), "title": .string("nope")]
        ))
        let noTarget = await fixture.service.handle(request(.captureWindow))

        for response in [byApp, byBundle, byTitle] {
            #expect(response.ok)
            #expect(response.data?.objectValue?["target"]?.objectValue?["windowId"] == .integer(42))
        }
        #expect(missing.error?.code == .targetNotFound)
        #expect(wrongTitle.error?.code == .targetNotFound)
        #expect(noTarget.error?.code == .invalidRequest)
    }

    @Test("window matching prefers exact app names, real windows, and the frontmost candidate")
    func bestWindowRanking() {
        func window(_ id: UInt32, _ app: String, _ title: String?, size: CGSize, z: Int) -> TaAgentWindowTarget {
            TaAgentWindowTarget(
                id: id, ownerProcessID: 1, appName: app, bundleIdentifier: nil, title: title,
                frame: CGRect(origin: .zero, size: size), zOrder: z
            )
        }
        let windows = [
            window(1, "Safari Technology Preview", "Docs", size: CGSize(width: 800, height: 600), z: 0),
            window(2, "Safari", nil, size: CGSize(width: 30, height: 24), z: 1),
            window(3, "Safari", "Inbox", size: CGSize(width: 800, height: 600), z: 3),
            window(4, "Safari", "Docs", size: CGSize(width: 800, height: 600), z: 2)
        ]

        #expect(TaAgentCapabilityService.bestWindow(in: windows, app: "safari", title: nil)?.id == 4)
        #expect(TaAgentCapabilityService.bestWindow(in: windows, app: "safari", title: "inbox")?.id == 3)
        #expect(TaAgentCapabilityService.bestWindow(in: windows, app: "technology", title: nil)?.id == 1)
        #expect(TaAgentCapabilityService.bestWindow(in: windows, app: nil, title: "docs")?.id == 1)
        #expect(TaAgentCapabilityService.bestWindow(in: windows, app: "chrome", title: nil) == nil)
    }

    @Test("OCR returns per-line pixel boxes only when boxes is requested")
    func ocrLineBoxes() async throws {
        let fixture = try CapabilityFixture()
        _ = await fixture.service.handle(request(.captureFrontmost))

        let plain = await fixture.service.handle(request(.recognizeOCR))
        #expect(plain.data?.objectValue?["lines"] == nil)

        let boxed = await fixture.service.handle(request(.recognizeOCR, params: ["boxes": .bool(true)]))
        let lines = try #require(boxed.data?.objectValue?["lines"]?.arrayValue)
        #expect(lines.count == 1)
        #expect(lines.first?.objectValue?["text"] == .string("hello from OCR"))
        // Normalized (0.25, 0.5, 0.5, 0.25) with a bottom-left origin on a 4x3 image.
        #expect(lines.first?.objectValue?["box"] == .object([
            "x": .number(1), "y": .number(0.75), "width": .number(2), "height": .number(0.75)
        ]))
        #expect(boxed.data?.objectValue?["imageWidth"] == .integer(4))
        #expect(boxed.data?.objectValue?["boxesAvailable"] == .bool(true))
    }

    @Test("capabilities advertise the optional parameters and limits")
    func capabilitiesAdvertiseOptionalParams() async throws {
        let fixture = try CapabilityFixture()

        let response = await fixture.service.handle(request(.systemCapabilities))
        let optional = try #require(response.data?.objectValue?["optionalParams"]?.objectValue)

        #expect(optional["capture.*"]?.arrayValue?.contains(.string("maxDimension")) == true)
        #expect(optional["capture.window"]?.arrayValue?.contains(.string("app")) == true)
        #expect(optional["recognize.ocr"]?.arrayValue?.contains(.string("boxes")) == true)
        #expect(optional["deliver.save"]?.arrayValue?.contains(.string("overwrite")) == true)
    }

    @Test("window list filters by either app name or bundle identifier")
    func windowListAppFilter() async throws {
        let fixture = try CapabilityFixture()

        let byName = await fixture.service.handle(request(
            .targetListWindows,
            params: ["app": .string("saf")]
        ))
        let byBundle = await fixture.service.handle(request(
            .targetListWindows,
            params: ["app": .string("com.apple.Safari")]
        ))
        let missing = await fixture.service.handle(request(
            .targetListWindows,
            params: ["app": .string("Finder")]
        ))

        #expect(byName.data?.objectValue?["windows"]?.arrayValue?.count == 1)
        #expect(byBundle.data?.objectValue?["windows"]?.arrayValue?.count == 1)
        #expect(missing.data?.objectValue?["windows"]?.arrayValue?.isEmpty == true)
    }

    @Test("privacy setting changes apply to the next request without restarting the Bridge")
    func privacyReloadsBetweenRequests() async throws {
        let box = MutablePrivacyPolicy(.testingAllowed)
        let fixture = try CapabilityFixture(
            privacyPolicy: nil,
            privacyPolicyProvider: { box.value }
        )

        #expect(await fixture.service.handle(request(.captureFrontmost)).ok)
        box.value = TaAgentPrivacyPolicy(
            isEnabled: false,
            automaticCaptureAllowed: false,
            cloudPolicy: .deny,
            blockedBundleIdentifiers: [],
            allowCaptureTa: false
        )
        let blocked = await fixture.service.handle(request(.captureFrontmost))

        #expect(blocked.error?.code == .targetBlockedByPrivacyPolicy)
        #expect(await fixture.backend.captureCount == 1)
    }

    private func request(
        _ method: AgentMethod,
        params: [String: JSONValue] = [:]
    ) -> AgentRequestEnvelope {
        AgentRequestEnvelope(
            requestID: "request-\(UUID().uuidString)",
            method: method,
            params: params,
            client: AgentClientInfo(name: "test", version: "1")
        )
    }
}

private struct CapabilityFixture {
    let root: URL
    let backend: CapabilityCaptureBackend
    let dependenciesProbe: CapabilityDependenciesProbe
    let service: TaAgentCapabilityService

    init(
        privacyPolicy: TaAgentPrivacyPolicy? = .testingAllowed,
        privacyPolicyProvider: @escaping @Sendable () -> TaAgentPrivacyPolicy = { .load() },
        ocrEngine: OCREnginePreference = .appleVision,
        cloudCallsFail: Bool = false
    ) throws {
        root = FileManager.default.temporaryDirectory
            .appendingPathComponent("ta-capability-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        backend = CapabilityCaptureBackend()
        dependenciesProbe = CapabilityDependenciesProbe()
        let captureService = TaAgentCaptureService(
            backend: backend,
            frontmostProcessID: { 420 }
        )
        let probe = dependenciesProbe
        let dependencies = TaAgentCapabilityDependencies(
            appVersion: { "1.0.0-test" },
            screenPermission: { true },
            accessibilityPermission: { true },
            ocrEngine: { ocrEngine },
            visionConfigured: { true },
            translationConfigured: { true },
            recognizeOCR: { _, _, _ in
                OCRResult(
                    text: "hello from OCR",
                    contentType: .plainText,
                    confidence: 0.98,
                    document: OCRDocumentLayout(
                        blocks: [OCRDocumentBlock(lines: [
                            OCRTextLine(
                                text: "hello from OCR",
                                confidence: 0.98,
                                boundingBox: CGRect(x: 0.25, y: 0.5, width: 0.5, height: 0.25)
                            )
                        ])],
                        tables: []
                    )
                )
            },
            analyzeImage: { _, _ in
                await probe.recordAnalyze()
                if cloudCallsFail { throw CapabilityTestError.providerFailed }
                return "image summary"
            },
            translateText: { _ in
                await probe.recordTranslate()
                if cloudCallsFail { throw CapabilityTestError.providerFailed }
                return "你好"
            },
            translateImage: { _ in "图片译文" },
            copyText: { _ in true },
            copyImage: { _ in true }
        )
        service = TaAgentCapabilityService(
            captureService: captureService,
            artifactStore: TaAgentArtifactStore(rootDirectory: root.appendingPathComponent("artifacts")),
            privacyPolicy: privacyPolicy,
            privacyPolicyProvider: privacyPolicyProvider,
            auditLog: nil,
            dependencies: dependencies
        )
    }
}

private final class MutablePrivacyPolicy: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: TaAgentPrivacyPolicy

    init(_ value: TaAgentPrivacyPolicy) {
        stored = value
    }

    var value: TaAgentPrivacyPolicy {
        get { lock.withLock { stored } }
        set { lock.withLock { stored = newValue } }
    }
}

private actor CapabilityDependenciesProbe {
    private(set) var analyzeCount = 0
    private(set) var translateCount = 0
    func recordAnalyze() { analyzeCount += 1 }
    func recordTranslate() { translateCount += 1 }
}

private enum CapabilityTestError: Error {
    case providerFailed
}

private actor CapabilityCaptureBackend: TaAgentCaptureBackend {
    private(set) var captureCount = 0

    func snapshot(frontmostProcessID: pid_t?) async throws -> TaAgentCaptureSnapshot {
        TaAgentCaptureSnapshot(
            displays: [
                TaAgentDisplayTarget(
                    id: 1,
                    frame: CGRect(x: 0, y: 0, width: 800, height: 600),
                    pixelScale: 1,
                    isMain: true
                )
            ],
            windows: [
                TaAgentWindowTarget(
                    id: 42,
                    ownerProcessID: 420,
                    appName: "Safari",
                    bundleIdentifier: "com.apple.Safari",
                    title: "Example",
                    frame: CGRect(x: 20, y: 20, width: 400, height: 300),
                    zOrder: 0
                )
            ],
            frontmostProcessID: frontmostProcessID
        )
    }

    func capture(_ request: TaAgentResolvedCaptureRequest) async throws -> CGImage {
        captureCount += 1
        return Self.image
    }

    private static let image: CGImage = {
        let context = CGContext(
            data: nil,
            width: 4,
            height: 3,
            bitsPerComponent: 8,
            bytesPerRow: 16,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        )!
        context.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: 4, height: 3))
        return context.makeImage()!
    }()
}

private extension TaAgentPrivacyPolicy {
    static let testingAllowed = testing(cloud: .allow)

    static func testing(cloud: AgentCloudPolicy) -> TaAgentPrivacyPolicy {
        TaAgentPrivacyPolicy(
            isEnabled: true,
            automaticCaptureAllowed: true,
            cloudPolicy: cloud,
            blockedBundleIdentifiers: [],
            allowCaptureTa: true
        )
    }
}

private extension JSONValue {
    var objectValue: [String: JSONValue]? {
        guard case .object(let value) = self else { return nil }
        return value
    }


    var stringValue: String? {
        guard case .string(let value) = self else { return nil }
        return value
    }

    var arrayValue: [JSONValue]? {
        guard case .array(let value) = self else { return nil }
        return value
    }
}
