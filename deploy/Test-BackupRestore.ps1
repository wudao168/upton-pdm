[CmdletBinding()]
param(
    [string]$BackupPath,
    [string]$Database = 'pdm_restore_qa'
)

$ErrorActionPreference = 'Stop'
if ($Database -notmatch '^[A-Za-z0-9_]+_qa$') { throw 'The restore test database name must end with _qa.' }
$projectRoot = Split-Path -Parent $PSScriptRoot
$localRoot = Join-Path $projectRoot '.local'
. (Join-Path $PSScriptRoot 'StorageBackup.Common.ps1')
if ([string]::IsNullOrWhiteSpace($BackupPath)) {
    $plan = Get-PdmStorageBackupPlan $projectRoot
    $searchRoots = @((Join-Path $localRoot 'backup'))
    if ($plan.backupRoot) { $searchRoots += $plan.backupRoot }
    $latest = $searchRoots | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { Get-ChildItem -LiteralPath $_ -Directory } | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'manifest.json') } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -eq $latest) { throw 'No complete PLM backup was found.' }
    $BackupPath = $latest.FullName
}
$backupRoot = [IO.Path]::GetFullPath($BackupPath)
Assert-PdmOrdinaryBackupPath $backupRoot

$manifestPath = Join-Path $backupRoot 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) { throw '未找到完整备份清单，不能执行恢复验证。' }
$manifest = Get-Content -LiteralPath (Join-Path $backupRoot 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$databasePath = Resolve-PdmBackupPath $backupRoot $manifest.databaseDump.relativePath
$rootClientPath = Join-Path $localRoot 'secrets\mysql-root-client.ini'
$receipt = Get-Content -LiteralPath (Join-Path $localRoot 'deployment-receipt.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$mysql = Join-Path $receipt.mysqlHome 'bin\mysql.exe'
if (-not (Test-Path -LiteralPath $manifestPath) -or -not (Test-Path -LiteralPath $databasePath)) { throw 'The backup manifest or database dump is missing.' }
if ($manifest.format -notin @('upton-pdm-backup-v1','upton-pdm-backup-v2')) { throw '备份格式不受支持。' }
Test-PdmStorageBackupFiles $manifest $backupRoot
if ((Get-FileHash -LiteralPath $databasePath -Algorithm SHA256).Hash -ne $manifest.databaseDump.sha256) { throw 'The database dump SHA-256 does not match.' }

$startedAt = [DateTimeOffset]::Now
$setupSql = "DROP DATABASE IF EXISTS $Database; CREATE DATABASE $Database CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci; GRANT ALL PRIVILEGES ON $Database.* TO 'pdm_app'@'127.0.0.1'; FLUSH PRIVILEGES;"
& $mysql "--defaults-extra-file=$rootClientPath" --batch --raw --execute $setupSql
if ($LASTEXITCODE -ne 0) { throw 'Failed to rebuild the isolated restore database.' }

$restoreLogRoot = Join-Path $localRoot 'qa\restore-logs'
New-Item -ItemType Directory -Path $restoreLogRoot -Force | Out-Null
$stdoutPath = Join-Path $restoreLogRoot ($Database + '.stdout.log')
$stderrPath = Join-Path $restoreLogRoot ($Database + '.stderr.log')
$defaultsArgument = '--defaults-extra-file="{0}"' -f $rootClientPath
$arguments = @($defaultsArgument, "--database=$Database", '--batch', '--raw')
$restore = Start-Process -FilePath $mysql -ArgumentList $arguments -NoNewWindow -Wait -PassThru -RedirectStandardInput $databasePath -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
if ($restore.ExitCode -ne 0) { throw "Database restore failed: $(Get-Content -LiteralPath $stderrPath -Raw -ErrorAction SilentlyContinue)" }

if ($manifest.format -eq 'upton-pdm-backup-v2') {
    $restored = Get-PdmStorageBackupPlan $projectRoot -IncludeTableCounts -Database $Database
    foreach ($property in $manifest.databaseCounts.PSObject.Properties) {
        $actual = $restored.tableCounts.PSObject.Properties[$property.Name]
        if ($null -eq $actual -or [long]$actual.Value -ne [long]$property.Value) { throw "恢复后的数据库表数量不一致：$($property.Name)" }
    }
    $referenceIndex = @{}
    foreach ($reference in $manifest.references) { $referenceIndex[$reference.sourcePath] = $reference }
    foreach ($reference in $restored.references) {
        $expected = $referenceIndex[$reference.path]
        if ($null -eq $expected -or [long]$expected.length -ne [long]$reference.length -or ($reference.sha256 -and $expected.sha256 -ne $reference.sha256)) {
            throw "恢复后的数据库文件引用与备份不一致：$($reference.path)"
        }
    }
} else {
$secretPath = Join-Path $localRoot 'secrets\pdm-secrets.json'
$dotnet = Join-Path $projectRoot '.dotnet\dotnet.exe'
$acceptanceDll = Join-Path $projectRoot 'tools\Pdm.Acceptance\bin\Debug\net10.0\Pdm.Acceptance.dll'
$secrets = Get-Content -LiteralPath $secretPath -Raw -Encoding UTF8 | ConvertFrom-Json
$env:PDM_DB_PASSWORD = $secrets.databasePassword
$env:PDM_ACCEPTANCE_CONNECTION = "Server=127.0.0.1;Port=3308;Database=$Database;UserID=pdm_app;GuidFormat=Binary16;SslMode=None;AllowUserVariables=true;ConnectionTimeout=5;DefaultCommandTimeout=30"
Remove-Item Env:PDM_ACCEPTANCE_SEED -ErrorAction SilentlyContinue
$verificationJson = (& $dotnet $acceptanceDll | Out-String)
if ($LASTEXITCODE -ne 0) { throw 'Restored database verification failed.' }
$verification = $verificationJson | ConvertFrom-Json
$restoredCounts = $verification.tableCounts
foreach ($property in @($manifest.databaseCounts.PSObject.Properties)) {
    $actual = $restoredCounts.PSObject.Properties[$property.Name].Value
    if ($property.Name -eq 'audit_entry' -and $null -ne $manifest.databaseCountBounds.audit_entry) {
        $before = [long]$manifest.databaseCountBounds.audit_entry.before
        $after = [long]$manifest.databaseCountBounds.audit_entry.after
        $minimum = [Math]::Min($before, $after)
        $maximum = [Math]::Max($before, $after)
        if ([long]$actual -lt $minimum -or [long]$actual -gt $maximum) {
            throw "Restored audit entry count is outside the backup window: $actual (expected $minimum..$maximum)"
        }
        continue
    }
    if ([long]$actual -ne [long]$property.Value) { throw "Restored table count mismatch: $($property.Name)" }
}
if (-not $verification.expectedMigrationApplied -or $verification.releaseColumns -ne 3) { throw 'The restored database is missing phase-one schema.' }
}

$restoreDataRoot = Join-Path $localRoot (Join-Path 'qa\restored-data' ([DateTimeOffset]::Now.ToString('yyyyMMdd-HHmmssfff')))
New-Item -ItemType Directory -Path $restoreDataRoot -Force | Out-Null
$sourceData = Join-Path $backupRoot 'data'
if (Test-Path -LiteralPath $sourceData) { Copy-Item -Path (Join-Path $sourceData '*') -Destination $restoreDataRoot -Recurse -Force }
if ($manifest.format -eq 'upton-pdm-backup-v2') {
    $sourceConfiguration = Join-Path $backupRoot 'configuration'
    if (Test-Path -LiteralPath $sourceConfiguration) { Copy-Item -LiteralPath $sourceConfiguration -Destination (Join-Path $restoreDataRoot 'configuration') -Recurse -Force }
}
foreach ($file in @($manifest.files | Where-Object { $_.relativePath.StartsWith('data/') })) {
    $relative = ([string]$file.relativePath).Substring('data/'.Length).Replace('/', '\')
    $restoredPath = Join-Path $restoreDataRoot $relative
    if (-not (Test-Path -LiteralPath $restoredPath)) { throw "Restored file is missing: $relative" }
    $info = Get-Item -LiteralPath $restoredPath
    if ($info.Length -ne [long]$file.length -or (Get-FileHash -LiteralPath $restoredPath -Algorithm SHA256).Hash -ne $file.sha256) { throw "Restored file verification failed: $relative" }
}
foreach ($file in @($manifest.files | Where-Object { $_.relativePath.StartsWith('configuration/') })) {
    $restoredPath = Resolve-PdmBackupPath $restoreDataRoot $file.relativePath
    if (-not (Test-Path -LiteralPath $restoredPath) -or (Get-FileHash -LiteralPath $restoredPath -Algorithm SHA256).Hash -ne $file.sha256) { throw '恢复配置副本校验失败。' }
}

[pscustomobject]@{
    status = 'passed'
    backupPath = $backupRoot
    restoredDatabase = $Database
    restoredDataPath = $restoreDataRoot
    restoredFiles = @($manifest.files).Count
    elapsedSeconds = [Math]::Round(([DateTimeOffset]::Now - $startedAt).TotalSeconds, 3)
    migration004 = 'passed'
    tableCounts = 'passed'
    fileHashes = 'passed'
} | ConvertTo-Json -Depth 4
