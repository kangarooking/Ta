import AIScreenshotCore
import AppKit

/// Opt-in recorder for diagnosing long-capture problems on real apps.
///
/// Enable with `defaults write <bundle id> longCaptureDebugRecording -bool YES`.
/// Every frame that changed something plus a text log is written to
/// `~/Library/Caches/Ta/LongCaptureDebug/<timestamp>/`. Nothing is recorded
/// unless the flag is set, and nothing leaves the machine.
final class LongCaptureDebugRecorder: @unchecked Sendable {
  static let preferenceKey = "longCaptureDebugRecording"

  private let directory: URL
  private let queue = DispatchQueue(label: "ta.long-capture-debug", qos: .utility)
  private let started = Date()
  private var frameIndex = 0
  private var lines: [String] = []

  @MainActor
  static func makeIfEnabled(selection: CaptureSelection) -> LongCaptureDebugRecorder? {
    guard UserDefaults.standard.bool(forKey: preferenceKey),
      let caches = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first
    else { return nil }
    let formatter = DateFormatter()
    formatter.dateFormat = "yyyyMMdd-HHmmss"
    let directory =
      caches
      .appendingPathComponent("Ta/LongCaptureDebug", isDirectory: true)
      .appendingPathComponent(formatter.string(from: Date()), isDirectory: true)
    do {
      try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    } catch {
      return nil
    }
    let recorder = LongCaptureDebugRecorder(directory: directory)
    recorder.note(
      "selection \(Int(selection.globalRect.width))x\(Int(selection.globalRect.height))pt"
        + " scale \(selection.backingScaleFactor)")
    return recorder
  }

  private init(directory: URL) {
    self.directory = directory
  }

  func note(_ text: String) {
    let elapsed = Date().timeIntervalSince(started)
    queue.async { [self] in
      lines.append(String(format: "%8.3f  %@", elapsed, text))
    }
  }

  func record(
    image: CGImage,
    disposition: ScrollingFrameDisposition,
    outputPixelHeight: Int,
    isAutoScrolling: Bool
  ) {
    // Stationary frames carry no information and would fill the disk.
    if case .duplicate = disposition { return }
    let elapsed = Date().timeIntervalSince(started)
    queue.async { [self] in
      frameIndex += 1
      let name = String(format: "frame-%05d.jpg", frameIndex)
      lines.append(
        String(
          format: "%8.3f  %@  %@  out=%d  auto=%@",
          elapsed, name, String(describing: disposition), outputPixelHeight,
          isAutoScrolling ? "1" : "0"))
      let representation = NSBitmapImageRep(cgImage: image)
      if let data = representation.representation(
        using: .jpeg, properties: [.compressionFactor: 0.8])
      {
        try? data.write(to: directory.appendingPathComponent(name))
      }
    }
  }

  func finish(outputHeights: [Int]) {
    note("finished parts=\(outputHeights)")
    queue.async { [self] in
      let text = lines.joined(separator: "\n") + "\n"
      try? text.write(
        to: directory.appendingPathComponent("log.txt"), atomically: true, encoding: .utf8)
    }
  }
}
