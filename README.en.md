<div align="center">

<img src="./Resources/Brand/Ta-AppIcon.png" width="128" alt="Ta Logo">

# Ta · 拓

### A thousand years ago, ink lifted words from stone. Today, AI lifts information from your screen.

[![License: MIT](https://img.shields.io/badge/License-MIT-D6402F.svg)](./LICENSE)
[![Platform: macOS 14+](https://img.shields.io/badge/macOS-14%2B-1A1A1A.svg)](https://www.apple.com/macos/)
[![Platform: Windows 10 2004+](https://img.shields.io/badge/Windows-10%202004%2B-1A1A1A.svg)](./windows)
[![Swift: 6.2](https://img.shields.io/badge/Swift-6.2-F05138.svg)](https://www.swift.org/)
[![Release: v1.0.1](https://img.shields.io/badge/Release-v1.0.1-C98B2E.svg)](https://github.com/kangarooking/Ta/releases/tag/v1.0.1)

**An AI-native screenshot tool: capture, OCR, understand images, translate, stitch, pin, and annotate in one flow.**

Ta ships for macOS 14+ today, and a **Windows preview** (a .NET 8 port, source in [`windows/`](./windows)) is available for download.

[简体中文](./README.md) · [English](./README.en.md) · [日本語](./README.ja.md)

</div>

![Ta home screen](./docs/brand/Ta-home-preview.png)

## More than a thousand years ago, China had its own kind of “screenshot”

Before cameras, photocopiers, or modern printing, people faced a practical question: how could they take the writing on a stone stele home?

They laid paper over the stone and gently dabbed it with ink. When the paper was lifted, the characters left the stone and travelled with them. This craft is called **ink rubbing**—*tà yìn* (拓印)—and it has been practised for more than a thousand years. It was an ancient way to capture what you saw and keep it.

The character 「拓」 tells the same story: `hand + stone`. The Chinese name is pronounced `tà`; the English name is **Ta**.

Today, the stone has become a screen. Information appears faster and disappears faster. Ta does the same job: frame it and lift it out. Text becomes copyable and translatable; images can be annotated or pinned in view. A scrolling capture lifts the whole “stele,” while AI acts like a pocket epigrapher, helping you read what you captured.

> Cangjie created characters; ink rubbing carried them forward. Creating information is only the beginning—it is complete when it can be preserved, understood, and taken with you.

## Why I built Ta

I use screenshot software almost every day. There are countless free and paid options, but after trying many of them, I still could not find one tool that covered everything I needed. OCR, translation, scrolling capture, image pins, annotation, and publishing-ready styling were scattered across different apps and disconnected workflows.

So I decided to build one.

Most screenshot apps add OCR or an isolated AI button to an existing workflow. Ta is an attempt to make AI part of the workflow itself—and to keep improving that experience around real, everyday needs, first mine and then yours.

## What “AI-native screenshot tool” means

AI-native does not mean attaching a chat box to a traditional screenshot app. It means AI can help from the moment a capture is made:

- **Capture it** — grab a region, a window, or a scrolling page;
- **Read it** — extract text, tables, formulas, and code with local OCR or multimodal models;
- **Understand it** — translate, explain, and structure what is on the screen;
- **Keep it useful** — copy, pin, annotate, beautify, save, or continue processing;
- **Respect boundaries** — prefer local recognition, ask before cloud processing, and keep API keys in macOS Keychain.

## Problems it solves

- **Too much work after capture** — OCR, translation, copy, save, and annotation live in one flow.
- **Unreliable scrolling screenshots** — manual or automatic scrolling, duplicate-frame filtering, fixed-region removal, seam review, and manual correction.
- **One OCR engine does not fit every task** — switch among Apple Vision, PaddleOCR, and remote vision services based on speed, structure, and privacy.
- **Slow annotation workflows** — in-place annotation, direct object manipulation, brush mosaic, and floating image pins inspired by Snipaste.
- **Unclear cloud boundaries** — local processing by default, explicit upload notices, and API keys stored in macOS Keychain.

## Classic workflows

Every image below was captured from the current working version of Ta.

<table>
  <tr>
    <td width="50%" valign="top">
      <strong>Capture once, choose what happens next</strong><br><br>
      <img src="./docs/showcase/02-capture-toolbar.png" alt="Ta universal capture toolbar">
      <br>Extract text, use AI vision, translate, copy, pin, annotate, beautify, or save without leaving the capture flow.
    </td>
    <td width="50%" valign="top">
      <strong>Bring your own AI model</strong><br><br>
      <img src="./docs/showcase/05-ai-model-settings.png" alt="Ta AI model settings">
      <br>Choose from multiple provider protocols while keeping API keys in macOS Keychain.
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <strong>Annotate in place</strong><br><br>
      <img src="./docs/showcase/03-annotation-tools.png" alt="Ta in-place annotation toolbar">
      <br>Add arrows, text, highlights, two kinds of mosaic, and resize objects directly over the original capture.
    </td>
    <td width="50%" valign="top">
      <strong>Keep references in view</strong><br><br>
      <img src="./docs/showcase/04-pin-image.png" alt="Ta pinned image example">
      <br>Pins stay on top and can be moved, resized, faded, or dismissed with a double-click.
    </td>
  </tr>
</table>

## How it works

Ta uses a native macOS capture pipeline:

```text
Global shortcut
    ↓
Select a screen region
    ↓
ScreenCaptureKit capture (excluding Ta's own windows)
    ↓
┌────────────────┬────────────────────┐
│ Local OCR      │ Multimodal vision  │
│ Apple Vision   │ OpenAI-compatible  │
│ PaddleOCR      │ Claude / Gemini    │
└────────────────┴────────────────────┘
    ↓
Copy · Translate · Pin · Annotate · Stitch · Save
```

If the clipboard changes while recognition is running, Ta will not overwrite the user's newer clipboard content. When no text is found, the workflow can safely fall back to copying a PNG.

## Core features

### Capture and shortcuts

- A universal capture toolbar with OCR, AI vision, translation, copy, pin, annotate, beautify, and save actions.
- Dedicated global shortcuts for instant OCR, image copy, translation, pinning, and scrolling capture.
- Record custom shortcuts, detect conflicts, and restore defaults from Settings.
- Cancel region selection with right-click or `Escape` without creating a file or changing the clipboard.
- Hide Ta while capturing so the app neither steals focus nor appears inside the capture.

### OCR and AI vision

- **Apple Vision** — the default local OCR path for Chinese, English, and common text layouts.
- **PaddleOCR optional pack** — offline on Apple Silicon, with install, update, checksum, warm-up, and persistent worker support.
- **DeepSeek-OCR-2** — connect to a user-hosted vLLM, SGLang, or compatible vision endpoint; Ta does not silently download large model weights.
- **Multimodal providers** — OpenAI-compatible, Azure OpenAI, Anthropic Claude, and Google Gemini protocols.
- Task templates for exact text extraction, code explanation, Markdown/CSV tables, LaTeX formulas, and general visual understanding.
- OCR-only, model-only, or local-first smart routing with explicit confirmation before upload.

### Screenshot translation

- Customizable source and target languages; the default is auto-detect to Simplified Chinese.
- Translate a capture and copy the result directly to the clipboard.
- Return plain text, replace text inside the image, or append a bilingual panel below the original.
- Text localization and final image composition happen locally; only text that needs translation is sent to the configured model.

### AI beautification (in development)

- Automatically add whitespace, rounded corners, shadows, backgrounds, and publishing layouts for social posts and product documentation.
- Planned capabilities include smart callouts, sensitive-data redaction, multi-size export, and generated backgrounds or decorative elements that match the capture.
- The current release includes a reserved Beautify entry point. The full AI beautification workflow is still in development and is not presented as finished functionality.

### Scrolling capture

- Manual or automatic scrolling capture for browsers, chats, and common desktop apps.
- Match adjacent frames, filter duplicates, and detect scroll direction.
- Detect and remove fixed headers, footers, and input areas.
- Review seams before export and adjust them by `±1` or `±10 px`.
- Segment extremely tall images to reduce memory and export pressure.

### Annotation and image pins

- Annotate in place over a translucent overlay while the capture stays where it was taken.
- Rectangle, ellipse, arrow, pen, highlighter, text, numbering, mosaic, blur, eraser, and magnifier tools.
- Select, move, resize, rotate, and re-edit annotation objects directly.
- Mosaic supports both rectangular selection and freehand painting.
- Pins support drag, resize, opacity, rotation, flip, filters, crop, click-through, groups, hide/restore, and double-click to close.
- Create pins from captures, clipboard images, text, HTML, or files.

## Quick start

### Requirements

- macOS 14 or later
- Apple Silicon or Intel Mac (the packaged PaddleOCR add-on currently targets Apple Silicon)
- Xcode 26 or another Swift 6.2-compatible toolchain

### Download

[**Download Ta v1.0.1 (Universal macOS DMG)**](https://github.com/kangarooking/Ta/releases/latest/download/Ta-1.0.1-macOS-universal.dmg)

The installer supports both Apple Silicon and Intel Macs. Open the DMG and drag 「拓」 into `Applications`. This build is not yet Apple-notarized; on first launch, Control-click the app in Finder, choose **Open**, and confirm once more.

[**Download Ta for Windows (Preview, ZIP, no installer)**](https://github.com/qbdx-hub/Ta/releases/download/windows-v0.1.0/Ta-windows-v0.1.0-x64.zip)

For Windows 10 version 2004 or later, and Windows 11. The package **bundles the .NET runtime and needs no installation**: after unzipping, run `Ta.Shell.exe` first (tray icon and global shortcuts), then `Ta.Settings.exe` (panel and settings). It is built from [`windows/`](./windows) and is not code-signed, so SmartScreen may warn on first launch — choose **Run anyway**.

### Build from source

```bash
git clone https://github.com/kangarooking/Ta.git
cd Ta
swift test
./scripts/build-app.sh
open "artifacts/拓.app"
```

On first launch, grant Screen & System Audio Recording permission. Accessibility permission is only required for automatic scrolling capture.

### Default shortcuts

| Action | Shortcut |
|--------|----------|
| Instant OCR | `⇧⌥⌘1` |
| Universal capture | `⇧⌥⌘2` |
| Copy image | `⇧⌥⌘3` |
| Capture and pin | `⇧⌥⌘4` |
| Scrolling capture | `⇧⌥⌘5` |
| Screenshot translation | `⇧⌥⌘6` |

Open **Settings → Shortcuts** to record any new combination. Conflicting shortcuts are rejected or rolled back automatically.

## Use Ta from an Agent

Ta can now act as an Agent's visual input layer in three forms:

- **Ta Agent Skill** teaches compatible Agents how to combine capture, OCR, vision, and translation safely;
- **`ta` CLI** provides stable JSON commands for shells, scripts, and general Agents;
- **`dsh-ta`** is a native DeepSeek Harness Cordis Plugin + Bundle that registers capture and understanding tools directly.

All three call the local Bridge hosted by 「拓.app」. Ordinary Agent captures do not show Ta, steal focus, move the pointer, or send keyboard events. Permissions, model profiles, and API keys remain managed by Ta.

Install or update both the `ta` CLI and Ta Agent Skill with one command:

```bash
curl -fsSL --retry 3 --retry-all-errors --retry-delay 1 https://github.com/kangarooking/Ta/releases/latest/download/install.sh | bash
```

The installer verifies SHA-256, installs the CLI at `~/.local/bin/ta`, and installs the Skill into Codex and common Agent Skills directories. Restart the Agent, then verify the connection:

```bash
ta status --json
ta capture frontmost --json
ta ocr last --json
```

Use **Settings → Agent** to disable automation, deny cloud processing, block sensitive apps by bundle ID, clear temporary artifacts, and inspect redacted recent calls. See the [Agent integration guide](./docs/agent-integration.md) for CLI, Skill, and DeepSeek Harness installation.

## Privacy and security

- Standard capture and Apple Vision OCR always run on-device.
- The PaddleOCR add-on runs offline after installation.
- A selected region or extracted text is sent out only when the user explicitly chooses remote OCR, multimodal vision, or translation.
- Low-confidence smart routing never uploads silently and always requires confirmation.
- API keys are stored only in macOS Keychain, never in preferences, logs, or this repository.
- Ta does not continuously record the screen; it reads only a region the user actively selects.
- The Agent Bridge uses a current-user-only local Unix socket, and its audit log stores no request parameters, recognized text, or image data.

## Repository structure

```text
Ta/
├── README.md / README.en.md / README.ja.md
├── Package.swift
├── Resources/                 icons, Info.plist, and brand assets
├── Sources/
│   ├── AIScreenshotCore/      OCR, stitching, providers, clipboard policy
│   ├── AIScreenshotApp/       capture, editor, routing, system, and UI
│   ├── TaAgentContracts/      Bridge protocol
│   ├── TaAgentClient/         local Bridge client
│   └── TaCLI/                 ta CLI
├── Integrations/              Agent Skill and native DeepSeek Harness plugin
├── Tests/                     Core and App tests
├── ocr-packs/paddleocr/       optional PaddleOCR pack definitions
├── scripts/                   build, run, and OCR pack scripts
└── docs/                      PRD, research, validation, and plans
```

## Project status

Ta v1.0.1 is the current public release. It ships as a universal DMG and ZIP for Apple Silicon and Intel Macs. The current build is not yet Apple-notarized, so first launch requires Control-clicking the app in Finder and choosing **Open**.

Known limitations:

- Region selection currently focuses on the display under the pointer; cross-display selection and automatic window snapping are not complete.
- Scrolling capture can still require manual seam correction on video, animation, translucent overlays, or heavily reflowing layouts.
- Image translation uses local cover-and-redraw composition; complex textures, gradients, shadows, vertical text, and dense layouts can leave artifacts.
- The repository contains PaddleOCR pack definitions, not the large locally built archives or model weights.
- v1.0.1 uses Apple Development signing; Developer ID signing and Apple notarization are still in progress.

## Documentation

- [Product requirements (Chinese)](./AI截图软件-产品需求文档-PRD-v1.0.md)
- [Market and user pain-point research (Chinese)](./AI截图软件市场与用户痛点调研.md)
- [Alpha verification](./docs/alpha-verification.md)
- [Scrolling capture acceptance matrix](./docs/long-capture-acceptance-matrix.md)
- [PaddleOCR add-on specification](./docs/ocr-enhancement-pack-spec.md)
- [Agent Skill, CLI, and DeepSeek Harness integration guide](./docs/agent-integration.md)

## Vision: screenshots as an Agent's eyes

Ta aims to become more than a feature-complete screenshot utility. Over time, we want to add more **Agent capabilities**, turning a static image into an entry point for understanding the screen and completing useful work.

With explicit confirmation, an Agent could:

- recognize tasks, dates, links, and tables, then turn them into notes, todos, or structured data;
- understand interface state and suggest the next step, or connect translation, annotation, beautification, and export into one workflow;
- detect and redact phone numbers, email addresses, avatars, and other sensitive information;
- turn long conversations, code errors, product pages, and research material into editable output;
- learn your preferred capture actions and package repetitive steps into reusable personal workflows.

Agent features will remain permission-based, visible, and reversible. Ta should help you take information off the screen, understand it, and keep working with it—not take control of your screen away from you.

## Roadmap

- [x] Native capture, OCR, clipboard output, and custom shortcuts
- [x] Scrolling capture, automatic scrolling, and seam review
- [x] In-place annotation, brush mosaic, and image pins
- [x] Screenshot translation and multi-provider configuration
- [x] Optional offline PaddleOCR pack protocol
- [x] Agent Skill, `ta` CLI, and native DeepSeek Harness plugin
- [ ] Cross-display region selection and window snapping
- [ ] Publishing templates and parameterized screenshot styling
- [ ] AI beautification, smart privacy redaction, and multi-size generation
- [ ] History, search, and result re-copy
- [ ] Confirmable and reversible screenshot Agent workflows
- [x] Windows version (.NET 8 port, see [`windows/`](./windows))
- [x] Universal macOS DMG, ZIP, and checksums
- [ ] Developer ID signing and Apple notarization

## Free, open source, and built together

Ta is free and open source under the [MIT License](./LICENSE). Anyone can download the source, build it, use it, modify it, and redistribute it.

If your screenshot workflow has a problem Ta does not solve yet, open an Issue. If you would like to help improve it, pull requests are welcome. Read [CONTRIBUTING.md](./CONTRIBUTING.md) before making changes, and include tests or reproducible validation steps for behavioral changes.

If Ta saves you an app switch or a repetitive step, please give the project a **Star**. It is the simplest way to support its development.

## About the Author

**kangarooking** — AI blogger, indie developer. Creator of AI Top WeChat Official Account「袋鼠帝 AI 客栈」

Volcengine Navigation KOL, Baidu Qianfan Developer Ambassador, GLM Evangelist, Trae Kunming's First Fellow

| Platform | Link |
|----------|------|
| 𝕏 Twitter | https://x.com/aikangarooking |
| Xiaohongshu | https://xhslink.com/m/5YejKvIDBbL |
| Douyin | https://v.douyin.com/hYpsjphuuKc |
| WeChat Official Account | 袋鼠帝 AI 客栈 |
| WeChat Video Channel | AI 袋鼠帝 |

WeChat Official Account「袋鼠帝 AI 客栈」QR code:

![](https://raw.githubusercontent.com/kangarooking/cangjie-skill/main/assets/kangarooking-gzh.png)

To share Ta workflows, report screenshot pain points, or help build an AI-native screenshot tool, join the Ta WeCom community:

<img src="https://raw.githubusercontent.com/kangarooking/Ta/main/assets/wecom-ta-group-qr.png" width="220" alt="Ta WeCom community QR code">

## ⭐ Star History

If Ta helps you, consider giving the project a Star.

<a href="https://www.star-history.com/?repos=kangarooking%2FTa&type=date&legend=top-left">
 <picture>
   <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/chart?repos=kangarooking/Ta&type=date&legend=top-left" />
   <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/chart?repos=kangarooking/Ta&type=date&legend=top-left" />
   <img alt="Ta Star History Chart" src="https://api.star-history.com/chart?repos=kangarooking/Ta&type=date&legend=top-left" />
 </picture>
</a>

## License

MIT. See [LICENSE](./LICENSE).
