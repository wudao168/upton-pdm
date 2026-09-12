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
Copy-Item -LiteralPath $bootstrapSource -Destination (Join-Path $webRoot 'client-bootstrap.json') -Force
Copy-Item -Path (Join-Path $updatesSource '*') -Destination $updatesTarget -Force

$bootstrap = Invoke-RestMethod 'http://127.0.0.1:5173/client-bootstrap.json'
if ([string]::IsNullOrWhiteSpace($bootstrap.Desktop.Version) -or [string]::IsNullOrWhiteSpace($bootstrap.SolidWorksAddin.Version)) {
    throw '服务器客户端升级清单验证失败。'
}
Write-Host "客户端自动升级文件已发布：$($bootstrap.ConfigurationVersion)" -ForegroundColor Green
Write-Host '无需重启 UptonPdmApi 服务。'
