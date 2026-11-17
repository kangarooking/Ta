import AppKit
import SwiftUI

extension Notification.Name {
    static let taMenuBarShouldClose = Notification.Name("TaMenuBarShouldClose")
}

@MainActor
final class MenuBarController: NSObject {
    private let statusItem: NSStatusItem
    private let popover = NSPopover()

    init(appModel: AppModel) {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        super.init()

        if let button = statusItem.button {
            if let sourceImage = TaBrandAssets.appIcon,
               let image = sourceImage.copy() as? NSImage {
                image.size = NSSize(width: 19, height: 19)
                image.isTemplate = false
                button.image = image
                button.imageScaling = .scaleProportionallyDown
            } else {
                button.image = NSImage(systemSymbolName: "seal.fill", accessibilityDescription: TaBrand.name)
            }
            button.toolTip = "\(TaBrand.name) · \(TaBrand.englishName)"
            button.target = self
            button.action = #selector(togglePopover(_:))
        }

        popover.behavior = .transient
        popover.animates = true
        // The menu is drawn on Ta's fixed light paper palette; pin the
        // appearance so system text stays dark in Dark Mode.
        popover.appearance = NSAppearance(named: .aqua)
        popover.contentSize = NSSize(width: 326, height: 574)
        popover.contentViewController = NSHostingController(
            rootView: MenuBarContentView(appModel: appModel)
        )

        NotificationCenter.default.addObserver(
            self,
            selector: #selector(closePopover(_:)),
            name: .taMenuBarShouldClose,
            object: nil
        )
    }

    @objc private func togglePopover(_ sender: Any?) {
        guard let button = statusItem.button else { return }
        if popover.isShown {
            popover.performClose(sender)
        } else {
            popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
        }
    }

    @objc private func closePopover(_ notification: Notification) {
        popover.performClose(nil)
    }
}
