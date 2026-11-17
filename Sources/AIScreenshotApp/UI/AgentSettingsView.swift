import AppKit
import SwiftUI
import TaAgentContracts

enum TaAgentInstallationGuide {
  static let installCommand =
    "curl -fsSL --retry 3 --retry-all-errors --retry-delay 1 https://github.com/kangarooking/Ta/releases/latest/download/install.sh | bash"
  static let verificationCommand = "ta status --json"
  static let agentPrompt = """
    请帮我在这台 Mac 安装拓（Ta）的 CLI 和 Agent Skill。请执行下面这条命令：
    curl -fsSL --retry 3 --retry-all-errors --retry-delay 1 https://github.com/kangarooking/Ta/releases/latest/download/install.sh | bash

    安装完成后，运行 ~/.local/bin/ta status --json 验证。确认 JSON 顶层 ok 为 true、data.bridge 为 ready；如果只是权限尚未开启，请告诉我去“拓 → 设置 → 权限 / Agent”完成授权。不要索要、读取或输出任何 API Key，也不要替我修改云端模型配置。
    """
}

struct TaAgentToolInstallationStatus: Equatable {
  let cliInstalled: Bool
  let installedSkillLocations: [String]

  var skillInstalled: Bool { !installedSkillLocations.isEmpty }

  static func detect(
    homeDirectory: URL = FileManager.default.homeDirectoryForCurrentUser,
    codexHomeDirectory: URL? = nil,
    fileManager: FileManager = .default
  ) -> TaAgentToolInstallationStatus {
    let cli = homeDirectory.appendingPathComponent(".local/bin/ta").path
    let codexRoot = codexHomeDirectory ?? homeDirectory.appendingPathComponent(".codex")
    let candidates = [
      codexRoot.appendingPathComponent("skills/ta"),
      homeDirectory.appendingPathComponent(".agents/skills/ta"),
      homeDirectory.appendingPathComponent(".claude/skills/ta"),
    ]
    let installedSkills = candidates.filter {
      fileManager.fileExists(atPath: $0.appendingPathComponent("SKILL.md").path)
    }
    return TaAgentToolInstallationStatus(
      cliInstalled: fileManager.isExecutableFile(atPath: cli),
      installedSkillLocations: installedSkills.map(\.path)
    )
  }
}

struct AgentSettingsView: View {
  @AppStorage(TaAgentPreferenceKey.accessEnabled) private var accessEnabled = true
  @AppStorage(TaAgentPreferenceKey.automaticCaptureAllowed) private var automaticCaptureAllowed =
    true
  @AppStorage(TaAgentPreferenceKey.cloudPolicy) private var cloudPolicy = AgentCloudPolicy.auto
    .rawValue
  @AppStorage(TaAgentPreferenceKey.privacyDenylist) private var privacyDenylist =
    TaAgentPrivacyPolicy.defaultDenylistText
  @AppStorage(TaAgentPreferenceKey.allowCaptureTa) private var allowCaptureTa = true

  @State private var recentCalls: [TaAgentAuditEntry] = []
  @State private var operationMessage: String?
  @State private var installationStatus = TaAgentToolInstallationStatus.detect()
  private let auditLog = TaAgentAuditLog()
  private let artifactStore = TaAgentArtifactStore()

