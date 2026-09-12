[CmdletBinding()]
param([string]$InstallRoot = 'C:\UPLM\pdm')

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请以管理员身份运行服务器升级脚本。'
}

$packageRoot = Split-Path -Parent $PSCommandPath
$source = Join-Path $packageRoot 'server-publish'
$webUiSource = Join-Path $packageRoot 'web-ui'
$bootstrapSource = Join-Path $source 'client-bootstrap.json'
$updatesSource = Join-Path $source 'updates'
$webRoot = Join-Path $InstallRoot 'app\wwwroot'
$bootstrapTarget = Join-Path $webRoot 'client-bootstrap.json'
$updatesTarget = Join-Path $webRoot 'updates'
foreach ($required in @($bootstrapSource, $updatesSource, (Join-Path $webUiSource 'index.html'), (Join-Path $webUiSource 'assets'), (Join-Path $webRoot 'index.html'))) {
    if (-not (Test-Path -LiteralPath $required)) { throw "升级文件或服务器目录不存在：$required" }
}

$bootstrap = Get-Content -LiteralPath $bootstrapSource -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($componentName in @('Desktop', 'SolidWorksAddin')) {
    $component = $bootstrap.$componentName
    $packageName = [IO.Path]::GetFileName([string]$component.PackageUrl)
    $packagePath = Join-Path $updatesSource $packageName
    if (-not (Test-Path -LiteralPath $packagePath)) { throw "缺少升级文件：$packageName" }
    $actualHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
    if (-not [string]::Equals($actualHash, [string]$component.Sha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "升级文件校验失败：$packageName"
    }
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupRoot = Join-Path $InstallRoot "backup\client-plugin-upgrade-$stamp"
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
$webRootBackup = Join-Path $backupRoot 'wwwroot'
Copy-Item -LiteralPath $webRoot -Destination $webRootBackup -Recurse -Force
$hadBootstrap = Test-Path -LiteralPath $bootstrapTarget
$hadUpdates = Test-Path -LiteralPath $updatesTarget
if ($hadBootstrap) { Copy-Item -LiteralPath $bootstrapTarget -Destination $backupRoot -Force }
if ($hadUpdates) { Copy-Item -LiteralPath $updatesTarget -Destination (Join-Path $backupRoot 'updates') -Recurse -Force }

try {
    Copy-Item -Path (Join-Path $webUiSource '*') -Destination $webRoot -Recurse -Force
    New-Item -ItemType Directory -Path $updatesTarget -Force | Out-Null
    Copy-Item -LiteralPath $bootstrapSource -Destination $bootstrapTarget -Force
    Copy-Item -Path (Join-Path $updatesSource '*') -Destination $updatesTarget -Force

    foreach ($componentName in @('Desktop', 'SolidWorksAddin')) {
        $component = $bootstrap.$componentName
        $packageName = [IO.Path]::GetFileName([string]$component.PackageUrl)
        $deployedPath = Join-Path $updatesTarget $packageName
        $deployedHash = (Get-FileHash -LiteralPath $deployedPath -Algorithm SHA256).Hash
        if (-not [string]::Equals($deployedHash, [string]$component.Sha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw "服务器升级文件校验失败：$packageName"
        }
    }

    foreach ($sourceFile in Get-ChildItem -LiteralPath $webUiSource -Recurse -File) {
        $relativePath = $sourceFile.FullName.Substring($webUiSource.Length + 1)
        $deployedFile = Join-Path $webRoot $relativePath
        if (-not (Test-Path -LiteralPath $deployedFile)) { throw "服务器网页文件缺失：$relativePath" }
        if ((Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $deployedFile -Algorithm SHA256).Hash) {
            throw "服务器网页文件校验失败：$relativePath"
        }
    }

    $published = Invoke-RestMethod 'http://127.0.0.1:5173/client-bootstrap.json'
    if ($published.ConfigurationVersion -ne $bootstrap.ConfigurationVersion) {
        throw "服务器返回版本不正确：$($published.ConfigurationVersion)"
    }
    $webResponse = Invoke-WebRequest 'http://127.0.0.1:5173/' -UseBasicParsing
    if ($webResponse.StatusCode -ne 200) { throw "服务器网页验证失败：HTTP $($webResponse.StatusCode)" }

    $receiptRoot = Join-Path $InstallRoot 'deployment-receipts'
    New-Item -ItemType Directory -Path $receiptRoot -Force | Out-Null
    [ordered]@{
        deployedAt = [DateTimeOffset]::Now.ToString('O')
        version = $bootstrap.ConfigurationVersion
        backupRoot = $backupRoot
        desktopSha256 = $bootstrap.Desktop.Sha256
        solidWorksAddinSha256 = $bootstrap.SolidWorksAddin.Sha256
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $receiptRoot "client-plugin-upgrade-$stamp.json") -Encoding UTF8
}
catch {
    if (Test-Path -LiteralPath $webRootBackup) {
        Get-ChildItem -LiteralPath $webRoot -Force | Remove-Item -Recurse -Force
        Get-ChildItem -LiteralPath $webRootBackup -Force | Copy-Item -Destination $webRoot -Recurse -Force
    }
    if ($hadBootstrap) {
        Copy-Item -LiteralPath (Join-Path $backupRoot 'client-bootstrap.json') -Destination $bootstrapTarget -Force
    }
    elseif (Test-Path -LiteralPath $bootstrapTarget) {
        Remove-Item -LiteralPath $bootstrapTarget -Force
    }
    if ($hadUpdates -and (Test-Path -LiteralPath (Join-Path $backupRoot 'updates'))) {
        New-Item -ItemType Directory -Path $updatesTarget -Force | Out-Null
        Copy-Item -Path (Join-Path $backupRoot 'updates\*') -Destination $updatesTarget -Force
    }
    throw
}

Write-Host "客户端及插件升级已发布：$($bootstrap.ConfigurationVersion)" -ForegroundColor Green
Write-Host "回滚备份：$backupRoot"
Write-Host '服务器网页已同步；本次没有修改 MySQL、API 程序或业务数据，也不需要重启 UptonPdmApi。'
