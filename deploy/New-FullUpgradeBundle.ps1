[CmdletBinding()]
param(
    [string]$Version = ([DateTimeOffset]::Now.ToString('yyyy.MM.dd.HHmm')),
    [string]$ServerBaseUrl = 'http://10.7.7.62:5173',
    [string]$ReleaseNote = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'SystemReleaseHistory.ps1')
$ReleaseNote = Get-UplmReleaseNote -Version $Version -ReleaseNote $ReleaseNote -Fallback '本次发布未填写版本说明。'
$artifacts = Join-Path $root '.artifacts'
$stage = Join-Path $artifacts "uplm-full-upgrade-$Version"
$outputZip = Join-Path $artifacts "UPLM-Full-Upgrade-$Version-final.zip"
$serverRoot = Join-Path $stage 'Server'
$appOutput = Join-Path $serverRoot 'app'
$clientRoot = Join-Path $stage 'Client'
$clientRecoveryRoot = Join-Path $stage 'ClientRecovery'
$clientBuild = Join-Path $artifacts "uplm-client-test-$Version"
$clientInstaller = Join-Path $artifacts "UPLM-Client-Setup-$Version.exe"
$dotnet = Join-Path $root '.dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { throw "项目 .NET SDK 不存在：$dotnet" }

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'New-ClientTestPackage.ps1') -ServerBaseUrl $ServerBaseUrl -Version $Version -ReleaseNote $ReleaseNote
if ($LASTEXITCODE -ne 0) { throw "客户端与插件升级包生成失败，退出码：$LASTEXITCODE" }
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'New-ClientSetupExe.ps1') -Version $Version
if ($LASTEXITCODE -ne 0) { throw "客户端安装 EXE 生成失败，退出码：$LASTEXITCODE" }

if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $appOutput,$clientRoot,$clientRecoveryRoot -Force | Out-Null

Push-Location $root
try {
    & $dotnet restore 'Pdm.slnx' --nologo -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw '解决方案还原失败。' }
    & $dotnet build 'Pdm.slnx' --configuration Release --no-restore --nologo --disable-build-servers -m:1
    if ($LASTEXITCODE -ne 0) { throw '完整 Release 构建失败。' }
    & $dotnet test 'Pdm.slnx' --configuration Release --no-build --no-restore --nologo --disable-build-servers
    if ($LASTEXITCODE -ne 0) { throw '完整 Release 测试失败。' }
    & $dotnet publish 'src\Pdm.Api\Pdm.Api.csproj' --configuration Release --no-restore --output $appOutput --nologo
    if ($LASTEXITCODE -ne 0) { throw 'API 发布失败。' }
}
finally { Pop-Location }

$wwwroot = Join-Path $appOutput 'wwwroot'
New-Item -ItemType Directory -Path $wwwroot -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $root 'src\pdm-ui\dist') -Force | Copy-Item -Destination $wwwroot -Recurse -Force
$serverPublish = Join-Path $clientBuild 'server-publish'
Copy-Item -LiteralPath (Join-Path $serverPublish 'client-bootstrap.json') -Destination $wwwroot -Force
Copy-Item -LiteralPath (Join-Path $serverPublish 'updates') -Destination $wwwroot -Recurse -Force

$previewOutput = Join-Path $appOutput 'preview-worker'
New-Item -ItemType Directory -Path $previewOutput -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $root 'src\Pdm.SolidWorks.PreviewWorker\bin\Release\net48') -Force |
    Copy-Item -Destination $previewOutput -Recurse -Force
$addinPayload = Join-Path $clientBuild 'payload\solidworks-addin'
foreach ($interopName in @('SolidWorks.Interop.sldworks.dll','SolidWorks.Interop.swconst.dll','SolidWorks.Interop.swpublished.dll')) {
    $interopSource = Join-Path $addinPayload $interopName
    if (-not (Test-Path -LiteralPath $interopSource -PathType Leaf)) { throw "SolidWorks 预览组件缺少依赖：$interopName" }
    Copy-Item -LiteralPath $interopSource -Destination $previewOutput -Force
}

$migrationOutput = Join-Path $serverRoot 'database-migrations'
New-Item -ItemType Directory -Path $migrationOutput -Force | Out-Null
$migrationFiles = Get-ChildItem -LiteralPath (Join-Path $root 'src\Pdm.Infrastructure\Migrations') -Filter '*.sql' -File | Sort-Object Name
$migrationFiles | Copy-Item -Destination $migrationOutput -Force
Copy-Item -LiteralPath $clientInstaller -Destination $clientRoot -Force
$clientRecoveryScript = Join-Path $PSScriptRoot 'Repair-UPLMClientAtStartup.ps1'
$clientRecoveryTarget = Join-Path $clientRecoveryRoot 'Repair-UPLMClientAtStartup.ps1'
$webRecoveryTarget = Join-Path $wwwroot 'updates\Repair-UPLMClientAtStartup.ps1'

