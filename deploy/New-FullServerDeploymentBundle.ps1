[CmdletBinding()]
param(
    [string]$Version = ([DateTimeOffset]::Now.ToString('yyyy.MM.dd.HHmm')),
    [string]$ReleaseNote = '',
    [string]$PlaceholderServerBaseUrl = 'http://127.0.0.1:5173'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'SystemReleaseHistory.ps1')
$ReleaseNote = Get-UplmReleaseNote -Version $Version -ReleaseNote $ReleaseNote -Fallback '本次发布未填写版本说明。'
$dotnet = Join-Path $root '.dotnet\dotnet.exe'
$mysqlZip = Join-Path $root '.runtime\downloads\mysql-8.4.11-winx64.zip'
$vcRedist = Join-Path $root '.artifacts\server-prerequisites\vc_redist.x64.exe'
$clientPrerequisites = Join-Path $root '.artifacts\client-prerequisites'
$artifacts = Join-Path $root '.artifacts'
$stage = Join-Path $artifacts "uplm-full-server-$Version"
$outputZip = Join-Path $artifacts "UPLM-Full-Server-$Version-final.zip"
$clientBuild = Join-Path $artifacts "uplm-client-test-$Version"
foreach ($required in @($dotnet, $mysqlZip, $vcRedist, (Join-Path $clientPrerequisites 'MicrosoftEdgeWebView2RuntimeInstallerX64.exe'), (Join-Path $clientPrerequisites 'NDP48-x86-x64-AllOS-ENU.exe'))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "缺少首装包构建依赖：$required" }
}
try { $uri = [Uri]$PlaceholderServerBaseUrl.TrimEnd('/') } catch { throw "客户端服务器占位地址无效：$PlaceholderServerBaseUrl" }
if (-not $uri.IsAbsoluteUri -or $uri.Scheme -notin @('http', 'https')) { throw '客户端服务器占位地址必须是完整 HTTP/HTTPS 地址。' }
$PlaceholderServerBaseUrl = $uri.AbsoluteUri.TrimEnd('/')

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'New-ClientTestPackage.ps1') -ServerBaseUrl $PlaceholderServerBaseUrl -Version $Version -DesktopVersion $Version -SolidWorksAddinVersion $Version -ReleaseNote $ReleaseNote
if ($LASTEXITCODE -ne 0) { throw "客户端工具构建失败，退出码：$LASTEXITCODE" }
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'New-ClientSetupExe.ps1') -Version $Version
if ($LASTEXITCODE -ne 0) { throw "客户端安装 EXE 生成失败，退出码：$LASTEXITCODE" }
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
$serverRoot = Join-Path $stage 'Server'
$appOutput = Join-Path $serverRoot 'app'
$clientRoot = Join-Path $stage 'Client'
New-Item -ItemType Directory -Path $appOutput, $clientRoot -Force | Out-Null

Push-Location $root
try {
    & $dotnet restore 'Pdm.slnx' --nologo -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw '解决方案还原失败。' }
    & $dotnet restore 'src\Pdm.Api\Pdm.Api.csproj' --runtime win-x64 --nologo -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'Windows x64 自包含 API 还原失败。' }
    & $dotnet build 'Pdm.slnx' --configuration Release --no-restore --nologo --disable-build-servers -m:1
    if ($LASTEXITCODE -ne 0) { throw '完整 Release 构建失败。' }
    & $dotnet test 'Pdm.slnx' --configuration Release --no-build --no-restore --nologo --disable-build-servers
    if ($LASTEXITCODE -ne 0) { throw '完整 Release 测试失败。' }
    & $dotnet publish 'src\Pdm.Api\Pdm.Api.csproj' --configuration Release --runtime win-x64 --self-contained true --no-restore --output $appOutput --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Windows x64 自包含 API 发布失败。' }
}
finally { Pop-Location }

$wwwroot = Join-Path $appOutput 'wwwroot'
New-Item -ItemType Directory -Path $wwwroot -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $root 'src\pdm-ui\dist') -Force | Copy-Item -Destination $wwwroot -Recurse -Force
Copy-Item -LiteralPath (Join-Path $clientBuild 'server-publish\client-bootstrap.json') -Destination $wwwroot -Force
Copy-Item -LiteralPath (Join-Path $clientBuild 'server-publish\updates') -Destination $wwwroot -Recurse -Force
$previewWorkerOutput = Join-Path $appOutput 'preview-worker'
New-Item -ItemType Directory -Path $previewWorkerOutput -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $root 'src\Pdm.SolidWorks.PreviewWorker\bin\Release\net48') -Force | Copy-Item -Destination $previewWorkerOutput -Recurse -Force
foreach ($interopName in @('SolidWorks.Interop.sldworks.dll', 'SolidWorks.Interop.swconst.dll', 'SolidWorks.Interop.swpublished.dll')) {
    $source = Join-Path $clientBuild "payload\solidworks-addin\$interopName"
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "SolidWorks 预览组件缺少依赖：$interopName" }
    Copy-Item -LiteralPath $source -Destination $previewWorkerOutput -Force
}

