#!/bin/bash

set -euo pipefail

TA_INSTALLER_VERSION="${TA_INSTALL_VERSION:-1.1.0}"
TA_RELEASE_REPOSITORY="${TA_RELEASE_REPOSITORY:-kangarooking/Ta}"
TA_RELEASE_BASE_URL="${TA_RELEASE_BASE_URL:-https://github.com/${TA_RELEASE_REPOSITORY}/releases/download/v${TA_INSTALLER_VERSION}}"
TA_INSTALL_HOME="${TA_INSTALL_ROOT:-$HOME}"
TA_CLI_ARCHIVE="Ta-CLI-${TA_INSTALLER_VERSION}-macOS-universal.tar.gz"
TA_SKILL_ARCHIVE="Ta-Agent-Skill-${TA_INSTALLER_VERSION}.zip"
TA_CHECKSUM_FILE="SHA256SUMS.txt"

say() {
    printf '%s\n' "$*"
}

fail() {
    printf '安装失败：%s\n' "$*" >&2
    exit 1
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || fail "缺少系统命令：$1"
}

verify_checksum() {
    local file_name="$1"
    local expected
    local actual
    expected="$(awk -v name="$file_name" '$2 == name { print $1; exit }' "$TA_TEMP_DIR/$TA_CHECKSUM_FILE")"
    [[ -n "$expected" ]] || fail "校验文件中没有 $file_name"
    actual="$(shasum -a 256 "$TA_TEMP_DIR/$file_name" | awk '{ print $1 }')"
    [[ "$actual" == "$expected" ]] || fail "$file_name 的 SHA-256 校验不一致"
}

install_skill_at() {
    local source_dir="$1"
    local target_dir="$2"
    local parent_dir
    local staged_dir
    local backup_dir

    case "$target_dir" in
        */skills/ta) ;;
        *) fail "拒绝写入不安全的 Skill 目标：$target_dir" ;;
    esac

    parent_dir="$(dirname "$target_dir")"
    staged_dir="${target_dir}.install.$$"
    backup_dir="${target_dir}.backup.$$"
    mkdir -p "$parent_dir"
    rm -rf "$staged_dir" "$backup_dir"
    ditto "$source_dir" "$staged_dir"

    if [[ -e "$target_dir" || -L "$target_dir" ]]; then
        mv "$target_dir" "$backup_dir"
    fi
    if mv "$staged_dir" "$target_dir"; then
        rm -rf "$backup_dir"
    else
        rm -rf "$staged_dir"
        if [[ -e "$backup_dir" || -L "$backup_dir" ]]; then
            mv "$backup_dir" "$target_dir"
        fi
        fail "无法安装 Skill 到 $target_dir"
    fi
}

if [[ "$(uname -s)" != "Darwin" && -z "${TA_ALLOW_NON_MACOS_TEST:-}" ]]; then
    fail "Ta 当前仅支持 macOS"
fi

if [[ -z "${TA_ALLOW_NON_MACOS_TEST:-}" ]]; then
    TA_MACOS_MAJOR="$(sw_vers -productVersion | cut -d. -f1)"
    [[ "$TA_MACOS_MAJOR" -ge 14 ]] || fail "需要 macOS 14 或更高版本"
fi

for required in curl tar ditto shasum awk install; do
    require_command "$required"
done

TA_TEMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/ta-installer.XXXXXX")"
trap 'rm -rf "$TA_TEMP_DIR"' EXIT

say "正在下载 Ta CLI 与 Agent Skill v${TA_INSTALLER_VERSION}…"
for asset in "$TA_CLI_ARCHIVE" "$TA_SKILL_ARCHIVE" "$TA_CHECKSUM_FILE"; do
    curl -fsSL --retry 3 --retry-all-errors --retry-delay 1 \
        "$TA_RELEASE_BASE_URL/$asset" \
        -o "$TA_TEMP_DIR/$asset"
done

verify_checksum "$TA_CLI_ARCHIVE"
verify_checksum "$TA_SKILL_ARCHIVE"
say "下载校验通过。"

