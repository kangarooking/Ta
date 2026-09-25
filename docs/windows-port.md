# Ta（拓）→ Windows 移植参考文档

> 目标：**1:1 还原** macOS 版「拓 · Ta」的全部功能与行为。
> 本文档基于对 v1.0.1 源码（`Package.swift` 声明 swift-tools 6.2，`platforms: .macOS(.v14)`）的逐文件通读整理。
> 所有数字、常量、路径、prompt 原文均标注了 `文件:行号`，可直接对照。

---

## 目录

1. [项目概览](#1-项目概览)
2. [工程结构与规模](#2-工程结构与规模)
3. [架构与核心数据流](#3-架构与核心数据流)
4. [截图模式与全局快捷键](#4-截图模式与全局快捷键)
5. [截图与框选子系统](#5-截图与框选子系统)
6. [标注子系统（含两套渲染器）](#6-标注子系统含两套渲染器)
7. [钉图子系统](#7-钉图子系统)
8. [长截图子系统](#8-长截图子系统)
9. [OCR / AI 识图 / 翻译](#9-ocr--ai-识图--翻译)
10. [配置存储与数据迁移](#10-配置存储与数据迁移)
11. [Ta Agent 桥与 CLI](#11-ta-agent-桥与-cli)
12. [视觉设计系统](#12-视觉设计系统)
13. [macOS API → Windows 对照表](#13-macos-api--windows-对照表)
14. [移植风险分级](#14-移植风险分级)
15. [技术选型建议](#15-技术选型建议)
16. [验收清单](#16-验收清单)

---

## 1. 项目概览

| 项 | 值 |
|---|---|
| 名称 | 拓 · Ta（中文「拓」，英文 Ta） |
| 定位 | AI 原生截图工具：截图、OCR 取字、AI 识图、翻译、长截图、钉图、标注 |
| 当前版本 | **v1.0.1**（`Resources/Info.plist:22` `CFBundleShortVersionString`，build `101`） |
| 平台 | macOS 14+，Swift 6.2，AppKit + SwiftUI + SwiftPM |
| Bundle ID | `com.kangarooking.AIScreenshot`（`Info.plist:12`，也是升级兼容契约，见 §10） |
| 代码规模 | **约 12,400 行 Swift**（Sources）+ **约 4,300 行测试** |
| 测试 | **44 个测试文件 / 190 个 test 函数**，覆盖全部几何与算法层 |
| License | MIT |
| 上游状态 | README Roadmap 明确列出 `Windows 版本` 为**未完成**项 |

### 1.1 产品主张（移植时必须保留的行为契约）

- **本地优先**：普通截图与 Apple Vision OCR 始终在本机完成，不联网。
- **云端必须显式**：只有用户主动选择远程 OCR / 多模态识图 / 翻译时才上传。
- **智能路由不静默上传**：低置信度必须由用户二次确认。
- **API Key 只存 Keychain**，不写入偏好设置、日志或仓库。
- **不持续录屏**：只读取用户主动框选的区域。
- **截图时自动隐藏 Ta 自身**，不抢焦点、不把自身截进图。

---

## 2. 工程结构与规模

### 2.1 目录（`Package.swift:17-65` 声明 5 个 target + 5 个 test target）

```
Ta/
├── Package.swift                    6 个 product、5 个 target
├── Resources/
│   ├── Info.plist                   Bundle ID、版本、权限用途说明
│   ├── Ta.icns                      406 KB 图标
│   └── Brand/                       品牌图标
├── Sources/
│   ├── AIScreenshotCore/            ← 纯逻辑，无 AppKit（除 Vision 链接），最易移植
│   │   ├── Models/CaptureModels.swift       230  枚举与状态机
│   │   ├── OCR/                             212  Vision OCR + 布局分析 + 内容分类
│   │   ├── LongCapture/                    1,129  长截图匹配/拼接/进度
│   │   ├── Recognition/                      900  AI Provider 客户端
│   │   └── Clipboard/                          9  剪贴板安全策略
│   ├── AIScreenshotApp/             ← 全部 AppKit/UI，移植工作量主体
│   │   ├── App/AppModel.swift                245  启动、快捷键注册、Agent 桥
│   │   ├── Capture/                         2,907  框选/捕获/长截图会话
│   │   ├── Editor/                          2,192  标注编辑器
│   │   ├── Recognition/                     1,512  OCR 增强包、翻译、渲染
│   │   ├── Agent/                           1,794  Agent 能力服务
│   │   ├── System/                             884  快捷键/权限/Keychain/导出
│   │   └── UI/                              4,000+ 设置/菜单栏/钉图/结果栏
│   ├── TaAgentContracts/             780  Bridge 协议（跨平台契约）
│   ├── TaAgentClient/                303  本机 Bridge 客户端
│   └── TaCLI/                        548  ta 命令行
├── Integrations/
│   ├── AgentSkill/ta/               Agent Skill 定义（SKILL.md + references + scripts）
│   └── DeepSeekHarness/dsh-ta/      TypeScript 原生 Cordis 插件（package.json + src + tests）
├── Tests/                           4,300 行 / 190 个测试
├── ocr-packs/paddleocr/             可选离线 OCR 增强包构建定义
└── scripts/                         build / install / release / 图标生成脚本
```

### 2.2 框架链接关系（`Package.swift:18-44`）

| Target | 链接框架 |
|---|---|
| `AIScreenshotCore` | **Vision** |
| `AIScreenshotApp` | AppKit, Carbon, ApplicationServices, **ScreenCaptureKit**, Security |
| `TaAgentClient` | AppKit |
| `TaAgentContracts` / `TaCLI` | 仅 Foundation（可移植性最好） |

> **移植分层结论**：`TaAgentContracts` 与 `AIScreenshotCore` 的数学/算法层几乎无平台依赖，可 1:1 直译；`AIScreenshotApp` 是工作量所在。

### 2.3 macOS API 使用热度（全 Sources 统计）

| 出现次数 | API | 次数 | API |
|---|---|---|---|
| 102 | `CGImage` | 16 | `NSAttributedString` |
| 40 | `NSEvent` | 14 | `AXUIElement` |
| 38 | `NSGraphicsContext` | 13 | `NSWindow` / `NSBitmapImageRep` |
| 35 | `NSImage` | 11 | `NSPanel` |
| 23 | `NSFont` | 7 | `NSWorkspace` |
| 18 | `CGContext` | 6 | `NSPasteboard` |
| 17 | `CGDirectDisplayID` | 3 | ScreenCaptureKit / SCShareableContent |

---

## 3. 架构与核心数据流

```
NSStatusItem 菜单栏图标                                    MenuBarController.swift:14
  └─ NSPopover 326×574 (.transient, animates)  → MenuBarContentView.swift
        └─ 6 个截图按钮 ─────────────┐
GlobalHotKeyManager                  │                        AppModel.swift:51-58
(Carbon RegisterEventHotKey) ────────┴──→ AppModel.startCapture(mode)      AppModel.swift:168
                                                │
                        CaptureCoordinator.start(mode:completion:)   CaptureCoordinator.swift:141
                                                │
                    ┌───────────────────────────┼───────────────────────────┐
                    ▼                           ▼                           ▼
          ensureScreenCapturePermission   [.long] 分支               prepareStartContext()
          (CGPreflightScreenCaptureAccess)  startLongCapture      (鼠标所在屏 + 窗口吸附目标)
                    │                           │                           │
                    └───────────┬───────────────┘                           │
                                ▼                                           │
              captureDisplay(全屏, 冻结, pixelScale=backingScaleFactor)  ◄───┘
                                │      CaptureCoordinator.swift:169-173
                                ▼
              SelectionOverlayController.begin(context:frozenDisplayImage:)
                                │
                    SelectionOverlayPanel (.borderless,.nonactivatingPanel, level .screenSaver)
                                │
                         SelectionOverlayView (AppKit 自绘)   SelectionOverlayView.swift
                                │  onFinish(rect, action?)
                                ▼
              CaptureCoordinator.process(jobID:action:selection:)   CaptureCoordinator.swift:261
                                │
                                ├─ copyImage   → ClipboardService（带竞态保护）
                                ├─ pin         → PinnedImageWindowController
                                ├─ localOCR    → ConfiguredOCRService
                                ├─ multimodal  → MultimodalRecognitionService
                                ├─ translateText→ ScreenshotTranslationService + TranslatedImageRenderer
                                ├─ edit        → InlineAnnotationController（原位标注）
                                ├─ save        → ImageExportService
                                └─ beautify    → 预留入口，**未实现**，必须如实提示
                                ▼
                         ResultBarController（结果反馈条）
```

### 3.1 关键设计：**先冻结整屏，再框选**

`CaptureCoordinator.swift:162-173` 的源码注释说明了这个设计：

> "The overlay is shown only after the full display frame has been captured, so the user always selects from the shortcut-time image rather than a live page."

**移植必须保留**：快捷键按下 → 立即把鼠标变十字 → 捕获**整屏**快照 → 显示覆盖层 → 用户从**这张冻结帧**上框选 → 完成后从冻结帧裁剪（`FrozenDisplayCropper.crop`，`ScreenCaptureService.swift:71-73`），**不再二次实时截图**。

这带来两个好处，也是行为契约：
1. 覆盖层无闪烁（背景是位图而非实时画面）。
2. 选区内容与快捷键按下瞬间一致。

---

## 4. 截图模式与全局快捷键

### 4.1 六种模式（`CaptureMode`，`AIScreenshotCore/Models/CaptureModels.swift:3-10`）

| Mode | rawValue | 菜单栏文案 | 显示操作栏? | 直接动作 |
|---|---|---|---|---|
| `.interactive` | interactive | 开始拓取 / 框选后选择操作 | **是**（当 `postCaptureAction == .choose`） | 无 → 显示工具栏 |
| `.intelligent` | intelligent | 极速识别内容 / 选择需要识别的区域 | 否 | `recognitionRoute` → `.localOCR` 或 `.multimodal` |
| `.translation` | translation | 截图翻译 | 否 | `.translateText` |
| `.image` | image | 截图图片 | 否 | `.copyImage` |
| `.pin` | pin | 截图并钉住 | 否 | `.pin` |
| `.long` | long | 滚动长截图 / 从起点框到滚动区域底部 | 否 | 走长截图会话 |

**不存在**的模式（PRD 要求但代码未实现）：全屏、窗口模式、定时/延时截图。

### 4.2 默认快捷键（`HotKeyPreferences.swift:61-77`）

修饰键统一为 `cmdKey | optionKey | shiftKey`（**注意：无 controlKey**）：

| 快捷键 | Action | hotKeyID | 显示名 |
|---|---|---|---|
| `⌘⌥⇧1` | intelligentCapture | 2 | 极速识别内容 |
| `⌘⌥⇧2` | interactiveCapture | 1 | 通用截图 |
| `⌘⌥⇧3` | imageCapture | 3 | 截图图片 |
| `⌘⌥⇧4` | pinCapture | 4 | 截图并钉住 |
| `⌘⌥⇧5` | longCapture | 5 | 滚动长截图 |
| `⌘⌥⇧6` | translationCapture | 6 | 截图翻译 |

- Carbon signature `0x41495353`（"AISS"，`GlobalHotKeyManager.swift:18`）。
- 全部可重绑定；冲突时**自动回滚**到上一组成功配置并提示（`GlobalHotKeyManager.swift:104-123`）。
- 持久化为 JSON，key = `globalHotKey.<rawValue>`（`HotKeyPreferences.swift:146`）。

### 4.3 启动参数（`AppModel.swift:78-126`）

`--agent-bridge`、`--capture-fixed`、`--capture-intelligent|-image|-pin`、
`--ui-smoke-capture-toolbar`、`--ui-smoke-result-bar-success|-failure`、`--ui-smoke-editor`、`--ui-smoke-inline-editor`
（smoke 延迟 180/220 ms，启动截图延迟 500 ms）

---

## 5. 截图与框选子系统

### 5.1 坐标系（**三套，必须精确复现**）

| 空间 | 原点 | Y 方向 | 用途 |
|---|---|---|---|
| **(a) 全局 AppKit 屏空间** | 主屏左下角 | **向上** | `CaptureSelection.globalRect` / `.screenFrame`；`panel.convertToScreen(localRect)` |
| **(b) 覆盖层视图局部空间** | 面板左下角 = `screen.frame.minX/minY` | **向上**，未翻转 | 所有拖拽/缩放数学 |
| **(c) 像素空间** | 左上角 | **向下** | 截图输出 |

**关键转换（`ScreenCaptureService.swift:38-50`，FrozenDisplayCropper）**：
```
scaleX = imageWidth  / screenFrame.width        // 比例法 → 天然 DPI 正确
scaleY = imageHeight / screenFrame.height
localX   = clipped.minX - screenFrame.minX
localTop = screenFrame.maxY - clipped.maxY      // ← Y 轴翻转
pixels   = CGRect(localX*scaleX, localTop*scaleY, clipped.width*scaleX, clipped.height*scaleY).integral
```

**锁定测试**（`Tests/AIScreenshotAppTests/WindowSnapAndSelectionTests.swift:6-22`）：
选区 `(50, 25, 100×50)`，屏 `200×150`，scale 2 → 像素矩形 **`(100, 150, 200×100)`**。

> Windows 屏幕坐标统一左上原点、Y 向下，因此 §5.1 中的翻转转换在 Windows 上会**消失**——必须逐一验证删除翻转后接缝修正偏移与自动滚动命中点没有被反向。

### 5.2 ScreenCaptureKit 调用面（`ScreenCaptureService.swift`）

| API | 行号 |
|---|---|
| `SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)` | :105, :138 |
| `SCContentFilter(display:excludingWindows:)` | :114 |
| `SCContentFilter(desktopIndependentWindow:)` | :145 |
| `SCScreenshotManager.captureImage(contentFilter:configuration:)` | :127, :154 |
| `SCStreamConfiguration` — `.sourceRect/.width/.height/.scalesToFit/.showsCursor/.ignoreShadowsSingleWindow` | :167-174 |
| `CGImage.cropping(to:)` | :62 |

**注意：未使用 `SCStream` / `SCStreamOutput`** —— 全部是一次性 `SCScreenshotManager`，`SCStreamConfiguration` 仅作为参数结构体。

配置常量：`scalesToFit = false`（**禁止 SCK 重采样**）、`width/height = max(1, Int((size × pixelScale).rounded()))`、`showsCursor = false`、`ignoreShadowsSingleWindow = true`。

### 5.3 窗口枚举（`WindowSnapService.swift`，用于吸附）

`CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID)`
过滤条件（`:45-56`）：
- `kCGWindowLayer >= 0`
- `kCGWindowAlpha > 0.01`
- Quartz 尺寸 `>= 24×16`
- **owner PID 必须等于 frontmost app 的 PID**
- 与当前屏相交，且 Quartz→AppKit 翻转后仍 `>= 24×16`
- `zOrder = 枚举索引`（CGWindowList 返回前→后）

**命中规则**（`WindowSnapTargetSelector.target(at:from:)`，`:10-19`）：保留包含该点的矩形 → 取 **zOrder 最小者** → 并列时取**面积最小者**。

### 5.4 覆盖层行为规格

**面板配置**（`SelectionOverlayController.swift:121-135`）：
```
NSPanel(contentRect: screen.frame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
panel.level              = .screenSaver
panel.backgroundColor   = .clear ; isOpaque = false ; hasShadow = false
panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
panel.animationBehavior = .none ; hidesOnDeactivate = false
panel.acceptsMouseMovedEvents = true ; ignoresMouseEvents = false
panel.canBecomeKey = true ; canBecomeMain = false      // 抓输入但不激活 Ta
panel.orderFrontRegardless() ; makeKeyAndOrderFront(nil) ; makeFirstResponder(overlay)
```

**绘制顺序**（`SelectionOverlayView.swift:446-481`）：
1. 冻结屏图（全分辨率，`imageInterpolation = .none`）
2. 黑色 **0.42** 遮罩填充整个 bounds
3. 无选区时：`setBlendMode(.clear)` + fill **挖出透明洞**（露出真实桌面）
4. 选区边框 `controlAccentColor`，lineWidth **2**（有选区）/ **1.5**（悬停预览），`insetBy(dx:1, dy:1)`
5. 8 个 8×8 调整手柄（白色填充 + accent 描边，仅当 `showsActionToolbar`）
6. 尺寸读数 `"W × H"`，13pt medium 白字，位于 `y = max(26, rect.minY - 18)`，黑底 0.70
7. 中央提示 `"拖动选择区域 · 右键 / Esc 取消"`

**交互常量**：

| 常量 | 值 | 位置 |
|---|---|---|
| 最小选区 | **4×4 pt** | :283, :369 |
| 拖拽阈值 | **3 pt**（欧氏距离） | :329 |
| 缩放命中容差 | **10 pt** | :116 |
| 手柄尺寸 | **8×8**（半径 4），1.5pt 描边 | :689 |
| 内部移动光标矩形 | `selectionRect.insetBy(dx:4, dy:4)` | :245 |

**鼠标按下 < 3pt（未拖动）→ 提交吸附的窗口矩形**，而非退化的拖拽矩形（`:318-319`，测试 `:89-106`）；任何真实拖拽都取消吸附候选（`:333-335`）。

**光标状态机**（`cursor(at:)`，`:603-612`）：工具栏上 `.pointingHand` → 命中手柄 `handle.cursor` → 选区内 `.openHand` → 其他 `.crosshair`；拖拽中 move → `.closedHand`，resize → 手柄光标。

**键盘（`keyDown`，`:430-440`，即全部键盘面）**：
- `keyCode 53`（Esc）→ 取消
- `showsActionToolbar && (36 \|\| 76)`（Return / 小键盘 Enter）且在选区内 → 提交为 `.copyImage`
- 其他 → `super.keyDown`（无操作）

**没有**方向键微调、比例锁定、空格切换窗口模式（PRD 要求但未实现）。

**取消路径**：Esc、右键（`:407-409` + 面板级 `NSEvent.addLocalMonitorForEvents(matching: [.rightMouseDown])`）。

### 5.5 操作栏（`showActionToolbar`，`:504-572`）

**顺序**（左→右，测试 `WindowSnapAndSelectionTests.swift:148-153`）：
`beautify · multimodal · localOCR · translate ‖ edit · pin ‖ copyImage · save`
（`NSBox` 分隔线 1×22 插在 index 4 和 6 之前）

| 元素 | 值 |
|---|---|
| 容器 | `NSVisualEffectView`，`material = .hudWindow`，`blendingMode = .withinWindow`，`state = .active`，cornerRadius **12** |
| 布局 | 水平，`alignment = .centerY`，spacing **3**，edgeInsets **(6,7,6,7)** |
| 按钮 | **36×32 pt**，`bezelStyle = .recessed`，`imageScaling = .scaleProportionallyDown` |
| SF Symbol | `pointSize 13, weight .medium` |
| 图标 | `wand.and.stars`, `sparkles`, `text.viewfinder`, `character.book.closed`, `pencil.tip.crop.circle`, `pin.fill`, `doc.on.doc`, `square.and.arrow.down` |
| 面板高度 | `max(44, stack.fittingSize.height)` |
| 位置 | 优先**下方** `rect.minY - height - 10`（若 ≥ 8）；否则翻到**上方** `min(bounds.height - height - 8, rect.maxY + 10)`；x 右对齐 `rect.maxX`，夹到 `[8, bounds.width - width - 8]` |

**Tooltip**（`:629-674`）：12pt medium 白字，padding (10,6)，黑底 **0.90**，radius **7**，阴影 0.35/6/(0,-2)，`zPosition = 10_000`，距按钮 **7pt**。

### 5.6 延迟消失（`.edit` 路径）

若 action == `.edit`，调用 `overlay.prepareForDeferredDismissal()`（`:153-157`）而非 `finish()` —— 覆盖层面板**保留存活**在原位标注编辑器之后，遮罩持续存在且 Ta 从不激活。`CaptureCoordinator.process` 的 `defer` 仅对 `.edit` 调用 `selectionOverlay.dismiss()`（`:268-273`）。

### 5.7 结果反馈条（ResultBar）

`minimumWidth 320`, `maximumWidth 352`, `height 58`, `cornerRadius 18`（`ResultBarView.swift:36-41`）
面板 `level = .floating`，`[.canJoinAllSpaces, .fullScreenAuxiliary, .transient]`，`hidesOnDeactivate = false`
位置：鼠标所在屏水平居中，`y = visibleFrame.minY + 28`
自动隐藏 = `UserDefaults "resultBarDuration"`（默认 **3 秒**，设置滑块 1.5…8，步进 0.5）；`.processing` **永不自动隐藏**
颜色：success `rgb(65,143,61)`、warning `.orange`、failure = `TaPalette.cinnabar` `rgb(214,64,47)`

### 5.8 菜单栏

Popover **326×574**，`.transient`，`animates = true`；状态图标 **19×19**（`MenuBarController.swift:20,32,34`）

---

## 6. 标注子系统（含两套渲染器）

> ⚠️ **最关键发现：项目中有两套独立的标注子系统**，移植时必须区分。

| | A. 原生编辑器 | B. Agent 配方渲染器 |
|---|---|---|
| 位置 | `AnnotationEditorWindowController.swift`（1570 行）、`InlineAnnotationController.swift` | `TaAgentAnnotationRenderer.swift`、`TaAgentContracts/AnnotationRecipe.swift` |
| 数据模型 | `private enum AnnotationElement`（`:500-512`）— **非 Codable，从不持久化** | `AnnotationOperation` — **Codable、JSON、v1、有校验** |
| 光栅化器 | `AnnotationCanvasView.draw(_:)` → `NSBezierPath` + **CoreImage** | 纯 `CGContext` + **Accelerate/vImage** + **CoreText** |
| 入口 | 交互截图 → `.edit`；独立窗口；翻译预览 | `ta` CLI / Agent 桥 |

**移植顺序建议：先 B（序列化契约、有测试锁定），再 A（在其上做交互编辑器）。**

### 6.1 数据模型 A（`AnnotationEditorWindowController.swift:500-512`）

```swift
private enum AnnotationElement {
  case rectangle(CGRect, NSColor, CGFloat, Bool)        // rect, color, width, dashed
  case ellipse(CGRect, NSColor, CGFloat, Bool)
  case arrow(CGPoint, CGPoint, NSColor, CGFloat, Bool)  // start, end
  case pen([CGPoint], NSColor, CGFloat, Bool)
  case highlighter([CGPoint], NSColor, CGFloat)         // 无 dashed
  case text(String, CGPoint, NSColor, CGFloat)          // text, origin(基线底), color, size
  case number(Int, CGPoint, NSColor, CGFloat)           // 值, 中心, color, 直径
  case mosaic(CGRect)
  case mosaicStroke([CGPoint], CGFloat)                 // 点, 笔刷直径
  case blur(CGRect)
  case magnify(CGRect, CGFloat)                         // rect, factor
}
private struct AnnotationSnapshot { let elements: [AnnotationElement]; let cropRect: CGRect? }
```

13 个元素。坐标为**图像空间**（AppKit 左下原点，**未翻转**）。`cropRect` 单独存储（`:539`）。

### 6.2 数据模型 B（`TaAgentContracts/AnnotationRecipe.swift`）—— 跨平台契约

`AnnotationRecipe { version: Int, operations: [AnnotationOperation] }`（`:3`），`version` 必须为 **1**。

**原语**：

| 类型 | 字段 | 说明 |
|---|---|---|
| `AnnotationPoint`（`:70`） | `x, y: Double` | 配方空间 Y 向下，渲染器翻转 |
| `AnnotationRect`（`:82`） | `x, y, width, height: Double` | 有效 ⇒ 全有限**且** `width>0 && height>0`（`:95-97`） |
| `AnnotationColor`（`:100`） | `red, green, blue, alpha: Double` (0–1) | **序列化为十六进制串** `#RRGGBB` 或 `#RRGGBBAA`；仅当 `alpha >= 0.999` 时省略 alpha（`:145-151`） |

命名色（`:134-135`）：
- `.red = (1, 59/255, 48/255, 1)` = **`#FF3B30`**
- `.highlighter = (1, 214/255, 10/255, alpha 0.35)` = **`#FFD60A` @ 0.35**

**操作与默认值**（每个都有手写 `init(from:)`，缺失键补默认值）：

| 操作 | 结构体（行号） | 必需键 | 可选 → 默认 |
|---|---|---|---|
| `.crop` | `AnnotationCropOperation` `:155` | `rect` | 必须是**第 0 个**且最多一次（`:26-32`） |
| `.rectangle` | `AnnotationRectOperation` `:160` | `id`, `rect` | `color=.red`, `lineWidth=5`, `dashed=false` |
| `.ellipse` | 同上 `:160` | `id`, `rect` | 同上 |
| `.arrow` | `AnnotationArrowOperation` `:194` | `id`, `start`, `end` | 同上 |
| `.pen` | `AnnotationStrokeOperation` `:232` | `id`, `points` | 同上 |
| `.highlighter` | 同上 | `id`, `points` | `color=.highlighter`, `lineWidth 5→16`（重写 `:561`），强制 `dashed=false` |
| `.text` | `AnnotationTextOperation` `:266` | `id`, `origin`, `text` | `color=.red`, `fontSize=24` |
| `.number` | `AnnotationNumberOperation` `:300` | `id`, `center`, `number` | `color=.red`, `diameter=28` |
| `.mosaic` | `AnnotationMosaicOperation` `:339` | `id`, `mode` | `lineWidth=24`, `scale=14`；`mode ∈ {rect, brush}` |
| `.blur` | `AnnotationBlurOperation` `:377` | `id`, `rect` | `radius=12` |
| `.magnify` | `AnnotationMagnifyOperation` `:399` | `id`, `rect` | `factor=2`（必须 > 1） |
| `.eraser` | `AnnotationEraserOperation` `:421` | `targetIds`（**camelCase JSON key**） | 非空、唯一 |

**判别字段**：每个 JSON 对象带 `"type"`（`:540-543`），结构体字段**平铺进同一对象**（`:573-590`）。

**校验规则（`:456-536`，须逐条复现）**：crop 必须首个且仅一次；eraser 目标必须存在并从 ID 集合移除；`id` 重复报错（trim 后为空白也算空）；rect `w,h > 0`（**x/y 可为负**）；arrow `start != end` 且两点有限；pen/highlighter ≥1 个有限点；text 非空；mosaic rect 模式**只接受** rect、brush 模式**只接受**非空 points（`lineWidth > 0` 仅在 brush 模式校验）；magnify factor 有限且 > 1。

⚠️ **`highlighter` 解码期强制转换（必须逐字复现 —— 这是线上语义，不是渲染细节）**（`AnnotationOperation.swift:553-563`）：
- 缺 `color` → `.highlighter`（`#FFD60A59`）
- `lineWidth` **缺失或显式等于 `5`** → **16**（`5` 与默认值不可区分，显式的 `5` 也会被改写）
- `dashed` **强制为 `false`**，即使 JSON 里传了 `"dashed": true`

⚠️ **`eraser` 的 JSON 键是 `targetIds`（小写 `d`）**（从 Swift `targetIDs` 映射，`:425`）—— 最容易写错的一处。且 `targetIds` 必须非空、无空白项、**无重复**。

⚠️ **`elementCount` 不统计 `crop`**。且会话级校验比配方级更严：**已存在标注后，新配方里的第二个 `crop` 会被拒绝**（`cropMustPrecedeAnnotations`），即使该配方自身合法。裁剪边界检查：`0 <= x`、`0 <= y`、`x+width <= 图像宽`、`y+height <= 图像高`。

**会话状态**（`TaAgentAnnotationSession.swift:12`）：undo/redo 栈上限 **100**（`:57-58`），每次 apply/undo/redo **全量重渲染**。`apply` 在**任何变更前**完成校验 → **原子性**。

### 6.3 14 个工具（`AnnotationTool: Int`，`:301-335`，rawValue 0–13）

| # | 工具 | 显示名 | 内联图标 |
|---|---|---|---|
| 0 | `.select` | 选择/移动已有标注 | （无） |
| 1 | `.crop` | 裁剪 | `crop` |
| 2 | `.rectangle` | 矩形 | `rectangle`（默认） |
| 3 | `.ellipse` | 椭圆 | `circle` |
| 4 | `.arrow` | 箭头 | `arrow.up.right` |
| 5 | `.pen` | 画笔 | `pencil.tip` |
| 6 | `.highlighter` | 高亮笔 | `highlighter` |
| 7 | `.text` | 文字 | **"T" 文字按钮**（无符号，`:152`） |
| 8 | `.number` | 编号 | `1.circle` |
| 9 | `.mosaic` | 框选马赛克 | `square.grid.3x3.fill` |
| 10 | `.mosaicBrush` | 涂抹马赛克 | `paintbrush.pointed.fill` |
| 11 | `.blur` | 模糊 | `drop.degreesign` |
| 12 | `.eraser` | 橡皮 | `eraser` |
| 13 | `.magnify` | 局部放大 | `plus.magnifyingglass` |

**参数与默认值**（canvas 状态 `:540-545`）：

| 参数 | 默认 | 范围 | 控件 |
|---|---|---|---|
| `selectedColor` | `NSColor.systemRed` | 任意 | `NSColorWell` 36×24（独立）/ 32×26（内联） |
| `selectedWidth` | **5** | 滑块 1…22，步进 1 | `NSSlider` 104pt / 72pt |
| `selectedTextSize` | **24** | 离散阶梯 `[12,14,16,18,20,24,28,32,40,48,64,72]` | `NSPopUpButton` |
| `selectedOpacity` | **1** | 滑块 0.15…1 | `NSSlider` 84pt |
| `selectedDashed` | `false` | 复选框 | — |

**派生值**：
- `effectiveColor = selectedColor.withAlphaComponent(selectedOpacity)`（`:1183`）
- `mosaicBrushDiameter = max(12, selectedWidth * 4)`（`:1187`）
- 高亮笔存储色 alpha `= min(0.45, selectedOpacity)`；存储宽度 `= selectedWidth * 4`（`:801`）
- 编号气泡直径 `= max(22, selectedWidth * 6)`（`:732`）
- 放大倍率硬编码 **2**（`:816`）

**每工具最小手势**（`mouseUp` `:776`）：
rect/ellipse `w≥2 && h≥2`；mosaic/blur `w≥3 && h≥3`；magnify/crop `w≥12 && h≥12`；pen/highlighter `points.count > 1`；arrow 无最小；number 单击即生成并**自动递增** `max(已有)+1 ?? 1`（`:728-731`）；eraser 按下即删最上层命中元素（`:678-687`）。

### 6.4 绘制数学（**必须逐像素一致**）

**坐标映射**（`:1229-1245`）：
```swift
map(p) = (dst.minX + (p.x-src.minX)/src.width*dst.width,
          dst.minY + (p.y-src.minY)/src.height*dst.height)
strokeScale = dst.width / src.width
lineWidth = max(1, width * strokeScale)
```

| 元素 | 光栅化 | 精确数值 |
|---|---|---|
| `rectangle` | `roundedRect(xRadius:5, yRadius:5)` **仅描边，不填充** | 半径**固定 5pt** |
| `ellipse` | `ovalIn` 描边 | — |
| `arrow` | **7 点锥形多边形填充** | 见下 |
| `pen` | 折线，round cap/join | `lw = max(1, w*scale)` |
| `highlighter` | 折线，round cap/join，**不支持虚线** | `lw = max(3, w*scale)`，alpha ≤ 0.45 |
| `text` | `NSString.draw(with:options:)`，系统体 regular，原点在**左下角** | `font = max(11, size*scale)`；换行宽 `max(pt*2, dst.maxX - x)` |
| `number` | 填充圆 + 居中白色粗体数字 | `diameter = max(18, size*scale)`；数字字体 `diameter * 0.56` bold |
| `mosaic` | 全帧像素化图 `draw(in:from:operation:.copy)` | `CIPixellate`, `kCIInputScaleKey = 14` |
| `mosaicStroke` | clip 到**描边路径**（`replacePathWithStrokedPath()`）后画全帧像素化图 | `mappedWidth = max(4, width*scale)`；单点 → 椭圆 clip |
| `blur` | `blurImage.draw(in:from:operation:.copy)` | `CIGaussianBlur`, `kCIInputRadiusKey = 12` |
| `magnify` | 椭圆 clip → 画源图 → 白色圆环 | 源矩形 = 目标/2 居中并与图像边界求交；环 `lw = max(2, 3*strokeScale)` |

**两个滤镜都懒计算一次**，且从**原始** `sourceCGImage` 生成（`filteredImage` `:1559-1569`）—— 马赛克/模糊**从不采样已被标注的像素**。这是重要的行为保证。

**箭头几何**（`TaperedArrowGeometry.polygon` `:338-369`）：
```swift
if length <= 0.5 → 返回 7 个 start
direction = (dx/length, dy/length);  normal = (-direction.y, direction.x)
baseWidth     = max(2, width)
tailHalfWidth = max(1,    baseWidth * 0.28)
neckHalfWidth = max(tailHalfWidth + 0.8, baseWidth * 0.72)
headHalfWidth = max(7,    baseWidth * 2.2)
headLength    = min(max(12, baseWidth * 4), length * 0.55)
neck          = end - direction * headLength
```
返回 7 点顺序：`tail⁻, neck⁻, neck⁻head, end(尖端), neck⁺head, neck⁺, tail⁺`。**填充，从不描边**。
测试锁定（`AnnotationDrawingTests.swift:6-29`）：7 个点；`points[3] == end`；`width=6` 时 `headWidth > 20`。

**虚线模式**（`:1247-1251`）：`pattern = [max(4, width*2), max(3, width*1.4)]`，count 2，phase 0。
⚠️ **该模式基于已经过 strokeScale 缩放的 lineWidth 计算** —— 顺序错了虚线就不一致。

**文字盒度量**（`AnnotationTextMetrics.boundingSize` `:433`）：
```
w = max(font.pointSize * 1.5, ceil(测量宽) + 6)
h = max(font.pointSize * 1.35, ceil(测量高) + 6)
```
空字符串按 `"文字"` 测量。选项 `[.usesLineFragmentOrigin, .usesFontLeading]`。

**文字可用字号阶梯**：`[12,14,16,18,20,24,28,32,40,48,64,72]`，默认 **24**（`:415-416`）。

**选中装饰**（`drawSelection` `:1518-1542`）：bounds `insetBy(dx:-4, dy:-4)`；`controlAccentColor` 描边，虚线 `[5,4]`，`lineWidth 1.5`；4 个 8×8 圆手柄（白填充 + accent 描边）。

**裁剪覆盖线**：`controlAccentColor`，虚线 `[7,5]`，`lineWidth 2`。

**涂抹马赛克光标环**：`diameter = mosaicBrushDiameter * dst.width/src.width`；外圈黑 0.72 / `lw 2.5` / `insetBy(dx:-1,dy:-1)`；内圈白 0.92 / `lw 1`。

**画布外观**：背景黑 **0.82**（layer）；绘制时 bounds 填黑 **0.22**；图 `operation: .sourceOver`；边框 `roundedRect(xRadius:3)`，白 **0.18**，`lw 1`。

### 6.5 渲染管线

**屏幕渲染** `AnnotationCanvasView.draw(_:)`（`:613-670`），严格顺序：
1. 黑 0.22 填 bounds
2. `sourceImage.draw(in: imageFrame, from: visibleSource, operation: .sourceOver, fraction: 1)`
3. 逐元素：跳过正在编辑的文字；`draw(element)`；若选中再 `drawSelection`
4. `previewElement`（进行中的形状）**最后画**
5. 裁剪线、马赛克光标环
6. 画布边框

`imageFrame` = `bounds.insetBy(contentInset)` 等比**居中**适配；`contentInset` **24**（独立窗口）/ **0**（内联面板）。
`visibleImageRect` = `cropRect ?? CGRect(0,0,w,h)` —— **裁剪是视图变换，不是重采样**。
**每帧全量重绘**，无脏矩形优化（`needsDisplay = true` 每次鼠标移动）。

**导出渲染** `renderedImage()`（`:969-994`）：
```swift
visibleSource = visibleImageRect.integral
NSBitmapImageRep(8bpc, 4 samples, hasAlpha, .deviceRGB)
destination = CGRect(0, 0, visibleSource.width, visibleSource.height)
sourceImage.draw(in: destination, from: visibleSource, operation: .copy, fraction: 1)   // ← .copy 非 sourceOver
for element in elements { draw(element, in: destination, sourceRect: visibleSource) }
```
输出尺寸 = **裁剪区域尺寸**（`strokeScale = 1.0`）；底色用 **`.copy`**（不透明覆盖）；**不导出**选中手柄与预览。创建失败则原样返回 `sourceCGImage`。
**重渲染触发**：`renderedImage()` 顶部先 `finishTextEntry(commit: true)`，导出前必定提交待处理文字。

### 6.6 交互

**命中测试**：
- `hitTestElement(at:)`（`:1368-1375`）：**倒序**遍历（最上层优先），`boundingRect(el).insetBy(dx:-10, dy:-10).contains(point)`，10px 图像空间容差
- `resizeHandle(at:for:)`（`:1388-1394`）：`hypot(viewPt - handle.point) <= 9`；手柄顺序 `.bottomLeft, .bottomRight, .topLeft, .topRight`
- 鼠标按下优先级（`:672-742`）：图像边界守卫 → 提交文字 → **橡皮** → 已选元素手柄 → 命中选中 → 取消选中/新形状
- 双击已选元素 → 仅对 `.text` 重新打开编辑器（`:1030-1034`）

**移动**：`translated(el, dx: p - dragStart, dy: ...)`，纯偏移，**无边界夹取、无吸附**。

**缩放**（`AnnotationSelectionGeometry.uniformScale` `:397-412`）—— **等比**：
```
original = handle - anchor;  dragged = current - anchor
projected = (dragged·original) / (original·original)
scale = clamp(projected, 0.1, 20)      // 分母 <= 0.001 时返回 1
```
`scaled(el, around: anchor, by: scale)` 同时缩放**几何、线宽、字号**；mosaic/blur/magnify 只缩放矩形，magnify.factor 固定。

**旋转 90°**（`transformed` `:1488-1516`）：绕 bbox 中心，`(dx,dy) → (dy,-dx)`，rect 交换 w/h。
**缩小/放大**：`transformSelected(scale: 0.9 / 1.1)`。

**撤销/重做 —— 不用 NSUndoManager**（`:1215-1227`）：
```swift
func registerUndo() { undoSnapshots.append(currentSnapshot)
                      if undoSnapshots.count > 100 { undoSnapshots.removeFirst() }
                      redoSnapshots.removeAll() }
```
上限 **100**。NSUndoManager 仅作防御：`windowWillClose` 调 `undoManager?.removeAllActions()`，`finishTextEntry` 清除 NSTextView 注册以避免陈旧条目。`⌘Z` 在 `keyDown` 中拦截并显式路由到 `performUndoStep`。

**文字编辑**（`InlineAnnotationTextView: NSTextView`，`:474-498`）：`drawsBackground=false`, `isRichText=false`, `textContainerInset=(2,2)`, `lineFragmentPadding=0`, `widthTracksTextView=true`。**每次按键重新计算 frame**（`textDidChange` `:1083`），视图**底部锚定**，`activeTextOrigin` 每次重推为 `(x, y + height)`。提交时 trim 空白与换行，空串丢弃。

**`boundingRect` 每元素**（`:1406-1434`，命中测试依赖）：rect/ellipse/mosaic/blur/magnify → 原始 rect；arrow → `normalizedRect(start,end)`；pen/highlighter/mosaicStroke → 点集并集 `insetBy(∓w/2)`；text → 重新测量的盒；number → `(cx-s/2, cy-s/2, s, s)`。

### 6.7 导出与剪贴板（`ImageExportService.swift`）

| 路径 | 允许类型 | 编码器 | 质量 |
|---|---|---|---|
| `save(CGImage)`（`:20`） | `.png`, `.jpeg` | 按 URL 扩展名选择 | JPEG `.compressionFactor = 0.92`；PNG `[:]` |
| `save([CGImage])`（`:35`） | 仅 `.png` | PNG | — |
| 编辑器「复制」（`:961`） | — | PNG | — |
| `ClipboardService.copyImage` | — | PNG | — |

写入 `data.write(to: url, options: .atomic)`。
**命名**：单图 `"AI-Screenshot-\(timestamp).png"`，格式 `yyyy-MM-dd-HH-mm-ss`；长图 base `"AI-Long-Screenshot.png"`，分段 → `"\(base)-Part-\(suffix).png"`，`digits = max(2, String(count).count)`，`suffix = String(format: "%0*d", digits, index+1)`。

**剪贴板内容**：始终 **PNG bytes on `public.png`**（`clearContents()` 后 `setData(data, forType: .png)`）。**无 TIFF、无文件 URL**。纯文字识别结果用 `.string`。

**保存对话框**：`NSSavePanel`，标题 `"保存截图"` / `"保存分段长截图"`，`canCreateDirectories = true`；多图 message = `"图片过长，将在所选位置保存为 \(count) 个连续编号的 PNG 文件。"`。

### 6.8 快捷键表（标注内）

| 按键 | keyCode | 动作 |
|---|---|---|
| `[` | — | `adjustActiveToolSize(direction: -1)` |
| `]` | — | `adjustActiveToolSize(direction: +1)` |
| `Esc` | **53** | 取消 |
| `Return` | **36** | 提交 → 复制并完成 |
| `Enter`(小键盘) | **76** | 同上 |
| `⌘Z` | — | undo |
| `⇧⌘Z` | — | redo |
| `Delete`(fwd) | **51** | 删除选中元素 |
| `Backspace` | **117** | 删除选中元素 |

**滚轮**：当 tool ∈ {`.text, .mosaicBrush, .pen, .highlighter`} 时，滚轮用于调整尺寸（`:859-867`）；否则默认。
**光标**：`.select` → `.openHand`；`.text` → `.iBeam`；**其余全部** → `.crosshair`。

---

## 7. 钉图子系统

### 7.1 初始尺寸（`PinnedImageLayout.initialSize`，`:5-27`）

- 有 `preferredLogicalSize`（截图钉图：`selection.globalRect.size`）：limit = 屏 − 24，`scale = min(1, limit.w/base.w, limit.h/base.h)`
- 无（剪贴板钉图）：limit = `min(640, 屏−24) × min(480, 屏−24)`
- 原点夹到 `visibleFrame.minX/Y + 12 … maxX/Y − w/h − 12`

面板：`[.borderless, .nonactivatingPanel]`，`level = .floating`，`[.canJoinAllSpaces, .fullScreenAuxiliary]`，`hasShadow = true`，`isMovableByWindowBackground = false`。
`PinnedImagePanel.constrainFrameRect` **原样返回**（`:249-253`）—— 允许钉图伸到菜单栏下方。

### 7.2 外观与绘制

内容视图：layer `cornerRadius = 10`，`masksToBounds = true`，边框白 **0.28**，宽度 **1**（`:315-318`）。
绘制：黑填充 → 平移到中心 → `rotate(quarterTurns * π/2)` → 镜像 → `respectFlipped: true, hints: [.interpolation: .high]`，侧向时交换 w/h（`:357-384`）。

### 7.3 交互

**滚轮**（`:450-465`）：`⌘+wheel` → `alphaValue ±= delta * 0.015`，夹到 `[0.2, 1]`；普通滚轮 → `factor = delta >= 0 ? 1.06 : 0.94`，宽夹 `[120,1200]`，高夹 `[60,900]`，保持宽高比，绕窗口中心缩放。**无捏合手势处理**。
双击 → 关闭。**无 `NSEvent` 全局监听**。

### 7.4 右键菜单（`PinnedImageWindowController.swift:320-346`）

复制图片 `c` · 裁剪… · 重置裁剪 · — · 向左旋转 `[` · 向右旋转 `]` · 水平翻转 · 垂直翻转 · — · 灰度显示 · 反色显示 · 显示边框 · 窗口阴影 · 保持最前 · 恢复显示 `0` · — · 缩略图模式 · 鼠标穿透 · 将可见钉图编为一组 · 隐藏本组 · — · 关闭钉图 `w`

灰度与反色**互斥**（`:515`, `:522`）。
**缩略图** = `160 × min(220, max(72, 160*aspect))`（`:584`）。
**裁剪映射**视图矩形 → 图像矩形，**带 Y 翻转**：`y = current.minY + (1 - selected.maxY/bounds.height) * current.height`，最小 12pt 选区，最小 2px 结果（`:438-446`）。
关闭的钉图保留（最多 **3**）供 `restoreLastClosed`（`:177-178`）。

### 7.5 从剪贴板生成钉图（`pinFromPasteboard`，`:56-94`）

按顺序尝试：`.png` 数据 → `.tiff` 数据 → `NSURL` 文件列表（图片，否则用 `NSWorkspace.icon` 渲染文字卡片）→ `.html`（`NSAttributedString` HTML）→ `.string` 纯文本（渲染为 `520 × min(1200, max(120, h+48))` 卡片）。

---

## 8. 长截图子系统

> 这是算法最密集的部分，`AIScreenshotCore/LongCapture/` 共 1129 行，有完整测试锁定。
> **好消息：全部是纯整数算术 + CGImage，无 AppKit，可 1:1 直译。**

### 8.1 常量总表

| 常量 | 值 | 位置 |
|---|---|---|
| 自动滚动 tick | **300 ms**（手动 **420 ms**） | `ScrollingCaptureSessionController.swift:35-36, :264` |
| 最大稳定时间 | **1.20 s** | :37 |
| 需要的底部确认次数 | **3** | :38 |
| `recommendedScrollDistance` | `min(460, max(120, Int(globalRect.height * 0.42)))` | :269-271 |
| 重试步长 | `max(96, recommended / 2)` | :206-209 |
| `maximumAttemptsWithoutProgress` | **3** | :31 |
| 轮事件最大像素增量 | **32 px**，事件间隔 **14 ms** | `AccessibilityAutoScrollService.swift:40-41` |
| `isAtEnd` 阈值 | **0.9995** | :17 |
| `progressAdvanced` 容差 | **0.00005** | :190 |
| AX 父级遍历上限 | **32** 层；滚动条搜索深度 **2** | :93, :111 |
| HUD | **520×124**，偏移 ±12，边距 8，半径 16，描边白 0.18，padding 14 | :360-373 |
| 复查窗口 | **1040×720**（min 820×560）；缩放 0.2…1.5 默认 **0.55**；微调 **±1/±10**；低置信阈值 **0.58**；预览上限 **8,000 px**，导出上限 **30,000 px** | `ScrollingSeamReviewWindowController.swift:43-49` |
| 拼接采样 | **96×720**；最大输出 **120,000,000 px** | `ScrollingImageStitcher.swift:88-90` |

### 8.2 滚动匹配器（`VerticalScrollMatcher.swift`）

默认值：`maximumShiftFraction 0.82`, `ignoredTopFraction 0.10`, `ignoredBottomFraction 0.22`,
`ignoredSideFraction 0.28`, `maximumMeanAbsoluteDifference 18`, `minimumConfidence 0.56`, `sampleStride 2`

**流程**（`match` `:103-341`）：
1. 边距：`topMargin = max(h*0.10, fixedEdges.topRows)`，`bottomMargin = max(h*0.22, fixedEdges.bottomRows)`，`sideMargin = min(w*0.28, w/3)`；要求 `endX - startX >= 8` 且 `maximumShift = min(h - top - bottom - 12, h*0.82) >= 0`
2. 候选位移范围按 constraint 裁剪
3. **打分**（`difference` `:191-252`）：x 方向分 **5 个竖直带**；每带**只统计边缘像素**（类 Sobel 梯度 `|Δy|+|Δx| >= 10`）；每带需 `edgeSamples >= max(8, (endY-startY)/14)`；若 ≥2 带得分：排序，≥4 带时**去掉最差**，取**下中位数**；否则回退到全 x 范围普通平均绝对差
4. `bestDifference > 18` → 拒绝；再要求全宽 `unweightedDifference <= max(36, 45)`（`:275-281`）
5. `constraint != .any` 时：禁止方向得分 `>= 0.5` 更好 → 拒绝（`:297-300`）；**远歧义**（`|shift-best| >= max(8, h/18)` 且 `diff <= best+0.15`）→ 拒绝，除非 `allowsAmbiguousMatch`（`:306-311`）；`.downwardOnly` 时在 `+0.35` 差内**向上偏移最多 2 行**（多保留像素）（`:317-324`）
6. `quality = max(0, 1 - best/18)`；`separation = min(1, max(0, (second-best)/second*6))`；**`confidence = quality*0.8 + separation*0.2`**；低于 `minimumAcceptedConfidence ?? 0.56` 拒绝
7. `isDuplicate = |shift| <= 1 && meanAbsDiff <= 2.5`（`:71-73`）

**稳定边缘**（`stableEdges` `:346-391`）：逐行平均绝对差；`limit = min(h/3, h*0.25)`；行差 `<= maximumRowDifference`（默认 3.0）；**少于 3 行 → 0**（`:386-390`）。

> 边缘加权打分的意义：文字/代码边缘主导，空白与侧栏不主导。

### 8.3 拼接器（`ScrollingImageStitcher.swift`）

构造（`:86-96`）：`sampleWidth = max(32, 96)`, `sampleHeight = max(120, 720)`, `maximumOutputPixels = 120_000_000`

`makeSample`（`:493-515`）：灰度 1 字节，`CGColorSpaceCreateDeviceGray`，`alphaInfo .none`，`interpolationQuality = .medium`。
⚠️ **采样高度独立于宽度**（注释 `:495-497`）：从宽度推导高度会让 2× 宽帧过短而察觉不到小幅滚动。**Windows 移植时不要「修正」成单一宽高比采样。**

`append(_:constraint:preferredPixelShift:)`（`:159-198`）→ `.firstFrame / .appended(newPixelHeight:confidence:) / .duplicate / .rejected`
`appendTerminalFrame`（`:205`）：强制 `.downwardOnly`，`allowsAmbiguousMatch: true`，`minimumAcceptedConfidence: 0.05`，**confidence 上限 0.35**（`:241`）
`appendFallback(_:signedPixelShift:)`（`:310`）：视口确实移动但匹配失败时；`maximumSafeShift = max(2, Int(h*0.55))`，记录 `confidence: 0`（会进复查）
`adjustSegment(id:pixelDelta:)`（`:125-146`）：复查 UI 就地微调接缝，夹到 `[1, image.height]`，**不重新截图**

**高度记账**（`:267-297`）：追踪 `viewportOffset / minimumViewportOffset / maximumViewportOffset`；`accumulatedHeight = firstImage.height + max - min`（支持上下游走）

**条带组装** `makeStrips()`（`:386-433`），输出顺序：
1. 最早向上帧的行 `0..<safeTop`
2. `upward.reversed()`，每段 `sourceY = min(safeTop, max(0, h - newPixelHeight))`，`height = min(newPixelHeight, h - sourceY)`
3. 基准帧行 `baseTop..<(h - baseBottom)`（该方向为空时 baseTop/baseBottom 为 0）
4. `downward`，`height = min(newPixelHeight, h - safeBottom)`，`sourceY = max(0, h - safeBottom - height)`
5. 最晚向下帧的行 `(h-safeBottom)..<h`

`safeTop/safeBottom = min(conservativeStableHeight(...), firstImage.height / 3)`
`conservativeStableHeight` = **下中位数** `sorted[(count-1)/2]`（`:435-441`）—— 一次错误检测不能主导。

`render(strips:startY:height:)`（`:443-491`）：**sRGB**、8bpc premultiplied-last、`interpolationQuality = .high`；`destinationY = height - (upper - startY)`（左下翻转）

`makeImage()`：`width * accumulatedHeight > maximumOutputPixels` → 抛 `outputTooLarge`
`makeImages(maximumPixelHeight: 30_000)`（`:371`）：`partHeight = max(1, min(30000, maximumOutputPixels / width))`，顺序分块

### 8.4 视口运动检测（`ViewportMotionDetector.swift:26`）

采样 **72×360**；忽略上/下 0.12、两侧 0.06；`stationaryMeanDifference = 2.8`，`stationaryChangedFraction = 0.025`，`changedPixelDifference = 12`

⚠️ **`isStationary = meanDiff <= 2.8 && changedFraction <= 0.025`（AND，不是 OR）**（注释 `:107-110`）

与接缝匹配**刻意分离**：匹配不上的帧不等于到底了。

### 8.5 自动滚动驱动（`AccessibilityAutoScrollService.swift`）

1. `resolveTarget(for:)`：计算 Quartz 命中点 → `AXIsProcessTrusted()` 门槛 → `AXUIElementCopyElementAtPosition` 系统级命中测试 → `AXUIElementGetPid` 必须匹配 `selection.sourceApplicationProcessID` → 沿父级**最多 32 层**找 `kAXVerticalScrollBarAttribute`，或 `kAXScrollAreaRole` → `findVerticalScrollBar(depth: 2)`；接受第一个 `progress(of:)` 非空的候选。回退：`.eventFallback`
2. `progress(of:)`：从滚动条取 `kAXValueAttribute`（+min/max，默认 0/1）；`isAtEnd = normalizedValue >= 0.9995`
3. ⚠️ **滚动不由 AX 驱动**（注释 `:160-166`）：AX 值常被归一化到 0..1，加 300px 步长在 Chromium/Electron 会直接跳到最大值。改为 `CGEventSource(stateID: .hidSystemState)` + `CGEvent(scrollWheelEvent2Source:units: .pixel, ...)` 投递到 `.cghidEventTap`，位置锁定在命中点
4. 大手势**分块**：`maximumPixelDeltaPerEvent = 32`，`scrollEventIntervalMilliseconds = 14`。测试：`pixelDeltas(for: 130) == [-32,-32,-32,-32,-2]`
5. `scrollTargetLocation(for:)`：命中点 = 选区**垂直中心、水平中心**，经 `CGDisplayBounds` 转 Quartz 全局坐标

### 8.6 自动滚动尝试状态机（`:273-295`）

```
noPending:  .appended → .progress   否则 → .waiting
pending:    .appended → .progress
            AX 进度前进 → .progress
            motion.hasReference && !stationary → .progress   // "不确定"，不是"无移动"
            now < settleDeadline → .waiting
            否则 → .noMovement
```
`.progress` → `finishPendingWithProgress()`，重算步长，`nextAutoScrollDate = now + 0.30`
`.noMovement` → `finishPendingWithoutProgress()`（预算 −1）
`.duplicate/.rejected` 而 `hasPendingScroll` 且 AX 未到底 → `stitcher.appendFallback`（`:139-144`）
`shouldStop`（`:191`）：AX 说到底（或 `.eventFallback`）→ `stopAutoScrollAtBottom()`；否则**重新解析目标**、`begin()`、步长减半 `max(96, step/2)` 重试（`:203-211`）
底部信号后不再发新手势：`nextAutoScrollDate = .distantFuture`（`:216`）—— 完成完全由像素稳定性控制
`stopAutoScrollAtBottom()` 当 `bottomConfirmationCount >= 3`（`:175-185`）—— **3 个连续「静止且在底部」的帧**
`advanceBottomConfirmation`（`:297-313`）：除非 AX 到底 且 `.appended == false` 且 `motion.hasReference && motion.isStationary`，否则归零；否则 `min(3, count+1)`

### 8.7 接缝复查窗口（`ScrollingSeamReviewWindowController.swift`）

窗口 1040×720（min 820×560），标题 `检查长截图接缝`，居中，`NSHostingView`。
`HSplitView`：左 = 预览（`ScrollView`，`Image.interpolation(.high)`，缩放滑块 0.2…1.5 默认 0.55，`.shadow(.black.opacity(0.18), radius: 8)`，`.padding(24)`，背景 `windowBackgroundColor @ 0.65`，`minWidth: 500`，>1 段时有分段选择器）；右 = 接缝列表（`minWidth: 300, idealWidth: 340`），每行：8×8 圆点（`confidence < 0.58` 时红色）、`接缝 N · 向上|向下`、`%`、`新增 N px · 固定顶 N · 固定底 N`、4 个微调按钮 `[-10, -1, +1, +10]`
预览上限 `maximumPixelHeight: 8_000`；最终导出 `30_000`
按钮：丢弃 / 确认并保存。`windowWillClose` → 取消（除非 `isClosingProgrammatically`）

### 8.8 长截图 HUD（`ScrollingCaptureSessionController.swift:352-390`）

520×124 面板，`.screenSaver`，`[.canJoinAllSpaces, .fullScreenAuxiliary, .transient]`，`hasShadow = true`。
视觉：`.ultraThickMaterial` in `RoundedRectangle(cornerRadius: 16, .continuous)` + `stroke(.white.opacity(0.18))`，`padding(14)`。
按钮：自动滚动 / 暂停|继续 / 取消 / 完成并保存（`.borderedProminent`，`acceptedFrames == 0` 时禁用）
实时读数 `"\(acceptedFrames) 帧 · \(pixelHeight) px"`
位置：选区水平居中夹到 `visibleFrame ± 12`；垂直方向选區下方 12pt（若 ≥ `visibleFrame.minY + 8`），否则上方 12pt

---

## 9. OCR / AI 识图 / 翻译

### 9.1 OCR 引擎（`OCREnginePreference`，`CaptureModels.swift:40-62`）

| 引擎 | rawValue | 显示名 | 本地? | 用增强包? |
|---|---|---|---|---|
| Apple Vision | `appleVision` | Apple Vision（内置） | ✅ | ❌ |
| RapidOCR | `rapidOCR` | RapidOCR 增强包 | ✅ | ✅ |
| PaddleOCR | `paddleOCR` | PaddleOCR 增强包 | ✅ | ✅ |
| DeepSeek-OCR-2 | `deepSeekOCR2` | DeepSeek-OCR-2（最新） | ❌ 云端 | ❌ |

**默认**：`ocrEngine = appleVision`，`recognitionLanguages = "zh-Hans,en-US"`

### 9.2 Apple Vision 调用（`VisionOCRService.swift`）

macOS < 26 → `recognizeLegacy`（`:38-110`）：
```swift
let request = VNRecognizeTextRequest()
request.recognitionLevel = .accurate                  // ← .accurate，不是 .fast
request.usesLanguageCorrection = true
request.automaticallyDetectsLanguage = true
request.recognitionLanguages = languages              // 非空时
let barcodeRequest = VNDetectBarcodesRequest()
try handler.perform([request, barcodeRequest])
```
取 `observation.topCandidates(1).first` 的 `string` + `confidence` + `boundingBox`。

**排序**（`:65-71`）：垂直距离 `< 0.015` 时按 `minX` 升序；否则按 `midY` 降序（图像 Y 向上）。

**文本产出优先级**（`:74-99`）：
1. `OCRTextNormalizer().normalize(rawText, mergeWrappedLines:)`
2. 文字空但有条码 → 条码 payload 拼接
3. **首个表格 rows >= 2 → 用 `table.tsv` 覆盖**
4. 内容类型：有码无字 → `.qrCode`；有表格 → `.table`；否则 `ContentClassifier().classify(text)`

`confidence = lines 平均值`（无行时为 0）。`isLowConfidence = confidence < 0.72`（`CaptureModels.swift:227-229`）

macOS >= 26 → `recognizeDocument`（`:113-195`）：用 `RecognizeDocumentsRequest`，原生段落/表格/条码，失败自动回退 legacy（`VisionOCRFallbackRunner` `:198-212`）。

### 9.3 AI Provider（`VisionProviderKind`，`MultimodalProviderClient.swift:3-15`）

| kind | 显示名 | 端点拼接规则（`endpoint()` `:169-197`） | 鉴权头 |
|---|---|---|---|
| `openAICompatible` | OpenAI-compatible | **原样使用 Base URL**（见下） | `Authorization: Bearer` |
| `azureOpenAI` | Azure OpenAI | 非 `chat/completions` 结尾 → 追加 `openai/v1/chat/completions` | `api-key: <key>` |
| `anthropic` | Anthropic Claude | 非 `v1/messages` 结尾 → 追加 `v1/messages` | `x-api-key` + **`anthropic-version: 2023-06-01`** |
| `googleGemini` | Google Gemini | 无 `:generateContent` → 追加 `v1beta/models/<model>:generateContent` | `x-goog-api-key` |

> ⚠️ **`max_tokens` 按路径不同，必须分别复现**：
> - `OpenAICompatibleVisionClient`（`.openAICompatible` 的识图路径）：`max_tokens: **4096**`（`OpenAICompatibleVisionClient.swift:67`）
> - `MultimodalProviderClient` 的 Azure/Anthropic/Gemini 分支：`max_tokens: **2048**`（`:127`, `:206`）
> - `TranslationProviderClient`：默认 `max_tokens: **4096**`，`testConnection` 用 **512**（`:145`）
> - `DeepSeekOCR2Client`：`max_tokens: **4096**`

**超时**：识图 **60 s**；翻译与 DeepSeek-OCR-2 **120 s**。**全项目无任何 HTTP 重试**（`URLSession.data(for:)` 一次性）。

**端点校验**（`ProviderEndpointValidator`）：必须有 scheme + host；非 HTTPS 一律拒绝，**除非** host 是 `localhost` / `127.0.0.1` / `::1`。消息 `"Base URL 格式无效"` / `"远程服务必须使用 HTTPS"`。

**思考抑制**（`OpenAIChatResponseSupport.applyThinkingPreference`）：host 后缀命中 `deepseek.com` / `bigmodel.cn` / `zhipuai.cn` 时注入顶层 `"thinking": {"type":"disabled"}`。

**流式：全程未使用**。翻译客户端显式发送 `"stream": false`。

**OpenAI 兼容识图请求体**（`OpenAICompatibleVisionClient.swift:56-67`）：
```json
{"model":"…",
 "messages":[{"role":"user","content":[
   {"type":"text","text":"<prompt>"},
   {"type":"image_url","image_url":{"url":"data:<mime>;base64,<b64>"}}]}],
 "temperature":0,"max_tokens":4096}
```

Azure 复用同一结构但 `max_tokens: 2048`；Anthropic（`max_tokens 2048`，**无 temperature**）：
```json
{"model":"…","max_tokens":2048,
 "messages":[{"role":"user","content":[
   {"type":"image","source":{"type":"base64","media_type":"<mime>","data":"<b64>"}},
   {"type":"text","text":"<prompt>"}]}]}
```
Gemini：
```json
{"contents":[{"parts":[
   {"inlineData":{"mimeType":"<mime>","data":"<b64>"}},
   {"text":"<prompt>"}]}],
 "generationConfig":{"temperature":0,"maxOutputTokens":2048}}
```
⚠️ Anthropic 在**翻译**路径下会额外带 `temperature: 0`，且图片放在文字**之前**（与识图路径顺序相反）。

**纯文字翻译请求**（OpenAI/Azure，`:185-189`）：`content` 直接是**字符串**而非 parts 数组（源码注释说明 GLM 文字模型拒绝多模态数组）。带图时用 parts 数组，图片追加在文字**之后**。

**分段翻译的 ID 完整性校验**（`:97-102`）：`Set(解码出的 ids) == Set(输入 ids)` **且**数量相等，否则 `.invalidSegmentResponse`。JSON 模式（`jsonMode`）时追加 `"response_format":{"type":"json_object"}`。

**DeepSeek-OCR-2**（`DeepSeekOCR2Client.swift`）：
- `latestOfficialModel = "deepseek-ai/DeepSeek-OCR-2"`（`:44`）；`recommendedLocalBaseURL = "http://127.0.0.1:8000/v1"`（`:45`）
- 硬性拦截：host 为 `api.deepseek.com` → `.officialAPIUnsupported`（`:120-122`）
- **API Key 可选**：为空时**完全不发送** Authorization 头（本地 vLLM/SGLang）（`:69-72`）
- 返回的 `confidence` **硬编码为 0.85**，且**无 layout/barcodes**

**图像编码规格**：

| 调用点 | 格式 | 质量 | 最长边 | mimeType |
|---|---|---|---|---|
| 多模态识图 | JPEG | **0.88** | **2048** | `image/jpeg` |
| 连接测试（识图） | JPEG | 0.88 | 1024 | `image/jpeg` |
| 翻译视觉 / DeepSeek-OCR-2 / OCR 增强包 | PNG（ImageIO） | 无损 | **2560** | `image/png` |

**无任何字节大小上限**，只有像素尺寸上限。

**错误分类**（三套平行枚举，全部中文 `errorDescription`）：
- `VisionClientError`：`invalidEndpoint`, `missingModel`, `missingAPIKey`, `invalidResponse`, `emptyResponse`, `reasoningOnlyOutput(truncated:)`, `server(statusCode:message:)`
- `TranslationProviderError`：同上 + `emptyInput`, `invalidSegmentResponse`
- `DeepSeekOCR2ClientError`：同上 + `officialAPIUnsupported`
- `reasoningOnlyOutput` 触发条件：`choices[0].message.reasoning_content` 或 `.reasoning` 非空而 `content` 为空；`finish_reason == "length"` 时 `truncated = true`。**这是「关闭深度思考」功能，Windows 版必须同时复现检测逻辑与文案。**
- 错误体解析：`error.message` → `error.status` / `error.detail` → 顶层 `message` / `detail`

### 9.4 任务模板与 Prompt 原文（`MultimodalTaskTemplate`，`:20-66`）—— ⚠️ 必须逐字一致

| 模板 | rawValue | 显示名 | Prompt（原文） |
|---|---|---|---|
| 通用识图 | `general` | 通用识图 | `这是视觉理解任务，不是单纯的文字识别。请先描述图片中实际可见的主体、人物或动物、物体、场景、动作、颜色与构图；即使图片完全没有文字，也必须说明画面内容，不能只回答"没有文字"。如果图片包含文字，再准确整理文字，并保留原语言、段落、列表、代码和表格结构。直接输出结果，不要猜测看不清的内容。` |
| 精确取字 | `extractText` | 精确取字 | `逐字提取截图中的全部可见文字，保持阅读顺序、段落和换行；不要总结、翻译或补写。` |
| 译中 | `translateChinese` | 翻译成中文 | `识别截图中的内容并翻译成自然、准确的中文；代码、专有名词和数字保持原意。只输出译文。` |
| 译英 | `translateEnglish` | 翻译成英文 | `识别截图中的内容并翻译成自然、准确的英文；代码、专有名词和数字保持原意。只输出译文。` |
| 代码 | `explainCode` | 提取并解释代码 | `提取截图中的代码，先输出可复制的完整代码块，再用简洁中文说明语言、用途和明显问题；不要虚构被遮挡的代码。` |
| Markdown | `tableMarkdown` | 表格转 Markdown | `识别截图中的表格，严格按行列输出为 Markdown 表格。合并单元格用最接近的重复值表达，不要输出额外说明。` |
| CSV | `tableCSV` | 表格转 CSV | `识别截图中的表格并输出合法 CSV。正确转义逗号、引号和换行，只输出 CSV 内容。` |
| LaTeX | `formulaLaTeX` | 公式转 LaTeX | `识别截图中的数学公式并输出可复制的 LaTeX；多行公式使用 aligned 环境。只输出 LaTeX，不要解释或猜测模糊符号。` |

**通用识图重试机制**（`GeneralVisionResponsePolicy`，`MultimodalRecognitionService.swift:14-38`）：若 `general` 模板的回复（trim 标点 + 小写后）命中以下任一串，则用 recoveryPrompt 重试一次：
`图片中未包含任何文字内容`、`图片中未包含任何文字`、`图片中没有文字`、`图片中没有任何文字`、`未检测到文字`、`没有检测到文字`、`未发现文字`、`没有可识别的文字`、`no text in the image`、`the image contains no text`、`no readable text`

recoveryPrompt 原文：
```
请重新查看图片本身。这是视觉理解任务，不是 OCR。请直接描述图中可见的主体、动物或人物、物体、场景、动作、颜色和构图。即使没有任何文字，也必须描述画面；不要只回答"没有文字"。
```

**连接测试 Prompt**（`:97`）：`这是拓的连接测试图片。请只回复你在图片中看到的英文单词和数字，不要添加解释。`
测试图：520×180，底色 `rgb(250,246,238)`，文字 `"TA VISION 2026"` 位于 `(72,68)`，34pt bold，色 `rgb(214,64,47)`。

### 9.4b 翻译 Prompt 原文（`TranslationProviderClient.swift`）—— ⚠️ 必须逐字一致

> 注意：`sourceLanguage` / `targetLanguage` 是**中文自由文本**，会被**直接插值进英文 prompt**。
> 默认组合下首条 prompt 实际是 `Translate the following content from 自动检测 to 简体中文.`
> 语言预设：源 `[自动检测, 英文, 简体中文, 日文, 韩文]`；目标 `[简体中文, 英文, 繁体中文, 日文, 韩文, 西班牙文]`

**纯文字翻译**（`translateText` `:50-58`，`jsonMode = false`）：
```
Translate the following content from <sourceLanguage> to <targetLanguage>.
Preserve paragraphs, lists, code, numbers, names, and Markdown structure.
Do not summarize, explain, or add labels. Output only the translation.

<content>
<trimmed text>
</content>
```

**分段翻译**（`translateSegments` `:81-88`，`jsonMode = true`）：
```
Translate every JSON item's text from <sourceLanguage> to <targetLanguage>.
Return valid JSON only in this exact shape: {"translations":[{"id":0,"text":"..."}]}.
Preserve every id exactly once and keep short UI labels concise. Do not add explanations.

Input JSON:
<[TranslationSourceSegment] 的 JSON>
```

**图片翻译**（`translateImage` `:115-119`，PNG）：
```
Read all visible text in this screenshot and translate it from <sourceLanguage> to <targetLanguage>.
Preserve reading order, paragraphs, lists, code, numbers, and names.
Do not describe the image or explain. Output only the translated text.
```

**翻译连接测试**（`:143`）：`Reply with exactly: OK`，`maxTokens: 512`
（`testVisionModel` 复用 `translateImage`，但**硬编码** `sourceLanguage: "英文"`, `targetLanguage: "简体中文"`）

**分段翻译策略**（`ScreenshotTranslationService.translateLines` `:63-91`）：**一次请求携带全部文字行**，无分块、无并发、无大小限制。返回时**丢弃译文缺失或为空的文字行**，保留原始 bbox 与 confidence。

### 9.4c DeepSeek-OCR-2 Prompt 原文（`DeepSeekOCR2Client.swift:14-19`）

| mode | 显示名 | Prompt |
|---|---|---|
| `plainText` | 纯文本（推荐截图取字） | `<image>\nFree OCR.` |
| `documentMarkdown` | Markdown（保留文档结构） | `<image>\n<\|grounding\|>Convert the document to markdown.` |

### 9.5 翻译默认配置（`TranslationConfiguration.swift:5-9`）

```swift
defaultBaseURL     = "https://api.deepseek.com/chat/completions"
defaultTextModel   = "deepseek-v4-flash"
defaultVisionModel = "deepseek-v4-flash-vision-exp"
defaultSourceLanguage = "自动检测"
defaultTargetLanguage = "简体中文"
translationDefaultMode = "textOnly"
translationUsesVisionFallback = true
```

⚠️ 源/目标语言是**中文自由文本**，直接插值进英文 prompt（见 §9.4b）。

三种模式（`ScreenshotTranslationMode`）：`textOnly`（翻译文字并复制）/ `fullImage`（全文翻译图片）/ `bilingualImage`（双语翻译图片）
**模式路由**（`TranslationModeRouting`）：`CaptureQuickAction.translate` → 配置的默认值；`.translateText` → **强制** `.textOnly`；其他 → nil。所以**翻译快捷键永远只产出文字**。

### 9.6 图片文字替换渲染（`TranslatedImageRenderer.swift`）

三种模式（`ScreenshotTranslationMode`）：`.textOnly`（原样返回图）、`.fullImage`（`:29`）、`.bilingualImage`（`:61`）。空 `lines` → `missingTextBoxes`。

**fullImage 逐行**：
- `pixelRect` 来自归一化盒（`.integral`，`:148`）
- 内缩 `(-max(2, h*0.08), -max(2, h*0.12))`，与图像求交，`< 4×4` 跳过
- `background = averageColor(...)` —— 1×1 降采样绘制，`interpolationQuality = .low`，Y 翻转裁剪（`:157-185`）
- 填充 `background.withAlphaComponent(0.96)` 到 `roundedRect(xRadius: min(5, h*0.12))`
- 文字画在 `rect.insetBy(max(2, h*0.07), max(1, h*0.05))`，颜色 `contrastingTextColor`：亮度 `0.2126R + 0.7152G + 0.0722B > 0.54 ? 黑 : 白`（`:187-191`）
- `drawFittedText`（`:193`）：起始 `min(72, max(8, rect.height*0.72))`，每次减 1 直到适配 `rect ± 0.5` 或字号 ≤ 7。段落 `byWordWrapping`，左对齐

**bilingualPanel**（`:61`）—— 在**原图上方**追加面板：
- `horizontalPadding = max(28, width * 0.035)`，`contentWidth = width - 2*padding`
- `sourceFont = system medium, max(15, min(24, width/70))`
- `targetFont = system semibold, max(16, min(27, width/62))`
- `headerFont = system bold, max(18, min(30, width/55))`
- `rowSpacing = max(14, targetFont.pt * 0.7)`，`textSpacing = max(4, targetFont.pt * 0.22)`
- 面板底色 `NSColor(calibratedWhite: 0.97)`；接缝处 1px `NSColor.separatorColor`；行分隔线 `separatorColor @ 0.55`，1px，位于 `cursorY + rowSpacing*0.45`
- 标题 `"双语翻译 · \(targetLanguage)"`，画在 `panelHeight - headerFont.pt*1.65`
- 源文字 `secondaryLabelColor`，译文 `labelColor`；**自下而上**用递减游标排版

### 9.7 PaddleOCR 增强包协议（`docs/ocr-enhancement-pack-spec.md`）

**目录格式**：
```
PaddleOCR-Pack-arm64-1.1.0/
├─ manifest.json
├─ bin/paddleocr-adapter
├─ adapter/adapter.py
├─ runtime/           独立 Python 运行时 + PaddleOCR/ONNX Runtime
├─ models/            PP-OCRv5_mobile_det_onnx/ + PP-OCRv5_mobile_rec_onnx/
├─ SBOM-pip-freeze.txt
└─ THIRD-PARTY-INVENTORY.json
```

**manifest.json**：
```json
{
  "engine": "paddleOCR",
  "version": "1.1.0",
  "executable": "bin/paddleocr-adapter",
  "executableSHA256": "小写十六进制 SHA-256",
  "architecture": "arm64",
  "minimumMacOS": "14.0",
  "healthCheckArguments": ["--health-check"],
  "workerArguments": ["--worker"]
}
```
`engine` 只能为 `rapidOCR` 或 `paddleOCR`。安装器验证 OS、CPU 架构、压缩包 SHA-256、包内可执行文件 SHA-256，**拒绝绝对路径、`..`、反斜杠或越出包目录的路径**。新包必须健康检查通过才替换旧版。

**进程协议**：
```bash
paddleocr-adapter --input /abs/image.png --output json
```
stdout 必须是单个 JSON：`{"text":"识别结果","confidence":0.93}`；stderr 用于诊断；退出码必须 **0**。未安装/失败 → 停止增强路径，**自动回退 Apple Vision**。

**常驻 Worker（1.1.0+）**：
```bash
paddleocr-adapter --worker
# 启动首行: {"event":"ready","ok":true,"engine":"paddleOCR","packVersion":"1.1.0"}
# 请求: {"id":"唯一 ID","command":"recognize","input":"/abs/image.png","detectionSideLimit":2560}
# 响应: {"id":"唯一 ID","ok":true,"text":"识别结果","confidence":0.93}
# 错误: {"id":"…","ok":false,"error":"原因"}
```
主 App **串行**发送请求（一个 Worker 同时只执行一次识别）；`id` 必须原样返回；异常退出/超时/错误 id → 结束旧进程并**自动重启重试一次**。

**健康检查**：`paddleocr-adapter --health-check` → `{"ok":true,"engine":"paddleOCR","packVersion":"1.1.0","architecture":"arm64","offline":true}`

**固定版本与性能**：PaddleOCR 3.7.0 / PaddleX 3.7.2；ONNX Runtime CPU 1.29.0；PP-OCRv5 mobile det+rec；CPython 3.12.14 独立运行时（用户不需要 Python/Homebrew/Docker）。依赖锁定 `numpy==2.3.5`、`opencv-contrib-python==4.10.0.84`、`pillow==12.3.0`、`PyYAML==6.0.2`。宿主把最长边 > **2560** 的图等比缩小；适配器同时设 `text_det_limit_side_len = max(960, min(limit, 4096))`。空闲 **5 分钟**后退出并释放约 **700–800 MB** 峰值内存。

**安装目录**：`<ApplicationSupport>/AI Screenshot/OCRPacks/<engine.rawValue>/`
（⚠️ 注意是 **`AI Screenshot`**，而 socket 路径用的是 **`Ta`** —— 两个不同的目录名，别混）

**安装管线**（`OptionalOCRPackManager.installRecommended`，`:188-237`），进度阶段（中文标签）：`正在查找增强包…` / `正在下载…` / `正在校验…` / `正在解压…` / `正在启动检查…` / `正在安装…`
1. 解析 catalog 包；校验架构 + `minimumMacOS`
2. 流式下载（64 KB 块，进度来自 `expectedContentLength`）到 UUID 临时目录 `pack.zip`
3. 流式 SHA-256（1 MiB 块，进度 ×0.9）比对 `archiveSHA256.lowercased()`
4. **ZIP slip 防护**：`/usr/bin/zipinfo -1` 列条目，拒绝含前导 `/`、含 `\`、或含 `..` 组件的条目
5. `/usr/bin/ditto -x -k` 解压（300 s 超时）
6. `locatePackRoot`：取 `extracted/manifest.json`，否则要求**恰好一个**子目录含 `manifest.json`
7. 健康检查 + 版本必须与 catalog 一致
8. 暂存安装：拷到 `.<engine>-staged-<uuid>` 同级目录 → 重新校验 → `replaceItemAt` / `moveItem` 就位 → 再校验可执行文件

**可执行文件校验**（`:498-512`）：`resolvingSymlinksInPath()` 解析 root 与 executable，要求 `executable.path.hasPrefix(root.path + "/")` 且 `isExecutableFile`；若有 `executableSHA256` 则必须匹配。

**健康检查**（`:526-548`）：默认参数 `["--health-check"]`，**120 s** 超时；要求 `ok == true` **且** `engine == manifest.engine.rawValue`；若返回 `architecture` 必须与宿主一致。

**Worker 读取循环**：`poll(fd, POLLIN|POLLHUP|POLLERR, min(100ms))` + `read(…, 64KB)`；**每次读取 30 s 超时**；**恰好 2 次尝试**（任何失败 → `stopSync()` 后重试一次）。
**进程终止**：`SIGTERM` → 每 0.02 s 轮询最多 1 s → `SIGKILL` → `waitUntilExit`。

**catalog 解析顺序**（`:397-411`）：`UserDefaults["ocrPackCatalogURL"]`（**必须 https**）→ `<app>/../ocr-packs/catalog.json` → `<app>/Contents/Resources/OCRPacks/catalog.json` → `.packageUnavailable`。包选择：按 engine + 架构过滤，按数字版本降序取第一个。相对 `downloadURL` 相对 catalog 所在目录解析，且必须 `file://` 或 `https`。

⚠️ **`.rapidOCR` 是死引擎**：枚举与增强包管线都在，但**没有构建脚本、没有 catalog、没有安装按钮**（设置页下载按钮仅在 `selectedEngine == .paddleOCR` 时出现）。Windows 版要么去掉该分支，要么补全 RapidOCR 包。

⚠️ **增强包识别路径不返回 layout/barcodes** —— 所以依赖 `document.blocks` 的图像翻译模式从增强包拿不到数据。

### 9.9 Provider 预设（`ModelProviderPreset`，`ModelSettingsView.swift:876-1024`）

`guidedCases` 顺序（`:881-891`）：`zhipuAPI, zhipuCodingPlan, deepSeek, openAI, gemini, anthropic, openRouter, azure, custom`

| preset | 标题 | providerKind | baseURL | 视觉模型 | 文字模型 | 支持视觉? | 推荐? |
|---|---|---|---|---|---|---|---|
| `zhipuAPI` | 智谱 API | openAICompatible | `https://open.bigmodel.cn/api/paas/v4` | `glm-5v-turbo` | `glm-5.2` | ✅ | ✅ |
| `zhipuCodingPlan` | 智谱 Coding Plan | openAICompatible | `https://open.bigmodel.cn/api/coding/paas/v4` | — | `glm-5.2` | ❌ | ✅ |
| `deepSeek` | DeepSeek | openAICompatible | `https://api.deepseek.com` | `deepseek-v4-flash-vision-exp` | `deepseek-v4-flash` | ✅ | ✅ |
| `openAI` | OpenAI | openAICompatible | `https://api.openai.com/v1` | — | — | ✅ | ❌ |
| `gemini` | Gemini | googleGemini | `https://generativelanguage.googleapis.com` | — | — | ✅ | ❌ |
| `anthropic` | Claude | anthropic | `https://api.anthropic.com` | — | — | ✅ | ❌ |
| `openRouter` | OpenRouter | openAICompatible | `https://openrouter.ai/api/v1` | — | — | ✅ | ❌ |
| `azure` | Azure | azureOpenAI | `""`（需自填） | — | — | ✅ | ❌ |
| `custom` | 自定义 | openAICompatible | `""` | — | — | ✅ | ❌ |

`requiresCustomEndpoint = azure || custom`（自动展开高级设置）
`suggestedConfigurationName` = `"自定义模型"` 或 `"<标题> 日常"`
Coding Plan 的特殊提示：`Coding Plan 官方直连不支持图片输入；这套配置不会出现在 AI 识图或截图翻译列表中。`

**AI 模型设置是 3 步引导向导**：`.provider`（选择服务商）→ `.credentials`（填 API Key + 推荐模型）→ `.complete`（测试并保存）。左栏宽 **154**，步骤圆点 24px、连接线 52px。
保存门禁（`persistDraft`）：校验通过 **且**（Key 非空 **或** 已有存储的 Key），否则 `"请粘贴 API Key。"`
`saveAndTest`：**先持久化再测试**（测试失败配置仍保留）→ 依次 `testTextModel` → 设置 `visionVerifiedAt` → 若支持视觉则 `testConnection`
成功消息：`两种模型连接成功。文字：<前24字符> · 视觉：<前24字符>`
失败消息：`配置已保存，但连接测试失败：<err>` 并退回第 2 步

### 9.10 设置窗口结构（`SettingsView.swift`）

窗口 **780×600**，**7 个标签页**（`:85-119`）：

| tab | 标题 | 图标 |
|---|---|---|
| `.permissions` | 权限 | `lock.shield` |
| `.general` | 常规 | `gearshape` |
| `.hotKeys` | 快捷键 | `command` |
| `.recognition` | 识别 | `text.viewfinder` |
| `.translation` | 翻译 | `character.book.closed` |
| `.models` | AI 模型 | `sparkles` |
| `.agent` | Agent | `cpu` |

跨页导航通知：`Notification.Name("Ta.OpenAIModelSettings")` → 切到 `.models`

**欢迎窗口**（`WelcomeView.swift`）：**900×620**，最小 **820×590**，padding 32，3 列 × 6 个快捷操作。⚠️ **欢迎窗口没有 AI 模型配置步骤**，只链接到设置。

### 9.11 智能路由确认流程（`CaptureCoordinator.swift:520-568, 920-937`）

当 `recognitionRoute == .smart` **且** `isLowConfidence`（< 0.72）**且**多模态已配置 → 弹出 `NSAlert`：
- 标题 `本地 OCR 置信度较低（N%）`
- 正文 `是否把本次主动框选的图片上传到已配置的视觉模型进行增强？不确认就不会上传。`
- 按钮 `上传并增强` / `使用本地结果`

确认后按内容类型选任务模板：`.code → explainCode`、`.table → tableMarkdown`、`.formula → formulaLaTeX`、`plainText|qrCode|image → extractText`

### 9.12 OCR 文本后处理（必须逐条复现）

**`OCRTextNormalizer`**：CRLF/CR → LF；每行去尾部 `[ \t]+`；连续空行折叠为一个；去尾部空行。
**断行合并**（仅 `mergeWrappedLines == true`，默认 **false**）：上一行以 `。！？.:：` 结尾、或以 `; { }` 结尾、或下一行以 `• -` 开头 → **不合并**。连接符 = **仅当**上一行末字符与下一行首字符**都是 ASCII**（<128）时用 `" "`，否则用 `""`。

**`OCRDocumentLayoutAnalyzer`**：
- 分块：`verticalGap > typicalHeight * 2.4`（`typicalHeight = max(0.01, 平均高)`）**或** `|ΔminX| > 0.28`
- 分行：midY 差 ≤ `max(0.012, max(h1,h2) * 0.65)`
- 表格：单格行按正则 `\s{2,}` 拆分；需 ≥2 个多格行；取众数列数，保留列数差 ≤1 的行；仍需 ≥2 行

**`ContentClassifier`**（**严格按此顺序**，`:6-27`）：
1. 空 → `.image`
2. 单行匹配 `^(https?://|mailto:|tel:|otpauth://|WIFI:)`（忽略大小写）→ `.qrCode`
3. `.table` —— ≥2 行 **且**（制表符数量一致 ≥1）**或**（`|` 数量一致 ≥2）**或** ≥`max(2, rows-1)` 行匹配 `\S\s{2,}\S`
4. `.formula` —— 含 `\(frac|sqrt|sum|int|begin|alpha|beta|theta)\b`，或含 `∫∑√≈≠≤≥∞∂` 之一，或含上下标数字
5. `.code` —— **5 个正则中命中 ≥2 个**
6. 否则 `.plainText`

`OCRResult.isLowConfidence = confidence < 0.72`
⚠️ **翻译路径另有 0.55 阈值**（`shouldUseVision = usesVisionFallback && (text.isEmpty || confidence < 0.55)`）—— 与 0.72 是**两个不同阈值**，别混淆。

**翻译路径的 OCR 特殊性**：始终直接用 `VisionOCRService`，**忽略 `ocrEngine` 设置**，且 `languages: []`（纯自动检测）、`mergeWrappedLines: false`（因为需要 bbox 做图像渲染）。视觉回退**仅在 `textOnly` 模式可达**，图像模式直接失败。

---

## 10. 配置存储与数据迁移

### 10.1 身份常量（`PersistentConfigurationIdentity.swift`）—— ⚠️ 升级兼容契约

源码注释明确：**Changing them requires an explicit migration from the previous values.**

```swift
static let bundleIdentifier = "com.kangarooking.AIScreenshot"
static let keychainService = "com.kangarooking.AIScreenshot.providers"
static let multimodalProviderAccount = "openai-compatible-default"
static let translationProviderAccount = "deepseek-translation-default"
static let providerProfileAccountPrefix = "ai-provider-profile-"
static let deepSeekOCRAccount = "deepseek-ocr-2"
```

### 10.2 Keychain（`KeychainSecretStore.swift`）

`kSecClassGenericPassword` + `kSecAttrService = keychainService` + `kSecAttrAccount`
可访问性：`kSecAttrAccessibleWhenUnlockedThisDeviceOnly`（**本机专用，不进 iCloud 钥匙串**）
每 profile 的 account = `"ai-provider-profile-<uuid 小写>"`（`AIProviderProfileStore.swift:126-128`）

### 10.3 Provider 配置（`AIProviderProfileStore.swift`）

状态存 `UserDefaults` key **`aiProviderProfileStateV1`**，JSON 编码：
```swift
struct AIProviderProfileState {            // schemaVersion = 1
  var schemaVersion: Int
  var profiles: [AIProviderProfile]
  var activeProfileID: UUID?
  var translationProfileID: UUID?
}
struct AIProviderProfile {
  var id: UUID; var name: String
  var providerKind: VisionProviderKind
  var baseURL: String
  var visionModel: String; var textModel: String
  var visionVerifiedAt: Date?
}
```
校验消息（`AIProviderProfile.validationMessage` `:43-57`，中文原文）：
- 名称为空 → `请给这套配置起一个名称。`
- Base URL 为空 → `请先选择服务商，或填写服务地址。`
- 端点非法 → `ProviderEndpointValidator` 的消息
- 需视觉模型但为空 → `请填写一个支持图片输入的视觉模型。`
- 需文字模型但为空 → `请填写文字模型，截图翻译需要同时使用文字与视觉模型。`

翻译可用性（`translationEligibility` `:130-134`）：需 textModel **且**有 API Key（否则 `请先为这套配置保存 API Key。`）

`normalize`（`:140-151`）：active 指向无效时回退到第一个有效 profile；translation 指向不存在时置 nil。

**遗留迁移**（`migrateLegacyConfiguration` `:153-212`）：读 `providerBaseURL/providerVisionModel/providerTextModel/providerKind` + Keychain `openai-compatible-default` / `deepseek-translation-default`，构造名为「原有 AI 模型」「原有翻译模型」的 profile，按 `normalizedURL` 去重合并。

### 10.5 全部磁盘路径（⚠️ 注意 `Ta` 与 `AI Screenshot` 两个不同目录名并存）

| 用途 | macOS 路径 | Windows 建议 |
|---|---|---|
| Agent Bridge socket | `~/Library/Application Support/`**`Ta`**`/agent-v1.sock` | **Named Pipe** `\\.\pipe\Ta\agent-v1` |
| OCR 增强包 | `~/Library/Application Support/`**`AI Screenshot**`/OCRPacks/<engine>/` | `%APPDATA%\AI Screenshot\OCRPacks\<engine>\` |
| 审计日志 | `~/Library/Application Support/Ta/Agent/audit-v1.json` | `%LOCALAPPDATA%\Ta\Agent\audit-v1.json` |
| Agent 工件 | `~/Library/Caches/Ta/AgentRuns/<requestID>/{capture.png, transformed.png}` | `%LOCALAPPDATA%\Ta\AgentRuns\` |
| CLI 安装 | `~/.local/bin/ta`（mode 755） | `%LOCALAPPDATA%\Programs\Ta\bin` + 加入 PATH |
| Skill 安装 | `~/.codex/skills/ta`、`~/.agents/skills/ta`、`~/.claude/skills/ta`（**仅当 `~/.claude` 存在**） | `%USERPROFILE%\.codex\skills\ta` 等 |
| 环境变量 | `TA_APP_PATH`（覆盖 App 路径） | 保留同名 |
| 保留期 | **24 h**（`defaultRetention`） | 同 |

⚠️ **工件保存后设置的是「目录」的 mtime，不是文件的**（`:45-46`）—— 过期清理读的是子目录的 `.contentModificationDate`。NTFS 上该技巧仍有效。
⚠️ **`cleanupExpired()` 在生产环境无调用方**，只有测试引用。实际过期只是元数据建议值，由客户端（dsh-ta）与 Skill 契约强制。

### 10.6 全部 UserDefaults Key

| Key | 默认 | 用途 |
|---|---|---|
| `postCaptureAction` | `choose` | 截图后行为 |
| `lastPostCaptureQuickAction` | — | 「记住上次动作」 |
| `recognitionRoute` | `localOCR` | 识别路由 |
| `ocrEngine` | `appleVision` | OCR 引擎 |
| `recognitionLanguages` | `zh-Hans,en-US` | 识别语言 |
| `mergeWrappedLines` | **false** | 合并换行 |
| `resultBarDuration` | `3`（滑块 1.5…8 步进 0.5） | 结果条时长 |
| `multimodalTaskTemplate` | `general` | 多模态任务 |
| `translationSourceLanguage` | `自动检测` | 翻译源语言 |
| `translationTargetLanguage` | `简体中文` | 翻译目标语言 |
| `translationDefaultMode` | `textOnly` | 翻译模式 |
| `translationUsesVisionFallback` | **true** | 视觉回退 |
| `deepSeekOCRBaseURL` | `""` | DeepSeek-OCR-2 地址 |
| `deepSeekOCRModel` | `deepseek-ai/DeepSeek-OCR-2` | DeepSeek-OCR-2 模型 |
| `deepSeekOCRPromptMode` | `plainText` | DeepSeek-OCR-2 输出格式 |
| `ocrPackCatalogURL` | —（**设置时必须 https**） | 增强包目录 |
| `aiProviderProfileStateV1` | — | Provider 状态（JSON blob） |
| `globalHotKey.<rawValue>` | 见 §4.2 | 快捷键 |
| `agentAccessEnabled` | **true** | Agent 开关 |
| `agentAutomaticCaptureAllowed` | **true** | Agent 自动截图 |
| `agentCloudPolicy` | `auto` | Agent 云策略 |
| `agentPrivacyDenylist` | `""`（按 `,;\n` 分隔） | 隐私 App 黑名单 |
| `agentAllowCaptureTa` | **true** | 允许截 Ta 自身 |
| `providerBaseURL` / `providerVisionModel` / `providerTextModel` / `providerKind` | — | 遗留，仅迁移读取 |
| `translationBaseURL` / `translationTextModel` / `translationVisionModel` | — | 遗留，仅迁移读取 |

⚠️ `loadState()` 在 `normalize` 结果与已存状态不同时会**回写存储** —— 这种自愈写入必须复现。
⚠️ 遗留键在迁移后**从不删除**。

---

## 11. Ta Agent 桥与 CLI

### 11.1 传输层（`AgentFraming.swift` + `TaBridgeClient.swift`）

**Unix domain socket**，路径：
```
~/Library/Application Support/Ta/agent-v1.sock
```
（`TaAppLauncher.swift:5-15`，`defaultSocketURL`）

**帧格式**（`AgentFrameCodec`，`AgentFraming.swift:10-50`）—— ✅ 平台无关，可原样复用：
```
[4 字节大端 UInt32 长度][JSON payload]
maximumPayloadBytes = 16 * 1024 * 1024   // 16 MiB
```
错误：`frameTooLarge` / `truncatedHeader` / `truncatedPayload` / `unexpectedTrailingBytes`

**JSON 编码**（`AgentJSONCoding`，`AgentEnvelope.swift:123-136`）：
`dateEncodingStrategy = .iso8601`；`outputFormatting = [.sortedKeys, .withoutEscapingSlashes]`

**协议版本**：`AgentProtocol.currentVersion = 1`（`AgentEnvelope.swift:4`）

**请求封装**（`AgentRequestEnvelope`，`:17-51`）：
```json
{"protocolVersion":1,"requestId":"<ID>","method":"<method>","params":{…},"client":{"name":"…","version":"…"}}
```
⚠️ `requestId` 用 **camelCase**（`CodingKeys` 映射自 `requestID`）

**响应封装**（`AgentResponseEnvelope`，`:63-121`）：
```json
{"protocolVersion":1,"requestId":"<ID>","ok":true,"data":{…},
 "artifacts":[…],"meta":{"durationMs":0,"cloudUploaded":false},"error":null}
```
`error` = `AgentErrorPayload { code, message, hint, retryable }`

**客户端行为**（`TaBridgeClient`）：
- 默认 timeout **5 秒**（`setsockopt SO_RCVTIMEO/SO_SNDTIMEO`）
- 连接**重试一次**（间隔 50ms），仅对 `ENOENT` / `ECONNREFUSED`
- 取消时 `shutdown(descriptor, SHUT_RDWR)`
- App 通过 `--agent-bridge` 启动，`activates = false`（`TaAppLauncher.swift:46-54`）

> **Windows 移植决策点**：Unix socket → **Named Pipe**。帧格式与 JSON 保持不变，CLI 与 Skill 无需改动即可跨平台工作。
> 具体映射：`CreateNamedPipeW(L"\\\\.\\pipe\\Ta\\agent-v1", PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED, PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT, …)`。
> - 管道名保留 `v1` 版本标记；`--socket` 应改为可配置**管道名**而非路径
> - 认证：`getpeereid` → `GetNamedPipeClientProcessId` 比对自身 PID **并/或** 用 SDDL 限制仅当前用户（如 `D:P(A;;FA;;;<user SID>)`）**并/或** 比对客户端 token 的 `TokenUser` 与完整性级别
> - 权限：`chmod 0700/0600` → `SetNamedSecurityInfoW` + SDDL；工件目录同样加 ACL
> - `lstat`/S_IFSOCK 陈旧 socket 清理 → `WaitNamedPipeW`（`ERROR_FILE_NOT_FOUND` 表示无服务端），`pathOccupied` 变为不可达（保留错误码以维持 parity）
> - `SO_RCVTIMEO/SO_SNDTIMEO` → `SetNamedPipeHandleState` 或显式超时 + `CancelIoEx`
> - `shutdown(fd, SHUT_RDWR)` 取消挂起读取 → `CancelIoEx` / `CloseHandle`
> - `SOMAXCONN` → 管道实例数（建议 ≥ 255）；`MSG_NOSIGNAL` / `EINTR` 处理可直接删除
> - 错误码映射：`ENOENT`/`ECONNREFUSED` → `ERROR_FILE_NOT_FOUND` / `ERROR_PIPE_NOT_CONNECTED` / `WSAECONNREFUSED`，汇入同一条「桥不可用 → 启动 App + 重试」分支
>
> 并发模型：服务端 accept 串行队列 + 连接并发队列；`TaAgentRequestRouter` 与 `TaAgentCapabilityService` 均为 Swift **actor**，因此「last image」状态实际是全局串行的 —— Windows 版必须保持等价串行（`SemaphoreSlim(1,1)` 或单线程调度器）。测试验证 8 个并发独立请求。

### 11.2b 审计日志细节（`TaAgentAuditLog.swift`）

文件：`<AppSupport>/Ta/Agent/audit-v1.json`（Windows: `%LOCALAPPDATA%\Ta\Agent\audit-v1.json`）
权限：目录 **0700**、文件 **0600**，**每次写入都重新设置**

**⚠️ requestID 从不原文存储**：存的是 `"sha256:" + SHA256(requestID) 前 8 字节的十六进制`（`:90-96`）
`clientName` / `clientVersion`：剥离控制字符并截断到 80 字符
条目字段：`requestID, occurredAt, clientName, clientVersion, method, succeeded, durationMs, cloudUploaded, errorCode?`
上限 **100** 条，FIFO 裁剪；`recent(limit: 20)` 返回最新 N 条**倒序**
`system.handshake` 因 router 短路而**不被审计**
**`params` 与 `data` 的任何内容都不会写入**

### 11.2c CLI 退出码（`CLIOutput.swift:4-25`）

| 码 | 常量 | 触发 |
|---|---|---|
| **0** | success | `response.ok == true` |
| **2** | usage | 任何解析错误、help |
| **3** | appNotInstalled | `TaAppLauncherError` 或 `TA_APP_NOT_INSTALLED` |
| **4** | bridgeUnavailable | `TaBridgeClientError` 或 `BRIDGE_UNAVAILABLE` |
| **5** | permissionDenied | `SCREEN_PERMISSION_REQUIRED`、`ACCESSIBILITY_PERMISSION_REQUIRED` |
| **6** | privacyBlocked | `TARGET_BLOCKED_BY_PRIVACY_POLICY`、`CLOUD_UPLOAD_NOT_ALLOWED` |
| **7** | requestFailed | 其他任何错误码（含未分类异常） |
| **130** | cancelled | `CancellationError`，stderr 输出 `CANCELLED: 请求已取消。` |

**stdout/stderr 约定**：成功 → stdout，失败 → stderr；输出始终以恰好一个 `\n` 结尾。SIGINT 由 `DispatchSource` 捕获并取消执行 Task。

**`--json` 模式**输出整个响应信封（字节级一致）。**human 模式**：有 error → `"<CODE>: <message>"` + `"提示：<hint>"`；`data.text` 存在 → 只打印原文；否则逐 sorted key 打印 `"<key>: <value>"`。

**⚠️ `--output` 不是捕获参数**（`CLICommands.swift:124-148`）：捕获成功后**再发第二个 `deliver.save` 请求**，requestID 后缀 `-save`；保存失败则保存的响应替换捕获的响应（退出码反映保存结果）；成功则注入 `data.savedPath`。**不要「优化」成并入捕获调用。**

**自动拉起**：CLI 连接 `ENOENT/ECONNREFUSED` → `TaAppLauncher.launch()` → 每 **80 ms** 轮询重试，上限 `min(timeout, 5) s`。启动参数 `["--agent-bridge"]`，`activates = false`。App 路径 `/Applications/拓.app`，可用环境变量 **`TA_APP_PATH`** 覆盖。

**客户端身份**（线上可见）：`ta` CLI → `{name:"ta-cli", version:"1.0.1"}`；`dsh-ta` → `{name:"dsh-ta", version:"1.0.1"}`

**⚠️ `translate text` 的解析陷阱**：必须字面 token `text` 紧邻 `--text`；`ta translate --text "x"` 会走 OCR+翻译路径（`:198-205`）。

**⚠️ `transform.image` 完全绕过云端策略** —— 无 `cloudError` 检查，保持纯本地。

### 11.2d 请求 ID 路径白名单（易踩坑）

`TaAgentArtifactStore` 对 `requestID` 与 `filename` 施加 `^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$`（且非 `.` / `..`）。
**调用方传入的 `--request-id` 若违规**（以 `-` 开头、含 `/`、长度 > 128）会导致 `save()` 抛异常 —— 该异常逃出 `CapabilityFailure` 落入通用 `catch`，最终呈现为 `INTERNAL_ERROR` 而非 `INVALID_REQUEST`。Windows 版应保留同样行为或明确修正。

### 11.2 方法全集（`AgentMethods.swift:3-26`，**22 个**）

```
system.handshake        system.status  system.capabilities  system.permissions
target.listDisplays     target.listWindows
capture.display  capture.frontmost  capture.window  capture.region
capture.interactive  capture.scroll
recognize.ocr  analyze.image  translate.text  translate.image  transform.image
deliver.copy  deliver.save  deliver.pin
job.status  job.cancel
```

**只对外宣告 16 个**（`TaAgentCapabilityService.supportedMethods` `:81-88`）：status / capabilities / permissions / listDisplays / listWindows / capture.{display,frontmost,window,region} / recognize.ocr / analyze.image / translate.{text,image} / transform.image / deliver.{copy,save}

**未宣告的 6 个**：`system.handshake`（由 router 直接应答，**不走** capability service，因此**不被审计**）、`capture.interactive`、`capture.scroll`、`deliver.pin`、`job.status`、`job.cancel` —— 调用返回 `INVALID_REQUEST` / `该 Agent 方法尚未支持：<raw>。`

策略枚举：`AgentCapturePolicy { silent, idle, interactive }`、`AgentCloudPolicy { auto, allow, deny }`
⚠️ `capturePolicy` 虽被宣告但**从不被读取**，`showsCursor` 在所有路径硬编码 `false`。

**`system.handshake` 的应答**（router 短路，`TaAgentRequestRouter.swift:33-41`）：
```json
{"protocolVersion":1,"requestId":"<id>","ok":true,
 "data":{"protocolVersion":1,"server":"Ta"},"artifacts":[],
 "meta":{"cloudUploaded":false,"durationMs":<n>}}
```

**`system.capabilities` 数据体**：`{methods:[16 个 raw], capturePolicies:[silent,idle,interactive], cloudPolicies:[auto,allow,deny]}`

**`meta.cloudUploaded` 真值表**：`analyze.image|translate.text|translate.image` → `response.ok`；`recognize.ocr` → `ok && ocrEngine == .deepSeekOCR2`；其余 → `false`

**⚠️ 对端认证（安全边界）**：服务端用 `getpeereid()` 比对 `geteuid()`（`TaAgentBridgeConnection.swift:14-19`），**失败即静默关闭连接，不返回任何帧**（连错误信封都没有）。另加文件权限纵深：目录 `0700`、socket 文件 `0600`。**Windows 版必须保留「失败即静默关闭」语义** —— 返回 `INVALID_REQUEST` 会泄露信息并破坏客户端预期。

### 11.3 CLI 命令面（`TaCLI/CLIParser.swift`）

```
ta status|capabilities|permissions [--json]
ta screen list [--json]
ta window list [--app <名称或 Bundle ID>] [--json]
ta capture screen [--display <ID>] [--output <PNG>] [--json]
ta capture frontmost [--output <PNG>] [--json]
ta capture window --window-id <ID> [--output <PNG>] [--json]
ta capture region --display <ID> --x <N> --y <N> --width <N> --height <N>
ta ocr [last|图片路径] [--languages zh-Hans,en-US] [--json]
ta analyze [last|图片路径] --task general|extractText|explainCode|tableMarkdown|formulaLaTeX
ta translate [last|图片路径] --mode text|image [--cloud auto|allow|deny]
ta translate text --text <内容> [--cloud auto|allow|deny]
ta transform [last|图片路径] --recipe <JSON> [--output <PNG>] [--json]
ta transform undo|redo [--output <PNG>] [--json]
ta copy [last|图片路径] | --text <内容>
ta save [last|图片路径] --output <PNG>
```
**通用参数**：`--json --timeout <秒> --request-id <ID> --socket <路径> --cloud auto|allow|deny`
（timeout 默认 **10**，必须 > 0；`requestID` 默认 `ta_<uuid 无横线小写>`；`--cloud` 仅接受 auto/allow/deny）
`save` 必须有 `--output`（否则 `save 需要 --output <PNG>。`）

### 11.4 隐私与审计（`TaAgentPrivacyPolicy.swift`）

**规则**（`captureError(for:)` `:60-96`）：
1. `isEnabled == false` → `拓的 Agent 调用已关闭。`
2. `automaticCaptureAllowed == false` → `Agent 自动截图已关闭。`
3. 目标 bundle 在黑名单 **或** 可见窗口集合与黑名单有交集 **或**（`!allowCaptureTa` 且含 Ta）→ `目标 App 已被 Agent 截图隐私策略阻止。`
   - hint：`打开拓 → 设置 → Agent 与自动化后启用。` / `可在拓的 Agent 与自动化设置中启用。` / `如确有需要，请在拓的隐私 App 黑名单中调整。`
4. 全部 `retryable: false`，code `targetBlockedByPrivacyPolicy`

`cloudError(requested:)`（`:105-116`）：effective == `.deny` → `当前请求禁止把图片或文字发送到云端模型。`，code `cloudUploadNotAllowed`

`allowsWindowMetadata(bundleIdentifier:)`（`:98-103`）：黑名单或（`!allowCaptureTa` 且是 Ta）→ 不返回元数据

**审计日志**（`TaAgentAuditLog.swift`）：**不保存请求参数、识别正文或图片数据** —— 这是产品承诺，移植时必须保持。

**工件存储**（`TaAgentArtifactStore.swift`）：缓存目录，可在设置中清理。

### 11.5 集成物

- **Agent Skill**（`Integrations/AgentSkill/ta/`）：`SKILL.md` + `references/commands.md`（85 行）+ `references/editing-recipes.md`（95 行）+ `scripts/` + `agents/`
- **DeepSeek Harness 插件**（`Integrations/DeepSeekHarness/dsh-ta/`）：TypeScript，Cordis 原生 Plugin + Bundle，`package.json` + `src/` + `tests/` + `cordis.patch.yml`，**本身就是跨平台的**
- **安装器**（`scripts/install.sh`，159 行）：`curl -fsSL --retry 3 --retry-all-errors --retry-delay 1 .../install.sh | bash`，校验 SHA-256，装到 `~/.local/bin/ta`，Skill 装到 Codex 与通用 Agent Skills 目录

> Windows 需要一个 `.ps1` / `.exe` 安装器，把 CLI 装到 `%LOCALAPPDATA%\Programs\Ta\bin` 并加入 PATH。

---

## 12. 视觉设计系统

### 12.1 品牌（`TaDesignSystem.swift`）

| 项 | 值 |
|---|---|
| 中文名 | `拓` |
| 英文名 | `Ta` |
| Tagline | `把屏幕上的信息，拓下来。` |
| 描述 | `截图、识别、翻译与标注，一步完成` |
| 图标资源 | `Resources/Brand/Ta-AppIcon.png` + `Resources/Ta.icns`（406KB） |

### 12.2 调色板（`TaPalette`，`:11-18`）—— ⚠️ 精确 RGB

```swift
ink          = rgb(26, 26, 26)          // #1A1A1A
paper        = rgb(250, 246, 238)       // #FAF6EE
elevatedPaper= rgb(255, 253, 248)       // #FFFDF8
cinnabar     = rgb(214, 64, 47)         // #D6402F   ← 品牌主色（朱砂）
mutedInk     = rgb(104, 100, 94)        // #68645E
hairline     = ink @ 0.10
```
（`.red` 标注色 = `#FF3B30`，`.highlighter` = `#FFD60A` @ 0.35 —— 见 §6.2）

### 12.3 字体与版式

- 标题：`.system(.headline, design: .serif, weight: .bold)`
- 正文：系统默认（San Francisco）
- 编号数字：system **bold**
- 回退图标标记：`RoundedRectangle(cornerRadius: size*0.22)` + 内嵌 `size*0.08` 朱砂块 + 衬线体「拓」字 `size*0.42`

### 12.4 关键 UI 度量

| 元素 | 度量 |
|---|---|
| 菜单栏 Popover | **326×574**，`.transient`，`animates = true` |
| 状态图标 | **19×19** |
| 标注工具栏 | 2 行，高 **96**，水平 inset **16**，垂直 inset **8**，行距 **6**，组距 **8**，组圆角 **8**，图标按钮 **30×30**，工具选择器 **184**，宽度滑块 **104**，透明度滑块 **84** |
| 内联工具栏 | 按钮 32×30，gap **10**，margin **8**（`InlineAnnotationLayout.toolbarFrame` `:16-37`）；优先**下方**，`belowY < screen.minY + margin` 时翻到**上方**；x 夹到 `[screen.minX+margin, screen.maxX-width-margin]`；宽 `min(toolbarWidth, screen.width - 16)`；高 `max(42, natural)` |
| 内联画布 | 遮罩黑 **0.48**；画布 2pt `controlAccentColor` 边框；`contentInset = 0` |
| 独立编辑器画布 | `contentInset = 24`；背景黑 0.82 |
| 结果条 | 见 §5.7 |
| 长截图 HUD | 见 §8.8 |
| 钉图 | 圆角 **10**，边框白 0.28 / 1px |

---

## 13. macOS API → Windows 对照表

| macOS API | Windows 对应 | 备注 |
|---|---|---|
| `SCScreenshotManager.captureImage` | **Windows.Graphics.Capture**（`GraphicsCaptureSession` + `Direct3D11CaptureFramePool`） | ⚠️ 无静态截图 API，需每次起停 frame pool，有 1–2 帧延迟 |
| `SCContentFilter(desktopIndependentWindow:)` | `GraphicsCaptureSession.CreateForWindow(hwnd)` | 边框/阴影裁剪需自行处理 |
| `SCStreamConfiguration.showsCursor` | `GraphicsCaptureSession.IsCursorCaptureEnabled` | 代码恒传 `false` |
| `SCStreamConfiguration.ignoreShadowsSingleWindow` | 无 | 需自行裁掉阴影边 |
| `SCDisplay.frame` | `GetMonitorInfo` → `MONITORINFOEX.rcMonitor` | 需自建显示器模型 |
| `SCWindow.frame/.windowID/.owningApplication` | `EnumWindows` + `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` + `GetWindowThreadProcessId` | zOrder = 枚举顺序 |
| `CGWindowListCopyWindowInfo` | `EnumWindows` + 过滤 `IsWindowVisible` / `WS_EX_TOOLWINDOW` / `DWMWA_CLOAKED` / alpha | 须复现 `WindowSnapService.targets` 每个过滤条件 |
| `CGImage.cropping(to:)` / `CGContext` | `ID2D1Bitmap::CopyFromBitmap`（带源矩形）/ WIC `IWICBitmapClipper` | 拼接器会做上千次裁剪+绘制，应预计算条带 |
| `NSPanel(.borderless,.nonactivatingPanel)` + `level .screenSaver` | `WS_POPUP` + `WS_EX_NOACTIVATE` + `WS_EX_TOPMOST` + `SetWindowPos` | ⚠️ 见风险 #4 |
| `collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]` | `WS_EX_TOPMOST` + `SetWindowDisplayAffinity` + （Win11）`SetWindowBand` | ⚠️ 无虚拟桌面跟随等价物 |
| `NSTrackingArea(.mouseMoved, .activeAlways)` | `TrackMouseEvent(TME_LEAVE)` / 全屏覆盖层直接 `SetCapture` | |
| `addCursorRect` / `resetCursorRects` | `WM_SETCURSOR` + `SetCursor(LoadCursor(nullptr, IDC_*))` | 8 向缩放需 8 个不同光标 |
| `NSCursor.crosshair/.openHand/.closedHand/.iBeam/.pointingHand` | `IDC_CROSS` / `IDC_HAND` / `32642`(iBeam) / `32649` / `32642-32646` 等 | |
| `NSEvent.addLocalMonitorForEvents` | 覆盖层 wndproc 中处理 `WM_RBUTTONDOWN` | |
| `keyDown` / `keyCode 53/36/76/51/117` | `WM_KEYDOWN` `VK_ESCAPE` / `VK_RETURN` / `VK_NUMPAD…` / `VK_DELETE` / `VK_BACK` | macOS 36=Return，76=小键盘 Enter |
| `NSScreen.backingScaleFactor` | `GetDpiForWindow`/`GetDpiForMonitor` + `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)` | Retina 倍率 == DPI 缩放 |
| `CGDirectDisplayID` / `CGDisplayBounds` | `HMONITOR` / `GetMonitorInfo.rcMonitor` | |
| `Carbon RegisterEventHotKey` + `kEventHotKeyPressed` | `RegisterHotKey(hwnd, id, MOD_*\|MOD_NOREPEAT, VK_*)` + `WM_HOTKEY` | ⚠️ 冲突码为 `ERROR_HOTKEY_ALREADY_REGISTERED`；VK 码不同，**必须建翻译表** |
| `Carbon kVK_*` / `modifierFlags` | `VK_*` + `MOD_ALT/MOD_CONTROL/MOD_SHIFT/MOD_WIN` | 默认 `⌘⌥⇧1-6` → `MOD_CONTROL\|MOD_ALT\|MOD_SHIFT\|VK_1..6` |
| `AXIsProcessTrusted` / `AXUIElement*` | **UI Automation**（`IUIAutomation`, `ScrollPattern`, `RangeValuePattern`）或 MSAA `AccessibleObjectFromPoint` | ⚠️ 见风险 #3 |
| `CGEvent(scrollWheelEvent2Source:units: .pixel)` | `SendInput` `MOUSEEVENT_WHEEL`（`mouseData = delta*120`）或 `IUIAutomationScrollPattern` | ⚠️ Windows 是档位式（120/档），无逐像素滚轮 |
| `NSPasteboard.changeCount` / `.setData(.png)` | `OpenClipboard`/`EmptyClipboard`/`SetClipboardData(CF_*)` + `GetClipboardSequenceNumber()` | 需重试/退避循环 |
| `NSBitmapImageRep` PNG/JPEG | **WIC** `PNGEncoder` / `JpegEncoder` | PNG `[:]`，JPEG `0.92` |
| `NSBezierPath` + `setLineDash` | Direct2D `ID2D1StrokeStyle`（`D2D1_DASH_STYLE_CUSTOM` + `dashes[]` + `dashOffset = 0`） | ⚠️ Win32 `PS_DASH` 是设备像素单位，不适用 |
| `NSImage.draw(in:from:operation:.copy)` | `ID2D1RenderTarget::DrawBitmap(srcRect, dest, opacity, D2D1_BITMAP_INTERPOLATION_MODE_*)`；`.copy` → `D2D1_COMPOSITE_MODE_SOURCE_COPY` | |
| `NSAttributedString.boundingRect` + `.usesFontLeading` | `IDWriteFactory::CreateTextLayout` + `GetMetrics` | ⚠️ 字体度量差异会改变每个文字盒 |
| `CTFontCreateUIFontForLanguage(.system, size, "zh-Hans")` | `IDWriteFactory` + 中文字形回退 | 基线语义须精确复现 |
| `CIPixellate(scale 14)` / `CIGaussianBlur(radius 12)` | 移植 renderer B 的 `pixelated()`（两级缩放）与 `vImageBoxConvolve` 等价实现 | ⚠️ A/B 两套模糊输出**不同**，见风险 #8 |
| `Accelerate/vImageBoxConvolve_ARGB8888` | 可分离 3 遍 box blur（edge-extend 夹取） | 注意核是**单遍 box**，非真高斯 |
| `NSTextView` 内联编辑 | RichEdit / 自定义 IME 感知覆盖层 | ⚠️ 见风险 #13 |
| `NSSavePanel` | `IFileSaveDialog`（COM Shell） | 按扩展名判格式 |
| `NSSound.beep()` | `MessageBeep(MB_OK)` | |
| `NSStatusBar.statusItem` + `NSPopover` | `Shell_NotifyIcon` + `TrackPopupMenu` 或自定义弹出 | |
| `NSVisualEffectView(.hudWindow)` | `SetWindowCompositionAttribute`（acrylic）/ `DwmSetWindowAttribute(DWMSA_SYSTEMBACKDROP_TYPE)` | |
| `CALayer` shadow / `zPosition` | D2D 效果 / `WS_EX_COMPOSITED` | |
| `NSAlert.runModal()` | `MessageBoxW` 或 TaskDialog | |
| `NSWorkspace.shared.open("x-apple.systempreferences:…")` | `ms-settings:privacy` | ⚠️ 无法程序化授予权限 |
| `CGPreflightScreenCaptureAccess` / `CGRequestScreenCaptureAccess` | 无直接等价 | ⚠️ UAC 提权进程、RDP、安全桌面会得到黑帧 |
| Security `SecItem*`（Keychain） | **DPAPI**（`CryptProtectData`）或 Credential Manager | 需保留 `WhenUnlockedThisDeviceOnly` 语义 |
| `UserDefaults` | `ApplicationData.Current.LocalSettings` | |
| Unix domain socket | **Named Pipe** | 帧格式不变 |

---

## 14. 移植风险分级

### 🔴 高风险（无干净等价物，需专门设计）

1. **「整屏静态截图」模型**（`CaptureCoordinator.swift:169`）
   Windows.Graphics.Capture 是 frame pool 流式，无一次性静态截图。需起 `Direct3D11CaptureFramePool` → 等一帧 → 拷到 staging texture → 拆除，不可避免 1–2 帧延迟。「快捷键时刻冻结帧」的不变量只能**近似**复现。

2. **`.cghidEventTap` 合成滚轮事件**（`AccessibilityAutoScrollService.swift:179`）
   macOS 允许任意进程以**像素**增量在任意全局点投递滚轮手势。Windows 滚轮是档位式（120/档）且走系统光标，**无逐像素等价物**。整个 32px 分块方案与 `recommendedScrollDistance` 粒度无法映射。备选：`PostMessage(WM_MOUSEWHEEL)` 到目标 HWND（Chromium/Electron 常忽略）、`IUIAutomationScrollPattern.Scroll`、`SetScrollPos`+`WM_VSCROLL`—— 都会改变保真度契约。另 Windows 无 `kAXTrustedCheckOptionPrompt` 式的内联授权提示。

3. **`kAXVerticalScrollBarAttribute` 进度追踪**
   UI Automation `RangeValuePattern` 在很多滚动条上可用，但恰恰在 Chromium/Electron 上常不可用或被归一化。`AXUIElementCopyElementAtPosition`（返回无障碍元素而非 HWND 的系统级命中测试）无单次调用等价物 —— MSAA `AccessibleObjectFromPoint` 最接近但陈旧，`WindowFromPoint` 则命中不到**内层**可滚动控件。**这是最大的功能风险点。**

4. **「非激活键盘面板」**（`level = .screenSaver`）
   macOS 提供 `.nonactivatingPanel` + `canBecomeMain = false` + `canBecomeKey = true` + `orderFrontRegardless()`，覆盖层可抓键鼠而 Ta 从不成为前台。Windows 上 `WS_EX_NOACTIVATE` **阻止**激活（会丢键盘焦点），普通置顶弹窗则**抢**前台 —— 没有中间态。标准绕法（`AttachThreadInput` + `SetForegroundWindow`）脆弱且可能在全屏独占应用中抢焦点。

5. **`[.canJoinAllSpaces, .fullScreenAuxiliary]`**
   Windows 无「跟随用户跨虚拟桌面」标志；在全屏独占 DX swapchain 之上绘制也不保证。`SetWindowBand`（Win11 `ZBID_UIACCESS`）需特权，最接近。

6. **`level = .screenSaver`**
   高于所有普通应用、菜单与 Dock 的层级。Windows 置顶 + band 是近似物，无严格等价的排序。

7. **Carbon `RegisterEventHotKey`**
   `RegisterHotKey` 接近，但：无进程级签名命名空间、`MOD_NOREPEAT` 需显式请求、冲突错误信息不够描述性、保留系统组合行为不同。**Carbon VK 码（`kVK_ANSI_1` = 18）不是 Windows VK 码（`VK_1` = 0x31）—— 翻译表必须自建，且部分键非 1:1。**

8. **两套模糊实现不一致**
   编辑器 A 用 `CIGaussianBlur(radius: 12)`（GPU），渲染器 B 用 `vImageBoxConvolve`（核宽 `max(3, round(radius*2+1))`，强制奇数，**单遍 box**，非真高斯）。**同名参数下输出不同**。同样，A 用 `CIPixellate` 而 B 用两级缩放（降采样 `.low` + 升采样 `.none`）。需明确 Windows 上以哪套为准。

9. **`NSBezierPath.setLineDash`**
   虚线模式在**用户空间单位**、phase 0，且**基于已缩放 lineWidth** 计算。Win32 `PS_DASH` 是设备像素单位。必须用 Direct2D `ID2D1StrokeStyle` + `D2D1_DASH_STYLE_CUSTOM`。

10. **`NSColor.systemRed` 动态色**
    macOS 14 暗色 ≈ `#FF453A`，亮色 ≈ `#FF3B30`。Agent 契约的 `.red` 是**固定** `#FF3B30`。两者在暗色下**不一致**，需选定并记录。

11. **字体度量**
    `AnnotationTextMetrics.boundingSize` 用 `boundingRect` + `.usesFontLeading`，文字盒尺寸依赖字体的行距。换成 Segoe UI 会移动**每一个**文字盒、命中测试与布局夹取。建议提供度量兼容回退或固定字体。

12. **CoreText 中文排版**
    `CTFontCreateUIFontForLanguage(.system, size, "zh-Hans")` → `IDWriteFactory::CreateTextLayout`。基线语义须精确复现（`textPosition.y = height - origin.y - fontSize`）。

13. **IME 中文输入（最大的保真风险）**
    文字框**每次按键**重新布局，IME 组合串期间的测量与 `boundingRect` 在空串/部分串上的结果不会一致。**必须尽早锁定组合串处理方式。**

### 🟡 中风险

14. `NSColor.controlAccentColor` —— 系统强调色，用于选区、手柄、裁剪线、内联画布边框。
15. `NSPasteboard` 语义 → `GetClipboardSequenceNumber()`。Windows `EmptyClipboard`+`SetClipboardData` 要求唯一打开窗口，且受 `GetOpenClipboardWindow` 争用影响，`ClipboardCommitPolicy` 需要 macOS 上没有的重试/退避循环。
16. 面板从全局屏矩形直接定位 —— Windows 需 `SetWindowPos` + per-monitor DPI v2 工作区坐标换算，并处理任务栏/自动隐藏与**负原点**显示器。
17. `NSBitmapImageRep(.deviceRGB)` —— 设备色彩空间，显示配置变化会使导出偏移。渲染器 B 用 `CGColorSpaceCreateDeviceRGB()`，拼接器用 **`.sRGB`**（不一致）。Windows 上应统一到 sRGB。
18. `NSImage.draw(in:from:operation:.copy)` —— D2D 无对应混合模式，需 `D2D1_COMPOSITE_MODE_SOURCE_COPY`。
19. `NSGraphicsContext.save/restoreGraphicsState` → `PushLayer/PopLayer` 或显式矩阵保存。每个 clip 都是 save/restore 作用域的。
20. `NSTextView` 内联编辑 → 需 IME 感知的 RichEdit/自定义覆盖层。
21. `NSAttributedString(data:.html)`（钉图 HTML 粘贴）→ 需要 Windows HTML/RTF 解析器或重写。
22. `NSVisualEffectView` → acrylic / WinUI Mica。
23. `NSUndoManager` 交互 —— 编辑器**刻意避开**它但必须防御 NSTextView 注册。Windows 无此隐患，保留快照栈模型即可。
24. Y 轴翻转在 ≥5 处被假设（`WindowSnapService:60`、`FrozenDisplayCropper:41`、`ScreenCaptureService:83`、`scrollTargetLocation:212`、stitcher `render:475`）。Windows 统一左上原点会**消除**这些翻转 —— 必须验证接缝修正偏移与自动滚动命中点未被反向。
25. `NSScreen.screens.first` 被假设为主屏（`WindowSnapService:38`）。`EnumDisplayMonitors` 顺序不保证，须用 `MONITORINFOF_PRIMARY`。
26. **单屏选区**：覆盖层面板正好一个 `screen.frame`，裁剪器与 `screenFrame` 求交 → **跨屏拖拽今天就不可能**（也是 PRD P0 未完成项）。Windows 移植会**继承**此限制，除非把面板扩到所有显示器并集（但那会破坏单一 `backingScaleFactor` 模型 —— `CaptureSelection` 只携带一个 scale）。
27. **吸附目标限制在 frontmost app PID**。Windows 上「前台进程拥有的窗口」信号弱得多 —— UWP/Store 应用、Electron 辅助进程、不同完整性级别的提权窗口都不可命中或对 `EnumWindows` 不可见。
28. **`isTransitioning` 是单个布尔**，不是完整状态枚举；`.edit` 延迟消失路径依赖它 + `defer`。移植应换成显式状态机。
29. 屏幕录制权限模型（TCC）无 Windows 对应；UAC 提权、RDP、安全桌面（UAC 提示/锁屏）行为不同，会在 macOS 仅拒绝权限的地方得到**黑帧**。
30. `CaptureJob` / `CaptureJobPhase`（`CaptureModels.swift:144-198`）定义了完整状态机与 `allowedTransitions` 表但**从未被调用** —— 是规格产物。移植应移植**实际行为**（`isCapturing` + `jobID` + `isFinishing` 标志），而非这个枚举。
31. **`deliver.save` 硬性要求 `path.hasPrefix("/")`**（`TaAgentCapabilityService.swift:455`）—— **Windows 上直接是阻塞性缺陷**。必须改为接受 `C:\…`、UNC `\\server\share\…` 并拒绝相对路径。（`dsh-ta` 的 `isAbsolute` 已同时处理两种形态，服务端是唯一需改处。）
32. **架构字符串字面量比较**：`currentArchitecture` 返回 `"arm64"` / `"x86_64"`（`:577-585`），并**逐字**与 manifest/catalog 的 `architecture` 比较。Windows 版要么沿用 `"x86_64"`，要么同时改生产者与消费者（改用 RID `win-x64` 会直接失配）。
33. **WinRT OCR 不返回逐行置信度**。`OCRResult.confidence`（行置信度均值）、`isLowConfidence < 0.72` 与翻译的 **0.55** 阈值都会失去依据 —— 需合成值或重设计启发式。同时 WinRT bbox 是**图像像素坐标**而非归一化（必须归一化）；`usesLanguageCorrection` 无对应开关。
34. **条形码检测**（`VNDetectBarcodesRequest`）无直接等价物 —— 需 ZXing.Net 等，因为 `.qrCode` 内容类型与「文字为空但有条码时用 payload 替代」依赖它。
35. **`URL.standardizedFileURL`** 不等于 `Path.GetFullPath`：反斜杠/正斜杠混用、盘符相对路径、`\\?\` 前缀行为都不同。路径组件白名单校验前必须先归一化。
36. **`data.write(to:options:[.atomic])`** —— Windows 上需 `MoveFileEx(…, MOVEFILE_REPLACE_EXISTING)` 或写临时文件后替换；`File.WriteAllBytes` 不是原子的。
37. **`install.sh` 依赖 `ditto` / `tar` / `shasum` / `install -m 755` / `[[ -L ]]` / `.zprofile` PATH 追加** —— 需 PowerShell 重写。
38. **`bundleIdentifier` 是自截黑名单的键** —— 必须保持**稳定字面量**，**不要**改成 PID 或 exe 名（会破坏黑名单语义）。
39. **`CGDirectDisplayID` / `CGWindowID` 作为 `UInt32` 出现在 `data.displays[].id` 与 `windows[].windowId`** —— Windows 的 `HMONITOR` 是指针。建议按快照分配合成索引 id（与 macOS 同为临时值，Skill 文档已说明 `TARGET_NOT_FOUND` → 重新列举）。
40. **JSON 字节级 parity**：`.sortedKeys` 需显式排序（`System.Text.Json` 默认按声明顺序）；`.withoutEscapingSlashes` 需 `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`；`.iso8601` 为 `yyyy-MM-ddTHH:mm:ssZ`（**无小数秒**，带了会与 macOS 客户端失配）；可选属性（`data`/`meta`/`error`/`hint`/`width`/`height`）nil 时**省略**，但 `artifacts` 作为 `[]` **必须始终存在**。

### 🟢 低风险（可 1:1 直译）

**整个 `AIScreenshotCore` 数学层无 AppKit 依赖**，且被 XCTest 完整覆盖 —— **先做这些，几何就会精确**：
`TaperedArrowGeometry`、`AnnotationSelectionGeometry`、`AnnotationTextMetrics.stepped/clamped/boundingSize`、`AnnotationStrokeSmoother`、`VerticalScrollMatcher`、`ScrollingImageStitcher` 条带逻辑、`FrozenDisplayCropper.pixelRect`、`SelectionRectEditor`、`InlineAnnotationLayout`、`PinnedImageLayout`、`ClipboardCommitPolicy`、`AgentFrameCodec`、`AnnotationRecipe` 校验、`AnnotationColor` 十六进制编解码。

---

## 15. 技术选型建议

### 15.1 语言与 UI 框架

| 方案 | 适用 | 评价 |
|---|---|---|
| **C# + WinUI 3 / WPF** | 推荐主力 | 覆盖层、设置 UI、SwiftUI 视图对应关系最自然；.NET 有成熟 WIC/Direct2D 互操作 |
| **C++ / WinUI 3 或 Win32 + D2D** | 性能关键（拼接器、渲染器） | 长截图算法与标注渲染需密集像素操作，C++ 最直接 |
| 混合 | **推荐** | C++ 核心库（`AIScreenshotCore` 对应层 + 两个渲染器）+ C#/WinUI 外壳 |

**理由**：macOS 版的分层（Core 纯逻辑 / App 平台 UI）恰好对应 C++ 核心库 + 托管外壳。

### 15.2 关键库

| 用途 | 建议 |
|---|---|
| 屏幕捕获 | **Windows.Graphics.Capture**（Win10 1903+）+ CsWinRT / `Microsoft.Windows.CSDK` |
| 2D 渲染 | **Direct2D 1.1**（`ID2D1DeviceContext`、`D2D1_COMPOSITE_MODE_SOURCE_COPY`、自定义 `ID2D1StrokeStyle`） |
| 图像编解码 | **WIC**（`PNGEncoder`/`JpegEncoder`、`IWICFormatConverter`） |
| 文字排版 | **DirectWrite**（`IDWriteFactory`、`IDWriteTextLayout`）+ 中文回退字体 |
| 模糊 | 可分离 3 遍 box blur（edge-extend），或移植 `vImageBoxConvolve` 语义 |
| 像素化 | 两级缩放（降采样 `.low` 等价 + 升采样最近邻）—— 照抄 renderer B 算法 |
| OCR | **Windows.Media.Ocr**（内置离线，等价 Apple Vision）→ 不足处用本地 ONNX Runtime + PaddleOCR / RapidOCR |
| HTTP | `HttpClient`（保留 `timeoutInterval = 60`、`temperature 0`、`max_tokens 2048`） |
| 密钥存储 | **DPAPI**（`DataProtectionScope.CurrentUser`，对应 `ThisDeviceOnly` 语义） |
| 桥接 IPC | **Named Pipe**（`\\.\pipe\Ta-agent-v1`），帧格式与 JSON 完全不变 |
| 全局热键 | `RegisterHotKey` + `WM_HOTKEY`，自建 VK 翻译表 |
| 设置 | `ApplicationData.Current.LocalSettings`（键名与 §10.4 完全一致） |
| 托盘 | `Shell_NotifyIcon` + 自定义弹出面板 |

### 15.3 建议实施顺序

```
阶段 0  核心库（C++/C#）：AnnotationRecipe 校验 + AnnotationColor + AgentFrameCodec
        + ClipboardCommitPolicy + 全部几何算法    ← 纯移植，有测试锁定，先做完几何就精确
阶段 1  捕获链：WGC 整屏冻结 → 全屏非激活覆盖层 → 框选/吸附/裁剪
阶段 2  标注渲染器 B（CGContext 等价，纯算法）→ 渲染器 A（D2D）
阶段 3  标注编辑器 UI（工具栏 + 14 工具 + 命中测试 + 撤销栈）
阶段 4  OCR（Windows.Media.Ocr）+ AI Provider 4 协议 + 翻译渲染
阶段 5  钉图 + 菜单栏 + 结果条 + 设置 UI
阶段 6  长截图（拼接器 → 匹配器 → 自动滚动 → 接缝复查）
阶段 7  Agent 桥（Named Pipe）+ ta CLI（跨平台即通）+ 安装器
阶段 8  打包（MSIX/MSI + 代码签名）
```

### 15.3b 已实施与已变更（截至本轮开发）

| 项 | 状态 |
|---|---|
| 解决方案骨架 | ✅ `windows/Ta.sln`，3 个项目（`Ta.Core` 纯逻辑 / `Ta.Core.Tests` / `Ta.Spike.Overlay`） |
| .NET 版本 | ✅ **8.0.425**（本机实测）。`Ta.Core` 用 `net8.0`，平台项目用 `net8.0-windows` |
| 覆盖层钩子链路 | ✅ **已实测通过** —— `WH_KEYBOARD_LL` 安装成功，合成 Escape 被钩子收到 |
| 覆盖层生产组件 | ✅ `Ta.Platform/Overlay/SelectionOverlay`，**4/4 项自动验证通过** |
| 核心算法首批 | ✅ 箭头几何、等比缩放、字号阶梯、笔画平滑、冻结帧裁剪 |
| **Agent 契约层** | ✅ 颜色编解码、配方校验、JSON 编解码、帧编解码 —— **72 个测试全通过** |
| **长截图算法** | ✅ 匹配器 + 运动检测 + 拼接器记账 + 像素合成 —— **143 个测试全通过** |

#### ⚠️ 像素合成未引入 WIC（方案变更）

原计划用 WIC 做像素合成，**实际改为纯托管实现**，放在 `Ta.Core/Imaging`。

判断依据：拼接器实际只需要两种**内存位图**操作 —— 灰度降采样、条带裁剪拼接。WIC 真正必要的是**文件编解码**，那是独立关注点，且会引入 COM 互操作的大量样板。

因此：
- `RgbaBitmap` —— 字节序对齐 Mac 的 `premultipliedLast`，缓冲区显式复用
- `GrayscaleResampler` —— 面积平均 + Rec.709 亮度加权，对应 Mac `interpolationQuality = .medium` 的盒式滤波语义；**采样高度独立夹取**，保留 Mac 刻意的不对称性
- `VerticalCompositor` —— `Compose` / `ComposeRange`；全程左上原点，**无 Y 翻转**（见 §5.1）
- `BitmapLongCapture` —— 把已测好的记账逻辑与合成接起来的门面

附带好处：这完全遵守了 §15.2「绝不用 System.Drawing」的约束，且零互操作、可完整测试。

**WIC 仍然需要，但只用于 PNG 编解码**（写文件、写剪贴板 `CF_PNG`）—— 已列为任务 #7。原因：PNG 需要 zlib/deflate 压缩，手写容易出错且无收益。

#### 拼接器的缝已合上

此前 `MakeStrips()` 只产出 `ImageStrip` 契约。现在通过 `IStitchFrame` 的实现 `BitmapStitchFrame` 把记账与合成接通，端到端已有测试：

| 验证 | 结果 |
|---|---|
| 两帧滚动 40px 后合成 | 高度 `160 + 40`，宽度 96 |
| 合成图顶端 | 与首帧顶端**逐字节一致**（无固定区域时） |
| 分段合成之和 | 等于整图高度，且产生多段 |
| 单帧 | 合成结果与原图像素完全相同 |

#### 跨平台互操作已建立实证保证

#### 跨平台互操作已建立实证保证

Mac 版仓库的夹具 `Tests/Fixtures/annotation-recipe-v1.json` 被**直接复制**到 Windows 测试项目，而非本地另写样例。Windows 端解析同一份 JSON 得到 **14 个操作、11 个元素 ID**，与 Mac 侧 `AnnotationRecipeTests` 的断言一致。

这意味着 `ta` CLI 与 Agent Skill 的跨端协作有**可执行的回归门**而非口头约定 —— 将来谁动了字段名或默认值，测试立刻失败。

#### 长截图测试的对等性

匹配器的测试**不是自造数据**，而是把 Mac 版 `VerticalScrollMatcherTests.swift:126-214` 的四个数据构造函数逐行照搬过来，包括 seed 与全部系数：

| Helper | 移植内容 |
|---|---|
| `makeWorld(width:height:seed:)` | `(x*17 + y*31 + (y/3)*47 + (x*y)%83 + seed) % 256` |
| `crop(_:y:height:)` | 截取 `[y, y+height)` 行 |
| `makeAppFrame(offset:)` | 固定侧栏 24px + 固定输入框 28px 的应用窗口 |
| `makeRepeatingFrame(offset:)` | 周期 36 的重复内容，用于构造远歧义 |
| `makeBrowserFrame(offset:animationSeed:)` | 固定标题栏 20px + 随帧变化的动画带 |

10 个 Mac 用例逐一移植并全部通过，含这几个关键行为：

- 相同两帧 → `shift == 0` 且 `isDuplicate`
- 向下约束下**绝不接受**向上帧
- 向下约束下拒绝远距离重复内容的并列（避免自动截图跳到更早的对话）
- 侧栏与输入框固定时仍找到内容区位移（41px）
- 浏览器式固定标题 + 动画带**无法覆盖**内容带投票（52px）

> ⚠️ 一开始我用自造的周期条纹图做测试数据，结果"相同两帧"匹配出 `shift = -304` —— 与 Mac 的 `shift == 0` 不符。排查后发现**不是移植偏差，而是我的数据周期性为 8**，导致多个位移同时对齐，而 Mac 用的是非周期数据。这个坑值得记录：**测试数据的统计特性会直接改变算法的表观行为**，周期图案会掩盖真实的算法差异。

#### 拼接器的缝已合上（原留白已消除）

此前 `MakeStrips()` 只产出 `ImageStrip` 契约、像素合成待平台层。现在通过 `IStitchFrame` 的实现 `BitmapStitchFrame` 把记账与合成接通，端到端已有测试（见上）。

### 15.4 需要提前定案的问题


#### 移植中抓到的一处真实缺陷（值得留档）

帧长度上限检查：Mac 的 `Int` 是 64 位，`Int(UInt32(bigEndian: 0xFFFFFFFF))` 得 `4294967295`，超过 16 MiB 上限会抛错。

C# 若写成 `(int)BinaryPrimitives.ReadUInt32BigEndian(header)`，`0xFFFFFFFF` 窄化成 `-1`，**静默绕过上限检查** —— 一个可被构造的越界读取路径。已在 `AgentFrameCodec.PayloadLength` / `Payload` 中改为先按 `uint` 比较再窄化。

> 教训：Mac ↔ C# 移植时，所有「读一个无符号整数再与上限比较」的位置都要显式确认整数宽度。这类偏差编译器不会报，测试不覆盖就静默存在。

### 15.4 需要提前定案的问题

#### ⚠️ 互操作方案变更：放弃 CsWin32，改用手写 `[DllImport]`

原计划用 CsWin32 源码生成 P/Invoke。实测在该环境反复不产出生成代码：

- 生成器对 TFM 敏感 —— `net8.0`（无 `-windows`）时**静默失败**，不报错也不生成
- 增量构建状态易失效，需反复清理 `obj`/`bin` 才能恢复

已改为 `windows/src/Ta.Spike.Overlay/Native.cs` 中的手写声明。

并进一步在 `[LibraryImport]`（源生成）与 `[DllImport]`（经典）之间选了后者：结构体含 BOOL 字段、多个函数返回 BOOL，源生成 P/Invoke 对此要求逐个补封送特性（`SYSLIB1051`），经典封送更直接。

**这仍然满足"运行时零第三方依赖"的原始目标** —— 手写 P/Invoke 同样是编译进程序集的代码，不引入运行时 DLL。代价是签名需人工维护，因此把所有常量与签名集中在 `Native.cs` 一处，并逐条在注释里标注对应的 macOS API，便于对照 review。

> 生产代码若要引入 CsWin32，须先确认团队机器上生成器稳定工作，否则会重复本次的调试成本。

#### 覆盖层验证的完成度（已大幅收窄）

此前只有钩子链路一项可自动验证，焦点与拖拽三项均需人工。现已把覆盖层重构成生产组件 `Ta.Platform/Overlay/SelectionOverlay`，并加入 `--verify` 模式走**完整生命周期**（建窗 → 显示 → 钩子取消 → 销毁），自动验证项从 1 项增加到 4 项：

```
✅ ① 覆盖层被取消（Esc 经钩子生效）
✅ ② 钩子确实收到过按键（窗口未激活仍捕获）
✅ ③ 显示覆盖层后未抢走焦点
✅ ④ 关闭后焦点交还原窗口
```

**这四项是整条覆盖层方案的地基** —— 尤其 ③④ 直接证实了 §14 风险 #4（非激活键盘面板）在 Windows 上可以绕开：靠 `WS_EX_NOACTIVATE` + 低级键盘钩子的组合，而非标准绕法 `AttachThreadInput` + `SetForegroundWindow`。

**仍未覆盖、需人工**：鼠标拖拽产生选区。原因：需要真实的指针按下—移动—松开序列，`SendInput` 虽能合成但会把事件投给光标下的窗口，无法可靠驱动自身覆盖层。

运行方式：`dotnet run --project src/Ta.Spike.Overlay -- --verify`（会在屏幕上短暂出现一次全屏遮罩）。

#### 组件设计要点

| 决策 | 理由 |
|---|---|
| 窗口跑在**独立 STA 线程** | 低级钩子必须装在「有消息循环的线程」上；同时不阻塞调用方线程 |
| 状态用 `ConcurrentDictionary<HWND, State>` 反查 | 替代 `GCHandle` + `GWLP_USERDATA` —— 后者需手动固定/释放，异常路径上易泄漏 |
| 窗口类**静态注册一次** | 复用类名时 `ERROR_CLASS_ALREADY_EXISTS` 属预期，已显式放行 |
| `OverlayDiagnostics` 用记录 + 一个可变标记 | 焦点取样在各生命周期节点用 `with` 派生；钩子回调可能在任意线程，故该字段可变 |

#### ⚠️ 一个此前潜伏的缺陷

`GetModuleHandle` 被声明在 `user32.dll` 下，实际属于 `kernel32.dll`。这在 `--auto` 模式下**不会暴露**（不建窗），直到 `--verify` 真正创建窗口才抛 `EntryPointNotFoundException`。

> 教训：**互操作声明的正确性只在被调用时才验证**。此前"实测通过"的覆盖面比看起来小 —— 那条路径根本没走到窗口创建。新增的 `--verify` 走完整生命周期后，这类问题才会浮出来。后续每加一个平台调用，都应确保有路径真的调到它。

### 15.4 需要提前定案的问题

1. **命名管道名**：`\\.\pipe\Ta\agent-v1`？如何保留 `--socket` 的可配置性（改为管道名而非路径）？
2. **模糊/像素化以哪套为准**：A（CoreImage `CIGaussianBlur`/`CIPixellate` 语义）还是 B（可移植的两级缩放 + box blur）？**建议统一到 B**，并明确两者输出不同。
3. **标注默认色**：`NSColor.systemRed`（动态，暗色 `#FF453A`）还是契约 `.red`（固定 `#FF3B30`）？**建议统一到固定值**。
4. **色彩空间**：建议全程 sRGB，消除 `.deviceRGB` / `.sRGB` 不一致。
5. **Windows 最低版本**：WGC 需 1903+；`SetWindowBand` 需 Win11。建议基线 Win10 21H2，Win11 特性降级。
6. **多屏选区**：是否在本版解决跨屏？（macOS 侧也未完成，1:1 意味着继承该限制）
7. **无障碍滚动**：UIA 在 Chromium/Electron 上不可靠，是否接受保真度下降，或改用 `IUIAutomationScrollPattern` + 混合策略？
8. **IME 策略**：中文输入是最大保真风险，需尽早确定组合串测量与提交时机。
9. **OCR 置信度来源**：WinRT OCR 无逐行置信度，`0.72` / `0.55` 两个阈值如何落地？（合成值？改用本地 PaddleOCR 取真实置信度？）
10. **密钥存储语义**：DPAPI `CurrentUser` 是「按用户」而非「按设备」，与 `kSecAttrAccessibleWhenUnlockedThisDeviceOnly` 不完全等价 —— 是否需要额外加机器绑定？
11. **`.rapidOCR` 死引擎**：保留枚举并补全包，还是直接移除？
12. **架构字符串**：沿用 `"x86_64"` 以兼容现有 catalog/manifest 语义，还是改用 `win-x64`（需同时改生产者与消费者）？
13. **`deliver.save` 路径校验**：从 `hasPrefix("/")` 改为接受 Windows 绝对路径 —— 这是必修项，确认后需同步更新 dsh-ta 与 Skill 文档。
14. **错误文案**：是否保留全部简体中文原文？（CLI golden 测试要求逐字一致；若 Windows 版本地化，至少**错误码必须完全一致**）

---

## 16. 验收清单

### 16.1 必须 1:1 的几何与算法（有测试锁定，可量化验证）

- [ ] `FrozenDisplayCropper.pixelRect`：`(50,25,100×50)` @ `200×150` scale2 → `(100,150,200×100)`
- [ ] `TaperedArrowGeometry.polygon`：7 点、`points[3]==end`、`width=6` 时 `headWidth>20`、短箭头仍保持 `points[2].x < end.x`
- [ ] `AnnotationTextMetrics`：字号阶梯步进、`clamped`、`boundingSize`（`w=max(pt*1.5, ceil+6)`）
- [ ] `AnnotationSelectionGeometry.uniformScale`：角锚定、`0.1` 下限夹取
- [ ] `AnnotationStrokeSmoother`：间距 `max(0.5, maximumSpacing)`
- [ ] `VerticalScrollMatcher`：全部阈值（18 / 0.56 / 0.82 / 0.10 / 0.22 / 0.28 / stride 2 / 5 bands / grad 10 / 下中位数 / confidence = quality*0.8+sep*0.2）
- [ ] `ScrollingImageStitcher`：`makeStrips` 五段顺序、`conservativeStableHeight` 下中位数、`maximumOutputPixels` 抛错、分段 `partHeight`
- [ ] `ViewportMotionDetector`：`isStationary` 是 **AND** 不是 OR；采样 72×360 且 x/y 独立
- [ ] `AccessibilityAutoScrollService`：`pixelDeltas(for: 130) == [-32,-32,-32,-32,-2]`
- [ ] `ClipboardCommitPolicy.shouldCommit`：`jobIsLatest && initial == current`
- [ ] `AgentFrameCodec`：4 字节大端、16 MiB 上限、4 种错误
- [ ] `AnnotationRecipe` 校验：crop 首位且一次、eraser 目标存在、id 去重、各操作最小值
- [ ] `AnnotationColor` 编解码：`#RRGGBB` / `#RRGGBBAA`，`alpha >= 0.999` 时省略 alpha，输出**大写**
- [ ] `highlighter` 解码强制：`lineWidth` 缺失或 == 5 → **16**；`dashed` 强制 **false**；缺 color → `#FFD60A59`
- [ ] `eraser` JSON 键是 **`targetIds`**（小写 d），非空且无重复
- [ ] 配方校验：crop 首位且仅一次、ID 去重、eraser 目标存在、rect `w,h>0`、arrow `start!=end`、magnify factor > 1
- [ ] 会话：undo/redo 上限 100、`apply` 先校验后变更（原子）、`elementCount` **不统计 crop**、已有标注后第二个 crop 被拒
- [ ] `AnnotationRecipe` 夹具 `Tests/Fixtures/annotation-recipe-v1.json`：14 个操作 → `resultingElementIDs.count == 11`
- [ ] `InlineAnnotationLayout.toolbarFrame`：32×30、gap 10、margin 8、下方优先、翻上方、右夹取
- [ ] `PinnedImageLayout.initialSize`：首选逻辑尺寸保留、小图不放大、超限 → 876×584 @ 900×700
- [ ] 窗口吸附：frontmost **且** 最小者胜出、仅 frontmost app、吸附 vs 手动拖拽

### 16.2 行为验收

- [ ] 快捷键按下**立即**变十字光标，且在覆盖层出现**之前**
- [ ] 覆盖层从**冻结帧**呈现，非实时画面
- [ ] 遮罩黑 0.42，无选区时挖透明洞
- [ ] 尺寸读数、8 手柄、`W × H` 格式
- [ ] 点击（拖拽 < 3pt）→ 提交吸附窗口；任何拖拽 → 取消吸附
- [ ] 选区 < 4×4 → 静默重置为无选区（**不是**取消）
- [ ] Esc / 右键 → 取消，不产生文件、不改剪贴板
- [ ] Ta **从不**成为前台应用（用户不离开自己的虚拟桌面）
- [ ] Ta 自身窗口不出现在截图中
- [ ] 光标永不出现在截图里（`showsCursor = false` 处处）
- [ ] 无快门声、无屏幕闪烁
- [ ] `.edit` 路径：覆盖层面板延迟消失，遮罩持续
- [ ] 剪贴板竞态：处理期间用户改了剪贴板 → 提示「结果已就绪，但没有覆盖剪贴板」
- [ ] 识别期间用户已复制其他内容 → 不覆盖
- [ ] 无文字时安全回退为复制 PNG
- [ ] 多模态未配置 → 如实返回配置/不可用提示，**不静默换 provider**
- [ ] `beautify` 入口可见但明确标注为「开发中」，不谎报成功
- [ ] 快捷键冲突 → 自动回滚 + 提示
- [ ] 标注：`⌘Z` / `⇧⌘Z`、`Delete`/`Backspace`、`[`/`]`、滚轮调尺寸（text/mosaicBrush/pen/highlighter）
- [ ] 撤销栈上限 100，且**不用**系统 UndoManager 语义
- [ ] 导出：`.copy` 合成、裁剪区尺寸、**不含**选中手柄与预览、导出前提交待处理文字
- [ ] 保存对话框命名：`AI-Screenshot-yyyy-MM-dd-HH-mm-ss.png`、`AI-Long-Screenshot.png`、`-Part-%0Nd.png`
- [ ] 剪贴板只放 `public.png`（PNG bytes）
- [ ] 钉图：双击关闭、`⌘+滚轮`透明度 `[0.2,1]` 步进 `delta*0.015`、滚轮缩放 `1.06/0.94`、宽 `[120,1200]` 高 `[60,900]`、可伸到菜单栏下、关闭保留 3 个
- [ ] 长截图：300ms（自动）/ 420ms（手动）tick、3 次底部确认、32px 分块 + 14ms 间隔、接缝复查 ±1/±10、低置信 < 0.58 标红
- [ ] OCR：`.accurate` 级别、语言纠正开、自动检测语言、`zh-Hans,en-US` 默认、表格 ≥2 行转 TSV
- [ ] AI：`temperature 0`；**`max_tokens` 按路径区分**（OpenAI 兼容识图 **4096**，Azure/Anthropic/Gemini 识图 **2048**，翻译默认 **4096** / 测试 **512**，DeepSeek-OCR-2 **4096**）；超时识图 60 s / 翻译与 OCR-2 120 s；图最长边识图 2048（JPEG 0.88）/ 翻译与 OCR 2560（PNG）
- [ ] 端点校验：非 HTTPS 拒绝，例外仅 `localhost` / `127.0.0.1` / `::1`
- [ ] 思考抑制：host 后缀 `deepseek.com` / `bigmodel.cn` / `zhipuai.cn` 注入 `thinking:{type:disabled}`
- [ ] 分段翻译：**单次请求带全部文字行**；ID 集合与数量必须完全一致，译文缺失的行被丢弃
- [ ] 翻译源/目标语言是**中文自由文本**，直接插值进英文 prompt
- [ ] 翻译路径 OCR 始终用 Apple Vision 等价引擎，**忽略 `ocrEngine` 设置**，`languages: []`、`mergeWrappedLines: false`
- [ ] 视觉回退阈值 **0.55**（区别于 `isLowConfidence` 的 0.72），且**仅在 `textOnly` 模式可达**
- [ ] DeepSeek-OCR-2：Key 为空时**完全不发送** Authorization 头；拦截 host `api.deepseek.com`；confidence 硬编码 0.85
- [ ] 通用识图命中「无文字」11 条清单 → 用 recoveryPrompt 重试一次
- [ ] OCR 后处理：归一化规则、分块 2.4× / 0.28、表格众数列 ±1、`ContentClassifier` 五步顺序
- [ ] 9 个 Provider 预设的 baseURL 与模型名逐字一致
- [ ] 设置 7 个标签页、780×600；欢迎窗口 900×620（min 820×590）且**无 AI 配置步骤**
- [ ] Agent 桥：`requestId` camelCase、ISO8601 无小数秒、sortedKeys、不转义 `/`、16 MiB 上限、连接重试一次 50ms
- [ ] 只宣告 **16** 个方法；未宣告的 6 个返回 `INVALID_REQUEST`
- [ ] `system.handshake` 由 router 应答（`data:{protocolVersion,server:"Ta"}`），**不被审计**
- [ ] 对端认证失败 → **静默关闭，不返回任何帧**
- [ ] 审计日志：requestID 存 SHA256 前 8 字节，上限 100，目录 0700 / 文件 0600，**不含 params 与 data**
- [ ] 工件：路径白名单 `^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$`，设置**目录** mtime，保留 24 h
- [ ] CLI：全部命令与参数、`--timeout` 默认 10、`--cloud` 校验、**8 种退出码**（0/2/3/4/5/6/7/130）
- [ ] CLI `--output` 是**二次 `deliver.save` 请求**（requestID 后缀 `-save`），不是捕获参数
- [ ] `translate text` 必须字面 `text` 紧邻 `--text`
- [ ] `transform.image` **绕过云端策略**
- [ ] 目录名区分：socket 用 `Ta`，OCR 增强包用 `AI Screenshot`
- [ ] 隐私：智能路由不静默上传；API Key 只在 DPAPI/Keychain
- [ ] 深色/浅色模式下的强调色与文字对比度

### 16.3 明确**不做**（PRD 要求但 macOS 版未实现，1:1 意味着同样不做）

- 放大镜 / 取色器
- 方向键微调、比例锁定修饰键、空格切换窗口模式
- 全屏截图模式、窗口截图模式（交互路径）、定时/延时截图
- 快门声、屏幕闪烁动画
- 跨多屏框选、窗口自动吸附（跨屏）
- AI 美化完整工作流（入口已预留）
- 历史记录 / 搜索
- 捏合缩放手势（钉图与标注均无）

---

## 附录 A：文件索引（按移植优先级）

### P0 — 纯逻辑，可直译，有测试锁定
```
Sources/AIScreenshotCore/Models/CaptureModels.swift
Sources/AIScreenshotCore/Clipboard/ClipboardCommitPolicy.swift
Sources/AIScreenshotCore/LongCapture/VerticalScrollMatcher.swift
Sources/AIScreenshotCore/LongCapture/ScrollingImageStitcher.swift
Sources/AIScreenshotCore/LongCapture/ViewportMotionDetector.swift
Sources/AIScreenshotCore/LongCapture/AutoScrollProgressTracker.swift
Sources/AIScreenshotCore/OCR/OCRDocumentLayoutAnalyzer.swift
Sources/AIScreenshotCore/OCR/ContentClassifier.swift
Sources/TaAgentContracts/AnnotationRecipe.swift
Sources/TaAgentContracts/AgentEnvelope.swift
Sources/TaAgentContracts/AgentFraming.swift
Sources/TaAgentContracts/AgentMethods.swift
Sources/AIScreenshotApp/Editor/AnnotationEditorWindowController.swift   (几何部分 :298-470)
Sources/AIScreenshotApp/Agent/TaAgentAnnotationRenderer.swift
```

### P1 — 平台捕获层（需 WGC 重写）
```
Sources/AIScreenshotApp/Capture/ScreenCaptureService.swift
Sources/AIScreenshotApp/Capture/CaptureCoordinator.swift
Sources/AIScreenshotApp/Capture/SelectionOverlayView.swift
Sources/AIScreenshotApp/Capture/SelectionOverlayController.swift
Sources/AIScreenshotApp/Capture/WindowSnapService.swift
Sources/AIScreenshotApp/System/GlobalHotKeyManager.swift
Sources/AIScreenshotApp/System/HotKeyPreferences.swift
Sources/AIScreenshotApp/System/ScreenCapturePermissionService.swift
```

### P2 — 编辑与渲染 UI
```
Sources/AIScreenshotApp/Editor/AnnotationEditorWindowController.swift  (交互部分)
Sources/AIScreenshotApp/Editor/InlineAnnotationController.swift
Sources/AIScreenshotApp/UI/PinnedImageWindowController.swift
Sources/AIScreenshotApp/System/ImageExportService.swift
Sources/AIScreenshotCore/Clipboard/ (ClipboardService)
```

### P3 — 识别与 AI
```
Sources/AIScreenshotCore/OCR/VisionOCRService.swift
Sources/AIScreenshotCore/Recognition/*.swift  (4 个 Provider 客户端)
Sources/AIScreenshotApp/Recognition/*.swift
Sources/AIScreenshotApp/System/AIProviderProfileStore.swift
Sources/AIScreenshotApp/System/KeychainSecretStore.swift
```

### P4 — 长截图会话
```
Sources/AIScreenshotApp/Capture/ScrollingCaptureSessionController.swift
Sources/AIScreenshotApp/Capture/ScrollingSeamReviewWindowController.swift
Sources/AIScreenshotApp/System/AccessibilityAutoScrollService.swift
```

### P5 — Agent 与 CLI
```
Sources/TaAgentClient/TaBridgeClient.swift + TaAppLauncher.swift
Sources/AIScreenshotApp/Agent/Bridge/TaAgentBridgeServer.swift
Sources/AIScreenshotApp/Agent/TaAgentCapabilityService.swift
Sources/AIScreenshotApp/Agent/TaAgentPrivacyPolicy.swift
Sources/AIScreenshotApp/Agent/TaAgentAuditLog.swift
Sources/TaCLI/CLIParser.swift + CLICommands.swift + CLIOutput.swift
```

## 附录 B：测试文件清单（作为验收门）

`Tests/` 下 44 个文件 / 190 个测试。几何与算法关键测试：
`AnnotationDrawingTests`、`AnnotationEditorToolbarTests`、`InlineAnnotationLayoutTests`、
`PinnedImageInteractionTests`、`WindowSnapAndSelectionTests`、`TaAgentAnnotationRendererTests`、
`ScrollingImageStitcherTests`、`VerticalScrollMatcherTests`、`ViewportMotionDetectorTests`、
`AutoScrollProgressTrackerTests`、`ScrollingCaptureSessionControllerTests`、
`AnnotationRecipeTests`、`AgentEnvelopeTests`、`CLIParserTests`、`CLIGoldenOutputTests`、
`OptionalOCRPackManagerTests`、`AIProviderProfileStoreTests`、`HotKeyPreferencesTests`、
`TaAgentCapabilityServiceTests`、`TaAgentPrivacyPolicyTests`

> 建议：把这 20 个测试文件作为 Windows 版的**移植验收门** —— 同样的输入必须给出同样的输出。