$utf8Bom = New-Object Text.UTF8Encoding($true)
foreach ($scriptName in @('Install-FullUpgradeOnServer.ps1','Verify-FullUpgradePackage.ps1')) {
    $sourcePath = Join-Path $PSScriptRoot $scriptName
    $targetPath = if ($scriptName -eq 'Install-FullUpgradeOnServer.ps1') { Join-Path $serverRoot $scriptName } else { Join-Path $stage $scriptName }
    [IO.File]::WriteAllText($targetPath, (Get-Content -LiteralPath $sourcePath -Raw -Encoding UTF8), $utf8Bom)
}
[IO.File]::WriteAllText($clientRecoveryTarget, (Get-Content -LiteralPath $clientRecoveryScript -Raw -Encoding UTF8), $utf8Bom)
[IO.File]::WriteAllText($webRecoveryTarget, (Get-Content -LiteralPath $clientRecoveryScript -Raw -Encoding UTF8), $utf8Bom)

$readme = @"
UPLM 全量升级部署包
版本：$Version
版本说明：$ReleaseNote

包含：Web、API、数据库增量迁移、Windows 客户端、SolidWorks 插件、服务器预览组件。
数据库升级前会自动备份现有 API 和 pdm 数据库；不清空业务数据、账号、配置、Vault 或 Release 文件。

服务器管理员 PowerShell：
`$version = '$Version'
`$packageRoot = 'C:\UPLM\package'
`$zip = Join-Path `$packageRoot "UPLM-Full-Upgrade-`$version-final.zip"
`$dest = Join-Path `$packageRoot "upgrade-`$version"
Expand-Archive -LiteralPath `$zip -DestinationPath `$dest -Force
Set-Location `$dest
powershell.exe -NoProfile -ExecutionPolicy Bypass -File '.\Verify-FullUpgradePackage.ps1'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File '.\Server\Install-FullUpgradeOnServer.ps1' -InstallRoot 'C:\UPLM\pdm'

部署完成后：
(Invoke-RestMethod 'http://127.0.0.1:5080/health') | ConvertTo-Json
(Invoke-RestMethod 'http://127.0.0.1:5173/client-bootstrap.json').ConfigurationVersion

客户端：已安装客户端和 SolidWorks 插件在正常退出后自动升级；首次安装可运行 Client 目录内的 EXE。
旧版升级器已经卡住的域客户端：将 ClientRecovery\Repair-UPLMClientAtStartup.ps1 配置为一次性计算机启动脚本；脚本仅在 UPLM 与 SolidWorks 均未运行时更新所有已有用户配置，不强制结束程序。
"@
[IO.File]::WriteAllText((Join-Path $stage '升级说明.txt'), $readme, $utf8Bom)

$databaseMigrations = @($migrationFiles | ForEach-Object { $_.BaseName })
$files = Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object { $_.Name -ne 'manifest.json' }
$manifest = [ordered]@{
    format = 'upton-pdm-full-upgrade-v1'
    version = $Version
    releaseNote = $ReleaseNote
    createdAt = [DateTimeOffset]::Now.ToString('O')
    serverBaseUrl = $ServerBaseUrl.TrimEnd('/')
    scope = 'web-api-database-client-solidworks-addin-preview-worker'
    databaseMigrations = $databaseMigrations
    files = @($files | ForEach-Object {
        [ordered]@{
            path = $_.FullName.Substring($stage.Length + 1).Replace('\','/')
            length = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
}
[IO.File]::WriteAllText((Join-Path $stage 'manifest.json'), ($manifest | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))

& (Join-Path $stage 'Verify-FullUpgradePackage.ps1') -PackageRoot $stage
if (Test-Path -LiteralPath $outputZip) { Remove-Item -LiteralPath $outputZip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $outputZip -CompressionLevel Optimal

[pscustomobject]@{
    Package = $outputZip
    Length = (Get-Item -LiteralPath $outputZip).Length
    SHA256 = (Get-FileHash -LiteralPath $outputZip -Algorithm SHA256).Hash
    DatabaseMigrations = $databaseMigrations.Count
    LatestMigration = $databaseMigrations[-1]
} | Format-List
