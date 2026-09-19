# 拓 · Ta —— Windows 版

「拓」的 Windows 桌面实现（.NET 8 + WPF/Win32），与 macOS 版**逐文件对照**移植：
截图、取字（OCR）、AI 识图、翻译、滚动长截图、贴图、标注与美化。

## 环境要求

- Windows 10 2004 及以上 / Windows 11
  （依赖 `Windows.Graphics.Capture` 与 `SetWindowDisplayAffinity` 的 `WDA_EXCLUDEFROMCAPTURE`）
- .NET 8 SDK

## 构建

```bash
cd windows
dotnet build Ta.sln -c Release
```

> **exe 图标**：`windows/assets/app.ico` 是二进制产物，仓库不提交它。需要图标时运行
> `python windows/tools/make_appicon.py`（依赖 Pillow）从 `Resources/Brand/Ta-AppIcon.png` 生成。
> 不生成也能正常构建，只是 exe 用系统默认图标。

## 运行

```bash
# 常驻进程：托盘图标 + 截图/取字/长图全链路（单实例，参数经命名管道转发）
src/Ta.Shell/bin/Release/net8.0-windows10.0.19041.0/Ta.Shell.exe

# 面板与设置窗口（独立的 WPF 进程，点「开始拓取」拉起的仍是 Ta.Shell）
src/Ta.Settings/bin/Release/net8.0-windows10.0.19041.0/Ta.Settings.exe
```

## 测试

```bash
dotnet test
```

## 目录结构

| 目录 | 对应 macOS 侧 | 职责 |
| --- | --- | --- |
| `src/Ta.Core` | `Sources/TaCore` | 领域模型与契约（纯逻辑，无 Win32 依赖） |
| `src/Ta.Capture` | `ScreenCaptureService` | `Windows.Graphics.Capture` 冻结整屏、显示器/窗口枚举 |
| `src/Ta.Platform` | `CaptureOverlay*` | 框选覆盖层、结果条、托盘互操作 |
| `src/Ta.OCR` | `OCRService` | `Windows.Media.Ocr` 本地识别（深色界面/贴边文字有专门预处理）+ 云端引擎 |
| `src/Ta.AI` | `AIService` | AI 识图与美化（OpenAI 兼容协议，模型可在设置里配） |
| `src/Ta.Translate` | `TranslateService` | 截图翻译 |
| `src/Ta.LongSession` | `LongCapture*` | 滚动长图：视口运动匹配 + 接缝拼接 |
| `src/Ta.Pinning` | `PinService` | 贴图（钉在屏幕） |
| `src/Ta.Annotation` | `Annotation*` | 标注与美化 |
| `src/Ta.HotKeys` | `HotKey*` | 全局快捷键（Carbon 键码 → Windows 虚拟键映射） |
| `src/Ta.AgentBridge` | `TaAgentCaptureService` | 给 Agent 用的本地桥（`capture.display` 等方法） |
| `src/Ta.Shell` | `AppModel` | 常驻进程：把上面全部组装成完整链路 |
| `src/Ta.Settings` | `SettingsView*` | 面板与设置窗口（纯 C# 代码构造，无 XAML） |
| `tests/` | `Tests/` | 各层单元测试 |

## 说明

- 移植期的逐项对照记录见 [`docs/windows-port.md`](../docs/windows-port.md)。
- 截图结果为**显示器物理像素**（不乘 DPI 缩放），保证 1:1 不糊、无黑边；
  框选时的冻结帧如实反映屏幕（Ta 自己的窗口不会被抹掉）。
