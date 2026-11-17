# 让 Agent 使用拓：Skill、CLI 与 DeepSeek Harness 插件

拓可以作为 Agent 的视觉输入层，在不接管鼠标键盘的前提下完成截图、确定性标注、OCR、AI 识图、翻译和结果交付。三种适配器共用同一个由「拓.app」托管的本机 Bridge，因此 macOS 权限、模型配置和 API Key 只需要在拓里管理一次。

```text
Agent Skill ──> ta CLI ─────────────┐
                                    ├──> Ta Local Bridge ──> 截图 / OCR / AI / 翻译
DeepSeek Harness ──> dsh-ta Plugin ─┘
```

## 1. 前置条件

1. 安装 Ta v1.1.0 或更高版本，并把「拓.app」放入 `/Applications`。
2. 打开拓，在“设置 → 权限”中允许屏幕录制。
3. 打开“设置 → Agent”，确认“允许本机 Agent 调用拓”已开启。
4. 如需 AI 识图、远程 OCR 或翻译，先在拓中配置模型。Agent 无法读取 Keychain 中的 API Key。

普通 Agent 截图使用 ScreenCaptureKit 在后台完成：不弹框选层、不激活拓、不移动鼠标、不发送键盘事件。钉图、交互框选、标注等会改变屏幕状态的能力不属于无感调用。

## 2. 一键安装 CLI 与 Skill（推荐）

打开终端，执行这一行：

```bash
curl -fsSL --retry 3 --retry-all-errors --retry-delay 1 https://github.com/kangarooking/Ta/releases/latest/download/install.sh | bash
```

也可以直接把下面这段话发给 Codex、Claude Code 或其他本机 Agent：

> 请帮我在这台 Mac 安装拓（Ta）的 CLI 和 Agent Skill。请执行：`curl -fsSL --retry 3 --retry-all-errors --retry-delay 1 https://github.com/kangarooking/Ta/releases/latest/download/install.sh | bash`。安装完成后运行 `~/.local/bin/ta status --json` 验证，确认 JSON 顶层 `ok` 为 `true`、`data.bridge` 为 `ready`。如果只是权限尚未开启，请告诉我去“拓 → 设置 → 权限 / Agent”完成授权。不要索要、读取或输出任何 API Key，也不要替我修改云端模型配置。

安装器会完成以下操作：

- 从最新 GitHub Release 下载通用版 CLI 与 Ta Agent Skill；
- 在写入前校验两份压缩包的 SHA-256；
- 把 CLI 安装到 `~/.local/bin/ta`，并在需要时把该目录加入 `~/.zprofile`；
- 把 Skill 安装到 `~/.codex/skills/ta` 与 `~/.agents/skills/ta`；如果本机已有 Claude 配置目录，也会安装到 `~/.claude/skills/ta`；
- 重复执行时安全更新已有文件，不需要先卸载。

安装完成后重新启动 Agent，再运行：

```bash
ta status --json
```

如果当前 Shell 暂时找不到 `ta`，可先执行 `source ~/.zprofile`，或直接运行 `~/.local/bin/ta status --json`。

## 3. 手动安装 `ta` CLI

从 GitHub Release 下载 `Ta-CLI-1.1.0-macOS-universal.tar.gz`，然后安装到用户目录：

```bash
tar -xzf Ta-CLI-1.1.0-macOS-universal.tar.gz
mkdir -p "$HOME/.local/bin"
install -m 755 ta-cli-1.1.0/bin/ta "$HOME/.local/bin/ta"
export PATH="$HOME/.local/bin:$PATH"
ta status --json
```

压缩包同时支持 Apple Silicon 与 Intel Mac，并包含 `manifest.json`：

```json
{
  "name": "ta",
  "version": "1.1.0",
  "bridgeProtocol": 1,
  "requiresTaApp": ">=1.1.0",
  "minimumMacOS": "14.0",
  "architectures": ["arm64", "x86_64"]
}
```

### 常用命令

```bash
# 检查 Bridge、版本和权限
ta status --json
ta capabilities --json
ta permissions --json

# 不抢焦点地截取当前前台窗口
ta capture frontmost --json

# 列出并截取指定窗口
ta window list --app Safari --json
ta capture window --window-id 1234 --json

# 一步截取某个 App 的窗口，并把长边限制在 1568 像素以内
ta capture app "Safari" --max-size 1568 --json

# 使用最近截图执行本地 OCR
ta ocr last --json

# OCR 并返回每行文字的像素边框
ta ocr last --boxes --json

# AI 识图；会遵循拓中的云端策略
ta analyze last --task general --json

# 文字翻译
ta translate text --text "Hello from Ta" --json

# 保存最近图片到明确的绝对路径
ta save last --output "$PWD/ta-capture.png" --json

# 按 JSON 配方添加箭头、文字等标注；全程本地执行
ta transform last --recipe "$PWD/annotations.json" --output "$PWD/marked.png" --json

# 撤销或重做上一条标注配方
ta transform undo --json
ta transform redo --json
```

