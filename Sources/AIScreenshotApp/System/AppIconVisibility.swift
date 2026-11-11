import AppKit
import Foundation

/// 应用图标的可见性目标状态（Dock + 菜单栏）。纯数据映射，便于单测。
struct AppIconVisibility: Equatable, Sendable {
    let hideDockIcon: Bool
    let hideMenuBarIcon: Bool

    static let dockIconKey = "hideDockIcon"
    static let menuBarIconKey = "hideMenuBarIcon"

    /// 隐藏 Dock 图标时以 .accessory 运行（菜单栏形态），否则 .regular。
    var activationPolicy: NSApplication.ActivationPolicy {
        hideDockIcon ? .accessory : .regular
    }

    var menuBarIconVisible: Bool {
        !hideMenuBarIcon
    }

    /// 两个图标都隐藏：应用只能靠全局快捷键操作。
    var isCompletelyHidden: Bool {
        hideDockIcon && hideMenuBarIcon
    }

    static func fromDefaults(_ defaults: UserDefaults = .standard) -> AppIconVisibility {
        AppIconVisibility(
            hideDockIcon: defaults.bool(forKey: dockIconKey),
            hideMenuBarIcon: defaults.bool(forKey: menuBarIconKey)
        )
    }
}

/// 把 AppIconVisibility 应用到运行中的应用。
/// 启动时与设置页切换时各调用一次。
@MainActor
final class AppIconVisibilityController: ObservableObject {
    static let shared = AppIconVisibilityController()

    private var menuBarController: MenuBarController?

    @discardableResult
    func attach(_ controller: MenuBarController) -> AppIconVisibilityController {
        menuBarController = controller
        return self
    }

    func apply() {
        let state = AppIconVisibility.fromDefaults()
        NSApplication.shared.setActivationPolicy(state.activationPolicy)
        menuBarController?.setMenuBarIconVisible(state.menuBarIconVisible)
    }

    func apply(_ state: AppIconVisibility) {
        NSApplication.shared.setActivationPolicy(state.activationPolicy)
        menuBarController?.setMenuBarIconVisible(state.menuBarIconVisible)
    }
}
