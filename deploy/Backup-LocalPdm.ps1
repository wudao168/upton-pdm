[CmdletBinding()]
param([string]$DestinationRoot, [switch]$PlanOnly)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localRoot = Join-Path $projectRoot '.local'
. (Join-Path $PSScriptRoot 'StorageBackup.Common.ps1')
$plan = Get-PdmStorageBackupPlan $projectRoot
if ($PlanOnly) {
    [pscustomobject]@{ status = 'planned'; backupRoot = $plan.backupRoot; database = $plan.database; sources = $plan.sources; references = @($plan.references).Count; issues = $plan.issues; warnings = $plan.warnings; executed = $false } | ConvertTo-Json -Depth 8
    return
}
if ([string]::IsNullOrWhiteSpace($plan.backupRoot)) { throw '备份目标位置尚未确认，请先在系统设置中配置；未开始备份。' }
$configuredRoot = [IO.Path]::GetFullPath($plan.backupRoot).TrimEnd('\', '/')
if ($DestinationRoot -and -not [IO.Path]::GetFullPath($DestinationRoot).TrimEnd('\', '/').Equals($configuredRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw '备份目标必须与系统设置一致，请先在系统设置中确认目标地址。'
}
Assert-PdmOrdinaryBackupPath $configuredRoot
$backupRoot = Join-Path $configuredRoot ('full-' + [DateTimeOffset]::Now.ToString('yyyyMMdd-HHmmssfff'))
foreach ($source in $plan.sources) {
    $sourceRoot = [IO.Path]::GetFullPath($source.path).TrimEnd('\', '/')
    if (($configuredRoot + '\').StartsWith($sourceRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or ($sourceRoot + '\').StartsWith($configuredRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw '备份目录与源文件目录存在包含关系，已停止，防止递归复制。'
    }
}
$receipt = Get-Content -LiteralPath (Join-Path $localRoot 'deployment-receipt.json') -Raw | ConvertFrom-Json
$mysqlDump = Resolve-PdmMySqlTool $projectRoot $receipt 'mysqldump.exe'
$rootClientPath = Join-Path $localRoot 'secrets\mysql-root-client.ini'
if (-not (Test-Path -LiteralPath $mysqlDump) -or -not (Test-Path -LiteralPath $rootClientPath)) { throw '缺少数据库备份程序或受保护的连接配置。' }
if (Test-Path -LiteralPath $backupRoot) { throw '本次备份目录已存在，请重新执行以创建独立批次。' }
$apiRuntime = Get-PdmBackupApiRuntime $projectRoot
$workerPause = Join-Path $localRoot 'cad-archive-worker\storage-roots.txt.pause'
$workerPaused = $workerPause + 'd'
$pauseRequested = $false
try {
    Suspend-PdmBackupApiRuntime $apiRuntime
    if (Get-Process -Name 'Upton.Pdm.CadArchiveWorker' -ErrorAction SilentlyContinue) {
        Remove-Item -LiteralPath $workerPaused -Force -ErrorAction SilentlyContinue
        [IO.File]::WriteAllText($workerPause, [DateTimeOffset]::Now.ToString('O'))
        $pauseRequested = $true
        $deadline = [DateTimeOffset]::Now.AddSeconds(60)
        while (-not (Test-Path -LiteralPath $workerPaused) -and [DateTimeOffset]::Now -lt $deadline) { Start-Sleep -Milliseconds 500 }
        if (-not (Test-Path -LiteralPath $workerPaused)) { throw '网页存档后台仍在处理任务，未能进入备份暂停状态；请任务完成后重试，本次未备份。' }
    }
    $plan = Get-PdmStorageBackupPlan $projectRoot -IncludeTableCounts
    if ($plan.backupRoot -ne $configuredRoot) { throw '备份配置已变化，尚未复制文件，请重新生成清单。' }
    New-Item -ItemType Directory -Path (Join-Path $backupRoot 'database') -Force | Out-Null
    $incomplete = Join-Path $backupRoot 'backup.incomplete.json'
    @{ status = 'incomplete'; startedAt = [DateTimeOffset]::Now.ToString('O') } | ConvertTo-Json | Set-Content -LiteralPath $incomplete -Encoding UTF8
    $databasePath = Join-Path $backupRoot 'database\pdm.sql'
    Assert-PdmBackupApiQuiescent $apiRuntime
    & $mysqlDump "--defaults-extra-file=$rootClientPath" --single-transaction --routines --triggers --events --hex-blob "--result-file=$databasePath" $plan.database
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $databasePath)) { throw '数据库导出失败，本批次不算完整备份。' }
    # Keep executable trigger comments restorable by the bundled MySQL client.
    $dumpText = [IO.File]::ReadAllText($databasePath, [Text.Encoding]::UTF8)
    $dumpText = ConvertTo-PdmRestorableSqlDump $dumpText
    [IO.File]::WriteAllText($databasePath, $dumpText, [Text.UTF8Encoding]::new($false))
    $copied = Copy-PdmStorageSources $plan $backupRoot
    Assert-PdmBackupApiQuiescent $apiRuntime
    $configuration = Join-Path $backupRoot 'configuration'
    New-Item -ItemType Directory -Path $configuration -Force | Out-Null
    Copy-PdmBackupApiConfiguration $apiRuntime $configuration
    Copy-Item -LiteralPath (Join-Path $localRoot 'mysql\my.ini') -Destination (Join-Path $configuration 'mysql.ini')
    Copy-Item -LiteralPath (Join-Path $localRoot 'deployment-receipt.json') -Destination (Join-Path $configuration 'deployment-receipt.json')
    if (Get-ScheduledTask -TaskName 'UPLM-BrowserCadArchive' -ErrorAction SilentlyContinue) {
        Export-ScheduledTask -TaskName 'UPLM-BrowserCadArchive' | Set-Content -LiteralPath (Join-Path $configuration 'browser-cad-task.xml') -Encoding UTF8
    }
    $configurationFiles = @(Get-ChildItem -LiteralPath $configuration -File | ForEach-Object {
        [pscustomobject]@{ relativePath = 'configuration/' + $_.Name; length = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    $manifest = [ordered]@{
        format = 'upton-pdm-backup-v2'; createdAt = [DateTimeOffset]::Now.ToString('O'); sourceDatabase = $plan.database
        consistency = 'API实例停止且本仓库自动拉起任务暂停期间导出数据库并复制文件'; sourceMachine = $env:COMPUTERNAME
        databaseDump = @{ relativePath = 'database/pdm.sql'; length = (Get-Item -LiteralPath $databasePath).Length; sha256 = (Get-FileHash -LiteralPath $databasePath -Algorithm SHA256).Hash }
        sources = $copied.sources; files = @($copied.files) + $configurationFiles; references = $copied.references; databaseCounts = $plan.tableCounts
        warnings = $plan.warnings; historicalFilesPreserved = $true
    }
    Test-PdmStorageBackupFiles $manifest $backupRoot
    $manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $backupRoot 'manifest.json') -Encoding UTF8
    Remove-Item -LiteralPath $incomplete -Force
    [pscustomobject]@{ status = 'passed'; backupPath = $backupRoot; files = @($manifest.files).Count; references = @($manifest.references).Count; databaseBytes = $manifest.databaseDump.length; warnings = $plan.warnings } | ConvertTo-Json -Depth 5
} catch {
    # Keep partial files for diagnosis. Never publish a complete manifest or remove historical backups on failure.
    if (Test-Path -LiteralPath $backupRoot) {
        @{ status = 'incomplete'; failedAt = [DateTimeOffset]::Now.ToString('O'); reason = $_.Exception.Message; sourceMap = 'source-map.json'; copiedFileIndex = 'files-index.json' } |
            ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $backupRoot 'backup.incomplete.json') -Encoding UTF8
    }
    throw
} finally {
    try { Restore-PdmBackupApiRuntime $apiRuntime }
    finally { if ($pauseRequested) { Remove-Item -LiteralPath $workerPause -Force -ErrorAction SilentlyContinue } }
}
