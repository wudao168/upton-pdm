[CmdletBinding()]
param([string]$InstallRoot = 'C:\UPLM\pdm')

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '请使用管理员 PowerShell 执行网页升级。' }

$packageRoot = Split-Path -Parent $PSCommandPath
& (Join-Path $packageRoot 'Verify-WebUiUpgradePackage.ps1') -PackageRoot $packageRoot
$manifest = Get-Content -LiteralPath (Join-Path $packageRoot 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$installRootFull = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$webTarget = [IO.Path]::GetFullPath((Join-Path $installRootFull 'app\wwwroot'))
$webSource = [IO.Path]::GetFullPath((Join-Path $packageRoot 'Web'))
if (-not $webTarget.StartsWith($installRootFull + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "网页目录越界：$webTarget" }
if (-not (Test-Path -LiteralPath (Join-Path $webTarget 'client-bootstrap.json') -PathType Leaf)) { throw "服务器网页目录不完整：$webTarget" }

$health = Invoke-RestMethod 'http://127.0.0.1:5080/health' -TimeoutSec 5
if ($health.status -ne 'ok' -or $health.database -ne 'MySql') { throw '升级前API或MySQL状态不健康。' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupRoot = Join-Path $installRootFull "backup\web-ui-$stamp"
$backupWeb = Join-Path $backupRoot 'wwwroot'
$stageRoot = Join-Path $installRootFull "web-ui-next-$stamp"
New-Item -ItemType Directory -Path $backupWeb,$stageRoot -Force | Out-Null

$replaceNames = @('assets', 'index.html', 'review-overlay.html', 'client-bootstrap.json')
foreach ($name in $replaceNames) {
    $active = Join-Path $webTarget $name
    if (Test-Path -LiteralPath $active) { Copy-Item -LiteralPath $active -Destination (Join-Path $backupWeb $name) -Recurse -Force }
}
Get-ChildItem -LiteralPath $webSource -Force | Copy-Item -Destination $stageRoot -Recurse -Force

$deploymentStarted = $false
try {
    $deploymentStarted = $true
    $activeAssets = Join-Path $webTarget 'assets'
    if (Test-Path -LiteralPath $activeAssets) { Remove-Item -LiteralPath $activeAssets -Recurse -Force }
    Get-ChildItem -LiteralPath $stageRoot -Force | Copy-Item -Destination $webTarget -Recurse -Force

    $bootstrap = Invoke-RestMethod "http://127.0.0.1:5173/client-bootstrap.json?deployment=$([Uri]::EscapeDataString([string]$manifest.version))" -TimeoutSec 5
    if ($bootstrap.ConfigurationVersion -ne $manifest.version) { throw "服务器发布版本错误：$($bootstrap.ConfigurationVersion)" }
    if ($bootstrap.ReleaseNote -ne $manifest.releaseNote) { throw '服务器版本说明与升级包不一致。' }
    if (@($bootstrap.ReleaseHistory).Count -ne [int]$manifest.releaseHistoryCount) { throw '服务器版本记录数量与升级包不一致。' }
    $web = Invoke-WebRequest 'http://127.0.0.1:5173/' -UseBasicParsing -TimeoutSec 5
    if ($web.StatusCode -ne 200) { throw "网页健康检查失败：HTTP $($web.StatusCode)" }

    $receiptRoot = Join-Path $installRootFull 'deployment-receipts'
    New-Item -ItemType Directory -Path $receiptRoot -Force | Out-Null
    [ordered]@{
        deployedAt = [DateTimeOffset]::Now.ToString('O')
        version = $manifest.version
        releaseNote = $manifest.releaseNote
        releaseHistoryCount = $manifest.releaseHistoryCount
        scope = 'web-ui-and-release-metadata-only'
        backupRoot = $backupRoot
        health = $health.status
        database = $health.database
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $receiptRoot "web-ui-$stamp.json") -Encoding UTF8
}
catch {
    if ($deploymentStarted) {
        $activeAssets = Join-Path $webTarget 'assets'
        if (Test-Path -LiteralPath $activeAssets) { Remove-Item -LiteralPath $activeAssets -Recurse -Force }
        Get-ChildItem -LiteralPath $backupWeb -Force | Copy-Item -Destination $webTarget -Recurse -Force
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
}

Write-Host "UPLM网页与版本记录升级完成：$($manifest.version)" -ForegroundColor Green
Write-Host "回滚备份：$backupRoot"
