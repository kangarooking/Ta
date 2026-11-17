# Ta v1.1.0 · 长截图重构、Agent 安全加固与全面修复

## 长截图（重写）

- **手动跟拍**：直接滚动内容，拓以高帧率连续跟拍，上下滚动都能接上，不需要辅助功能权限。
- **自动滚动可切换方向**：聊天记录（微信、Codex、WorkBuddy 等）可选“向上”，把历史消息接到顶部。
- **更可靠的自动滚动**：鼠标离开截图区域时自动暂停并提示；Electron 应用（Codex、VS Code、飞书等）自动开启滚动区识别；优先锁定覆盖选区的主滚动区，不被内嵌代码块或输入框截胡。
- **不再误报完成**：没有检测到滚动时会明确提示，而不是显示“采集完成”；到底后等画面稳定再结束，懒加载页面会继续采集。
- **更精确的接缝**：在原始分辨率上精修到像素行，保留最后一小段内容。
- **画面更干净**：长截图浮条与结果提示不会再被截进图里。
- **超长图**：Retina 屏上内容过长时优先按 1x 合并为一张完整图片，而不是只复制第一段。

## 截图、标注与钉图

- 可以在已有矩形、椭圆内部继续画箭头、写字、加序号、打码。
- 撤销/重做更准确；箭头支持虚线；屏幕显示与导出的文字大小一致。
- 钉图缩放始终等比例；旋转、镜像后复制和裁剪结果正确。
- **美化**正式可用：渐变背景、圆角、阴影，提供 3 种预设。
- **截图历史**：开启后保存到 `~/Pictures/Ta/History`，保留最近 200 张。
- AI 识图、翻译处理中可点“取消”，或直接再次截图。
- 剪贴板在识别期间变化时，提供“仍然复制”。
- Retina 截图粘贴到备忘录、Pages、Keynote 时尺寸正确。
- 快捷键注册失败时会指出具体是哪一个，改设置后立即生效。
- 深色模式下设置、欢迎页与菜单栏弹窗文字可读。

## OCR、AI 与翻译

- 表格识别不再吞掉正文；二维码与文字同时返回。
- AI 结果被长度上限截断时会明确提示。
- 图片覆盖翻译支持密集截图（分批翻译），翻译底色与原图背景一致。
- 网络限流或服务端错误时自动重试；兼容更新的 Claude 与 OpenAI 模型参数。
- 支持本机与局域网的 `http` 模型地址（Ollama、LM Studio、自托管 DeepSeek-OCR-2），本地模型可不填 API Key。
- 文字翻译需要上传截图到云端时，会先征求确认。
- PaddleOCR 增强包支持覆盖翻译，识别可随时取消；导入增强包更安全且不冻结界面。

## Agent 与 CLI

- 用户设置的“禁止上传云端”不再能被 Agent 参数覆盖。
- 默认隐私黑名单：密码管理器等应用的窗口不会被 Agent 静默截图。
- 保存路径更安全：只写 PNG，拒绝隐藏路径、系统目录和覆盖已有文件（除非显式 `--overwrite`）。
- 新增 `ta capture app "<名称>"`、`--max-size`（为视觉模型降采样）与 `ocr --boxes`（带坐标的逐行结果）。
- 慢请求不再被误报为连接失败；新增 `REQUEST_TIMEOUT`；`--json` 下错误也输出 JSON。
- Agent 临时截图会按期清理；审计日志更完整。
- DeepSeek Harness 插件默认跟随 App 的云端设置（`defaultCloud: inherit`）。

## 下载

- `Ta-1.1.0-macOS-universal.dmg`：推荐安装包，支持 Apple Silicon 与 Intel Mac。
- `Ta-1.1.0-macOS-universal.zip`：备用 App 压缩包。
- `Ta-CLI-1.1.0-macOS-universal.tar.gz`：Agent 使用的通用 CLI。
- `Ta-Agent-Skill-1.1.0.zip`：适用于 Codex、Claude Code 等 Agent 的 Skill。
- `dsh-ta-1.1.0.tgz`：DeepSeek Harness 插件包。
- `install.sh`：CLI 与 Skill 一键安装脚本。
- `SHA256SUMS.txt`：所有发布资产的 SHA-256 校验值。

一键安装 CLI 与 Skill：

```bash
curl -fsSL --retry 3 --retry-all-errors --retry-delay 1 https://github.com/kangarooking/Ta/releases/latest/download/install.sh | bash
```

## macOS 提示

当前发布包使用 Apple Development 签名，尚未完成 Developer ID 公证。首次启动时，请在 Finder 中按住 Control 点击「拓」，选择“打开”，并再次确认。
