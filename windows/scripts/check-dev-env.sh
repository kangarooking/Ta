#!/usr/bin/env bash

set -euo pipefail

say() {
    printf '%s\n' "$*"
}

fail() {
    printf '环境检查失败：%s\n' "$*" >&2
    exit 1
}

case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*) ;;
    *) fail "Windows 版本只能在 Windows 10 22H2 或 Windows 11 上构建。" ;;
esac

export DOTNET_CLI_TELEMETRY_OPTOUT=1
if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks 2>/dev/null | grep -qE '^8\.'; then
    say "未检测到 .NET 8 SDK，正在通过 winget 自动安装。"
    command -v winget.exe >/dev/null 2>&1 || fail "系统没有 winget，无法自动安装 .NET 8 SDK。"
    winget.exe install \
        --id Microsoft.DotNet.SDK.8 \
        --exact \
        --silent \
        --accept-source-agreements \
        --accept-package-agreements \
        --disable-interactivity
    export PATH="/c/Program Files/dotnet:$PATH"
    hash -r
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
windows_root="$(cd "$script_dir/.." && pwd)"
sdk_version="$(cd "$windows_root" && dotnet --version)"
case "$sdk_version" in
    8.*) ;;
    *) fail "检测到的默认 SDK 为 $sdk_version，需要 .NET 8。" ;;
esac

windows_version="$(cmd.exe /c ver 2>/dev/null | tr -d '\r' | grep -Eo '[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+' | head -n 1 || true)"
windows_build="$(printf '%s' "$windows_version" | awk -F. '{print $3}')"
if [[ -n "$windows_build" && "$windows_build" -lt 19045 ]]; then
    fail "当前 Windows 内部版本为 $windows_build，需要 19045 或更高版本。"
fi
say "环境检查通过。"
say "Windows 版本：${windows_version:-无法读取，但当前为 Windows Shell}"
say ".NET SDK：$sdk_version"
say "验证命令：cd windows && dotnet test Ta.Windows.sln -c Release"