  var body: some View {
    VStack(spacing: 0) {
      installationPanel

      Divider()
        .overlay(TaPalette.hairline)

      Form {
        Section("Agent 与自动化") {
          Toggle("允许本机 Agent 调用拓", isOn: $accessEnabled)
          Text(
            accessEnabled
              ? "CLI、Agent Skill 和 DeepSeek Harness 可以通过仅限当前用户的本机 Bridge 调用拓。"
              : "除状态与权限检查外，所有 Agent 能力都会被拒绝。"
          )
          .font(.caption)
          .foregroundStyle(.secondary)

          Toggle("允许无感自动截图", isOn: $automaticCaptureAllowed)
            .disabled(!accessEnabled)
          Label("普通截图不会弹出拓、抢占前台 App、移动鼠标或发送键盘事件。", systemImage: "cursorarrow.rays")
            .font(.caption)
            .foregroundStyle(.secondary)
        }

        Section("云端与模型") {
          Picker("默认云端策略", selection: $cloudPolicy) {
            Text("按能力决定（推荐）").tag(AgentCloudPolicy.auto.rawValue)
            Text("允许已配置的云端模型").tag(AgentCloudPolicy.allow.rawValue)
            Text("始终禁止上传").tag(AgentCloudPolicy.deny.rawValue)
          }
          .disabled(!accessEnabled)
          Text(cloudPolicyDescription)
            .font(.caption)
            .foregroundStyle(.secondary)
          Label("Agent 只能请求拓执行模型任务，无法读取 Keychain 中的 API Key。", systemImage: "key.fill")
            .font(.caption)
            .foregroundStyle(.secondary)
        }

        Section("隐私 App 黑名单") {
          TextEditor(text: $privacyDenylist)
            .font(.system(.body, design: .monospaced))
            .frame(minHeight: 68)
            .padding(6)
            .scrollContentBackground(.hidden)
            .background(TaPalette.elevatedPaper.opacity(0.78))
            .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
            .overlay {
              RoundedRectangle(cornerRadius: 8, style: .continuous)
                .stroke(TaPalette.hairline, lineWidth: 1)
            }
            .disabled(!accessEnabled)
          Text("每行填写一个 Bundle ID，例如 com.1password.1password。截显示器时，只要画面包含黑名单 App 就会拒绝截图。")
            .font(.caption)
            .foregroundStyle(.secondary)
          HStack {
            Text("默认已屏蔽常见密码管理器（1Password、Bitwarden、钥匙串访问、密码 App、LastPass、Dashlane、KeePassXC、Enpass 等）。")
              .font(.caption)
              .foregroundStyle(.secondary)
            Spacer()
            Button("恢复默认") {
              privacyDenylist = TaAgentPrivacyPolicy.defaultDenylistText
            }
            .disabled(!accessEnabled || privacyDenylist == TaAgentPrivacyPolicy.defaultDenylistText)
          }
          Toggle("允许 Agent 截取拓自身", isOn: $allowCaptureTa)
            .disabled(!accessEnabled)
        }

        Section("缓存与调用记录") {
          HStack {
            VStack(alignment: .leading, spacing: 3) {
              Text("临时截图缓存")
              Text("图片保存在本机缓存，默认 24 小时后自动清理。")
                .font(.caption)
                .foregroundStyle(.secondary)
            }
            Spacer()
            Button("立即清理") { clearArtifacts() }
          }

          HStack {
            Text("最近调用")
            Spacer()
            Button("刷新") { refreshAudit() }
              .buttonStyle(.link)
            Button("清除记录") { clearAudit() }
              .buttonStyle(.link)
              .disabled(recentCalls.isEmpty)
          }

          if recentCalls.isEmpty {
            Text("暂无调用记录。审计不会保存 API Key、OCR/翻译正文或图片数据。")
              .font(.caption)
              .foregroundStyle(.secondary)
          } else {
            ForEach(recentCalls.prefix(8)) { entry in
              auditRow(entry)
            }
            Text(
              "仅保留最近 \(TaAgentAuditLog.defaultMaximumEntries) 次调用（状态、能力、权限查询不计入）；只记录截图目标 App 与保存路径，不记录请求参数和识别内容。"
            )
              .font(.caption)
              .foregroundStyle(.secondary)
          }

          if let operationMessage {
            Text(operationMessage)
              .font(.caption)
              .foregroundStyle(.secondary)
          }
        }
      }
      .formStyle(.grouped)
      .scrollContentBackground(.hidden)
      .background(Color.clear)
    }
    .onAppear {
      refreshAudit()
      refreshInstallationStatus()
    }
  }

  private var installationPanel: some View {
    VStack(alignment: .leading, spacing: 9) {
      HStack(spacing: 8) {
        Text("安装 CLI 与 Skill")
          .font(.headline)
        installationBadge(
          title: "ta CLI",
          installed: installationStatus.cliInstalled
        )
        installationBadge(
          title: installationStatus.skillInstalled
            ? "Ta Skill · \(installationStatus.installedSkillLocations.count) 处"
            : "Ta Skill",
          installed: installationStatus.skillInstalled
        )
        Spacer()
        Button("重新检测", systemImage: "arrow.clockwise") {
          refreshInstallationStatus()
        }
        .buttonStyle(.borderless)
      }

      Text("一条命令同时安装或更新 CLI 与 Skill；自动校验下载文件，并适配 Codex 和通用 Agent Skills 目录。")
        .font(.caption)
        .foregroundStyle(.secondary)

      HStack(alignment: .top, spacing: 10) {
        installationBlock(
          title: "方式一 · 终端",
          subtitle: "复制命令并执行",
          symbol: "terminal",
          content: TaAgentInstallationGuide.installCommand,
          maximumLines: 2,
          copiedMessage: "终端安装命令已复制。"
        )
        installationBlock(
          title: "方式二 · 交给 Agent",
          subtitle: "复制完整安装提示词",
          symbol: "sparkles",
          content: TaAgentInstallationGuide.agentPrompt,
          maximumLines: 2,
          copiedMessage: "Agent 安装提示词已复制。"
        )
      }

      Label(
        "安装后重启 Agent，再运行 \(TaAgentInstallationGuide.verificationCommand) 验证连接。",
        systemImage: "checkmark.shield"
      )
      .font(.caption)
      .foregroundStyle(.secondary)
    }
    .padding(.horizontal, 20)
    .padding(.top, 13)
    .padding(.bottom, 11)
    .background(TaPalette.paper.opacity(0.55))
  }

