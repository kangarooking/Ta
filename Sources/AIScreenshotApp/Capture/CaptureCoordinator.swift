import AppKit
import AIScreenshotCore

enum CaptureOutcome {
    case cancelled
    case completed(String)
    case failed(String)
}

@MainActor
final class CaptureCoordinator {
    private let selectionOverlay = SelectionOverlayController()
    private let captureService = ScreenCaptureService()
    private let ocrService = ConfiguredOCRService()
    private let clipboardService = ClipboardService()
    private let resultBar = ResultBarController()
    private let pinnedImageWindows = PinnedImageWindowController()
    private let scrollingCaptureSession = ScrollingCaptureSessionController()
    private let imageExportService = ImageExportService()
    private let annotationEditor = AnnotationEditorWindowController()
    private let inlineAnnotation = InlineAnnotationController()
    private let multimodalRecognitionService = MultimodalRecognitionService()
    private let translationService = ScreenshotTranslationService()
    private let translationRenderer = TranslatedImageRenderer()
    private let translationOCR = VisionOCRService()

    private let captureHistory = CaptureHistoryService()

    private struct ActiveJob {
        let id: UUID
        let finish: (CaptureOutcome) -> Void
    }

    private enum PendingClipboardResult {
        case text(String)
        case image(CGImage, scale: CGFloat)
    }

    private var processingTask: Task<Void, Never>?
    private var latestJobID: UUID?
    /// The job whose completion has not been delivered yet.
    private var activeJob: ActiveJob?
    /// Set only while a job is waiting on OCR / AI / translation. Such a job
    /// may be cancelled from the toast or replaced by a new capture hotkey;
    /// the selection overlay and the inline editor never are.
    private var interruptibleJobID: UUID?

    /// Hook for capture flows that finish outside `process` (long capture):
    /// stores the images in the local history when the user enabled it.
    func recordCaptureHistory(_ images: [CGImage], scale: CGFloat? = nil) {
        captureHistory.recordIfEnabled(images, scale: scale)
    }

    /// Cancels the running job if (and only if) it is in its recognition /
    /// translation phase. The job's completion is delivered immediately as
    /// `.cancelled`, even when the underlying request never returns.
    @discardableResult
    func cancelInterruptibleJob() -> Bool {
        guard let jobID = interruptibleJobID,
              let job = activeJob,
              job.id == jobID else {
            return false
        }
        interruptibleJobID = nil
        activeJob = nil
        if latestJobID == jobID { latestJobID = nil }
        processingTask?.cancel()
        processingTask = nil
        resultBar.hide()
        job.finish(.cancelled)
        return true
    }

    func pinClipboardContent() -> Bool { pinnedImageWindows.pinFromPasteboard() }
    func hideAllPins() { pinnedImageWindows.hideAll() }
    func showAllPins() { pinnedImageWindows.showAll() }
    func enableAllPinInteraction() { pinnedImageWindows.enableInteractionForAll() }
    func restoreLastClosedPin() -> Bool { pinnedImageWindows.restoreLastClosed() }

    func openEditorSmokeFixture() {
        guard let image = makeSmokeFixtureImage() else { return }
        openAnnotationEditor(image: image, scale: nil)
    }

    /// Opens the standalone editor. Its "完成" button copies the annotated
    /// image; the toast and history entry are produced here.
    private func openAnnotationEditor(image: CGImage, scale: CGFloat?) {
        annotationEditor.onDelivered = { [weak self] delivered in
            guard let self else { return }
            captureHistory.recordIfEnabled(delivered, scale: scale)
            resultBar.show(
                ResultBarState(
                    kind: .success,
                    title: "标注图片已复制",
                    detail: "可直接粘贴到微信或文档"
                ),
                autoHide: true
            )
        }
        annotationEditor.open(image: image, scale: scale)
    }

    func openInlineEditorSmokeFixture(completion: @escaping () -> Void) {
        guard let image = makeSmokeFixtureImage(),
              let screen = NSScreen.main ?? NSScreen.screens.first,
              let screenNumber = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber else {
            completion()
            return
        }
        let scale = min(
            min(860, screen.frame.width * 0.72) / CGFloat(image.width),
            min(480, screen.frame.height * 0.58) / CGFloat(image.height)
        )
        let width = CGFloat(image.width) * scale
        let height = CGFloat(image.height) * scale
        let selection = CaptureSelection(
            globalRect: CGRect(
                x: screen.frame.midX - width / 2,
                y: screen.frame.midY - height / 2 + 40,
                width: width,
                height: height
            ),
            screenFrame: screen.frame,
            displayID: CGDirectDisplayID(screenNumber.uint32Value),
            backingScaleFactor: screen.backingScaleFactor
        )
        inlineAnnotation.open(
            image: image,
            selection: selection,
            actionHandler: { [weak self] action, renderedImage in
                guard let self else { return false }
                switch action {
                case .copy:
                    return clipboardService.copyImage(
                        renderedImage,
                        initialChangeCount: clipboardService.changeCount,
                        jobIsLatest: true
                    )
                case .save:
                    return (try? imageExportService.save(renderedImage)) != nil
                case .pin:
                    pinnedImageWindows.pin(renderedImage, near: selection)
                    return true
                }
            },
            completion: { _ in completion() }
        )
    }

