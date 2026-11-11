import AppKit

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var menuBarController: MenuBarController?

    func applicationDidFinishLaunching(_ notification: Notification) {
        if let icon = TaBrandAssets.appIcon {
            NSApplication.shared.applicationIconImage = icon
        }
        if let appModel = AppModel.current {
            let controller = MenuBarController(appModel: appModel)
            menuBarController = controller
            AppIconVisibilityController.shared.attach(controller)
        }
        AppIconVisibilityController.shared.apply()
    }

    func applicationShouldHandleReopen(
        _ sender: NSApplication,
        hasVisibleWindows flag: Bool
    ) -> Bool {
        guard let appModel = AppModel.current, !appModel.isCapturing else {
            return false
        }
        appModel.showWelcome()
        return true
    }
}