Agent 应始终检查 JSON 顶层的 `ok`。图片命令还必须确认 `artifacts` 非空；不能仅根据命令已经发出就宣称成功。

## 4. 手动安装 Ta Agent Skill

从 Release 下载并解压 `Ta-Agent-Skill-1.1.0.zip`。Skill 不重复实现截图算法，它负责教 Agent 选择正确的 `ta` 命令、验证结果并遵守隐私边界。

Codex 示例：

```bash
unzip Ta-Agent-Skill-1.1.0.zip
mkdir -p "$HOME/.codex/skills"
ditto ta "$HOME/.codex/skills/ta"
```

其他支持 Agent Skills 的工具，把解压后的 `ta/` 放入对应 Skills 目录即可。确保 `ta` CLI 已经位于该 Agent 进程的 `PATH` 中，然后重新启动 Agent。

Skill 的启动检查只有一条命令：

```bash
ta status --json
```

它不会读取 API Key，也不会在没有明确需求时修改剪贴板或保存文件。

## 5. 安装 DeepSeek Harness 原生插件

`dsh-ta` 是原生 Cordis Plugin + Bundle，不是 MCP 配置别名。它需要 DeepSeek Harness `0.1.0-rc.7` 或更高版本，以及 Node.js `22.19.0` 或更高版本。

```bash
dsh plugin --profile web add ./dsh-ta-1.1.0.tgz
dsh --profile web --dump-config
```

配置输出中应出现：

```yaml
- id: ta
  name: dsh-ta
```

插件注册以下 Harness Tools：

| Tool | 用途 |
|------|------|
| `ta_system` | 状态、能力与权限 |
| `ta_list_targets` | 显示器和窗口发现 |
| `ta_capture` | 前台窗口、显示器、窗口 ID 或已知区域截图 |
| `ta_ocr` | OCR 识别 |
| `ta_analyze` | 多模态识图 |
| `ta_translate` | 文字或图片翻译 |
| `ta_deliver` | 保存或按授权写入剪贴板 |

图片结果会导入 Harness Attachment Store。模型上下文收到的是持久附件引用，不是巨大 Base64，也不是可长期访问的 Ta 缓存路径。

### 覆盖插件配置

编辑 `$DSH_HOME/profiles/<profile>/cordis.patch.yml`，用同一个 `id: ta` 覆盖配置。例如强制禁止云端并禁止修改剪贴板：

```yaml
- id: ta
  config:
    defaultCloud: deny
    allowClipboard: false
```

`defaultCloud` 默认是 `inherit`：插件不发送 `cloud` 参数，直接使用用户在拓里设置的云端策略。显式填写 `auto`、`allow`、`deny` 只能让请求比拓的设置更严格，无法放宽——用户在拓中选择“始终禁止上传”后，任何调用方参数都不能绕过。

未填写的字段会使用插件默认值：`/Applications/拓.app`、当前用户的 Ta Socket、15 秒超时（OCR、识图、翻译这类可能上云的调用至少等待 180 秒）和自动后台启动。超时后插件不会自动重发请求，因为拓可能仍在执行。`allowClipboard` 默认是 `false`，只有明确需要 Agent 改写剪贴板时才开启。

卸载：

```bash
dsh plugin --profile web remove dsh-ta
```

## 5. App 内隐私控制与审计

“设置 → Agent”提供：

- Agent 总开关和无感自动截图开关；
- `auto`、`allow`、`deny` 三种云端策略。这是上限：调用方的 `cloud` 参数只能更严格（例如 `cloud=deny`），不能放宽；选择“始终禁止上传”后所有需要上云的调用都会返回 `CLOUD_UPLOAD_NOT_ALLOWED`；
- 按 Bundle ID 配置的隐私 App 黑名单。从未修改过时默认屏蔽常见密码管理器（1Password、Bitwarden、钥匙串访问、密码 App、LastPass、Dashlane、KeePassXC、Enpass 等），可在设置中编辑或恢复默认；
- 是否允许截取拓自身；
- 临时截图缓存清理：Bridge 启动时以及生成新截图时（最多每 10 分钟一次）自动删除超过 24 小时的缓存，也可手动立即清理；
- 最近 2000 次调用的本地审计（状态、能力、权限这类只读探测不计入，避免挤掉截图记录）。

设置修改会从下一次请求开始生效，不需要重启 Bridge。审计只保存调用时间、调用方、方法、成功状态、耗时、错误码、是否上云、截图目标（类型与 App 名称 / Bundle ID，不含窗口标题）以及 `deliver.save` 的保存路径；不保存其他请求参数、API Key、OCR/翻译正文或图片数据。只要图片或文字已经交给云端模型，即使该次调用随后失败，“是否上云”也会记为是。调用记录权限为当前用户可读写；日志文件损坏时会被改名为 `audit-v1.json.corrupt` 并重新开始记录。

## 6. Bridge v1 能力与限制

当前公开 Bridge v1 已实现：

