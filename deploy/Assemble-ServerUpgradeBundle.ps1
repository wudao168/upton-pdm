[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$ReleaseNote = '服务器、网页、数据库迁移及预览组件升级。'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $root ".artifacts\uplm-server-upgrade-$Version"
$serverRoot = Join-Path $stage 'Server'
$appOutput = Join-Path $serverRoot 'app'
$wwwroot = Join-Path $appOutput 'wwwroot'
$outputZip = Join-Path $root ".artifacts\UPLM-Server-Upgrade-$Version-final.zip"

foreach ($required in @(
    (Join-Path $appOutput 'Pdm.Api.exe'),
    (Join-Path $appOutput 'hostfxr.dll'),
    (Join-Path $root 'src\pdm-ui\dist\index.html'),
    (Join-Path $root 'src\pdm-ui\dist\review-overlay.html')
)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "组包所需文件不存在：$required" }
}
foreach ($clientPath in @('client-bootstrap.json', 'updates')) {
    if (Test-Path -LiteralPath (Join-Path $wwwroot $clientPath)) {
        throw "仅服务器包不能包含客户端更新文件：$clientPath"
    }
}

New-Item -ItemType Directory -Path $wwwroot -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $root 'src\pdm-ui\dist') -Force |
    Copy-Item -Destination $wwwroot -Recurse -Force

$previewOutput = Join-Path $appOutput 'preview-worker'
New-Item -ItemType Directory -Path $previewOutput -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $root 'src\Pdm.SolidWorks.PreviewWorker\bin\Release\net48') -Force |
    Copy-Item -Destination $previewOutput -Recurse -Force
foreach ($interopName in @('SolidWorks.Interop.sldworks.dll', 'SolidWorks.Interop.swconst.dll', 'SolidWorks.Interop.swpublished.dll')) {
    $source = Join-Path $root "src\Pdm.SolidWorks.Addin\bin\Release\net48\$interopName"
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "预览组件缺少依赖：$interopName" }
    Copy-Item -LiteralPath $source -Destination $previewOutput -Force
}

$migrationOutput = Join-Path $serverRoot 'database-migrations'
New-Item -ItemType Directory -Path $migrationOutput -Force | Out-Null
$migrationFiles = @(Get-ChildItem -LiteralPath (Join-Path $root 'src\Pdm.Infrastructure\Migrations') -Filter '*.sql' -File | Sort-Object Name)
if ($migrationFiles.Count -eq 0) { throw '未找到数据库迁移文件。' }
$migrationFiles | Copy-Item -Destination $migrationOutput -Force

$utf8Bom = [Text.UTF8Encoding]::new($true)
foreach ($scriptName in @('Install-FullUpgradeOnServer.ps1', 'Verify-FullUpgradePackage.ps1')) {
    $source = Join-Path $PSScriptRoot $scriptName
    $target = if ($scriptName -eq 'Install-FullUpgradeOnServer.ps1') {
        Join-Path $serverRoot $scriptName
    } else {
        Join-Path $stage $scriptName
    }
    [IO.File]::WriteAllText($target, (Get-Content -LiteralPath $source -Raw -Encoding UTF8), $utf8Bom)
}

$readme = @"
UPLM 仅服务器全量升级包 $Version
版本说明：$ReleaseNote

包含 Web、Windows x64 自包含 API、数据库迁移和服务器预览组件。
不包含客户端安装包或插件安装包；升级时保留服务器现有 client-bootstrap.json 和 updates 目录，不触发客户端版本变更。
仅用于已有且健康的 C:\UPLM\pdm 安装；程序会先备份 API 和 pdm 数据库，升级失败时尝试回滚。

管理员 PowerShell 在解压目录执行：
& '.\Verify-FullUpgradePackage.ps1' -PackageRoot (Get-Location).Path
& '.\Server\Install-FullUpgradeOnServer.ps1' -InstallRoot 'C:\UPLM\pdm'
"@
[IO.File]::WriteAllText((Join-Path $stage '升级说明.txt'), $readme, $utf8Bom)

$files = @(Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object Name -ne 'manifest.json')
$manifest = [ordered]@{
    format = 'upton-pdm-full-upgrade-v1'
    version = $Version
    releaseNote = $ReleaseNote
    createdAt = [DateTimeOffset]::Now.ToString('O')
    sourceCommit = (& git -C $root rev-parse HEAD).Trim()
    scope = 'web-api-database-preview-worker'
    databaseMigrations = @($migrationFiles | ForEach-Object BaseName)
    files = @($files | ForEach-Object {
        [ordered]@{
            path = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
            length = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
}
[IO.File]::WriteAllText((Join-Path $stage 'manifest.json'), ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))

& (Join-Path $stage 'Verify-FullUpgradePackage.ps1') -PackageRoot $stage
if (Test-Path -LiteralPath $outputZip) { throw "升级包已存在，拒绝覆盖：$outputZip" }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $outputZip -CompressionLevel Optimal

[pscustomobject]@{
    Package = $outputZip
    Length = (Get-Item -LiteralPath $outputZip).Length
    SHA256 = (Get-FileHash -LiteralPath $outputZip -Algorithm SHA256).Hash
    DatabaseMigrations = $migrationFiles.Count
    LatestMigration = $migrationFiles[-1].BaseName
} | Format-List