  private var cloudPolicyDescription: String {
    switch AgentCloudPolicy(rawValue: cloudPolicy) ?? .auto {
    case .auto:
      "本地截图和 OCR 留在设备上；识图、翻译或 DeepSeek OCR 等模型能力按任务使用云端。调用方仍可用 cloud=deny 强制本地。"
    case .allow:
      "允许 Agent 使用你已经在拓中配置的云端模型。每次调用仍会写入是否上云的审计标记。"
    case .deny:
      "所有需要上传图片或文字的 Agent 调用都会被拒绝，只保留本地截图和本地 OCR。"
    }
  }

  private func installationBadge(title: String, installed: Bool) -> some View {
    Label(title, systemImage: installed ? "checkmark.circle.fill" : "circle.dashed")
      .font(.caption.weight(.semibold))
      .foregroundStyle(installed ? Color.green : TaPalette.mutedInk)
      .padding(.horizontal, 9)
      .padding(.vertical, 5)
      .background(
        Capsule(style: .continuous)
          .fill(installed ? Color.green.opacity(0.10) : TaPalette.ink.opacity(0.06))
      )
  }

  private func installationBlock(
    title: String,
    subtitle: String,
    symbol: String,
    content: String,
    maximumLines: Int,
    copiedMessage: String
  ) -> some View {
    VStack(alignment: .leading, spacing: 8) {
      HStack(spacing: 8) {
        Image(systemName: symbol)
          .foregroundStyle(TaPalette.cinnabar)
          .frame(width: 18)
        VStack(alignment: .leading, spacing: 1) {
          Text(title)
            .font(.subheadline.weight(.semibold))
          Text(subtitle)
            .font(.caption2)
            .foregroundStyle(.secondary)
        }
        Spacer()
        Button("复制", systemImage: "doc.on.doc") {
          copyInstallationText(content, message: copiedMessage)
        }
        .buttonStyle(.bordered)
        .controlSize(.small)
      }

      Text(content)
        .font(.system(size: 11, design: .monospaced))
        .foregroundStyle(TaPalette.ink.opacity(0.82))
        .textSelection(.enabled)
        .lineLimit(maximumLines)
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(10)
        .background(TaPalette.ink.opacity(0.045))
        .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
    }
    .padding(11)
    .background(TaPalette.elevatedPaper.opacity(0.72))
    .clipShape(RoundedRectangle(cornerRadius: 11, style: .continuous))
    .overlay {
      RoundedRectangle(cornerRadius: 11, style: .continuous)
        .stroke(TaPalette.hairline, lineWidth: 1)
    }
  }

  @ViewBuilder
  private func auditRow(_ entry: TaAgentAuditEntry) -> some View {
    HStack(spacing: 10) {
      Image(systemName: entry.succeeded ? "checkmark.circle.fill" : "xmark.circle.fill")
        .foregroundStyle(entry.succeeded ? Color.green : Color.red)
      VStack(alignment: .leading, spacing: 2) {
        Text(entry.method.rawValue)
          .font(.system(.caption, design: .monospaced).weight(.semibold))
        Text(
          "\(entry.clientName) · \(entry.occurredAt.formatted(date: .omitted, time: .shortened))"
        )
        .font(.caption2)
        .foregroundStyle(.secondary)
        if let detail = entry.detailText {
          Text(detail)
            .font(.caption2)
            .foregroundStyle(.secondary)
            .lineLimit(1)
            .truncationMode(.middle)
        }
      }
      Spacer()
      if entry.cloudUploaded {
        Label("云端", systemImage: "icloud.and.arrow.up")
          .font(.caption2)
          .foregroundStyle(.orange)
      } else {
        Text("本地")
          .font(.caption2)
          .foregroundStyle(.secondary)
      }
      Text("\(entry.durationMs) ms")
        .font(.system(.caption2, design: .monospaced))
        .foregroundStyle(.secondary)
        .frame(width: 68, alignment: .trailing)
    }
  }

  private func refreshAudit() {
    Task {
      do {
        recentCalls = try await auditLog.recent(limit: 20)
        operationMessage = nil
      } catch {
        operationMessage = "无法读取调用记录：\(error.localizedDescription)"
      }
    }
  }

  private func refreshInstallationStatus() {
    installationStatus = TaAgentToolInstallationStatus.detect()
  }

  private func copyInstallationText(_ text: String, message: String) {
    let pasteboard = NSPasteboard.general
    pasteboard.clearContents()
    if pasteboard.setString(text, forType: .string) {
      operationMessage = message
    } else {
      operationMessage = "复制失败，请手动选择并复制。"
    }
  }

  private func clearAudit() {
    Task {
      do {
        try await auditLog.clear()
        recentCalls = []
        operationMessage = "调用记录已清除。"
      } catch {
        operationMessage = "清除失败：\(error.localizedDescription)"
      }
    }
  }

  private func clearArtifacts() {
    Task {
      do {
        let count = try await artifactStore.clearAll()
        operationMessage = count == 0 ? "当前没有临时截图缓存。" : "已清理 \(count) 组临时截图。"
      } catch {
        operationMessage = "缓存清理失败：\(error.localizedDescription)"
      }
    }
  }
}