    func openCaptureToolbarSmokeFixture(completion: @escaping () -> Void) {
        guard let screen = NSScreen.main ?? NSScreen.screens.first else {
            completion()
            return
        }
        let size = screen.frame.size
        let selection = CGRect(
            x: size.width * 0.18,
            y: size.height * 0.34,
            width: size.width * 0.64,
            height: size.height * 0.42
        )
        selectionOverlay.begin(showsActionToolbar: true, presetRect: selection) { _, _ in
            completion()
        }
    }

    func openResultBarSmokeFixture(kind: ResultBarKind) {
        let state = switch kind {
        case .processing:
            ResultBarState(kind: .processing, title: "正在识别图片…", detail: "本地处理，不会上传")
        case .success:
            ResultBarState(kind: .success, title: "已复制图片", detail: "910 × 358")
        case .warning:
            ResultBarState(kind: .warning, title: "已复制，部分文字可能有误", detail: "请检查识别结果")
        case .failure:
            ResultBarState(kind: .failure, title: "复制失败", detail: "剪贴板被其他应用占用")
        }
        resultBar.show(state, autoHide: false, dismissalOverrideSeconds: 60)
    }

    private func makeSmokeFixtureImage() -> CGImage? {
        guard let representation = NSBitmapImageRep(
            bitmapDataPlanes: nil, pixelsWide: 1000, pixelsHigh: 620,
            bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
            colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0
        ) else { return nil }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: representation)
        NSColor.white.setFill()
        NSRect(x: 0, y: 0, width: 1000, height: 620).fill()
        "Ta · 标注案例".draw(at: CGPoint(x: 60, y: 470), withAttributes: [
            .font: NSFont.systemFont(ofSize: 46, weight: .bold), .foregroundColor: NSColor.black
        ])
        "箭头 · 文字 · 高亮 · 马赛克 · 自由缩放".draw(at: CGPoint(x: 60, y: 390), withAttributes: [
            .font: NSFont.systemFont(ofSize: 28), .foregroundColor: NSColor.darkGray
        ])
        NSColor.systemBlue.withAlphaComponent(0.16).setFill()
        NSBezierPath(roundedRect: NSRect(x: 60, y: 90, width: 880, height: 220), xRadius: 24, yRadius: 24).fill()
        NSGraphicsContext.restoreGraphicsState()
        return representation.cgImage
    }

    func start(mode: CaptureMode, completion: @escaping (CaptureOutcome) -> Void) {
        processingTask?.cancel()

        guard ensureScreenCapturePermission(completion: completion) else {
            return
        }

        if mode == .long {
            startLongCapture(completion: completion)
            return
        }

        let jobID = UUID()
        latestJobID = jobID
        let configuredAction = action(for: mode)
        guard let overlayContext = selectionOverlay.prepareStartContext() else {
            completion(.failed("找不到显示器"))
            return
        }
        let finish = beginJob(jobID, completion: completion)
        // Holding Option while confirming the selection always brings the
        // action toolbar back, even when a default / remembered action exists.
        let optionRevealsActionToolbar = mode == .interactive && configuredAction != nil

        // Change the pointer as soon as the shortcut is handled. The overlay is
        // shown only after the full display frame has been captured, so the user
        // always selects from the shortcut-time image rather than a live page.
        NSCursor.crosshair.set()
        processingTask = Task { [weak self] in
            guard let self else { return }
            do {
                let frozenDisplayImage = try await captureService.captureDisplay(
                    displayID: overlayContext.displayID,
                    pixelScale: overlayContext.screen.backingScaleFactor,
                    showsCursor: false
                )
                try Task.checkCancellation()
                guard latestJobID == jobID else { return }

                selectionOverlay.begin(
                    context: overlayContext,
                    frozenDisplayImage: frozenDisplayImage,
                    showsActionToolbar: configuredAction == nil,
                    optionRevealsActionToolbar: optionRevealsActionToolbar
                ) { [weak self] selection, selectedAction in
                    guard let self else { return }
                    guard let selection else {
                        finish(.cancelled)
                        return
                    }

                    let action = selectedAction ?? configuredAction ?? .copyImage
                    RememberedCaptureAction.rememberIfNeeded(mode: mode, toolbarChoice: selectedAction)
                    // Sample the clipboard only now that the selection is
                    // confirmed: the guard exists to protect something the
                    // user copies *during* slow recognition, not while they
                    // were still dragging the selection.
                    let initialChangeCount = clipboardService.changeCount

                    processingTask = Task { [weak self] in
                        guard let self else { return }
                        await process(
                            jobID: jobID,
                            action: action,
                            selection: selection,
                            initialChangeCount: initialChangeCount,
                            completion: finish
                        )
                    }
                }
            } catch is CancellationError {
                NSCursor.arrow.set()
                finish(.cancelled)
            } catch {
                NSCursor.arrow.set()
                let message = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
                resultBar.show(
                    ResultBarState(kind: .failure, title: "截图准备失败", detail: message),
                    autoHide: false
                )
                finish(.failed("截图准备失败"))
            }
        }
    }

    func startFixedTestRegion(
        mode: CaptureMode,
        completion: @escaping (CaptureOutcome) -> Void
    ) {
        guard ensureScreenCapturePermission(completion: completion) else {
            return
        }

        guard let screen = NSScreen.main ?? NSScreen.screens.first,
              let screenNumber = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber else {
            completion(.failed("找不到显示器"))
            return
        }

        processingTask?.cancel()
        let jobID = UUID()
        latestJobID = jobID
        let initialChangeCount = clipboardService.changeCount
        let action = action(for: mode) ?? .localOCR
        let frame = screen.frame
        let selection = CaptureSelection(
            globalRect: CGRect(
                x: frame.minX + frame.width * 0.12,
                y: frame.maxY - min(280, frame.height * 0.36),
                width: min(680, frame.width * 0.58),
                height: min(210, frame.height * 0.28)
            ),
            screenFrame: frame,
            displayID: CGDirectDisplayID(screenNumber.uint32Value),
            backingScaleFactor: screen.backingScaleFactor
        )

        let finish = beginJob(jobID, completion: completion)
        processingTask = Task { [weak self] in
            guard let self else { return }
            await process(
                jobID: jobID,
                action: action,
                selection: selection,
                initialChangeCount: initialChangeCount,
                completion: finish
            )
        }
    }

    /// Registers the job and returns a completion that fires at most once, so
    /// a job cancelled from the toast cannot report a second outcome when its
    /// task eventually unwinds.
    private func beginJob(
        _ jobID: UUID,
        completion: @escaping (CaptureOutcome) -> Void
    ) -> (CaptureOutcome) -> Void {
        var didFinish = false
        let finish: (CaptureOutcome) -> Void = { [weak self] outcome in
            guard !didFinish else { return }
            didFinish = true
            if let self, self.activeJob?.id == jobID {
                self.activeJob = nil
                if self.interruptibleJobID == jobID { self.interruptibleJobID = nil }
            }
            completion(outcome)
        }
        interruptibleJobID = nil
        activeJob = ActiveJob(id: jobID, finish: finish)
        return finish
    }

    private func process(
        jobID: UUID,
        action: CaptureQuickAction,
        selection: CaptureSelection,
        initialChangeCount: Int,
        completion: @escaping (CaptureOutcome) -> Void
    ) async {
        let shouldDismissDeferredOverlay = action == .edit
        defer {
            if shouldDismissDeferredOverlay {
                selectionOverlay.dismiss()
            }
            if interruptibleJobID == jobID {
                interruptibleJobID = nil
            }
        }
        let scale = selection.backingScaleFactor
        do {
            try Task.checkCancellation()
            let image = try await captureService.capture(selection)
            try Task.checkCancellation()
            if action != .edit {
                // Annotated captures are recorded when the editor delivers
                // the final image instead.
                captureHistory.recordIfEnabled(image, scale: scale)
            }

            if action == .copyImage {
                let committed = await clipboardService.copyImageEncodingInBackground(
                    image,
                    scale: scale,
                    initialChangeCount: initialChangeCount,
                    jobIsLatest: { latestJobID == jobID }
                )
                if committed {
                    resultBar.show(
                        ResultBarState(kind: .success, title: "已复制图片", detail: "\(image.width) × \(image.height)"),
                        autoHide: true
                    )
                    completion(.completed("已复制图片"))
                } else {
                    showClipboardChanged(.image(image, scale: scale), completion: completion)
                }
                return
            }

            if action == .pin {
                pinnedImageWindows.pin(image, near: selection)
                resultBar.show(
                    ResultBarState(
                        kind: .success,
                        title: "已钉在屏幕上",
                        detail: "拖动移动 · 滚轮缩放 · ⌘滚轮调透明度 · 双击关闭"
                    ),
                    autoHide: true
                )
                completion(.completed("已钉图"))
                return
            }

            if action == .multimodal {
                interruptibleJobID = jobID
                showProcessing("正在调用多模态模型…", detail: "仅上传本次主动框选的图片")
                do {
                    let text = try await multimodalRecognitionService.recognize(image: image)
                    try Task.checkCancellation()
                    let committed = clipboardService.copyText(
                        text,
                        initialChangeCount: initialChangeCount,
                        jobIsLatest: latestJobID == jobID
                    )
                    if committed {
                        resultBar.show(
                            ResultBarState(
                                kind: .success,
                                title: "AI 识图结果已复制",
                                detail: "\(text.count) 个字符 · \(multimodalRecognitionService.activeVisionModelName)"
                            ),
                            autoHide: true
                        )
                        completion(.completed("AI 识图结果已复制"))
                    } else {
                        showClipboardChanged(.text(text), completion: completion)
                    }
                } catch {
                    if CaptureCancellation.isCancellation(error) { throw CancellationError() }
                    let message = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
                    resultBar.show(
                        ResultBarState(kind: .failure, title: "AI 识图失败", detail: message),
                        autoHide: false
                    )
                    completion(.failed("AI 识图失败"))
                }
                return
            }

            if action == .translate || action == .translateText {
                let configuration = TranslationConfiguration.load()
                guard let mode = TranslationModeRouting.mode(
                    for: action,
                    configuredDefault: configuration.defaultMode
                ) else {
                    completion(.cancelled)
                    return
                }
                interruptibleJobID = jobID
                do {
                    try await performTranslation(
                        image: image,
                        scale: scale,
                        mode: mode,
                        initialChangeCount: initialChangeCount,
                        jobID: jobID,
                        completion: completion
                    )
                } catch {
                    if CaptureCancellation.isCancellation(error) { throw CancellationError() }
                    let message = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
                    resultBar.show(
                        ResultBarState(kind: .failure, title: "截图翻译失败", detail: message),
                        autoHide: false
                    )
                    completion(.failed("截图翻译失败"))
                }
                return
            }

            if action == .save {
                do {
                    if let url = try imageExportService.save(image) {
                        resultBar.show(
                            ResultBarState(
                                kind: .success,
                                title: "截图已保存",
                                detail: url.lastPathComponent
                            ),
                            autoHide: true
                        )
                        completion(.completed("截图已保存"))
                    } else {
                        completion(.cancelled)
                    }
                } catch {
                    let message = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
                    resultBar.show(
                        ResultBarState(kind: .failure, title: "保存失败", detail: message),
                        autoHide: false
                    )
                    completion(.failed("保存失败"))
                }
                return
            }

            if action == .edit {
                var savedFilename: String?
                inlineAnnotation.open(
                    image: image,
                    selection: selection,
                    actionHandler: { [weak self] inlineAction, renderedImage in
                        guard let self else { return false }
                        switch inlineAction {
                        case .copy:
                            guard clipboardService.copyImage(
                                renderedImage,
                                scale: scale,
                                initialChangeCount: clipboardService.changeCount,
                                jobIsLatest: latestJobID == jobID
                            ) else { return false }
                        case .save:
                            do {
                                guard let url = try imageExportService.save(renderedImage) else { return false }
                                savedFilename = url.lastPathComponent
                            } catch {
                                let message = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
                                resultBar.show(
                                    ResultBarState(kind: .failure, title: "保存失败", detail: message),
                                    autoHide: false
                                )
                                return false
                            }
                        case .pin:
                            pinnedImageWindows.pin(renderedImage, near: selection)
                        }
                        captureHistory.recordIfEnabled(renderedImage, scale: scale)
                        return true
                    },
                    completion: { [weak self] inlineAction in
                        guard let self else { return }
                        switch inlineAction {
                        case .copy:
                            resultBar.show(
                                ResultBarState(
                                    kind: .success,
                                    title: "标注图片已复制",
                                    detail: "可直接粘贴到微信或文档"
                                ),
                                autoHide: true
                            )
                            completion(.completed("标注图片已复制"))
                        case .save:
                            resultBar.show(
                                ResultBarState(
                                    kind: .success,
                                    title: "标注图片已保存",
                                    detail: savedFilename ?? "已保存到所选位置"
                                ),
                                autoHide: true
                            )
                            completion(.completed("标注图片已保存"))
                        case .pin:
                            resultBar.show(
                                ResultBarState(
                                    kind: .success,
                                    title: "标注图片已钉住",
                                    detail: "拖动移动 · 滚轮缩放 · 双击关闭"
                                ),
                                autoHide: true
                            )
                            completion(.completed("标注图片已钉住"))
                        case nil:
                            completion(.cancelled)
                        }
                    }
                )
                return
            }

            if action == .beautify {
                let preset = ScreenshotBeautifyPreset.loadSelected()
                let source = UncheckedSendableImage(image: image)
                let rendered = await Task.detached(priority: .userInitiated) {
                    ScreenshotBeautifyRenderer.render(source.image, preset: preset)
                        .map(UncheckedSendableImage.init(image:))
                }.value?.image
                try Task.checkCancellation()
                guard let rendered else {
                    resultBar.show(
                        ResultBarState(kind: .failure, title: "美化失败", detail: "无法渲染美化后的图片"),
                        autoHide: false
                    )
                    completion(.failed("美化失败"))
                    return
                }
                let committed = await clipboardService.copyImageEncodingInBackground(
                    rendered,
                    scale: scale,
                    initialChangeCount: initialChangeCount,
                    jobIsLatest: { latestJobID == jobID }
                )
                if committed {
                    resultBar.show(
                        ResultBarState(
                            kind: .success,
                            title: "已美化并复制",
                            detail: "\(rendered.width) × \(rendered.height) · \(preset.displayName)背景"
                        ),
                        autoHide: true
                    )
                    completion(.completed("已美化并复制"))
                } else {
                    showClipboardChanged(.image(rendered, scale: scale), completion: completion)
                }
                return
            }

            let selectedOCRRaw = UserDefaults.standard.string(forKey: "ocrEngine")
                ?? OCREnginePreference.appleVision.rawValue
            let selectedOCR = OCREnginePreference(rawValue: selectedOCRRaw) ?? .appleVision
            interruptibleJobID = jobID
            showProcessing(
                selectedOCR == .deepSeekOCR2 ? "正在使用 DeepSeek-OCR-2 识别…" : "正在本地识别文字…",
                detail: selectedOCR == .deepSeekOCR2 ? "截图将发送到你配置的 OCR 服务" : "图片不会上传"
            )

            let languageSetting = UserDefaults.standard.string(forKey: "recognitionLanguages")
                ?? "zh-Hans,en-US"
            let languages = languageSetting
                .split(separator: ",")
                .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
                .filter { !$0.isEmpty }
            let mergeWrappedLines = UserDefaults.standard.bool(forKey: "mergeWrappedLines")
            let result = try await ocrService.recognize(
                image: image,
                languages: languages,
                mergeWrappedLines: mergeWrappedLines
            )
            try Task.checkCancellation()

            let routeRaw = UserDefaults.standard.string(forKey: "recognitionRoute")
                ?? RecognitionRoute.localOCR.rawValue
            if routeRaw == RecognitionRoute.smart.rawValue,
               result.isLowConfidence,
               multimodalRecognitionService.isConfigured,
               confirmCloudEnhancement(confidence: result.confidence) {
                showProcessing("正在进行云端增强…", detail: "已按你的确认上传本次选区")
                do {
                    let enhanced = try await multimodalRecognitionService.recognize(
                        image: image,
                        task: SmartRecognitionRoutePolicy.cloudTask(for: result.contentType)
                    )
                    try Task.checkCancellation()
                    let committed = clipboardService.copyText(
                        enhanced,
                        initialChangeCount: initialChangeCount,
                        jobIsLatest: latestJobID == jobID
                    )
                    if committed {
                        resultBar.show(
                            ResultBarState(
                                kind: .success,
                                title: "AI 增强结果已复制",
                                detail: "本地置信度 \(Int(result.confidence * 100))% · 已经你确认后上传"
                            ),
                            autoHide: true
                        )
                        completion(.completed("AI 增强结果已复制"))
                    } else {
                        showClipboardChanged(.text(enhanced), completion: completion)
                    }
                    return
                } catch {
                    if CaptureCancellation.isCancellation(error) { throw CancellationError() }
                    resultBar.show(
                        ResultBarState(
                            kind: .warning,
                            title: "云端增强失败，继续使用本地结果",
                            detail: error.localizedDescription
                        ),
                        autoHide: true
                    )
                }
            }

            guard !result.text.isEmpty else {
                let committed = await clipboardService.copyImageEncodingInBackground(
                    image,
                    scale: scale,
                    initialChangeCount: initialChangeCount,
                    jobIsLatest: { latestJobID == jobID }
                )
                try Task.checkCancellation()
                if committed {
                    resultBar.show(
                        ResultBarState(kind: .warning, title: "未发现文字，已复制图片", detail: nil),
                        autoHide: true
                    )
                    completion(.completed("未发现文字，已复制图片"))
                } else {
                    showClipboardChanged(.image(image, scale: scale), completion: completion)
                }
                return
            }

            let committed = clipboardService.copyText(
                result.text,
                initialChangeCount: initialChangeCount,
                jobIsLatest: latestJobID == jobID
            )
            guard committed else {
                showClipboardChanged(.text(result.text), completion: completion)
                return
            }

            let kind: ResultBarKind = result.isLowConfidence ? .warning : .success
            let typeLabel = switch result.contentType {
            case .plainText: "文本"
            case .code: "代码"
            case .table: "表格"
            case .qrCode: "链接"
            case .formula: "公式"
            case .image: "图片"
            }
            resultBar.show(
                ResultBarState(
                    kind: kind,
                    title: result.isLowConfidence ? "已复制，部分文字可能有误" : "已复制\(typeLabel)",
                detail: "\(result.text.count) 个字符 · \(result.engine.displayName)"
                ),
                autoHide: true
            )
            completion(.completed("已复制\(typeLabel)"))
        } catch {
            if CaptureCancellation.isCancellation(error) {
                // A newer job may already own the toast; only clear our own.
                if latestJobID == jobID {
                    resultBar.hide()
                }
                completion(.cancelled)
                return
            }
            let message = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
            resultBar.show(
                ResultBarState(kind: .failure, title: "截图失败", detail: message),
                autoHide: false
            )
            completion(.failed("截图失败"))
        }
    }

    private func action(for mode: CaptureMode) -> CaptureQuickAction? {
        switch mode {
        case .interactive:
            let rawValue = UserDefaults.standard.string(forKey: "postCaptureAction")
                ?? PostCaptureAction.choose.rawValue
            let preference = PostCaptureAction(rawValue: rawValue) ?? .choose
            return switch preference {
            case .choose:
                nil
            case .recognize:
                recognitionAction()
            case .translateText:
                .translateText
            case .copyImage:
                .copyImage
            case .pin:
                .pin
            case .edit:
                .edit
            case .rememberLast:
                RememberedCaptureAction.load()
            }
        case .intelligent:
            return recognitionAction()
        case .translation:
            return .translateText
        case .image:
            return .copyImage
        case .pin:
            return .pin
        case .long:
            return nil
        }
    }

    private func performTranslation(
        image: CGImage,
        scale: CGFloat,
        mode: ScreenshotTranslationMode,
        initialChangeCount: Int,
        jobID: UUID,
        completion: @escaping (CaptureOutcome) -> Void
    ) async throws {
        try translationService.validateConfiguration()
        let configuration = TranslationConfiguration.load()
        showProcessing("正在本机识别待翻译文字…", detail: "目标语言：\(configuration.targetLanguage)")

        let ocrResult: OCRResult
        do {
            ocrResult = try await translationOCR.recognize(
                image: image,
                languages: [],
                mergeWrappedLines: false
            )
        } catch let error as CancellationError {
            throw error
        } catch {
            guard mode == .textOnly, configuration.usesVisionFallback else {
                throw ScreenshotTranslationServiceError.localOCRUnavailableForImage
            }
            try Task.checkCancellation()
            // Never upload silently: the README promises explicit consent.
            guard confirmTranslationVisionUpload(reason: .localOCRUnavailable) else {
                throw CaptureFlowError.visionUploadDeclined
            }
            showProcessing("本地识别不可用，正在切换视觉模型…", detail: "已按你的确认上传本次选区")
            let translated = try await translationService.translateWithVision(image: image)
            try Task.checkCancellation()
            finishTextTranslation(
                translated,
                configuration: configuration,
                initialChangeCount: initialChangeCount,
                jobID: jobID,
                completion: completion
            )
            return
        }
        try Task.checkCancellation()

        if mode == .textOnly {
            let localTextIsUnreliable = ocrResult.text.isEmpty || ocrResult.confidence < 0.55
            var shouldUseVision = false
            if configuration.usesVisionFallback, localTextIsUnreliable {
                let reason: TranslationVisionUploadReason = ocrResult.text.isEmpty
                    ? .noTextFound
                    : .lowConfidence(ocrResult.confidence)
                shouldUseVision = confirmTranslationVisionUpload(reason: reason)
                if !shouldUseVision, ocrResult.text.isEmpty {
                    throw CaptureFlowError.visionUploadDeclined
                }
            }
            let translated: String
            if shouldUseVision {
                showProcessing("正在使用视觉模型识别并翻译…", detail: "已按你的确认上传本次选区")
                translated = try await translationService.translateWithVision(image: image)
            } else {
                guard !ocrResult.text.isEmpty else { throw TranslationProviderError.emptyInput }
                showProcessing(
                    "正在翻译文字…",
                    detail: "\(configuration.sourceLanguage) → \(configuration.targetLanguage) · \(translationService.selectedTextModelName)"
                )
                translated = try await translationService.translateText(ocrResult.text)
            }
            try Task.checkCancellation()
            finishTextTranslation(
                translated,
                configuration: configuration,
                initialChangeCount: initialChangeCount,
                jobID: jobID,
                completion: completion
            )
            return
        }

        let lines = ocrResult.document.blocks
            .flatMap(\.lines)
            .filter { !$0.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
        guard !lines.isEmpty else { throw ScreenshotTranslationServiceError.missingTextBoxes }
        showProcessing(
            "正在批量翻译 \(lines.count) 个文字区域…",
            detail: "使用 \(translationService.selectedTextModelName)，原图不会发送给文字模型"
        )
        let translatedLines = try await translationService.translateLines(lines)
        try Task.checkCancellation()
        let rendered = try translationRenderer.render(
            image: image,
            lines: translatedLines,
            mode: mode,
            targetLanguage: configuration.targetLanguage
        )
        let copied = clipboardService.copyImage(
            rendered,
            scale: scale,
            initialChangeCount: initialChangeCount,
            jobIsLatest: latestJobID == jobID
        )
        openAnnotationEditor(image: rendered, scale: scale)
        let modeName = mode == .fullImage ? "全文翻译图片" : "双语翻译图片"
        resultBar.show(
            ResultBarState(
                kind: .success,
                title: "已生成\(modeName)",
                detail: copied ? "已复制图片，并在标注器中打开预览" : "已打开预览；识别期间剪贴板有变化，未自动覆盖"
            ),
            autoHide: true
        )
        completion(.completed("已生成\(modeName)"))
    }

    private func finishTextTranslation(
        _ translated: String,
        configuration: TranslationConfiguration,
        initialChangeCount: Int,
        jobID: UUID,
        completion: @escaping (CaptureOutcome) -> Void
    ) {
        let committed = clipboardService.copyText(
            translated,
            initialChangeCount: initialChangeCount,
            jobIsLatest: latestJobID == jobID
        )
        guard committed else {
            showClipboardChanged(.text(translated), completion: completion)
            return
        }
        resultBar.show(
            ResultBarState(
                kind: .success,
                title: "翻译结果已复制",
                detail: "\(translated.count) 个字符 · \(configuration.targetLanguage)"
            ),
            autoHide: true
        )
        completion(.completed("翻译结果已复制"))
    }

    private func startLongCapture(completion: @escaping (CaptureOutcome) -> Void) {
        let jobID = UUID()
        latestJobID = jobID

        selectionOverlay.begin(
            showsActionToolbar: false
        ) { [weak self] selection, _ in
            guard let self else { return }
            guard let selection else {
                completion(.cancelled)
                return
            }
            // Sample after the selection: anything copied while dragging the
            // region must not make the finished capture refuse to copy.
            let initialChangeCount = clipboardService.changeCount

            resultBar.show(
                ResultBarState(
                    kind: .processing,
                    title: "长截图已开始",
                    detail: "滚动内容即可，拓会跟拍；自动滚动可在浮条里开关和换方向"
                ),
                autoHide: true
            )
            scrollingCaptureSession.start(selection: selection) { [weak self] outcome in
                guard let self else { return }
                switch outcome {
                case .cancelled:
                    resultBar.show(
                        ResultBarState(kind: .warning, title: "已取消长截图", detail: "采集帧已从内存清除"),
                        autoHide: true
                    )
                    completion(.cancelled)
                case .failed(let message):
                    resultBar.show(
                        ResultBarState(kind: .failure, title: "长截图失败", detail: message),
                        autoHide: false
                    )
                    completion(.failed("长截图失败"))
                case .completed(
                    let images, let acceptedFrames, let skippedFrames, let reviewedSeams,
                    let outputNote):
                    guard let firstImage = images.first else {
                        completion(.failed("长截图没有生成图片"))
                        return
                    }
                    let copied = clipboardService.copyImage(
                        firstImage,
                        initialChangeCount: initialChangeCount,
                        jobIsLatest: latestJobID == jobID
                    )
                    recordCaptureHistory(images, scale: selection.backingScaleFactor)
                    do {
                        let urls = try imageExportService.save(
                            images,
                            suggestedBaseName: "AI-Long-Screenshot.png"
                        )
                        let status = urls == nil ? "长截图已生成" : "长截图已保存"
                        let totalHeight = images.reduce(0) { $0 + $1.height }
                        var details = "\(firstImage.width) × \(totalHeight) · \(acceptedFrames) 个有效画面"
                        if skippedFrames > 0 {
                            details += " · 跳过 \(skippedFrames) 帧"
                        }
                        if reviewedSeams > 0 {
                            details += " · \(reviewedSeams) 个低置信度接缝"
                        }
                        if let outputNote {
                            details += " · \(outputNote)"
                        }
                        if images.count > 1 {
                            details += " · 已分为 \(images.count) 段"
                        }
                        details += copied
                            ? (images.count > 1 ? " · 已复制第 1 段" : " · 已复制")
                            : " · 未覆盖已变化的剪贴板"
                        resultBar.show(
                            ResultBarState(kind: .success, title: status, detail: details),
                            autoHide: true
                        )
                        completion(.completed(status))
                    } catch {
                        let message = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
                        resultBar.show(
                            ResultBarState(
                                kind: .warning,
                                title: copied ? "长截图已复制，但保存失败" : "长截图保存失败",
                                detail: message
                            ),
                            autoHide: false
                        )
                        completion(copied ? .completed("长截图已复制") : .failed("长截图保存失败"))
                    }
                }
            }
        }
    }

    private func recognitionAction() -> CaptureQuickAction {
        let rawValue = UserDefaults.standard.string(forKey: "recognitionRoute")
            ?? RecognitionRoute.localOCR.rawValue
        switch RecognitionRoute(rawValue: rawValue) ?? .localOCR {
        case .localOCR, .smart:
            return .localOCR
        case .multimodal:
            return .multimodal
        }
    }

    /// Shows a processing toast with a "取消" button wired to the running job.
    private func showProcessing(_ title: String, detail: String?) {
        resultBar.show(
            ResultBarState(
                kind: .processing,
                title: title,
                detail: detail,
                action: ResultBarAction(title: "取消") { [weak self] in
                    _ = self?.cancelInterruptibleJob()
                }
            ),
            autoHide: false
        )
    }

    /// Runs a modal consent alert. While it is on screen the job is not
    /// interruptible, so a capture hotkey cannot start a second flow behind
    /// the alert.
    private func runConsentAlert(_ alert: NSAlert) -> NSApplication.ModalResponse {
        let interruptible = interruptibleJobID
        interruptibleJobID = nil
        let response = alert.runModal()
        if let interruptible, activeJob?.id == interruptible {
            interruptibleJobID = interruptible
        }
        return response
    }

    private func confirmCloudEnhancement(confidence: Float) -> Bool {
        let alert = NSAlert()
        alert.alertStyle = .informational
        alert.messageText = "本地 OCR 置信度较低（\(Int(confidence * 100))%）"
        alert.informativeText = "是否把本次主动框选的图片上传到已配置的视觉模型进行增强？不确认就不会上传。"
        alert.addButton(withTitle: "上传并增强")
        alert.addButton(withTitle: "使用本地结果")
        return runConsentAlert(alert) == .alertFirstButtonReturn
    }

    private enum TranslationVisionUploadReason {
        case localOCRUnavailable
        case noTextFound
        case lowConfidence(Float)
    }

    /// Asks before a "翻译文字" capture is uploaded to the cloud vision model.
    /// Returns `true` without asking once the user ticked "不再询问".
    private func confirmTranslationVisionUpload(reason: TranslationVisionUploadReason) -> Bool {
        guard TranslationVisionUploadConsent.needsConfirmation() else { return true }

        let alert = NSAlert()
        alert.alertStyle = .informational
        let declineTitle: String
        switch reason {
        case .localOCRUnavailable:
            alert.messageText = "本地文字识别暂时不可用"
            declineTitle = "取消翻译"
        case .noTextFound:
            alert.messageText = "本地没有识别出文字"
            declineTitle = "取消翻译"
        case .lowConfidence(let confidence):
            alert.messageText = "本地 OCR 置信度较低（\(Int(confidence * 100))%）"
            declineTitle = "使用本地结果翻译"
        }
        alert.informativeText = "是否把本次主动框选的图片上传到已配置的视觉模型来识别并翻译？不确认就不会上传。"
        alert.addButton(withTitle: "上传并翻译")
        alert.addButton(withTitle: declineTitle)
        alert.showsSuppressionButton = true
        alert.suppressionButton?.title = "不再询问"

        let confirmed = runConsentAlert(alert) == .alertFirstButtonReturn
        if confirmed, alert.suppressionButton?.state == .on {
            TranslationVisionUploadConsent.setSkipsConfirmation(true)
        }
        return confirmed
    }

    /// The clipboard guard kept a finished result back because the user
    /// copied something else meanwhile. The result stays recoverable: the
    /// toast offers "仍然复制".
    private func showClipboardChanged(
        _ pending: PendingClipboardResult,
        completion: @escaping (CaptureOutcome) -> Void
    ) {
        resultBar.show(
            ResultBarState(
                kind: .warning,
                title: "结果已就绪，但没有覆盖剪贴板",
                detail: "识别期间你复制了其他内容",
                action: ResultBarAction(title: "仍然复制") { [weak self] in
                    self?.commitPendingClipboardResult(pending)
                }
            ),
            autoHide: false,
            dismissalOverrideSeconds: 20
        )
        completion(.completed("剪贴板已变化，未覆盖"))
    }

    private func commitPendingClipboardResult(_ pending: PendingClipboardResult) {
        let copied: Bool
        let detail: String
        switch pending {
        case .text(let text):
            copied = clipboardService.copyText(
                text,
                initialChangeCount: clipboardService.changeCount,
                jobIsLatest: true
            )
            detail = "\(text.count) 个字符"
        case .image(let image, let scale):
            copied = clipboardService.copyImage(
                image,
                scale: scale,
                initialChangeCount: clipboardService.changeCount,
                jobIsLatest: true
            )
            detail = "\(image.width) × \(image.height)"
        }
        resultBar.show(
            copied
                ? ResultBarState(kind: .success, title: "已复制", detail: detail)
                : ResultBarState(kind: .failure, title: "复制失败", detail: "剪贴板被其他应用占用"),
            autoHide: true
        )
    }

    private func ensureScreenCapturePermission(
        completion: @escaping (CaptureOutcome) -> Void
    ) -> Bool {
        if ScreenCapturePermissionService.isGranted {
            return true
        }

        let granted = ScreenCapturePermissionService.request()
        guard granted else {
            resultBar.show(
                ResultBarState(
                    kind: .failure,
                    title: "需要屏幕录制权限",
                    detail: "请在系统设置中允许「\(TaBrand.name)」后重试"
                ),
                autoHide: false
            )
            completion(.failed("需要屏幕录制权限"))
            return false
        }
        return true
    }
}
