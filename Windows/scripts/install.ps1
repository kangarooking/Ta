[CmdletBinding()]
param(
  [string]$ReleaseBaseUrl = 'https://github.com/kangarooking/Ta/releases/latest/download',
  [string]$SourceDirectory,
  [string]$InstallRoot = $env:USERPROFILE,
  [string]$CodexHome,
  [string[]]$SkillDirectory,
  [switch]$SkipPathUpdate,
  [switch]$SkipStatusCheck
)

$ErrorActionPreference = 'Stop'
$version = '0.1.0'
$cliArchiveName = "Ta-CLI-$version-windows.zip"
$skillArchiveName = "Ta-Agent-Skill-$version-windows.zip"
$checksumName = 'SHA256SUMS.txt'

function Fail([string]$Message) { throw "Ta 安装失败：$Message" }
function Require-Node {
  $nodeVersion = (& node --version 2>$null)
  if ($LASTEXITCODE -ne 0 -or -not $nodeVersion) { Fail 'Ta Windows CLI 需要 Node.js 22 或更高版本；请先安装 Node.js 后重试。' }
  $major = [int](($nodeVersion -replace '^v', '').Split('.')[0])
  if ($major -lt 22) { Fail "检测到 Node.js $nodeVersion；需要 22 或更高版本。" }
}
function Get-SafePath([string]$Value) { return [System.IO.Path]::GetFullPath($Value) }
function Get-Sha256([string]$FilePath) {
  $algorithm = [System.Security.Cryptography.SHA256]::Create()
  $stream = [System.IO.File]::OpenRead($FilePath)
  try { return ([System.BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
  finally { $stream.Dispose(); $algorithm.Dispose() }
}
function Assert-SafeSkillTarget([string]$Target) {
  $full = Get-SafePath $Target
  $leaf = [System.IO.Path]::GetFileName($full)
  $parent = Split-Path -Parent $full
  if ($leaf -ne 'ta' -or [System.IO.Path]::GetFileName($parent) -ne 'skills') { Fail "拒绝写入不安全的 Skill 目标：$Target" }
  return $full
}
function Copy-SkillAtomically([string]$Source, [string]$Target) {
  $targetPath = Assert-SafeSkillTarget $Target
  if (-not (Test-Path -LiteralPath (Join-Path $Source 'SKILL.md'))) { Fail 'Skill 压缩包结构无效。' }
  $parent = Split-Path -Parent $targetPath
  New-Item -ItemType Directory -Force -Path $parent | Out-Null
  $suffix = [guid]::NewGuid().ToString('N')
  $stage = "$targetPath.install.$suffix"; $backup = "$targetPath.backup.$suffix"
  try {
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Copy-Item -Path (Join-Path $Source '*') -Destination $stage -Recurse -Force
    if (Test-Path -LiteralPath $targetPath) { Move-Item -LiteralPath $targetPath -Destination $backup }
    Move-Item -LiteralPath $stage -Destination $targetPath
    if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Recurse -Force }
  } catch {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    if ((Test-Path -LiteralPath $backup) -and -not (Test-Path -LiteralPath $targetPath)) { Move-Item -LiteralPath $backup -Destination $targetPath }
    throw
  }
}
function Copy-CliAtomically([string]$Source, [string]$BinDirectory) {
  foreach ($file in 'ta.cjs', 'ta.cmd', 'manifest.json') { if (-not (Test-Path -LiteralPath (Join-Path $Source $file))) { Fail "CLI 压缩包缺少 $file。" } }
  New-Item -ItemType Directory -Force -Path $BinDirectory | Out-Null
  $target = Join-Path $BinDirectory 'ta'; $suffix = [guid]::NewGuid().ToString('N')
  $stage = "$target.install.$suffix"; $backup = "$target.backup.$suffix"
  try {
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Copy-Item -Path (Join-Path $Source '*') -Destination $stage -Recurse -Force
    if (Test-Path -LiteralPath $target) { Move-Item -LiteralPath $target -Destination $backup }
    Move-Item -LiteralPath $stage -Destination $target
    $wrapper = "@echo off`r`ncall `"%~dp0ta\ta.cmd`" %*`r`n"
    Set-Content -LiteralPath (Join-Path $BinDirectory 'ta.cmd') -Value $wrapper -Encoding ascii
    if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Recurse -Force }
  } catch {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    if ((Test-Path -LiteralPath $backup) -and -not (Test-Path -LiteralPath $target)) { Move-Item -LiteralPath $backup -Destination $target }
    throw
  }
}
function Verify-Checksum([string]$Directory, [string]$Name) {
  $entries = @{}
  foreach ($line in Get-Content -LiteralPath (Join-Path $Directory $checksumName)) {
    if ($line -match '^([a-fA-F0-9]{64})\s+\*?(.+)$') { $entries[$matches[2].Trim()] = $matches[1].ToLowerInvariant() }
  }
  if (-not $entries.ContainsKey($Name)) { Fail "校验文件中没有 $Name。" }
  $actual = Get-Sha256 (Join-Path $Directory $Name)
  if ($actual -ne $entries[$Name]) { Fail "$Name 的 SHA-256 校验不一致。" }
}
function Get-DownloadedFile([string]$BaseUrl, [string]$Name, [string]$Destination) {
  $uri = "$($BaseUrl.TrimEnd('/'))/$Name"
  for ($attempt = 1; $attempt -le 3; $attempt++) {
    try { Invoke-WebRequest -Uri $uri -OutFile (Join-Path $Destination $Name) -UseBasicParsing; return } catch { if ($attempt -eq 3) { throw } }
  }
}

Require-Node
$temporary = Join-Path ([System.IO.Path]::GetTempPath()) ("ta-windows-installer-" + [guid]::NewGuid())
try {
  New-Item -ItemType Directory -Force -Path $temporary | Out-Null
  if ($SourceDirectory) {
    $source = Get-SafePath $SourceDirectory
    foreach ($asset in $cliArchiveName, $skillArchiveName, $checksumName) {
      if (-not (Test-Path -LiteralPath (Join-Path $source $asset))) { Fail "本地发布目录缺少 $asset。" }
      Copy-Item -LiteralPath (Join-Path $source $asset) -Destination $temporary
    }
  } else {
    foreach ($asset in $cliArchiveName, $skillArchiveName, $checksumName) { Get-DownloadedFile $ReleaseBaseUrl $asset $temporary }
  }
  Verify-Checksum $temporary $cliArchiveName; Verify-Checksum $temporary $skillArchiveName
  Write-Output 'Ta CLI 与 Agent Skill 下载校验通过。'

  $cliExpanded = Join-Path $temporary 'cli'; $skillExpanded = Join-Path $temporary 'skill'
  Expand-Archive -LiteralPath (Join-Path $temporary $cliArchiveName) -DestinationPath $cliExpanded -Force
  Expand-Archive -LiteralPath (Join-Path $temporary $skillArchiveName) -DestinationPath $skillExpanded -Force
  $cliSource = Get-ChildItem -LiteralPath $cliExpanded -Directory | Select-Object -First 1
  $skillSource = Join-Path $skillExpanded 'ta'
  if (-not $cliSource -or -not (Test-Path -LiteralPath $skillSource)) { Fail '发布压缩包结构无效。' }

  $root = Get-SafePath $InstallRoot
  $binDirectory = Join-Path $root '.local\bin'
  Copy-CliAtomically $cliSource.FullName $binDirectory
  if ($SkillDirectory) { $targets = @($SkillDirectory) }
  else {
    $resolvedCodexHome = if ($CodexHome) { Get-SafePath $CodexHome } elseif ($env:CODEX_HOME) { Get-SafePath $env:CODEX_HOME } else { Join-Path $root '.codex' }
    $targets = @((Join-Path $resolvedCodexHome 'skills\ta'), (Join-Path $root '.agents\skills\ta'))
    if (Test-Path -LiteralPath (Join-Path $root '.claude')) { $targets += Join-Path $root '.claude\skills\ta' }
  }
  foreach ($target in $targets) { Copy-SkillAtomically $skillSource $target }

  if (-not $SkipPathUpdate) {
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if (-not (($userPath -split ';' | Where-Object { $_.TrimEnd('\\') -ieq $binDirectory.TrimEnd('\\') }))) {
      [Environment]::SetEnvironmentVariable('Path', (($userPath.TrimEnd(';') + ';' + $binDirectory).TrimStart(';')), 'User')
      Write-Output "已把 $binDirectory 加入用户 PATH；请重新打开终端。"
    }
  }

  Write-Output "Ta CLI 已安装：$(Join-Path $binDirectory 'ta.cmd')"
  foreach ($target in $targets) { Write-Output "Ta Skill 已安装：$target" }
  if (-not $SkipStatusCheck) {
    & (Join-Path $binDirectory 'ta.cmd') status --json
    if ($LASTEXITCODE -ne 0) { Write-Warning 'CLI 与 Skill 已安装，但 Bridge 尚未就绪。请打开拓 Ta，并在“设置 → Agent”中启用 Bridge。' }
  }
  Write-Output '安装完成。重启你的 Agent 后运行 ta status --json。'
} finally {
  if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