mkdir -p "$TA_TEMP_DIR/cli" "$TA_TEMP_DIR/skill"
tar -xzf "$TA_TEMP_DIR/$TA_CLI_ARCHIVE" -C "$TA_TEMP_DIR/cli"
ditto -x -k "$TA_TEMP_DIR/$TA_SKILL_ARCHIVE" "$TA_TEMP_DIR/skill"

TA_CLI_SOURCE="$(find "$TA_TEMP_DIR/cli" -type f -path '*/bin/ta' -print | head -n 1)"
TA_SKILL_SOURCE="$TA_TEMP_DIR/skill/ta"
[[ -n "$TA_CLI_SOURCE" && -x "$TA_CLI_SOURCE" ]] || fail "CLI 压缩包结构无效"
[[ -f "$TA_SKILL_SOURCE/SKILL.md" ]] || fail "Skill 压缩包结构无效"
[[ -x "$TA_SKILL_SOURCE/scripts/check-ta.sh" ]] || fail "Skill 检查脚本不可执行"

TA_BIN_DIR="$TA_INSTALL_HOME/.local/bin"
TA_CLI_TARGET="$TA_BIN_DIR/ta"
mkdir -p "$TA_BIN_DIR"
install -m 755 "$TA_CLI_SOURCE" "${TA_CLI_TARGET}.install.$$"
mv -f "${TA_CLI_TARGET}.install.$$" "$TA_CLI_TARGET"

TA_SKILL_TARGETS=()
if [[ -n "${TA_SKILL_DIRS:-}" ]]; then
    IFS=':' read -r -a TA_SKILL_TARGETS <<< "$TA_SKILL_DIRS"
else
    if [[ -n "${TA_CODEX_HOME:-}" ]]; then
        TA_CODEX_ROOT="$TA_CODEX_HOME"
    elif [[ -z "${TA_INSTALL_ROOT:-}" && -n "${CODEX_HOME:-}" ]]; then
        TA_CODEX_ROOT="$CODEX_HOME"
    else
        TA_CODEX_ROOT="$TA_INSTALL_HOME/.codex"
    fi
    TA_SKILL_TARGETS+=("$TA_CODEX_ROOT/skills/ta")
    TA_SKILL_TARGETS+=("$TA_INSTALL_HOME/.agents/skills/ta")
    if [[ -d "$TA_INSTALL_HOME/.claude" ]]; then
        TA_SKILL_TARGETS+=("$TA_INSTALL_HOME/.claude/skills/ta")
    fi
fi

for target in "${TA_SKILL_TARGETS[@]}"; do
    [[ -n "$target" ]] || continue
    install_skill_at "$TA_SKILL_SOURCE" "$target"
done

if [[ -z "${TA_SKIP_SHELL_PROFILE:-}" && -z "${TA_INSTALL_ROOT:-}" ]]; then
    TA_PATH_LINE='export PATH="$HOME/.local/bin:$PATH"'
    TA_ZPROFILE="$TA_INSTALL_HOME/.zprofile"
    if [[ ":$PATH:" != *":$TA_BIN_DIR:"* ]] && ! grep -Fqx "$TA_PATH_LINE" "$TA_ZPROFILE" 2>/dev/null; then
        printf '\n# Ta CLI\n%s\n' "$TA_PATH_LINE" >> "$TA_ZPROFILE"
        say "已把 ~/.local/bin 加入 ~/.zprofile。"
    fi
fi

say ""
say "Ta CLI 已安装：$TA_CLI_TARGET"
for target in "${TA_SKILL_TARGETS[@]}"; do
    [[ -n "$target" ]] && say "Ta Skill 已安装：$target"
done

if [[ -z "${TA_SKIP_STATUS_CHECK:-}" ]]; then
    say ""
    say "正在检查 Ta Bridge…"
    if ! "$TA_CLI_TARGET" status --json; then
        say "CLI 与 Skill 已安装，但 Bridge 尚未就绪。请先安装并打开 /Applications/拓.app，在设置中开启屏幕录制与 Agent 权限。"
    fi
fi

say ""
say "安装完成。请重新启动你的 Agent，然后运行：ta status --json"
