[CmdletBinding()]
param([string]$PackageRoot)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($PackageRoot)) {
    $PackageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
}
$manifestPath = Join-Path $PackageRoot 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) { throw "缺少升级包清单：$manifestPath" }
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace([string]$manifest.desktopVersion) -or
    [string]::IsNullOrWhiteSpace([string]$manifest.solidWorksAddinVersion)) {
    throw '升级包缺少独立的桌面客户端或 SolidWorks 插件版本。'
}
foreach ($entry in $manifest.files) {
    $path = Join-Path $PackageRoot ([string]$entry.path)
    if (-not (Test-Path -LiteralPath $path)) { throw "缺少升级包文件：$($entry.path)" }
    $item = Get-Item -LiteralPath $path
    if ($item.Length -ne [long]$entry.length) { throw "升级包文件长度不正确：$($entry.path)" }
    $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if (-not [string]::Equals($actualHash, [string]$entry.sha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "升级包文件哈希不正确：$($entry.path)"
    }
}
$bootstrapPath = Join-Path $PackageRoot 'Server\server-publish\client-bootstrap.json'
if (-not (Test-Path -LiteralPath $bootstrapPath -PathType Leaf)) { throw "缺少客户端发布清单：$bootstrapPath" }
$bootstrap = Get-Content -LiteralPath $bootstrapPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($bootstrap.ConfigurationVersion -ne $manifest.version -or
    $bootstrap.Desktop.Version -ne $manifest.desktopVersion -or
    $bootstrap.SolidWorksAddin.Version -ne $manifest.solidWorksAddinVersion) {
    throw '服务器、桌面客户端或 SolidWorks 插件版本不匹配。'
}
Write-Host "升级包校验通过：$($manifest.version)" -ForegroundColor Green
