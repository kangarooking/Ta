import AppKit
import SwiftUI

enum ResultBarKind: Equatable {
    case processing
    case success
    case warning
    case failure

    var symbol: String {
        switch self {
        case .processing: return "circle.dotted"
        case .success: return "checkmark.circle"
        case .warning: return "exclamationmark.triangle.fill"
        case .failure: return "xmark.circle"
        }
    }

    var color: Color {
        switch self {
        case .processing: return TaPalette.mutedInk
        case .success: return Color(red: 65 / 255, green: 143 / 255, blue: 61 / 255)
        case .warning: return .orange
        case .failure: return TaPalette.cinnabar
        }
    }
}

/// An optional inline button on the toast, e.g. "取消" while a request is
/// running or "仍然复制" when the clipboard guard kept a result back.
struct ResultBarAction {
    let title: String
    let handler: @MainActor () -> Void
}

struct ResultBarState {
    let kind: ResultBarKind
    let title: String
    let detail: String?
    var action: ResultBarAction? = nil
}

enum ResultBarLayout {
    static let minimumWidth: CGFloat = 320
    static let maximumWidth: CGFloat = 352
    /// Toasts that carry an inline action button may grow a little wider so
    /// the text is not squeezed out.
    static let maximumWidthWithAction: CGFloat = 440
    static let height: CGFloat = 58
    static let cornerRadius: CGFloat = 18
    static let borderWidth: CGFloat = 0
    static let shadowOpacity: Double = 0

    @MainActor
    static func preferredWidth(for state: ResultBarState) -> CGFloat {
        let titleWidth = (state.title as NSString).size(withAttributes: [
            .font: NSFont.systemFont(ofSize: 13, weight: .semibold)
        ]).width
        let detailWidth = ((state.detail ?? "") as NSString).size(withAttributes: [
            .font: NSFont.systemFont(ofSize: 11, weight: .regular)
        ]).width
        let textWidth = max(titleWidth, detailWidth)
        let fixedControlsAndSpacing: CGFloat = state.kind == .processing ? 116 : 144
        guard let action = state.action else {
            return min(maximumWidth, max(minimumWidth, ceil(textWidth + fixedControlsAndSpacing)))
        }
        let actionWidth = actionButtonWidth(for: action.title) + 10
        return min(
            maximumWidthWithAction,
            max(minimumWidth, ceil(textWidth + fixedControlsAndSpacing + actionWidth))
        )
    }

    @MainActor
    static func actionButtonWidth(for title: String) -> CGFloat {
        let titleWidth = (title as NSString).size(withAttributes: [
            .font: NSFont.systemFont(ofSize: 11, weight: .semibold)
        ]).width
        return ceil(titleWidth) + 20
    }
}

struct ResultBarView: View {
    let state: ResultBarState
    let onDismiss: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: state.kind.symbol)
                .font(.system(size: 21, weight: .medium))
                .foregroundStyle(state.kind.color)
                .symbolEffect(.pulse, options: .repeating, isActive: state.kind == .processing)
                .frame(width: 24, height: 24)

            VStack(alignment: .leading, spacing: 2) {
                Text(state.title)
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(TaPalette.ink)
                    .lineLimit(1)
                if let detail = state.detail {
                    Text(detail)
                        .font(.system(size: 11))
                        .foregroundStyle(TaPalette.mutedInk)
                        .lineLimit(1)
                        .truncationMode(.tail)
                }
            }

            Spacer(minLength: 4)
            Text(TaBrand.name)
                .font(.system(size: 10, weight: .semibold, design: .serif))
                .foregroundStyle(TaPalette.cinnabar)
                .frame(width: 22, height: 22)
                .overlay {
                    RoundedRectangle(cornerRadius: 5, style: .continuous)
                        .stroke(TaPalette.cinnabar.opacity(0.82), lineWidth: 1)
                }
                .accessibilityLabel("拓")

            if let action = state.action {
                Button(action: { action.handler() }) {
                    Text(action.title)
                        .font(.system(size: 11, weight: .semibold))
                        .foregroundStyle(TaPalette.cinnabar)
                        .lineLimit(1)
                        .fixedSize()
                        .frame(width: ResultBarLayout.actionButtonWidth(for: action.title), height: 24)
                        .background(TaPalette.cinnabar.opacity(0.10), in: Capsule())
                        .contentShape(Capsule())
                }
                .buttonStyle(.plain)
                .help(action.title)
                .accessibilityIdentifier("result-bar-action")
            }

            if state.kind != .processing {
                Button(action: onDismiss) {
                    Image(systemName: "xmark")
                        .font(.system(size: 10, weight: .semibold))
                        .foregroundStyle(TaPalette.mutedInk)
                        .frame(width: 24, height: 24)
                        .background(TaPalette.ink.opacity(0.055), in: Circle())
                }
                .buttonStyle(.plain)
                .help("关闭提示")
            }
        }
        .padding(.horizontal, 14)
        .frame(width: ResultBarLayout.preferredWidth(for: state), height: ResultBarLayout.height)
        .background(TaPalette.paper, in: RoundedRectangle(cornerRadius: ResultBarLayout.cornerRadius, style: .continuous))
        .environment(\.colorScheme, .light)
    }
}
