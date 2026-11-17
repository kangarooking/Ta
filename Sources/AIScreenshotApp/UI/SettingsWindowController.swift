import AppKit
import SwiftUI

@MainActor
final class SettingsWindowController {
    private var window: NSWindow?

    func show() {
        if let window {
            window.makeKeyAndOrderFront(nil)
        } else {
            let newWindow = NSWindow(
                contentRect: NSRect(x: 0, y: 0, width: 780, height: 600),
                styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
                backing: .buffered,
                defer: false
            )
            newWindow.title = "\(TaBrand.name) · 设置"
            newWindow.titlebarAppearsTransparent = true
            newWindow.isMovableByWindowBackground = true
            newWindow.minSize = NSSize(width: 740, height: 560)
            newWindow.isReleasedWhenClosed = false
            newWindow.appearance = NSAppearance(named: .aqua)
            newWindow.contentView = NSHostingView(
                rootView: SettingsView()
                    .accentColor(TaPalette.cinnabar)
                    .tint(TaPalette.cinnabar)
            )
            newWindow.center()
            newWindow.makeKeyAndOrderFront(nil)
            window = newWindow
        }
        NSApplication.shared.activate()
    }
}
