import AppKit
import SwiftUI
import AIScreenshotCore

enum WelcomeViewMetrics {
    static let defaultWindowSize = CGSize(width: 900, height: 620)
    static let minimumWindowSize = CGSize(width: 820, height: 590)
    static let contentPadding: CGFloat = 32
    static let sectionSpacing: CGFloat = 24
    static let gridSpacing: CGFloat = 14
    static let quickActionColumnCount = 3
    static let quickActionCount = 6
    static let quickActionMinimumHeight: CGFloat = 74
    static let primaryCornerRadius: CGFloat = 20
    static let quickActionCornerRadius: CGFloat = 15
}

@MainActor
final class WelcomeWindowController {
    private var window: NSWindow?

    func hide() {
        window?.orderOut(nil)
    }

    func show(appModel: AppModel) {
        let rootView = WelcomeView(
            appModel: appModel,
            onOpenSettings: { [weak appModel] in
                appModel?.showSettings()
            }
        )

        if let window {
            window.contentView = NSHostingView(rootView: rootView)
            window.center()
            window.makeKeyAndOrderFront(nil)
        } else {
            let newWindow = NSWindow(
                contentRect: NSRect(origin: .zero, size: WelcomeViewMetrics.defaultWindowSize),
                styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
                backing: .buffered,
                defer: false
            )
            newWindow.title = TaBrand.name
            newWindow.titlebarAppearsTransparent = true
            newWindow.isMovableByWindowBackground = true
            newWindow.isReleasedWhenClosed = false
            newWindow.minSize = WelcomeViewMetrics.minimumWindowSize
            newWindow.appearance = NSAppearance(named: .aqua)
            newWindow.contentView = NSHostingView(rootView: rootView)
            newWindow.center()
            newWindow.makeKeyAndOrderFront(nil)
            window = newWindow
        }

        NSApplication.shared.activate()
    }
}

private struct WelcomeView: View {
    @ObservedObject var appModel: AppModel
    let onOpenSettings: () -> Void

    @State private var permissionGranted = ScreenCapturePermissionService.isGranted
    @State private var shortcuts = HotKeyPreferences().allShortcuts()

