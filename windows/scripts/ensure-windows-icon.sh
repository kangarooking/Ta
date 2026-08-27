#!/usr/bin/env bash

set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
source_icon="$repo_root/Resources/Brand/Ta-AppIcon.png"
target_icon="$repo_root/windows/src/Ta.Windows.App/Resources/Ta.ico"

if [[ ! -f "$target_icon" || "$source_icon" -nt "$target_icon" ]]; then
    dotnet run \
        --project "$repo_root/windows/tools/Ta.IconBuilder/Ta.IconBuilder.csproj" \
        --configuration Release \
        -- "$source_icon" "$target_icon"
else
    printf '%s\n' "Windows 图标已是最新版本。"
fi
