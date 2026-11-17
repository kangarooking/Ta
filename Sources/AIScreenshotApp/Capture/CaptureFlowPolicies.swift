import Foundation
import AIScreenshotCore

/// "记住上一次操作": the action the user last picked from the capture toolbar.
enum RememberedCaptureAction {
    static let defaultsKey = "lastPostCaptureQuickAction"

    /// Only an explicit toolbar choice made during a *general* capture is
    /// remembered. The dedicated hotkeys (极速识别 / 翻译 / 复制图片 / 钉图) run a
    /// fixed action and must never overwrite it.
    static func shouldRemember(mode: CaptureMode, toolbarChoice: CaptureQuickAction?) -> Bool {
        mode == .interactive && toolbarChoice != nil
    }

    static func rememberIfNeeded(
        mode: CaptureMode,
        toolbarChoice: CaptureQuickAction?,
        defaults: UserDefaults = .standard
    ) {
        guard shouldRemember(mode: mode, toolbarChoice: toolbarChoice), let toolbarChoice else { return }
        defaults.set(toolbarChoice.rawValue, forKey: defaultsKey)
    }

    static func load(defaults: UserDefaults = .standard) -> CaptureQuickAction? {
        defaults.string(forKey: defaultsKey).flatMap(CaptureQuickAction.init(rawValue:))
    }

    /// After a reset the next general capture shows the toolbar again.
    static func reset(defaults: UserDefaults = .standard) {
        defaults.removeObject(forKey: defaultsKey)
    }

    static func displayName(for action: CaptureQuickAction) -> String {
        switch action {
        case .localOCR: "取字"
        case .multimodal: "AI 识图"
        case .translate: "翻译"
        case .translateText: "翻译文字"
        case .copyImage: "复制图片"
        case .pin: "钉图"
        case .edit: "标注"
        case .beautify: "美化"
        case .save: "保存"
        }
    }
}

/// Consent for uploading a capture to the cloud vision model when local OCR
/// cannot read the text during "翻译文字".
enum TranslationVisionUploadConsent {
    /// `true` once the user ticked "不再询问" while agreeing to an upload.
    static let skipConfirmationDefaultsKey = "translationVisionUploadSkipConfirmation"

    static func needsConfirmation(defaults: UserDefaults = .standard) -> Bool {
        !defaults.bool(forKey: skipConfirmationDefaultsKey)
    }

    static func setSkipsConfirmation(_ skips: Bool, defaults: UserDefaults = .standard) {
        defaults.set(skips, forKey: skipConfirmationDefaultsKey)
    }
}

enum SmartRecognitionRoutePolicy {
    /// The smart route promises "the text, enhanced". The local content
    /// classifier is a heuristic that misfires on ordinary prose, so it must
    /// never switch the cloud request to an *explanation* or LaTeX task — the
    /// user would get commentary on the clipboard instead of their text.
    /// Tables keep their Markdown transcription, which is still the content.
    static func cloudTask(for contentType: CaptureContentType) -> MultimodalTaskTemplate {
        switch contentType {
        case .table: .tableMarkdown
        default: .extractText
        }
    }
}

enum CaptureCancellation {
    /// URLSession reports a cancelled task as `URLError.cancelled`, not
    /// `CancellationError`, so both (and any error surfacing after the task
    /// was cancelled) count as a cancellation rather than a failure.
    static func isCancellation(_ error: Error, taskIsCancelled: Bool = Task.isCancelled) -> Bool {
        if taskIsCancelled || error is CancellationError { return true }
        if let urlError = error as? URLError, urlError.code == .cancelled { return true }
        let nsError = error as NSError
        return nsError.domain == NSURLErrorDomain && nsError.code == NSURLErrorCancelled
    }
}

enum CaptureFlowError: LocalizedError {
    case visionUploadDeclined

    var errorDescription: String? {
        switch self {
        case .visionUploadDeclined:
            "本地没有识别出可翻译的文字，而你选择了不上传截图。"
        }
    }
}
