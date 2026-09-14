[CmdletBinding()]
param([string]$InstallRoot = 'C:\UPLM\pdm')

$ErrorActionPreference = 'Stop'
$serviceName = 'UptonPdmApi'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请使用管理员 PowerShell 执行完整升级。'
}

$serverRoot = Split-Path -Parent $PSCommandPath
$packageRoot = Split-Path -Parent $serverRoot
$manifestPath = Join-Path $packageRoot 'manifest.json'
$verifyScript = Join-Path $packageRoot 'Verify-FullUpgradePackage.ps1'
& $verifyScript -PackageRoot $packageRoot
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json

$installRootFull = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$appTarget = Join-Path $installRootFull 'app'
$appSource = Join-Path $serverRoot 'app'
$localRoot = Join-Path $installRootFull 'local'
$secretPath = Join-Path $localRoot 'secrets\pdm-secrets.json'
$runtimeRoot = Join-Path $installRootFull 'runtime'
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
foreach ($required in @($appTarget, $appSource, $secretPath, $runtimeRoot)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "服务器升级所需路径不存在：$required" }
}
if ($null -eq $service) { throw "没有找到服务 $serviceName；此包仅用于升级现有服务器。" }

$mysqlHome = Get-ChildItem -LiteralPath $runtimeRoot -Directory -Filter 'mysql-*-winx64' |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'bin\mysqldump.exe') } |
    Sort-Object Name -Descending |
    Select-Object -First 1
if ($null -eq $mysqlHome) { throw "没有找到服务器 MySQL 运行目录：$runtimeRoot" }
$mysql = Join-Path $mysqlHome.FullName 'bin\mysql.exe'
$mysqlDump = Join-Path $mysqlHome.FullName 'bin\mysqldump.exe'

$secrets = Get-Content -LiteralPath $secretPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace([string]$secrets.mysqlRootPassword)) {
    throw '服务器密钥文件缺少 mysqlRootPassword。'
}

$service.Refresh()
if ($service.Status -ne 'Running') {
    throw "服务 $serviceName 当前不是 Running；请先恢复旧 API 并确认健康检查通过。"
}
try {
    $preflightHealth = Invoke-RestMethod 'http://127.0.0.1:5080/health' -TimeoutSec 5
}
catch {
    throw '升级前 API 健康检查失败；请先恢复旧 API。'
}
if ($preflightHealth.status -ne 'ok' -or $preflightHealth.database -ne 'MySql') {
    throw '升级前 API 或 MySQL 状态不健康，已停止升级。'
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupRoot = Join-Path $installRootFull "backup\full-upgrade-$stamp"
$backupApp = Join-Path $backupRoot 'app'
$appNext = Join-Path $installRootFull "app-next-$stamp"
$databaseBackup = Join-Path $backupRoot 'pdm.sql'
$rootClient = Join-Path $backupRoot 'mysql-root-client.ini'
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
[IO.File]::WriteAllLines($rootClient, @(
    '[client]',
    'user=root',
    "password=$($secrets.mysqlRootPassword)",
    'host=127.0.0.1',
    'port=3308',
    'protocol=TCP'
), (New-Object Text.UTF8Encoding($false)))
& icacls.exe $rootClient /inheritance:r /grant:r 'Administrators:(F)' 'SYSTEM:(F)' | Out-Null
if ($LASTEXITCODE -ne 0) { throw '无法保护临时数据库备份凭据。' }

New-Item -ItemType Directory -Path $appNext -Force | Out-Null
Get-ChildItem -LiteralPath $appSource -Force | Copy-Item -Destination $appNext -Recurse -Force
if (-not (Test-Path -LiteralPath (Join-Path $appNext 'Pdm.Api.dll') -PathType Leaf)) {
    throw '新的 API 暂存目录不完整，尚未切换服务器文件。'
}
& $mysqlDump "--defaults-extra-file=$rootClient" --single-transaction --routines --triggers --hex-blob "--result-file=$databaseBackup" pdm
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $databaseBackup) -or (Get-Item -LiteralPath $databaseBackup).Length -eq 0) {
    throw '数据库备份失败，尚未替换任何程序文件。'
}

$apiRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
$previousEnvironment = @((Get-ItemProperty -LiteralPath $apiRegistryPath -Name Environment -ErrorAction SilentlyContinue).Environment)
$previewWorkerPath = Join-Path $appTarget 'preview-worker\Upton.Pdm.SolidWorks.PreviewWorker.exe'
$updatedEnvironment = @($previousEnvironment | Where-Object { $_ -and $_ -notlike 'PDM_PREVIEW_WORKER_PATH=*' }) + "PDM_PREVIEW_WORKER_PATH=$previewWorkerPath"
$deploymentStarted = $false
$originalAppMoved = $false
$newAppActivated = $false
$databaseMayHaveChanged = $false
try {
    Stop-Service -Name $serviceName -Force
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        $serviceProcess = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
        if ($null -eq $serviceProcess -or $serviceProcess.ProcessId -eq 0) { break }
        Start-Sleep -Milliseconds 500
    }
    if ($serviceProcess -and $serviceProcess.ProcessId -gt 0) {
        throw "服务进程仍未退出：PID $($serviceProcess.ProcessId)"
    }
    $deploymentStarted = $true

    $appTargetFull = [IO.Path]::GetFullPath($appTarget)
    $appNextFull = [IO.Path]::GetFullPath($appNext)
    if (-not $appTargetFull.StartsWith($installRootFull + '\', [StringComparison]::OrdinalIgnoreCase) -or
        -not $appNextFull.StartsWith($installRootFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "应用目录越界：$appTargetFull"
    }
    Move-Item -LiteralPath $appTargetFull -Destination $backupApp
    $originalAppMoved = $true
    Move-Item -LiteralPath $appNextFull -Destination $appTargetFull
    $newAppActivated = $true
    Set-ItemProperty -LiteralPath $apiRegistryPath -Name Environment -Value $updatedEnvironment

    $databaseMayHaveChanged = $true
    Start-Service -Name $serviceName
    $health = $null
    for ($attempt = 0; $attempt -lt 120; $attempt++) {
        try {
            $health = Invoke-RestMethod 'http://127.0.0.1:5080/health' -TimeoutSec 3
            if ($health.status -eq 'ok' -and $health.database -eq 'MySql') { break }
        }
        catch { }
        Start-Sleep -Seconds 1
    }
    if ($null -eq $health -or $health.status -ne 'ok' -or $health.database -ne 'MySql') {
        throw 'API 启动或数据库迁移失败，健康检查未通过。'
    }

    $bootstrap = Invoke-RestMethod 'http://127.0.0.1:5173/client-bootstrap.json' -TimeoutSec 5
    if ($bootstrap.ConfigurationVersion -ne $manifest.version) {
        throw "服务器客户端升级版本错误：$($bootstrap.ConfigurationVersion)"
    }
    $web = Invoke-WebRequest 'http://127.0.0.1:5173/' -UseBasicParsing -TimeoutSec 5
    if ($web.StatusCode -ne 200) { throw "网页健康检查失败：HTTP $($web.StatusCode)" }

    $appliedMigrations = @(& $mysql "--defaults-extra-file=$rootClient" --batch --skip-column-names --execute 'SELECT version FROM pdm.pdm_schema_migration ORDER BY version')
    if ($LASTEXITCODE -ne 0) { throw '无法读取数据库迁移记录。' }
    $missingMigrations = @($manifest.databaseMigrations | Where-Object { $_ -notin $appliedMigrations })
    if ($missingMigrations.Count -gt 0) {
        throw "数据库迁移未完整应用：$($missingMigrations -join ', ')"
    }

    $receiptRoot = Join-Path $installRootFull 'deployment-receipts'
    New-Item -ItemType Directory -Path $receiptRoot -Force | Out-Null
    [ordered]@{
        deployedAt = [DateTimeOffset]::Now.ToString('O')
        version = $manifest.version
        scope = 'web-api-database-client-solidworks-addin-preview-worker'
        backupRoot = $backupRoot
        databaseBackup = $databaseBackup
        migrationCount = @($manifest.databaseMigrations).Count
        latestMigration = @($manifest.databaseMigrations)[-1]
        health = $health.status
        database = $health.database
        clientBootstrapVersion = $bootstrap.ConfigurationVersion
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $receiptRoot "full-upgrade-$stamp.json") -Encoding UTF8
}
catch {
    $originalError = $_
    if ($deploymentStarted) {
        $currentService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($currentService -and $currentService.Status -ne 'Stopped') {
            Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
            $currentService.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        }
        if ($newAppActivated -and (Test-Path -LiteralPath $appTarget)) {
            Move-Item -LiteralPath $appTarget -Destination (Join-Path $backupRoot 'failed-app')
        }
        if ($originalAppMoved -and (Test-Path -LiteralPath $backupApp)) {
            Move-Item -LiteralPath $backupApp -Destination $appTarget
        }
        Set-ItemProperty -LiteralPath $apiRegistryPath -Name Environment -Value $previousEnvironment

        if ($databaseMayHaveChanged) {
            & $mysql "--defaults-extra-file=$rootClient" --execute 'DROP DATABASE IF EXISTS pdm; CREATE DATABASE pdm CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;'
            if ($LASTEXITCODE -ne 0) { throw "程序升级失败且数据库重建失败。原始错误：$originalError" }
            Get-Content -LiteralPath $databaseBackup -ReadCount 1000 | & $mysql "--defaults-extra-file=$rootClient" pdm
            if ($LASTEXITCODE -ne 0) { throw "程序升级失败且数据库恢复失败。备份位于：$databaseBackup。原始错误：$originalError" }
        }
        Start-Service -Name $serviceName
    }
    throw $originalError
}
finally {
    if (Test-Path -LiteralPath $rootClient) { Remove-Item -LiteralPath $rootClient -Force }
    if (Test-Path -LiteralPath $appNext) { Remove-Item -LiteralPath $appNext -Recurse -Force }
}

Write-Host "UPLM 全量升级完成：$($manifest.version)" -ForegroundColor Green
Write-Host "API 与数据库备份：$backupRoot"
Write-Host '现有客户端与 SolidWorks 插件将在程序正常退出后通过服务器发布文件自动升级。'
