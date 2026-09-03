[CmdletBinding()]
param(
  [string]$Version,
  [string]$ReleaseDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $Version) { $Version = (& node -p "require('./package.json').version" 2>$null).Trim() }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "无效版本号：$Version" }
if (-not $ReleaseDirectory) { $ReleaseDirectory = Join-Path $projectRoot "artifacts\release\v$Version" }

$cliArchive = Join-Path $ReleaseDirectory "Ta-CLI-$Version-windows.zip"
$skillArchive = Join-Path $ReleaseDirectory "Ta-Agent-Skill-$Version-windows.zip"
$installer = Join-Path $ReleaseDirectory 'install.ps1'
$checksums = Join-Path $ReleaseDirectory 'SHA256SUMS.txt'
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("ta-windows-release-" + [guid]::NewGuid())

function Get-Sha256([string]$FilePath) {
  $algorithm = [System.Security.Cryptography.SHA256]::Create()
  $stream = [System.IO.File]::OpenRead($FilePath)
  try { return ([System.BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
  finally { $stream.Dispose(); $algorithm.Dispose() }
}

try {
  New-Item -ItemType Directory -Force -Path $ReleaseDirectory, $staging | Out-Null
  $cliStage = Join-Path $staging "ta-cli-$Version"
  $skillStage = Join-Path $staging 'ta'
  New-Item -ItemType Directory -Force -Path $cliStage, $skillStage | Out-Null
  Copy-Item -LiteralPath (Join-Path $projectRoot 'CLI\ta.cjs') -Destination $cliStage
  Copy-Item -LiteralPath (Join-Path $projectRoot 'CLI\ta.cmd') -Destination $cliStage
  Copy-Item -LiteralPath (Join-Path $projectRoot 'CLI\manifest.json') -Destination $cliStage
  Copy-Item -Path (Join-Path $projectRoot 'Integrations\AgentSkill\ta\*') -Destination $skillStage -Recurse -Force
  if (-not (Test-Path -LiteralPath (Join-Path $skillStage 'SKILL.md'))) { throw 'Skill 包缺少 SKILL.md。' }

  Remove-Item -LiteralPath $cliArchive, $skillArchive, $installer, $checksums -Force -ErrorAction SilentlyContinue
  Compress-Archive -Path $cliStage -DestinationPath $cliArchive -CompressionLevel Optimal
  Compress-Archive -Path $skillStage -DestinationPath $skillArchive -CompressionLevel Optimal
  Copy-Item -LiteralPath (Join-Path $projectRoot 'scripts\install.ps1') -Destination $installer

  @($cliArchive, $skillArchive, $installer) |
    ForEach-Object { $hash = Get-Sha256 $_; "$hash  $([System.IO.Path]::GetFileName($_))" } |
    Set-Content -LiteralPath $checksums -Encoding utf8
  Write-Output "Release artifacts: $ReleaseDirectory"
} finally {
  if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
