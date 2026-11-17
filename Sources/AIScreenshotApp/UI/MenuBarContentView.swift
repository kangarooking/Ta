import AppKit
import SwiftUI
import AIScreenshotCore

struct MenuBarContentView: View {
    @ObservedObject var appModel: AppModel
    @State private var shortcuts = HotKeyPreferences().allShortcuts()

    var body: some View {
        ZStack {
            TaPalette.paper

            VStack(alignment: .leading, spacing: 0) {
                HStack(spacing: 10) {
                    TaAppIcon(size: 38)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(TaBrand.name)
                            .font(.system(.headline, design: .serif, weight: .bold))
                            .foregroundStyle(TaPalette.ink)
                        Label(appModel.statusText, systemImage: "lock.shield.fill")
                            .font(.caption)
                            .foregroundStyle(TaPalette.mutedInk)
                    }
                    Spacer()
                    Text("本地优先")
                        .font(.caption2.weight(.medium))
                        .foregroundStyle(TaPalette.mutedInk)
                        .padding(.horizontal, 8)
                        .padding(.vertical, 5)
                        .background(TaPalette.elevatedPaper, in: Capsule())
                        .overlay { Capsule().stroke(TaPalette.hairline) }
                }
                .padding(14)

                Divider()

                VStack(spacing: 5) {
                    MenuActionButton(
                        title: "开始拓取",
                        subtitle: "截图后选择取字、复制、钉图或编辑",
                        shortcut: shortcutText(for: .interactiveCapture),
                        symbol: "viewfinder",
                        emphasized: true
                    ) {
                        appModel.startCapture(.interactive)
                    }

                    MenuActionButton(
                        title: "极速识别内容",
                        subtitle: "按设置选择 OCR 或多模态",
                        shortcut: shortcutText(for: .intelligentCapture),
                        symbol: "text.viewfinder"
                    ) {
                        appModel.startCapture(.intelligent)
                    }

                    MenuActionButton(
                        title: "截图翻译",
                        subtitle: "识别、翻译并复制文字",
                        shortcut: shortcutText(for: .translationCapture),
                        symbol: "character.book.closed"
                    ) {
                        appModel.startCapture(.translation)
                    }

                    MenuActionButton(
                        title: "截图图片",
                        subtitle: "始终复制原图",
                        shortcut: shortcutText(for: .imageCapture),
                        symbol: "rectangle.dashed"
                    ) {
                        appModel.startCapture(.image)
                    }

                    MenuActionButton(
                        title: "截图并钉住",
                        subtitle: "保持在其他窗口上方",
                        shortcut: shortcutText(for: .pinCapture),
                        symbol: "pin.fill"
                    ) {
                        appModel.startCapture(.pin)
                    }

                    MenuActionButton(
                        title: "长截图",
                        subtitle: "适用于浏览器、微信和 AI 对话",
                        shortcut: shortcutText(for: .longCapture),
                        symbol: "rectangle.and.arrow.up.right.and.arrow.down.left"
                    ) {
                        appModel.startCapture(.long)
                    }
                }
                .padding(6)

                Divider()

                VStack(alignment: .leading, spacing: 7) {
                    Text("钉图管理")
                        .font(.caption.bold())
                        .foregroundStyle(TaPalette.mutedInk)
                    HStack(spacing: 10) {
                        Button("钉剪贴板") { appModel.pinClipboardContent() }
                        Button("隐藏全部") { appModel.hideAllPins() }
                        Button("显示全部") { appModel.showAllPins() }
                    }
                    .buttonStyle(.link)
                    HStack(spacing: 10) {
                        Button("恢复穿透") { appModel.restorePinInteraction() }
                        Button("恢复关闭") { appModel.restoreLastClosedPin() }
                        Spacer()
                        Button("截图历史") { appModel.openCaptureHistoryFolder() }
                            .help("打开本机截图历史文件夹（需在“设置 › 常规”中开启保存历史）")
                    }
                    .buttonStyle(.link)
                }
                .padding(.horizontal, 14)
                .padding(.vertical, 10)

                Divider()

                HStack {
                    Button {
                        appModel.showWelcome()
                    } label: {
                        Label("打开主界面", systemImage: "macwindow")
                    }
                    .buttonStyle(.plain)

                    Divider()
                        .frame(height: 14)

                    Button {
                        appModel.showSettings()
                    } label: {
                        Label("设置", systemImage: "gearshape")
                    }
                    .buttonStyle(.plain)

                    Spacer()

                    Text(AIScreenshotCore.version)
                        .font(.caption2)
                        .foregroundStyle(.tertiary)

                    Button("退出") {
                        NSApplication.shared.terminate(nil)
                    }
                    .buttonStyle(.plain)
                }
                .padding(12)
            }
        }
        .frame(width: 326)
        .tint(TaPalette.cinnabar)
        .environment(\.colorScheme, .light)
        .task {
            appModel.start()
        }
        .onReceive(NotificationCenter.default.publisher(for: HotKeyPreferences.didChangeNotification)) { _ in
            shortcuts = HotKeyPreferences().allShortcuts()
        }
    }

    private func shortcutText(for action: GlobalHotKeyAction) -> String {
        (shortcuts[action] ?? action.defaultShortcut).displayText
    }
}

private struct MenuActionButton: View {
    let title: String
    let subtitle: String
    let shortcut: String
    let symbol: String
    var emphasized = false
    let action: () -> Void
    @State private var isHovering = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 10) {
                Image(systemName: symbol)
                    .font(.system(size: 16, weight: .medium))
                    .frame(width: 22)
                    .foregroundStyle(emphasized ? TaPalette.paper : TaPalette.cinnabar)
                VStack(alignment: .leading, spacing: 2) {
                    Text(title)
                        .foregroundStyle(emphasized ? TaPalette.paper : TaPalette.ink)
                    Text(subtitle)
                        .font(.caption)
                        .foregroundStyle(emphasized ? TaPalette.paper.opacity(0.62) : TaPalette.mutedInk)
                }
                Spacer()
                Text(shortcut)
                    .font(.system(.caption, design: .rounded))
                    .foregroundStyle(emphasized ? TaPalette.paper.opacity(0.72) : TaPalette.mutedInk)
            }
            .contentShape(Rectangle())
            .padding(.horizontal, 10)
            .padding(.vertical, 8)
            .background(
                emphasized
                    ? TaPalette.ink.opacity(isHovering ? 0.92 : 1)
                    : TaPalette.cinnabar.opacity(isHovering ? 0.07 : 0),
                in: RoundedRectangle(cornerRadius: 10, style: .continuous)
            )
            .overlay {
                if emphasized {
                    RoundedRectangle(cornerRadius: 10, style: .continuous)
                        .stroke(TaPalette.cinnabar.opacity(isHovering ? 0.58 : 0.20))
                }
            }
        }
        .buttonStyle(.plain)
        .onHover { isHovering = $0 }
    }
}
