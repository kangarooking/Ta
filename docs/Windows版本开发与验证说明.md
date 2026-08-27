# Windows 版本开发与验证说明

## 实施结论

Windows 版采用 `.NET 8 + WPF + Win32` 独立实现，目录为 `windows/`。现有 macOS Swift Package、AppKit 界面、ScreenCaptureKit、Vision OCR 和 Keychain 代码保持不变。

选择独立平台实现的原因来自现有 ADR-001。截图、窗口、快捷键、密钥和权限管理都属于强平台能力，直接把 AppKit 代码加条件编译会形成大量分支并降低两个平台的可靠性。Windows 工程只复用产品语义、任务模型和协议设计。

## 已实现数据流

```text
按钮或全局快捷键
    ↓
隐藏拓自身窗口
    ↓
选择物理像素区域或读取虚拟桌面
    ↓
Win32 BitBlt 本地捕获
    ↓
自动保存 PNG → 在 Win32 剪贴板锁内写入完整文件路径
    ↓
普通截图 → 默认结束并释放位图
智能识图 → 压缩选区 → 配置中的 OpenAI 兼容视觉 API
    ↓
可选结果窗口 → 识图文字 / 复制图片 / PNG或JPEG保存 / 置顶贴图
```

自动路径写入在成功取得 Win32 剪贴板排他锁后再次比较捕获开始时的序列号和最新任务 ID。任何用户新复制或更新任务都会阻止迟到结果覆盖剪贴板。结果窗口默认显示，可在设置中关闭；关闭时自动保存完成后立即释放整张位图。

## 本机验证记录

- 开发系统：Windows 10 22H2，内部版本 19045.6466；
- .NET SDK：8.0.424；
- 非实机自动化测试：核心策略、虚拟桌面、捕获安全上限、任务排序、取消、重复区域、快捷键解析、设置导入、视觉请求结构与私密标记；
- 云端视觉实测：从 `claude-vision-skill` 导入 Base URL、模型与 API Key，直接 API 成功识别程序生成的 `HELLO 2026` 图片；
- GDI 实测：显式开启本机测试后成功读取 96×96 虚拟桌面区域，像素格式为 32 位预乘 Alpha；
- 启动实测：`Ta.Windows.App.exe` 正常运行，主窗口标题为“拓 · Ta Windows Alpha”；
- 凭据实测：用户设置 JSON 不含 API Key，Windows Credential Manager 存在 `Ta.Windows/VisionApiKey` 目标；
- 轻量版实测：嵌入原作者多尺寸 ICO 与默认设置后的单文件约 521KB，关闭主界面进入托盘后会主动归还空闲工作集；
- Computer Use 辅助管道在本机不可用，因此本轮没有自动点击区域选择、结果按钮和置顶贴图；这些交互保留为人工验收项，未伪造通过。

## 当前风险与后续顺序

1. 混合缩放、多显示器跨屏选择需要 100%、125%、150%、200% 实机矩阵；
2. GDI 对 DRM、受保护窗口和部分硬件叠加画面可能返回黑色，产品不得尝试绕过；
3. 默认发布框架依赖轻量版，要求共享 .NET 8 Desktop Runtime；无运行时电脑使用较大的自包含兼容版；
4. 下一阶段先完成窗口捕获、延时和混合 DPI，再进入长截图；
5. 长截图稳定后再实现非破坏性标注、美化、历史和 Agent Bridge；
6. 视觉模型的 Base URL、API Key、模型名称和任务指令均可在设置页修改；业务提示词只存在配置中，不写入运行时代码。

## 验收命令

```bash
./windows/scripts/check-dev-env.sh
./windows/scripts/run-tests.sh
./windows/scripts/run-tests.sh --live
./windows/scripts/run-tests.sh --vision-live
./windows/scripts/build-windows.sh
```

普通测试明确排除实机和云端分类。`--live` 只有显式执行时才读取当前桌面一小块像素；`--vision-live` 只有显式执行时才上传程序生成的测试图片。两者均不输出 API Key。