    var body: some View {
        ZStack {
            TaPaperBackground()

            VStack(alignment: .leading, spacing: WelcomeViewMetrics.sectionSpacing) {
                header
                primaryCaptureCard

                VStack(alignment: .leading, spacing: 11) {
                    TaSectionLabel(title: "快速操作")

                    LazyVGrid(
                        columns: Array(
                            repeating: GridItem(.flexible(), spacing: WelcomeViewMetrics.gridSpacing),
                            count: WelcomeViewMetrics.quickActionColumnCount
                        ),
                        spacing: WelcomeViewMetrics.gridSpacing
                    ) {
                        quickAction(
                            title: "极速取字",
                            subtitle: "识别文字并复制",
                            shortcut: shortcutText(for: .intelligentCapture),
                            symbol: "text.viewfinder"
                        ) { appModel.startCapture(.intelligent) }

                        quickAction(
                            title: "复制图片",
                            subtitle: "保留屏幕这一刻",
                            shortcut: shortcutText(for: .imageCapture),
                            symbol: "rectangle.dashed"
                        ) { appModel.startCapture(.image) }

                        quickAction(
                            title: "截图翻译",
                            subtitle: "识别、翻译并复制",
                            shortcut: shortcutText(for: .translationCapture),
                            symbol: "character.book.closed"
                        ) { appModel.startCapture(.translation) }

                        quickAction(
                            title: "钉在屏幕",
                            subtitle: "让参考内容留在眼前",
                            shortcut: shortcutText(for: .pinCapture),
                            symbol: "pin.fill"
                        ) { appModel.startCapture(.pin) }

                        quickAction(
                            title: "滚动长图",
                            subtitle: "自动滚动并拼接",
                            shortcut: shortcutText(for: .longCapture),
                            symbol: "arrow.down.to.line.compact"
                        ) { appModel.startCapture(.long) }

                        quickAction(
                            title: "钉剪贴板",
                            subtitle: "从已有内容生成钉图",
                            shortcut: "",
                            symbol: "clipboard"
                        ) { appModel.pinClipboardContent() }
                    }
                }

                Spacer(minLength: 0)
                statusBar
            }
            .padding(WelcomeViewMetrics.contentPadding)
        }
        .tint(TaPalette.cinnabar)
        .taPaperAppearance()
        .frame(
            minWidth: WelcomeViewMetrics.minimumWindowSize.width,
            minHeight: WelcomeViewMetrics.minimumWindowSize.height
        )
        .onAppear(perform: refreshPermission)
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            refreshPermission()
        }
        .onReceive(NotificationCenter.default.publisher(for: HotKeyPreferences.didChangeNotification)) { _ in
            shortcuts = HotKeyPreferences().allShortcuts()
        }
    }

    private var header: some View {
        HStack(spacing: 15) {
            TaAppIcon(size: 52)
                .shadow(color: TaPalette.ink.opacity(0.12), radius: 10, y: 4)

            VStack(alignment: .leading, spacing: 3) {
                Text(TaBrand.name)
                    .font(.system(size: 25, weight: .bold, design: .serif))
                    .foregroundStyle(TaPalette.ink)
                Text(TaBrand.tagline)
                    .font(.callout)
                    .foregroundStyle(TaPalette.mutedInk)
            }

            Spacer()

            HStack(spacing: 6) {
                Circle()
                    .fill(permissionGranted ? Color.green : Color.orange)
                    .frame(width: 7, height: 7)
                Text(permissionGranted ? "本地就绪" : "需要权限")
                    .font(.caption.weight(.medium))
                    .foregroundStyle(.secondary)
            }
            .padding(.horizontal, 10)
            .padding(.vertical, 6)
            .background(TaPalette.elevatedPaper, in: Capsule())
            .overlay {
                Capsule().stroke(TaPalette.hairline)
            }

            Button(action: onOpenSettings) {
                Label("设置", systemImage: "gearshape")
                    .font(.callout.weight(.medium))
            }
            .buttonStyle(.bordered)
            .controlSize(.regular)
            .tint(TaPalette.ink)
        }
    }

    private var primaryCaptureCard: some View {
        WelcomePrimaryActionButton(
            shortcut: shortcutText(for: .interactiveCapture),
            disabled: appModel.isCapturing
        ) {
            appModel.startCapture(.interactive)
        }
    }

    private var statusBar: some View {
        HStack(spacing: 12) {
            ZStack {
                Circle()
                    .fill((permissionGranted ? Color.green : Color.orange).opacity(0.12))
                Image(systemName: permissionGranted ? "checkmark.shield.fill" : "exclamationmark.triangle.fill")
                    .font(.system(size: 14, weight: .medium))
                    .foregroundStyle(permissionGranted ? .green : .orange)
            }
            .frame(width: 32, height: 32)

            VStack(alignment: .leading, spacing: 4) {
                Text(appModel.statusText)
                    .font(.callout.weight(.medium))
                Text(permissionGranted
                     ? "屏幕录制权限已开启 · 默认在本机识别"
                     : "首次使用需要允许读取你主动框选的屏幕区域")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Spacer()

            if permissionGranted {
                Button("重新检测", action: refreshPermission)
                    .buttonStyle(.plain)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            } else {
                Button("开启权限") {
                    _ = ScreenCapturePermissionService.request()
                    refreshPermission()
                }
                .buttonStyle(.borderedProminent)

                Button("系统设置") {
                    ScreenCapturePermissionService.openSystemSettings()
                }
            }

            Divider()
                .frame(height: 22)

            Text("v\(AIScreenshotCore.version)")
                .font(.caption2.monospacedDigit())
                .foregroundStyle(.tertiary)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 11)
        .background(TaPalette.elevatedPaper, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .stroke(TaPalette.hairline)
        }
    }

    private func quickAction(
        title: String,
        subtitle: String,
        shortcut: String,
        symbol: String,
        action: @escaping () -> Void
    ) -> some View {
        WelcomeQuickActionButton(
            title: title,
            subtitle: subtitle,
            shortcut: shortcut,
            symbol: symbol,
            disabled: appModel.isCapturing,
            action: action
        )
    }

    private func refreshPermission() {
        permissionGranted = ScreenCapturePermissionService.isGranted
    }

    private func shortcutText(for action: GlobalHotKeyAction) -> String {
        (shortcuts[action] ?? action.defaultShortcut).displayText
    }
}

