import AppKit
import Foundation
import XCTest
@testable import AIScreenshotApp

final class AppIconVisibilityTests: XCTestCase {
    func testActivationPolicyMapping() {
        XCTAssertEqual(
            AppIconVisibility(hideDockIcon: false, hideMenuBarIcon: false).activationPolicy,
            .regular
        )
        XCTAssertEqual(
            AppIconVisibility(hideDockIcon: true, hideMenuBarIcon: false).activationPolicy,
            .accessory
        )
        XCTAssertEqual(
            AppIconVisibility(hideDockIcon: true, hideMenuBarIcon: true).activationPolicy,
            .accessory
        )
    }

    func testMenuBarIconVisibilityMapping() {
        XCTAssertTrue(AppIconVisibility(hideDockIcon: false, hideMenuBarIcon: false).menuBarIconVisible)
        XCTAssertFalse(AppIconVisibility(hideDockIcon: true, hideMenuBarIcon: true).menuBarIconVisible)
        // 菜单栏图标只跟随自己的开关，与 Dock 无关
        XCTAssertTrue(AppIconVisibility(hideDockIcon: true, hideMenuBarIcon: false).menuBarIconVisible)
    }

    func testCompletelyHiddenRequiresBothToggles() {
        XCTAssertFalse(AppIconVisibility(hideDockIcon: true, hideMenuBarIcon: false).isCompletelyHidden)
        XCTAssertFalse(AppIconVisibility(hideDockIcon: false, hideMenuBarIcon: true).isCompletelyHidden)
        XCTAssertTrue(AppIconVisibility(hideDockIcon: true, hideMenuBarIcon: true).isCompletelyHidden)
    }

    func testDefaultsRoundTrip() {
        let suiteName = "app-icon-visibility-tests-\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suiteName)!
        defaults.removeObject(forKey: AppIconVisibility.dockIconKey)
        defaults.removeObject(forKey: AppIconVisibility.menuBarIconKey)

        let state = AppIconVisibility.fromDefaults(defaults)
        XCTAssertFalse(state.hideDockIcon)
        XCTAssertFalse(state.hideMenuBarIcon)

        defaults.set(true, forKey: AppIconVisibility.dockIconKey)
        defaults.set(true, forKey: AppIconVisibility.menuBarIconKey)
        let hidden = AppIconVisibility.fromDefaults(defaults)
        XCTAssertTrue(hidden.isCompletelyHidden)
        XCTAssertEqual(hidden.activationPolicy, .accessory)
        XCTAssertFalse(hidden.menuBarIconVisible)

        defaults.removePersistentDomain(forName: suiteName)
    }

    func testStorageKeysMatchSettingsView() {
        // 防止设置页与控制器各写一套 key 导致开关失效
        XCTAssertEqual(AppIconVisibility.dockIconKey, "hideDockIcon")
        XCTAssertEqual(AppIconVisibility.menuBarIconKey, "hideMenuBarIcon")
    }
}
