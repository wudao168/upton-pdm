[CmdletBinding()]
param([string]$PackageRoot = '')

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($PackageRoot)) { $PackageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path }
$packageRootFull = [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\')
$manifestPath = Join-Path $packageRootFull 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "部署包缺少清单：$manifestPath" }
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.format -ne 'upton-pdm-full-server-v1') { throw "不支持的部署包格式：$($manifest.format)" }
foreach ($name in @('version', 'desktopVersion', 'solidWorksAddinVersion')) {
    if ([string]::IsNullOrWhiteSpace([string]$manifest.$name)) { throw "部署包缺少版本字段：$name" }
}
foreach ($entry in @($manifest.files)) {
    $relativePath = ([string]$entry.path).Replace('/', '\')
    $filePath = [IO.Path]::GetFullPath((Join-Path $packageRootFull $relativePath))
    if (-not $filePath.StartsWith($packageRootFull + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "部署包文件路径越界：$relativePath" }
    if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) { throw "部署包文件缺失：$relativePath" }
    $file = Get-Item -LiteralPath $filePath
    if ($file.Length -ne [long]$entry.length) { throw "部署包文件长度不一致：$relativePath" }
    if (-not [string]::Equals((Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash, [string]$entry.sha256, [StringComparison]::OrdinalIgnoreCase)) { throw "部署包文件哈希不一致：$relativePath" }
}
$required = @(
    'Server/app/Pdm.Api.exe', 'Server/app/Pdm.Api.dll', 'Server/app/hostfxr.dll', 'Server/app/hostpolicy.dll', 'Server/app/coreclr.dll', 'Server/app/wwwroot/index.html', 'Server/app/wwwroot/client-bootstrap.json',
    'Server/mysql-8.4.11-winx64.zip', 'Server/vc_redist.x64.exe', 'Server/Install-FullServerDeployment.ps1',
    'Client/manifest.json', 'Client/payload/desktop/Upton.Pdm.Desktop.exe', 'Client/payload/solidworks-addin/Upton.Pdm.SolidWorks.Addin.dll',
    'Client/prerequisites/MicrosoftEdgeWebView2RuntimeInstallerX64.exe', 'Client/prerequisites/NDP48-x86-x64-AllOS-ENU.exe',
    'Client/Install-UPLMClient.ps1', 'Client/Uninstall-UPLMClient.ps1', 'Client/UPLM-Client-Setup.exe'
)
foreach ($relativePath in $required) { if (-not (Test-Path -LiteralPath (Join-Path $packageRootFull $relativePath) -PathType Leaf)) { throw "完整首装包缺少必要文件：$relativePath" } }
$clientManifest = Get-Content -LiteralPath (Join-Path $packageRootFull 'Client/manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($clientManifest.version -ne $manifest.version -or $clientManifest.desktopVersion -ne $manifest.desktopVersion -or $clientManifest.solidWorksAddinVersion -ne $manifest.solidWorksAddinVersion) { throw '客户端工具版本与服务器部署包不一致。' }
$bootstrap = Get-Content -LiteralPath (Join-Path $packageRootFull 'Server/app/wwwroot/client-bootstrap.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($bootstrap.ConfigurationVersion -ne $manifest.version -or $bootstrap.Desktop.Version -ne $manifest.desktopVersion -or $bootstrap.SolidWorksAddin.Version -ne $manifest.solidWorksAddinVersion) { throw '服务器客户端升级配置与部署包版本不一致。' }
Write-Host "Windows Server 全量首装包校验通过：$($manifest.version)" -ForegroundColor Green
