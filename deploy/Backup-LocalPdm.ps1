[CmdletBinding()]
param(
    [string]$DestinationRoot
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localRoot = Join-Path $projectRoot '.local'
$receiptPath = Join-Path $localRoot 'deployment-receipt.json'
$rootClientPath = Join-Path $localRoot 'secrets\mysql-root-client.ini'
if (-not (Test-Path -LiteralPath $receiptPath) -or -not (Test-Path -LiteralPath $rootClientPath)) {
    throw 'PDM deployment receipt or protected MySQL client file is missing.'
}

if ([string]::IsNullOrWhiteSpace($DestinationRoot)) {
    $DestinationRoot = Join-Path $localRoot (Join-Path 'backup' ('full-' + [DateTimeOffset]::Now.ToString('yyyyMMdd-HHmmss')))
}
$backupRoot = [IO.Path]::GetFullPath($DestinationRoot)
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $localRoot 'backup')) + [IO.Path]::DirectorySeparatorChar
if (-not ($backupRoot + [IO.Path]::DirectorySeparatorChar).StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The backup directory must be under .local\backup.'
}
if (Test-Path -LiteralPath $backupRoot) { throw "The backup directory already exists: $backupRoot" }

$receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
$mysqlDump = Join-Path $receipt.mysqlHome 'bin\mysqldump.exe'
if (-not (Test-Path -LiteralPath $mysqlDump)) {
    throw 'PDM MySQL client binaries are missing.'
}

New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
try {
$databasePath = Join-Path $backupRoot 'pdm.sql'
& $mysqlDump "--defaults-extra-file=$rootClientPath" --single-transaction --routines --triggers --hex-blob "--result-file=$databasePath" pdm
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $databasePath)) { throw 'PDM database backup failed.' }
$dumpText = [IO.File]::ReadAllText($databasePath, [Text.Encoding]::UTF8)
$signalTriggerPattern = '(?m)(/\*!\d+ TRIGGER [^\r\n]*SIGNAL SQLSTATE [^\r\n]*); (\*/;;)$'
$dumpText = [Text.RegularExpressions.Regex]::Replace($dumpText, $signalTriggerPattern, '$1 $2')
[IO.File]::WriteAllText($databasePath, $dumpText, (New-Object Text.UTF8Encoding($false)))

$dataRoot = Join-Path $backupRoot 'data'
foreach ($name in @('vault', 'release')) {
    $source = Join-Path $localRoot $name
    if (Test-Path -LiteralPath $source) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $dataRoot $name) -Recurse -Force
    }
}

$secretPath = Join-Path $localRoot 'secrets\pdm-secrets.json'
$dotnet = Join-Path $projectRoot '.dotnet\dotnet.exe'
$acceptanceDll = Join-Path $projectRoot 'tools\Pdm.Acceptance\bin\Debug\net10.0\Pdm.Acceptance.dll'
$secrets = Get-Content -LiteralPath $secretPath -Raw -Encoding UTF8 | ConvertFrom-Json
$env:PDM_DB_PASSWORD = $secrets.databasePassword
$env:PDM_ACCEPTANCE_CONNECTION = 'Server=127.0.0.1;Port=3308;Database=pdm;UserID=pdm_app;GuidFormat=Binary16;SslMode=None;AllowUserVariables=true;ConnectionTimeout=5;DefaultCommandTimeout=30'
Remove-Item Env:PDM_ACCEPTANCE_SEED -ErrorAction SilentlyContinue
$verificationJson = (& $dotnet $acceptanceDll | Out-String)
if ($LASTEXITCODE -ne 0) { throw 'PDM database verification failed.' }
$verification = $verificationJson | ConvertFrom-Json
if (-not $verification.expectedMigrationApplied -or $verification.releaseColumns -ne 3) { throw 'PDM database schema verification failed.' }
$databaseCounts = $verification.tableCounts

$files = @()
foreach ($file in @(Get-ChildItem -LiteralPath $dataRoot -Recurse -File -ErrorAction SilentlyContinue)) {
    $files += [ordered]@{
        relativePath = $file.FullName.Substring($backupRoot.Length + 1).Replace('\', '/')
        length = $file.Length
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
}
$manifest = [ordered]@{
    format = 'upton-pdm-backup-v1'
    createdAt = [DateTimeOffset]::Now.ToString('O')
    sourceDatabase = 'pdm'
    databaseDump = [ordered]@{ relativePath = 'pdm.sql'; length = (Get-Item -LiteralPath $databasePath).Length; sha256 = (Get-FileHash -LiteralPath $databasePath -Algorithm SHA256).Hash }
    databaseCounts = $databaseCounts
    files = $files
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $backupRoot 'manifest.json') -Encoding UTF8

[pscustomobject]@{
    status = 'passed'
    backupPath = $backupRoot
    databaseBytes = $manifest.databaseDump.length
    dataFiles = $files.Count
    dataBytes = (@($files | ForEach-Object { $_.length }) | Measure-Object -Sum).Sum
    databaseCounts = $databaseCounts
} | ConvertTo-Json -Depth 5
}
catch {
    if (Test-Path -LiteralPath $backupRoot) { Remove-Item -LiteralPath $backupRoot -Recurse -Force }
    throw
}
