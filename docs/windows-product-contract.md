# Ta for Windows：产品能力合同

## 目标

Windows 版本不是 macOS 客户端的缩水替代品。它以 macOS v1 的截图、模型设置、翻译、标注与 Agent 能力为最低基线，并优先采用 Windows 原生或跨平台方案补足多配置、多显示器和自动化能力。

## 不可破坏边界

- 现有 macOS Swift 包、DMG 构建与 Agent Bridge 行为保持不变。
- Windows 配置保存在当前 Windows 用户的应用数据目录；API Key 仅以 Windows DPAPI 加密形式持久化。
- 所有 AI 上传仅针对用户主动框选的图像。关闭云端 AI 后，不得发起任何 Provider 请求。
- 设置页的开关必须已接入行为；未实现的能力必须显示为计划状态，不能伪装为可用。

## 第一阶段：完整设置与 Provider 基座

| 设置项 | Windows 行为 | 验收 |
| --- | --- | --- |
| AI 模型 | 多个命名配置、预设、Key 加密、连接测试、活动配置与翻译配置分离 | 截图取字、识图、翻译均按指定配置请求 |
| 快捷键 | 六个可编辑全局快捷键，冲突检测与原子回滚 | 修改后马上生效；失败时保留旧配置 |
| 识别 | 默认任务模板、云端 AI 总开关、活动视觉配置选择 | 工具栏“识图/取字”读取这些设置 |
| 翻译 | 源/目标语言、翻译模式、专属 Provider 选择 | 工具栏“翻译”读取这些设置 |
| 权限与常规 | 截图状态、启动项、保存行为、数据目录提示 | 所有可切换项真实调用 Windows/Electron 能力 |
| Agent | 自动化与云端边界、审计保留策略 | 设置被持久化并在后续 Windows Agent Bridge 中强制执行 |

## 后续阶段

1. Windows OCR（Windows.Media.Ocr 或可选 PaddleOCR 包）、区域多显示器选择与窗口吸附。
2. UI Automation 驱动的自动滚动长截图、接缝检查和人工修正。
3. Windows named-pipe Agent Bridge、CLI 与审计日志，兼容现有 Agent 协议语义。
4. 历史记录、可搜索结果库、可撤销操作与 Windows Installer 自动更新。