private struct WelcomePrimaryActionButton: View {
    let shortcut: String
    let disabled: Bool
    let action: () -> Void
    @State private var isHovering = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 16) {
                ZStack {
                    RoundedRectangle(cornerRadius: 13, style: .continuous)
                        .fill(TaPalette.cinnabar)
                    Image(systemName: "viewfinder")
                        .font(.system(size: 24, weight: .semibold))
                        .foregroundStyle(TaPalette.paper)
                }
                .frame(width: 52, height: 52)

                VStack(alignment: .leading, spacing: 5) {
                    Text("开始拓取")
                        .font(.title3.weight(.bold))
                        .foregroundStyle(TaPalette.paper)
                    Text("框选屏幕，再取字、翻译、复制、钉图、标注或美化")
                        .font(.callout)
                        .foregroundStyle(TaPalette.paper.opacity(0.67))
                }

                Spacer(minLength: 16)
                ShortcutBadge(text: shortcut)

                HStack(spacing: 6) {
                    Text("开始截图")
                    Image(systemName: "arrow.right")
                }
                .font(.callout.weight(.semibold))
                .foregroundStyle(TaPalette.ink)
                .padding(.horizontal, 14)
                .padding(.vertical, 9)
                .background(TaPalette.paper, in: Capsule())
            }
            .padding(18)
            .background(
                TaPalette.ink.opacity(isHovering ? 0.94 : 1),
                in: RoundedRectangle(cornerRadius: WelcomeViewMetrics.primaryCornerRadius, style: .continuous)
            )
            .overlay {
                RoundedRectangle(cornerRadius: WelcomeViewMetrics.primaryCornerRadius, style: .continuous)
                    .stroke(TaPalette.cinnabar.opacity(isHovering ? 0.72 : 0.20), lineWidth: isHovering ? 2 : 1)
            }
            .shadow(color: TaPalette.ink.opacity(isHovering ? 0.16 : 0.10), radius: isHovering ? 16 : 10, y: 6)
            .contentShape(RoundedRectangle(cornerRadius: WelcomeViewMetrics.primaryCornerRadius, style: .continuous))
        }
        .buttonStyle(.plain)
        .disabled(disabled)
        .opacity(disabled ? 0.6 : 1)
        .onHover { isHovering = $0 }
    }
}

private struct WelcomeQuickActionButton: View {
    let title: String
    let subtitle: String
    let shortcut: String
    let symbol: String
    let disabled: Bool
    let action: () -> Void
    @State private var isHovering = false

    var body: some View {
        Button(action: action) {
            VStack(alignment: .leading, spacing: 9) {
                HStack {
                    ZStack {
                        RoundedRectangle(cornerRadius: 8, style: .continuous)
                            .fill(TaPalette.cinnabar.opacity(isHovering ? 0.14 : 0.09))
                        Image(systemName: symbol)
                            .font(.system(size: 15, weight: .medium))
                            .foregroundStyle(TaPalette.cinnabar)
                    }
                    .frame(width: 30, height: 30)

                    Spacer(minLength: 8)
                    if !shortcut.isEmpty {
                        ShortcutBadge(text: shortcut)
                    }
                }

                VStack(alignment: .leading, spacing: 3) {
                    Text(title)
                        .font(.callout.weight(.semibold))
                        .foregroundStyle(TaPalette.ink)
                    Text(subtitle)
                        .font(.caption)
                        .foregroundStyle(TaPalette.mutedInk)
                        .lineLimit(1)
                }
            }
            .frame(maxWidth: .infinity, minHeight: WelcomeViewMetrics.quickActionMinimumHeight, alignment: .topLeading)
            .padding(13)
            .background(
                isHovering ? TaPalette.cinnabar.opacity(0.055) : TaPalette.elevatedPaper,
                in: RoundedRectangle(cornerRadius: WelcomeViewMetrics.quickActionCornerRadius, style: .continuous)
            )
            .overlay {
                RoundedRectangle(cornerRadius: WelcomeViewMetrics.quickActionCornerRadius, style: .continuous)
                    .stroke(isHovering ? TaPalette.cinnabar.opacity(0.34) : TaPalette.hairline)
            }
            .contentShape(RoundedRectangle(cornerRadius: WelcomeViewMetrics.quickActionCornerRadius, style: .continuous))
        }
        .buttonStyle(.plain)
        .disabled(disabled)
        .opacity(disabled ? 0.6 : 1)
        .onHover { isHovering = $0 }
    }
}

private struct ShortcutBadge: View {
    let text: String

    var body: some View {
        Text(text)
            .font(.system(.caption2, design: .rounded, weight: .medium))
            .foregroundStyle(TaPalette.mutedInk)
            .padding(.horizontal, 7)
            .padding(.vertical, 4)
            .background(TaPalette.paper.opacity(0.82), in: Capsule())
            .overlay {
                Capsule().stroke(TaPalette.ink.opacity(0.08))
            }
    }
}