Copy-Item -LiteralPath $mysqlZip -Destination (Join-Path $serverRoot 'mysql-8.4.11-winx64.zip')
Copy-Item -LiteralPath $vcRedist -Destination (Join-Path $serverRoot 'vc_redist.x64.exe')
$utf8Bom = New-Object Text.UTF8Encoding($true)
foreach ($scriptName in @('Install-FullServerDeployment.ps1', 'Verify-FullServerDeploymentPackage.ps1')) {
    $target = if ($scriptName -eq 'Install-FullServerDeployment.ps1') { Join-Path $serverRoot $scriptName } else { Join-Path $stage $scriptName }
    [IO.File]::WriteAllText($target, (Get-Content -LiteralPath (Join-Path $PSScriptRoot $scriptName) -Raw -Encoding UTF8), $utf8Bom)
}
foreach ($name in @('manifest.json', 'payload', 'prerequisites')) { Copy-Item -LiteralPath (Join-Path $clientBuild $name) -Destination (Join-Path $clientRoot $name) -Recurse }
foreach ($scriptName in @('Install-UPLMClient.ps1', 'Uninstall-UPLMClient.ps1')) {
    [IO.File]::WriteAllText((Join-Path $clientRoot $scriptName), (Get-Content -LiteralPath (Join-Path $PSScriptRoot $scriptName) -Raw -Encoding UTF8), $utf8Bom)
}
Copy-Item -LiteralPath (Join-Path $artifacts "UPLM-Client-Setup-$Version.exe") -Destination (Join-Path $clientRoot 'UPLM-Client-Setup.exe')

$readme = @"
UPLM Windows Server 2019 全量首装包
版本：$Version
桌面客户端版本：$Version
SolidWorks 插件版本：$Version
版本说明：$ReleaseNote

本包内置：Windows x64 自包含 API、MySQL 8.4、微软 VC++ x64 运行库、网页端、客户端安装/卸载工具、SolidWorks 插件和客户端所需 WebView2/.NET Framework 4.8 安装程序。
服务器必须是全新部署；不要用于覆盖已有 UPLM 服务。

服务器管理员 PowerShell（ZIP 解压后）：
powershell.exe -NoProfile -ExecutionPolicy Bypass -File '.\Server\Install-FullServerDeployment.ps1' -LanBaseUrl 'http://服务器内网IP:5173' -InstallRoot 'C:\UPLM\pdm'

客户端工具在 Client 目录。将整个 Client 目录复制到用户电脑，双击 UPLM-Client-Setup.exe，输入服务器地址后完成安装；也支持无人值守：
UPLM-Client-Setup.exe --server 'http://服务器内网IP:5173'
安装完成后，在 Windows 设置 > 应用 > 已安装的应用（或控制面板 > 程序和功能）中选择“UPLM 客户端和 SolidWorks 插件”即可卸载。兼容卸载 2026.09.24 旧版的命令：
powershell.exe -NoProfile -ExecutionPolicy Bypass -File '.\Uninstall-UPLMClient.ps1'

服务器 API 监听 127.0.0.1:5080 和内网 5173；MySQL 仅监听 127.0.0.1:3308。首装会自动创建并保护数据库与首次管理员密码，不清空其他系统数据。
"@
[IO.File]::WriteAllText((Join-Path $stage '部署说明.txt'), $readme, $utf8Bom)

$files = Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object { $_.Name -ne 'manifest.json' }
$manifest = [ordered]@{
    format = 'upton-pdm-full-server-v1'; version = $Version; desktopVersion = $Version; solidWorksAddinVersion = $Version
    releaseNote = $ReleaseNote; createdAt = [DateTimeOffset]::Now.ToString('O'); scope = 'windows-server-2019-self-contained-api-mysql-web-client-solidworks-tools'
    files = @($files | ForEach-Object { [ordered]@{ path = $_.FullName.Substring($stage.Length + 1).Replace('\', '/'); length = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
}
[IO.File]::WriteAllText((Join-Path $stage 'manifest.json'), ($manifest | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
& (Join-Path $stage 'Verify-FullServerDeploymentPackage.ps1') -PackageRoot $stage
if (Test-Path -LiteralPath $outputZip) { Remove-Item -LiteralPath $outputZip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $outputZip -CompressionLevel Optimal
[pscustomobject]@{ Package = $outputZip; Length = (Get-Item -LiteralPath $outputZip).Length; SHA256 = (Get-FileHash -LiteralPath $outputZip -Algorithm SHA256).Hash } | Format-List
