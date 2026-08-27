<div align="center">

<img src="./Resources/Brand/Ta-AppIcon.png" width="128" alt="拓 Ta Logo">

# 拓 · Ta

### 一千多年前，纸墨拓下碑文。今天，AI 拓下屏幕上的信息。

[![License: MIT](https://img.shields.io/badge/License-MIT-D6402F.svg)](./LICENSE)
[![Platform: macOS 14+](https://img.shields.io/badge/macOS-14%2B-1A1A1A.svg)](https://www.apple.com/macos/)
[![Swift: 6.2](https://img.shields.io/badge/Swift-6.2-F05138.svg)](https://www.swift.org/)
[![Release: v1.0.1](https://img.shields.io/badge/Release-v1.0.1-C98B2E.svg)](https://github.com/kangarooking/Ta/releases/tag/v1.0.1)

**AI 原生截图工具：截图、取字、AI 识图、翻译、长截图、钉图与标注，一步完成。**

当前公开正式版支持 **macOS**。Windows 原生 Alpha 已开始开发，源码与构建说明见 [`windows/`](./windows/README.md)，尚未作为正式版本发布。

[简体中文](./README.md) · [English](./README.en.md) · [日本語](./README.ja.md)

</div>

## Change Log

### 2026-08-27 19:25 — 固定 Windows 启动窗口尺寸

- 修改：启动窗口固定为 `1220×793`，取消默认最大化，在小屏幕上自动缩小到工作区并保持居中。
- 保留：左右栏自适应滚动、高 DPI 支持、结果页完整预览和关闭后托盘驻留行为。

### 2026-08-27 18:56 — Windows 原生 Alpha 与轻量绿色版

- 新增：`.NET 8 + WPF + Win32` Windows 独立工程，支持区域、全屏、重复区域和 FastStone 式活动窗口/对象捕获。
- 新增：可自定义全局快捷键，区域截图默认 `Shift+A`，活动窗口截图默认 `Shift+W`，单项冲突不影响其他快捷键。
- 新增：截图自动保存到本机临时目录并在剪贴板排他锁内复制完整文件路径。
- 新增：结果窗口全屏适配、完整图片预览、AI 识别文字按钮、识别后自动复制文字与 `.vision.txt` 保存。
- 新增：可编辑 Base URL、API Key、模型名称和识图任务指令；API Key 使用 Windows Credential Manager 加密存储。
- 新增：原作者红色“拓”印章多尺寸 Windows ICO、单实例托盘、私密模式和可配置界面显隐策略。
- 优化：框架依赖绿色单文件约 534KB，关闭主界面后主动归还工作集；发布脚本自动检测并安装依赖、测试和版本化打包。
- 安全：限制截图/贴图/视觉请求内存预算，禁止远程自动保存目录，限制云端图片与响应体大小，修复剪贴板竞态与凭据回滚。

---

![拓主界面](./docs/brand/Ta-home-preview.png)

## 一千多年前，中国人就有了自己的“截图”

在没有相机、复印机和现代印刷技术的年代，人们遇到过一个很实际的问题：石碑上的字那么好，怎样才能把它带走？

他们把纸覆在碑上，再用墨包轻轻扑打。纸揭下来，文字便离开石头，跟着人回家。这门手艺叫作**拓印**，已经流传千余年。它像一种古老的截图：看见什么，就把什么留住。

「拓」这个字本身也在讲这件事：`扌 + 石`，一只手按在石头上。中文名为「拓」，读作 `tà`；英文名为 **Ta**。

今天，石碑变成了屏幕。屏幕上的信息更多，也消失得更快。Ta 做的仍是同一件事：框住它，拓下来——文字可以复制、翻译，图片可以标注、钉在眼前；截一张长图，就像把整通碑从头拓到尾；AI 帮你理解内容，就像随身带着一位金石学家。

> 仓颉造字，拓印传字。字被造出来只是开始，被留住、被读懂、被带走，才算完成。

## 我为什么做 Ta

截图工具是我几乎每天都在使用的软件。市面上的选择很多，有免费的，也有收费的；但用了这么多之后，我始终没有找到一个能满足全部需求的工具：取字、翻译、长截图、钉图、标注和图片美化，往往分散在不同应用和不同操作链路里。

所以，我决定自己做一个。

我也发现，大多数截图软件只是附加了一个 OCR 或 AI 按钮，还没有真正让 AI 深度参与截图后的工作。Ta 希望持续结合 AI，在满足我自己真实需求的同时，也和大家一起把截图这件每天都要做的小事，变得更简单、更聪明。

## 什么是“AI 原生截图工具”

AI 原生，不是给传统截图软件外挂一个聊天框，而是让 AI 从截图完成的那一刻起就参与工作：

- **拓下来**：普通截图、窗口内容和滚动长图，都能快速捕获；
- **读出来**：使用本地 OCR 或多模态模型提取文字、表格、公式与代码；
- **讲明白**：直接翻译、理解和结构化截图中的信息；
- **留在手边**：一键复制、钉图、标注、美化、保存或继续处理；
- **尊重边界**：本地识别优先，云端处理前明确提示，API Key 保存在 macOS Keychain。

## 它解决了什么问题

- **截图后还要二次处理**——取字、翻译、复制、保存和标注集中在同一条操作链路。
- **长截图不稳定**——支持手动或自动滚动、重复帧过滤、固定区域消除、接缝检查与人工修正。
- **OCR 方案难以取舍**——Apple Vision、PaddleOCR 与远程视觉模型可以按速度、结构和隐私要求切换。
- **截图标注效率低**——提供接近 Snipaste 的原位标注、对象移动缩放、马赛克涂抹和钉图体验。
- **云端识图缺少边界**——默认本地处理；需要上传时明确提示，并把 API Key 保存在 macOS Keychain。

## 经典用法

下面的图片均来自 Ta 当前版本的实际运行界面。

<table>
  <tr>
    <td width="50%" valign="top">
      <strong>框选一次，选择下一步</strong><br><br>
      <img src="./docs/showcase/02-capture-toolbar.png" alt="Ta 通用截图操作栏">
      <br>截图后直接取字、AI 识图、翻译、复制、钉图、标注、美化或保存。
    </td>
    <td width="50%" valign="top">
      <strong>配置自己的 AI 模型</strong><br><br>
      <img src="./docs/showcase/05-ai-model-settings.png" alt="Ta AI 模型设置">
      <br>支持多种 Provider 协议，API Key 只保存在 macOS Keychain。
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <strong>原位标注</strong><br><br>
      <img src="./docs/showcase/03-annotation-tools.png" alt="Ta 原位标注工具栏">
      <br>箭头、文字、高亮、两种马赛克和对象直接缩放，都在截图原位置完成。
    </td>
    <td width="50%" valign="top">
      <strong>把参考内容钉在眼前</strong><br><br>
      <img src="./docs/showcase/04-pin-image.png" alt="Ta 钉图案例">
      <br>钉图保持置顶，可移动、缩放、调整透明度，也可双击关闭。
    </td>
  </tr>
</table>

## 它是怎么工作的

Ta 使用一条原生 macOS 截图链路：

```text
全局快捷键
    ↓
框选屏幕区域
    ↓
ScreenCaptureKit 捕获（排除 Ta 自身窗口）
    ↓
┌───────────────┬────────────────────┐
│ 本地 OCR      │ 多模态视觉模型       │
│ Apple Vision  │ OpenAI-compatible  │
│ PaddleOCR     │ Claude / Gemini    │
└───────────────┴────────────────────┘
    ↓
复制 · 翻译 · 钉图 · 标注 · 长图 · 保存
```

识别期间如果用户已经复制了其他内容，Ta 不会覆盖新的剪贴板内容。没有识别到文字时，可以安全回退为复制 PNG。

## 核心功能

### 截图与快捷操作

- 通用截图操作栏：框选后选择取字、AI 识图、翻译、复制、钉图、标注、美化或保存。
- 极速取字、复制图片、截图翻译、截图钉图与滚动长截图均有独立全局快捷键。
- 所有快捷键都可以在设置中录制、检查冲突并恢复默认值。
- 框选过程中可用鼠标右键或 `Escape` 退出，不生成文件也不修改剪贴板。
- 截图时自动隐藏 Ta 的主界面，不抢焦点、不把应用自身截进去。

### OCR 与 AI 识图

- **Apple Vision**：默认的本地 OCR，支持中英文与常见文本布局。
- **PaddleOCR 增强包**：Apple Silicon 离线运行，支持一键安装、更新、校验、预热和常驻模型复用。
- **DeepSeek-OCR-2**：连接用户自行部署的 vLLM、SGLang 或兼容视觉服务，不在 Mac 上静默下载大型权重。
- **多模态模型**：支持 OpenAI-compatible、Azure OpenAI、Anthropic Claude 与 Google Gemini 协议。
- 任务模板包括精确取字、代码解释、Markdown/CSV 表格、LaTeX 公式与通用识图。
- 支持 OCR、多模态和“本地优先、低置信度再确认上传”的智能路由。

### 截图翻译

- 源语言与目标语言可自定义，默认自动检测并翻译为简体中文。
- 直接截图翻译后把译文复制到剪贴板。
- 截图工具栏支持纯文字、原图文字替换与原图下方双语对照。
- 图片文字定位和结果合成在本地完成，只把需要翻译的文字发送给配置的模型。

### AI 美化（开发中）

- 面向公众号、社交媒体和产品文档，自动补齐留白、圆角、阴影、背景与常用版式。
- 计划支持智能标注、隐私信息遮挡、多尺寸导出，以及根据截图内容生成匹配的背景或装饰元素。
- 当前版本已经预留“美化”入口，完整的 AI 美化工作流仍在开发中，不会把尚未完成的能力标为可用。

### 滚动长截图

- 支持浏览器、聊天窗口和常见桌面应用的手动/自动滚动捕获。
- 自动匹配相邻帧、过滤重复画面并识别滚动方向。
- 检测并消除固定标题栏、底栏和输入框。
- 完成前提供接缝检查，可按 `±1` / `±10 px` 人工修正。
- 支持超长图片分段，降低内存和导出压力。

### 标注与钉图

- 原位半透明遮罩标注，截图保持在原位置。
- 矩形、椭圆、箭头、画笔、高亮、文字、编号、马赛克、模糊、橡皮和局部放大。
- 标注对象可以直接选择、移动、缩放、旋转和重新编辑。
- 马赛克支持框选与画笔涂抹两种方式。
- 钉图支持拖动、缩放、透明度、旋转、翻转、滤镜、裁剪、鼠标穿透、分组、隐藏/恢复与双击关闭。
- 可从截图、剪贴板图片、文字、HTML 或文件生成钉图。

## 快速开始

### 环境要求

- macOS 14 或更高版本
- Apple Silicon 或 Intel Mac（PaddleOCR 发行增强包目前面向 Apple Silicon）
- Xcode 26，或兼容 Swift 6.2 的工具链

### 下载安装包

[**下载 Ta v1.0.1（macOS 通用版 DMG）**](https://github.com/kangarooking/Ta/releases/latest/download/Ta-1.0.1-macOS-universal.dmg)

安装包同时支持 Apple Silicon 与 Intel Mac。打开 DMG 后，把「拓」拖入 `Applications` 即可。当前版本尚未完成 Apple notarization；首次启动请在 Finder 中按住 Control 点击「拓」，选择“打开”，再确认一次。

### 从源码构建

```bash
git clone https://github.com/kangarooking/Ta.git
cd Ta
swift test
./scripts/build-app.sh
open "artifacts/拓.app"
```

首次运行需要允许“屏幕与系统音频录制”权限。只有使用自动滚动长截图时，才需要额外开启“辅助功能”权限。

### 默认快捷键

| 功能 | 快捷键 |
|------|--------|
| 极速取字 | `⇧⌥⌘1` |
| 通用截图 | `⇧⌥⌘2` |
| 复制图片 | `⇧⌥⌘3` |
| 截图并钉住 | `⇧⌥⌘4` |
| 滚动长截图 | `⇧⌥⌘5` |
| 截图翻译 | `⇧⌥⌘6` |

进入“设置 → 快捷键”可以重新录制任意组合键。冲突快捷键会被拒绝或自动回滚。

## 让 Agent 调用 Ta

Ta 现在也可以作为 Agent 的视觉输入层，以三种形式提供能力：

- **Ta Agent Skill**：教支持 Agent Skills 的 Agent 正确组合截图、OCR、识图和翻译流程；
- **`ta` CLI**：提供稳定的 JSON 命令，可用于 Shell、脚本和通用 Agent；
- **`dsh-ta`**：真正的 DeepSeek Harness 原生 Cordis Plugin + Bundle，直接注册截图与理解工具。

它们共同调用由「拓.app」托管的本机 Bridge。普通 Agent 截图不会弹出拓、抢焦点、移动鼠标或发送键盘事件；权限、模型和 API Key 仍由拓统一管理。

一条命令同时安装或更新 `ta` CLI 与 Ta Agent Skill：

```bash
curl -fsSL --retry 3 --retry-all-errors --retry-delay 1 https://github.com/kangarooking/Ta/releases/latest/download/install.sh | bash
```

安装器会校验 SHA-256，把 CLI 安装到 `~/.local/bin/ta`，并把 Skill 安装到 Codex 与通用 Agent Skills 目录。安装后重新启动 Agent，再检查连接：

```bash
ta status --json
ta capture frontmost --json
ta ocr last --json
```

在“设置 → Agent”可以关闭自动化、禁止云端、配置隐私 App 黑名单、清理缓存并查看不包含识别正文的最近调用记录。安装 CLI、Skill 和 DeepSeek Harness 插件的完整步骤见 [Agent 集成指南](./docs/agent-integration.md)。

## 隐私与安全

- 普通截图与 Apple Vision OCR 始终在本机完成。
- PaddleOCR 增强包安装后在本机离线运行。
- 只有主动选择远程 OCR、多模态识图或翻译时，选区或文字才会发送到用户配置的服务。
- 低置信度智能路由不会静默上传，必须由用户再次确认。
- API Key 只保存在 macOS Keychain，不写入偏好设置、日志或仓库。
- Ta 不持续录屏，只读取用户主动框选的区域。
- Agent Bridge 只监听当前用户可访问的本机 Unix Socket；审计不保存请求参数、识别正文或图片数据。

## 工程结构

```text
Ta/
├── README.md / README.en.md / README.ja.md
├── Package.swift
├── Resources/                 图标、Info.plist 与品牌资源
├── Sources/
│   ├── AIScreenshotCore/
│   │   ├── OCR/               Vision OCR、布局与内容分类
│   │   ├── LongCapture/       位移匹配、拼接与进度检测
│   │   ├── Recognition/       OCR/视觉/翻译 Provider 客户端
│   │   └── Clipboard/         剪贴板安全提交策略
│   ├── AIScreenshotApp/
│   │   ├── Capture/           框选、捕获与长截图会话
│   │   ├── Editor/            标注编辑器
│   │   ├── Recognition/       OCR 增强包与多模态路由
│   │   ├── System/            快捷键、权限、Keychain、剪贴板
│   │   └── UI/                主界面、菜单栏、设置、钉图与结果栏
│   ├── TaAgentContracts/      Bridge 协议
│   ├── TaAgentClient/         本机 Bridge 客户端
│   └── TaCLI/                 ta CLI
├── Integrations/              Agent Skill 与 DeepSeek Harness 原生插件
├── Tests/                     Core 与 App 测试
├── ocr-packs/paddleocr/       可选 PaddleOCR 增强包构建定义
├── scripts/                   构建、运行和增强包脚本
├── windows/                   .NET 8 + WPF Windows 原生 Alpha
└── docs/                      PRD、研究、验证记录与实现计划
```

## 当前状态

Ta v1.0.1 是当前公开版本，提供同时支持 Apple Silicon 与 Intel Mac 的通用 DMG 和 ZIP。当前发布包尚未完成 Apple notarization，因此首次启动需要在 Finder 中按住 Control 点击 App 并选择“打开”。

已知限制：

- 当前区域框选以鼠标所在显示器为主，跨屏框选与窗口自动吸附尚未完成。
- 长截图已具备自动拼接与接缝修正，但持续动画、视频、半透明浮层和大幅重排页面仍可能需要人工调整。
- 图片翻译使用本地遮盖和重绘；复杂纹理、渐变、阴影、竖排文字和极密集排版可能留下覆盖痕迹。
- PaddleOCR 公开仓库包含增强包构建定义，不提交体积较大的本地构建产物。
- v1.0.1 使用 Apple Development 签名；Developer ID 签名和 Apple notarization 仍在推进中。

## 文档

- [产品需求文档（中文）](./AI截图软件-产品需求文档-PRD-v1.0.md)
- [市场与用户痛点调研（中文）](./AI截图软件市场与用户痛点调研.md)
- [Alpha 验证记录](./docs/alpha-verification.md)
- [长截图验收矩阵](./docs/long-capture-acceptance-matrix.md)
- [PaddleOCR 增强包规范](./docs/ocr-enhancement-pack-spec.md)
- [Agent Skill、CLI 与 DeepSeek Harness 集成指南](./docs/agent-integration.md)

## 展望：让截图成为 Agent 的眼睛

Ta 不只想成为一个更全的截图工具。未来，我们希望逐步融入更多 **Agent 能力**，让截图从一张静态图片，变成 AI 理解屏幕和执行任务的入口。

例如，Agent 可以在你确认后：

- 识别截图中的任务、日期、链接和表格，并整理成待办、笔记或结构化数据；
- 理解界面状态，给出下一步操作建议，或串联翻译、标注、美化和导出流程；
- 自动发现并遮挡手机号、邮箱、头像等敏感信息；
- 把长对话、代码报错、产品页面或研究材料转成可继续编辑的工作成果；
- 记住你常用的截图处理方式，把重复操作变成可复用的个人工作流。

Agent 能力仍会坚持明确授权、过程可见、结果可撤销。Ta 希望做的不是替你接管屏幕，而是让你更快地把屏幕上的信息带走、读懂，并继续使用。

## Roadmap

- [x] 原生截图、OCR、复制与自定义快捷键
- [x] 长截图、接缝检查与自动滚动
- [x] 原位标注、马赛克画笔与钉图
- [x] 截图翻译与多 Provider 模型配置
- [x] PaddleOCR 可选离线增强包协议
- [x] Agent Skill、`ta` CLI 与 DeepSeek Harness 原生插件
- [ ] 多显示器跨屏框选与窗口吸附
- [ ] 公众号截图模板与参数化美化
- [ ] AI 美化、智能隐私遮挡与多尺寸生成
- [ ] 历史记录、搜索与结果重新复制
- [ ] 可确认、可撤销的截图 Agent 工作流
- [ ] Windows 版本（Alpha 已完成区域/全屏截图、自动保存路径、可配置 AI 识图、快捷键与轻量托盘主链路）
- [x] macOS 通用 DMG、ZIP 与校验文件
- [ ] Developer ID 签名与 Apple notarization

## 免费、开源，也希望和大家一起做

Ta 采用 [MIT 协议](./LICENSE) 免费开源。任何人都可以下载源码、构建、使用、修改和分发。

如果你在截图时遇到过 Ta 尚未解决的问题，欢迎提交 Issue；如果你愿意一起完善它，也欢迎发送 Pull Request。开始修改前请阅读 [CONTRIBUTING.md](./CONTRIBUTING.md)，并尽量为行为变化补充测试或验收步骤。

如果 Ta 帮你少切换一次应用、少做一步重复操作，欢迎点一个 **Star**。这就是对项目最直接的支持。

## 关于作者

**袋鼠帝 kangarooking** — AI 博主，独立开发者。AI Top 公众号「袋鼠帝 AI 客栈」主理人

<img src="https://raw.githubusercontent.com/kangarooking/cangjie-skill/main/assets/wechat-personal-qr.jpg" width="220" alt="袋鼠帝个人微信二维码">

火山引擎领航 KOL，百度千帆开发者大使，GLM 布道师，Trae 昆明第一任 Fellow

| 平台 | 链接 |
|------|------|
| 𝕏 Twitter（袋鼠帝） | https://x.com/aikangarooking |
| 小红书（袋鼠帝） | https://xhslink.com/m/5YejKvIDBbL |
| 抖音（袋鼠帝） | https://v.douyin.com/hYpsjphuuKc |
| 公众号 | 袋鼠帝 AI 客栈 |
| 视频号 | AI 袋鼠帝 |

微信公众号「袋鼠帝 AI 客栈」二维码：

![](https://raw.githubusercontent.com/kangarooking/cangjie-skill/main/assets/kangarooking-gzh.png)

如果你也想交流 Ta 的使用体验、反馈截图痛点，或一起参与 AI 原生截图工具的开发，欢迎加入 Ta 企微交流群：

<img src="https://raw.githubusercontent.com/kangarooking/Ta/main/assets/wecom-ta-group-qr.png" width="220" alt="Ta 企微交流群二维码">

## ⭐ Star History

如果 Ta 对你有帮助，欢迎点一个 Star。

<a href="https://www.star-history.com/?repos=kangarooking%2FTa&type=date&legend=top-left">
 <picture>
   <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/chart?repos=kangarooking/Ta&type=date&legend=top-left" />
   <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/chart?repos=kangarooking/Ta&type=date&legend=top-left" />
   <img alt="Ta Star History Chart" src="https://api.star-history.com/chart?repos=kangarooking/Ta&type=date&legend=top-left" />
 </picture>
</a>

## License

MIT，详见 [LICENSE](./LICENSE)。
