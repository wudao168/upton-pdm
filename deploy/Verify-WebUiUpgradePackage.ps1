[CmdletBinding()]
param([string]$PackageRoot = '')

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($PackageRoot)) { $PackageRoot = Split-Path -Parent $PSCommandPath }
$packageRootFull = [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\')
$manifestPath = Join-Path $packageRootFull 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "升级包缺少清单：$manifestPath" }
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.format -ne 'upton-pdm-web-ui-upgrade-v1') { throw "不支持的升级包格式：$($manifest.format)" }

foreach ($entry in @($manifest.files)) {
    $relativePath = ([string]$entry.path).Replace('/', '\')
    $filePath = [IO.Path]::GetFullPath((Join-Path $packageRootFull $relativePath))
    if (-not $filePath.StartsWith($packageRootFull + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "升级包文件路径越界：$relativePath" }
    if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) { throw "升级包文件缺失：$relativePath" }
    $file = Get-Item -LiteralPath $filePath
    if ($file.Length -ne [long]$entry.length) { throw "升级包文件长度不一致：$relativePath" }
    if ((Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash -ne [string]$entry.sha256) { throw "升级包文件哈希不一致：$relativePath" }
}

foreach ($required in @('Web\index.html', 'Web\client-bootstrap.json', 'Install-WebUiUpgradeOnServer.ps1')) {
    if (-not (Test-Path -LiteralPath (Join-Path $packageRootFull $required) -PathType Leaf)) { throw "网页升级包缺少必要文件：$required" }
}
$bootstrap = Get-Content -LiteralPath (Join-Path $packageRootFull 'Web\client-bootstrap.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($bootstrap.ConfigurationVersion -ne $manifest.version) { throw "发布清单版本不匹配：$($bootstrap.ConfigurationVersion)" }
if ([string]::IsNullOrWhiteSpace([string]$bootstrap.ReleaseNote)) { throw '发布清单缺少版本说明。' }
if (@($bootstrap.ReleaseHistory).Count -lt 1) { throw '发布清单缺少版本记录。' }
Write-Host "网页升级包校验通过：$($manifest.version)" -ForegroundColor Green
