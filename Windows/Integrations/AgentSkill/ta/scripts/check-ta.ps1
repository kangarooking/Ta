$ErrorActionPreference = 'Stop'

$command = Get-Command ta -ErrorAction SilentlyContinue
if (-not $command) {
  Write-Output '{"ok":false,"artifacts":[],"error":{"code":"CLI_NOT_INSTALLED","message":"未找到 ta CLI。请运行 Ta 的 install.ps1。","retryable":false}}'
  exit 1
}

& ta status --json
exit $LASTEXITCODE
