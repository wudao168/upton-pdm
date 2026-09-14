[CmdletBinding()]
param([string]$PackageRoot = '')

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($PackageRoot)) {
    $PackageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
}
$packageRootFull = [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\')
$manifestPath = Join-Path $packageRootFull 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "升级包缺少清单：$manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.format -ne 'upton-pdm-full-upgrade-v1') {
    throw "不支持的升级包格式：$($manifest.format)"
}
if ([string]::IsNullOrWhiteSpace([string]$manifest.version)) {
    throw '升级包版本为空。'
}

foreach ($entry in @($manifest.files)) {
    $relativePath = ([string]$entry.path).Replace('/', '\')
    $filePath = [IO.Path]::GetFullPath((Join-Path $packageRootFull $relativePath))
    if (-not $filePath.StartsWith($packageRootFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "升级包文件路径越界：$relativePath"
    }
    if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
        throw "升级包文件缺失：$relativePath"
    }
    $file = Get-Item -LiteralPath $filePath
    if ($file.Length -ne [long]$entry.length) {
        throw "升级包文件长度不一致：$relativePath"
    }
    $actualHash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash
    if (-not [string]::Equals($actualHash, [string]$entry.sha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "升级包文件哈希不一致：$relativePath"
    }
}

$required = @(
    'Server\app\Pdm.Api.dll',
    'Server\app\Pdm.Infrastructure.dll',
    'Server\app\wwwroot\index.html',
    'Server\app\wwwroot\client-bootstrap.json',
    'Server\app\preview-worker\Upton.Pdm.SolidWorks.PreviewWorker.exe',
    'Server\app\preview-worker\SolidWorks.Interop.sldworks.dll',
    'Server\app\preview-worker\SolidWorks.Interop.swconst.dll',
    'Server\app\preview-worker\SolidWorks.Interop.swpublished.dll',
    'Server\Install-FullUpgradeOnServer.ps1',
    "Client\UPLM-Client-Setup-$($manifest.version).exe"
)
foreach ($relativePath in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $packageRootFull $relativePath) -PathType Leaf)) {
        throw "完整升级包缺少必要文件：$relativePath"
    }
}

$bootstrap = Get-Content -LiteralPath (Join-Path $packageRootFull 'Server\app\wwwroot\client-bootstrap.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($bootstrap.ConfigurationVersion -ne $manifest.version) {
    throw "客户端自动升级版本不匹配：$($bootstrap.ConfigurationVersion)"
}
if (@($manifest.databaseMigrations).Count -eq 0) {
    throw '完整升级包没有数据库迁移清单。'
}
foreach ($migration in @($manifest.databaseMigrations)) {
    if (-not (Test-Path -LiteralPath (Join-Path $packageRootFull "Server\database-migrations\$migration.sql") -PathType Leaf)) {
        throw "数据库迁移脚本缺失：$migration"
    }
}

Write-Host "完整升级包校验通过：$($manifest.version)" -ForegroundColor Green
