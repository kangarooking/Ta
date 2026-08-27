#!/usr/bin/env bash

set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"

"$script_dir/check-dev-env.sh"
"$script_dir/ensure-windows-icon.sh"
cd "$repo_root"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet build windows/Ta.Windows.sln -c Release

mode="${1:-}"
if [[ "$mode" == "--live" ]]; then
    printf '%s\n' "已启用本机实测：测试会读取一小块当前桌面像素，但不会保存或上传。"
    app_exe="$repo_root/windows/src/Ta.Windows.App/bin/Release/net8.0-windows/Ta.Windows.App.exe"
    list_app_pids() {
        MSYS2_ARG_CONV_EXCL="*" tasklist.exe /fo csv /nh /fi "IMAGENAME eq Ta.Windows.App.exe" 2>/dev/null \
            | tr -d '\r' \
            | awk -F, '/Ta.Windows.App.exe/ {gsub(/"/, "", $2); print $2}'
    }
    before_pids=" $(list_app_pids | tr '\n' ' ') "
    "$app_exe" >/dev/null 2>&1 &
    launcher_pid=$!
    app_pid=""
    cleanup_app() {
        if [[ -n "$app_pid" ]] && MSYS2_ARG_CONV_EXCL="*" tasklist.exe /fi "PID eq $app_pid" 2>/dev/null | grep -q "$app_pid"; then
            MSYS2_ARG_CONV_EXCL="*" taskkill.exe /PID "$app_pid" /T /F >/dev/null 2>&1 || true
        fi
        kill "$launcher_pid" >/dev/null 2>&1 || true
    }
    trap cleanup_app EXIT
    window_ready=false
    for _ in 1 2 3 4 5 6 7 8 9 10; do
        while IFS= read -r candidate_pid; do
            if [[ -n "$candidate_pid" && "$before_pids" != *" $candidate_pid "* ]]; then
                app_pid="$candidate_pid"
                break
            fi
        done < <(list_app_pids)
        if MSYS2_ARG_CONV_EXCL="*" tasklist.exe /v /fi "PID eq $app_pid" 2>/dev/null | grep -q "拓 · Ta Windows Alpha"; then
            window_ready=true
            break
        fi
        sleep 1
    done
    [[ "$window_ready" == true ]] || {
        printf '%s\n' "本机实测失败：应用主窗口在 10 秒内没有就绪。" >&2
        exit 1
    }
    dotnet test windows/Ta.Windows.sln -c Release --no-build --filter "Category=Live"
    printf '%s\n' "Windows 本机 GDI 与应用窗口测试通过。"
elif [[ "$mode" == "--vision-live" ]]; then
    printf '%s\n' "已启用云端视觉实测：生成的 HELLO 2026 测试图片会发送到设置中的 Base URL 和模型。"
    dotnet test windows/tests/Ta.Windows.App.Tests/Ta.Windows.App.Tests.csproj \
        -c Release \
        --no-build \
        --filter "Category=CloudLive"
    printf '%s\n' "Windows 云端视觉 API 测试通过。"
elif [[ -n "$mode" ]]; then
    printf '未知参数：%s。可用参数为 --live 或 --vision-live。\n' "$mode" >&2
    exit 1
else
    dotnet test windows/Ta.Windows.sln \
        -c Release \
        --no-build \
        --filter "Category!=Live&Category!=CloudLive&Category!=HotKeyProbe&Category!=HotKeyAvailability"
    printf '%s\n' "Windows 全部非实机自动化测试通过。"
fi
