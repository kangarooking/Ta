#!/usr/bin/env bash

set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
build_stamp="$(date '+%Y%m%d-%H%M%S')"
mode="${1:-light}"
if [[ "$mode" == "light" ]]; then
    publish_dir="$repo_root/windows/artifacts/win-x64-light/$build_stamp"
    self_contained=false
    publish_extra=(-p:PublishSingleFile=true)
    package_label="轻量单文件版"
elif [[ "$mode" == "--self-contained" ]]; then
    publish_dir="$repo_root/windows/artifacts/win-x64-self-contained/$build_stamp"
    self_contained=true
    publish_extra=()
    package_label="自包含兼容版"
else
    printf '未知参数：%s。默认不传参数生成轻量版；可用参数为 --self-contained。\n' "$mode" >&2
    exit 1
fi

"$script_dir/check-dev-env.sh"
"$script_dir/ensure-windows-icon.sh"
cd "$repo_root"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

printf '%s\n' "正在还原 Windows 工程依赖。"
dotnet restore windows/Ta.Windows.sln

printf '%s\n' "正在运行 Release 测试。"
dotnet test windows/Ta.Windows.sln \
    -c Release \
    --no-restore \
    --filter "Category!=Live&Category!=CloudLive&Category!=HotKeyProbe&Category!=HotKeyAvailability"

printf '正在生成 win-x64 %s。\n' "$package_label"
dotnet restore windows/src/Ta.Windows.App/Ta.Windows.App.csproj -r win-x64
dotnet publish windows/src/Ta.Windows.App/Ta.Windows.App.csproj \
    -c Release \
    -r win-x64 \
    --self-contained "$self_contained" \
    "${publish_extra[@]}" \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    --no-restore \
    -o "$publish_dir"

exe_path="$publish_dir/Ta.Windows.App.exe"
[[ -f "$exe_path" ]] || {
    printf '构建失败：没有生成 %s\n' "$exe_path" >&2
    exit 1
}

size_bytes="$(du -sb "$publish_dir" | awk '{print $1}')"
size_mb="$(( (size_bytes + 1048575) / 1048576 ))"
size_kb="$(( (size_bytes + 1023) / 1024 ))"
printf '%s\n' "Windows 构建完成。"
printf '构建类型：%s\n' "$package_label"
printf '可执行文件：%s\n' "$exe_path"
printf '发布目录大小：约 %s KB（%s MB）\n' "$size_kb" "$size_mb"
if [[ "$self_contained" == false ]]; then
    printf '%s\n' "运行要求：目标电脑安装 .NET 8 Desktop Runtime；本机已满足。"
fi
printf '%s\n' "验证方法：双击 Ta.Windows.App.exe；应用默认显示主界面，关闭后驻留托盘，区域截图快捷键为 Shift+A。"
