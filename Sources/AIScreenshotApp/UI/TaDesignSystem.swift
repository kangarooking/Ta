import AppKit
import SwiftUI

enum TaBrand {
    static let name = "拓"
    static let englishName = "Ta"
    static let tagline = "把屏幕上的信息，拓下来。"
    static let productDescription = "截图、识别、翻译与标注，一步完成"
}

enum TaPalette {
    static let ink = Color(red: 26 / 255, green: 26 / 255, blue: 26 / 255)
    static let paper = Color(red: 250 / 255, green: 246 / 255, blue: 238 / 255)
    static let elevatedPaper = Color(red: 1.0, green: 253 / 255, blue: 248 / 255)
    static let cinnabar = Color(red: 214 / 255, green: 64 / 255, blue: 47 / 255)
    static let mutedInk = Color(red: 104 / 255, green: 100 / 255, blue: 94 / 255)
    static let hairline = ink.opacity(0.10)
}

extension View {
    /// Ta's windows are drawn on a fixed light "paper" palette. Semantic
    /// styles such as `.primary` / `.secondary` and system controls follow the
    /// system appearance, which made text white-on-paper (invisible) in Dark
    /// Mode. Every paper-backed root view pins itself to the light scheme so
    /// text, form rows and controls always contrast with the background.
    func taPaperAppearance() -> some View {
        self
            .environment(\.colorScheme, .light)
            .preferredColorScheme(.light)
    }
}

enum TaBrandAssets {
    static let appIcon: NSImage? = {
        guard let url = Bundle.main.url(
            forResource: "Ta-AppIcon",
            withExtension: "png",
            subdirectory: "Brand"
        ) else {
            return nil
        }
        return NSImage(contentsOf: url)
    }()
}

struct TaAppIcon: View {
    var size: CGFloat

    var body: some View {
        Group {
            if let image = TaBrandAssets.appIcon {
                Image(nsImage: image)
                    .resizable()
                    .interpolation(.high)
            } else {
                fallbackMark
            }
        }
        .frame(width: size, height: size)
        .accessibilityHidden(true)
    }

    private var fallbackMark: some View {
        ZStack {
            RoundedRectangle(cornerRadius: size * 0.22, style: .continuous)
                .fill(TaPalette.paper)
            RoundedRectangle(cornerRadius: size * 0.08, style: .continuous)
                .fill(TaPalette.cinnabar)
                .padding(size * 0.20)
            Text(TaBrand.name)
                .font(.system(size: size * 0.42, weight: .bold, design: .serif))
                .foregroundStyle(TaPalette.paper)
        }
    }

}

struct TaPaperBackground: View {
    var body: some View {
        GeometryReader { proxy in
            ZStack(alignment: .bottomTrailing) {
                TaPalette.paper

                TaAppIcon(size: min(proxy.size.width, proxy.size.height) * 0.62)
                    .opacity(0.025)
                    .offset(x: proxy.size.width * 0.12, y: proxy.size.height * 0.18)
            }
        }
        .ignoresSafeArea()
        .accessibilityHidden(true)
    }
}

struct TaSectionLabel: View {
    let title: String

    var body: some View {
        HStack(spacing: 8) {
            Rectangle()
                .fill(TaPalette.cinnabar)
                .frame(width: 18, height: 2)
            Text(title)
                .font(.subheadline.weight(.semibold))
                .foregroundStyle(TaPalette.mutedInk)
        }
    }
}