- 状态、能力和权限检查；
- 显示器与窗口发现；
- 显示器、前台窗口、指定窗口和已知坐标区域截图；可按 App 名称或窗口标题片段一步截图（`ta capture app "Safari"`），可用 `--max-size <像素>`（参数 `maxDimension`）限制输出长边，响应的 `data.image` 给出输出像素尺寸、原始尺寸和 `pixelsPerPoint`；
- OCR（`--boxes` / `boxes` 返回每行文字的像素边框）、AI 识图、文字翻译和图片翻译；
- JSON Recipe v1 标注变换、稳定 ID 擦除以及 100 步撤销/重做；
- 复制与保存。

文件安全限制：

- `deliver.save`（`ta save`、`--output`）只写 PNG：没有扩展名时自动补 `.png`，其他扩展名被拒绝；拒绝隐藏文件/目录（以 `.` 开头）、`~/Library`（拓自己的 Agent 缓存目录除外）和系统目录；目标已存在时默认拒绝，需要显式传 `overwrite: true`（CLI `--overwrite`）。
- 图片输入（`inputPath`）只接受普通图片文件：png、jpg、jpeg、tiff、heic、webp、gif、bmp。
- 标注配方的尺寸有上限（例如模糊半径 ≤ 256、马赛克块 ≤ 512、线宽与字号 ≤ 2000、坐标绝对值 ≤ 1000000），超出时返回 `INVALID_REQUEST`。
- 以上违规统一返回 `INVALID_REQUEST`。

超时与重试：

- CLI 默认超时按命令区分：状态/列表 10 秒，截图/标注/保存 30 秒，OCR/识图/翻译 180 秒（`ocr --cloud deny` 为 60 秒）；可用 `--timeout <秒>` 覆盖。
- 客户端等待超时返回独立错误码 `REQUEST_TIMEOUT`（退出码 8，`retryable: false`）：请求可能仍在拓中执行，内容也可能已经上云，**不要自动重试**。
- 使用 `--json` 时，包括启动失败、超时在内的所有错误都以 JSON 信封输出到 stderr。
- Bridge 对已接受的连接设置了接收超时（10 秒内必须发完请求），空闲连接会被关闭，不会拖住 App。

标注变换支持裁剪、矩形、圆形、箭头、画笔、高亮、文字、编号、矩形/笔刷马赛克、模糊、按 ID 擦除和放大镜。它使用左上角像素坐标、只在本机渲染，每次成功调用都会返回新的 PNG Artifact。

拓 App 已有但 Bridge v1 尚未开放的能力包括：交互框选、长截图、钉图、交互式标注编辑器和 AI 美化。Agent 必须先检查 `ta capabilities --json`，不得虚构不存在的命令。后续版本会继续按 `Capture → Understand → Transform → Deliver` 顺序扩展 Bridge，而不是在每个适配器中复制一套实现。

## 7. 故障排查

| 错误 | 处理方法 |
|------|----------|
| `TA_APP_NOT_INSTALLED` | 确认 `/Applications/拓.app` 已安装 |
| `BRIDGE_UNAVAILABLE` | 启动拓后重试一次，不要无限重试；`retryable` 为 `false` 时说明连接在请求发出后中断，先确认结果 |
| `REQUEST_TIMEOUT` | 客户端等待超时，请求可能仍在执行；不要自动重试，确认结果后再用更大的 `--timeout` 重新发起 |
| `INVALID_REQUEST` | 参数、路径或标注配方不合法；按 `message` 修正后再调用 |
| `INTERNAL_ERROR` | 拓内部或模型服务出错；把 `message` 告诉用户，不要循环重试 |
| `SCREEN_PERMISSION_REQUIRED` | 在“设置 → 权限”开启屏幕录制 |
| `TARGET_NOT_FOUND` | 重新运行 `ta window list --json` |
| `TARGET_BLOCKED_BY_PRIVACY_POLICY` | 检查 Agent 总开关和隐私 App 黑名单 |
| `CLOUD_UPLOAD_NOT_ALLOWED` | 使用本地 OCR；如需云端能力，由用户在拓的设置里调整云端策略（调用方参数无法放宽） |
| `MODEL_PROFILE_NOT_CONFIGURED` | 在拓中配置对应视觉或翻译模型 |

临时截图超过 24 小时会被清理。此时再引用旧路径会得到 `INVALID_REQUEST`（找不到图片文件）：重新截图即可；需要长期保留时使用 `ta save`。

## 8. 从源码生成发布包

```bash
./scripts/package-ta-cli.sh 1.1.0
./scripts/package-ta-skill.sh 1.1.0
TA_NODE_BIN=/path/to/node ./scripts/package-dsh-ta.sh 1.1.0
```

完整发布脚本会同时生成 App DMG/ZIP、CLI、Skill、Harness TGZ 和统一校验文件：

```bash
./scripts/package-release.sh 1.1.0
```

最终文件位于 `artifacts/release/v1.1.0/`，并全部写入 `SHA256SUMS.txt`。
