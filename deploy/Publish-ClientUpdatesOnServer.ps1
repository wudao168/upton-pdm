[CmdletBinding()]
param([string]$InstallRoot = 'C:\UPLM\pdm')

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请以管理员身份运行本脚本。'
}

$packageRoot = Split-Path -Parent $PSCommandPath
$source = Join-Path $packageRoot 'server-publish'
$bootstrapSource = Join-Path $source 'client-bootstrap.json'
$updatesSource = Join-Path $source 'updates'
$webRoot = Join-Path $InstallRoot 'app\wwwroot'
$updatesTarget = Join-Path $webRoot 'updates'
if (-not (Test-Path -LiteralPath $bootstrapSource) -or -not (Test-Path -LiteralPath $updatesSource)) {
    throw '客户端服务器发布文件不完整。'
}
if (-not (Test-Path -LiteralPath (Join-Path $webRoot 'index.html'))) {
    throw "没有找到 UPLM 服务器网页目录：$webRoot"
}

New-Item -ItemType Directory -Path $updatesTarget -Force | Out-Null
$bootstrap = Get-Content -LiteralPath $bootstrapSource -Raw -Encoding UTF8 | ConvertFrom-Json
$manifest = Join-Path $webRoot 'client-bootstrap.json'
$previous = if (Test-Path -LiteralPath $manifest) { Get-Content -LiteralPath $manifest -Raw -Encoding UTF8 }
$previousRelease = if ($previous) { $previous | ConvertFrom-Json }
foreach ($component in @('Desktop', 'SolidWorksAddin')) {
    $release = $bootstrap.$component
    if ($release.PackageUrl -notmatch '^/updates/([A-Za-z0-9._-]+\.zip)$' -or !$release.Version -or !$release.Signature) {
        throw "客户端发布条目不完整：$component"
    }
    $name = $Matches[1]
    $destination = Join-Path $updatesTarget $name
    if ($previousRelease.$component.Version -and [version]$previousRelease.$component.Version -gt [version]$release.Version) {
        throw "当前已发布更高的版本，请重新生成发布文件：$component"
    }
    if (Test-Path -LiteralPath $destination) {
        if ((Get-FileHash -LiteralPath $destination).Hash -ne $release.Sha256) { throw "同名更新包内容不同，请使用新的版本号：$name" }
        continue
    }
    $sourcePackage = Join-Path $updatesSource $name
    $temporary = "$destination.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::Copy($sourcePackage, $temporary)
        if ((Get-FileHash -LiteralPath $temporary).Hash -ne $release.Sha256) { throw "更新包校验失败：$name" }
        [IO.File]::Move($temporary, $destination)
    } finally { if (Test-Path -LiteralPath $temporary) { [IO.File]::Delete($temporary) } }
}
$temporary = "$manifest.$([guid]::NewGuid().ToString('N')).tmp"
try {
    [IO.File]::Copy($bootstrapSource, $temporary)
    if (Test-Path -LiteralPath $manifest) {
        if ([IO.File]::ReadAllText($manifest) -cne $previous) { throw '检测到其他发布，请重新检查服务器版本。' }
        [IO.File]::Replace($temporary, $manifest, [NullString]::Value)
    }
    else { [IO.File]::Move($temporary, $manifest) }
} finally { if (Test-Path -LiteralPath $temporary) { [IO.File]::Delete($temporary) } }
Write-Host "客户端自动升级文件已发布：$($bootstrap.ConfigurationVersion)" -ForegroundColor Green
Write-Host '无需重启 UptonPdmApi 服务。'
