[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localRoot = Join-Path $projectRoot '.local'
$receiptPath = Join-Path $localRoot 'deployment-receipt.json'
$secretPath = Join-Path $localRoot 'secrets\pdm-secrets.json'
$rootClientPath = Join-Path $localRoot 'secrets\mysql-root-client.ini'
$myIniPath = Join-Path $localRoot 'mysql\my.ini'
$mysqlServiceName = 'UptonPdmMySQL'
$apiServiceName = 'UptonPdmApi'
$migrationMarker = Join-Path $localRoot 'mysql\credentials-initialized'
$seedMarker = Join-Path $localRoot 'mysql\demo-project-seeded'
$transcriptPath = Join-Path $localRoot 'install-services.log'
Start-Transcript -Path $transcriptPath -Append | Out-Null

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an elevated Administrator PowerShell session.'
    }
}

function Wait-Port([int]$port, [int]$timeoutSeconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSeconds)
    do {
        $listening = netstat -ano -p TCP | Select-String -Pattern "127\.0\.0\.1:$port\s+.*LISTENING"
        if ($listening) {
            return
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Port $port did not become ready within $timeoutSeconds seconds."
}

function Wait-Health([int]$timeoutSeconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSeconds)
    do {
        try {
            $health = Invoke-RestMethod -Uri 'http://127.0.0.1:5080/health' -TimeoutSec 3
            if ($health.status -eq 'ok' -and $health.database -eq 'MySql') {
                return $health
            }
        }
        catch {
        }
        Start-Sleep -Milliseconds 750
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'PDM API did not report a healthy MySQL connection within the timeout.'
}

function Run-RootSql([string]$sql) {
    $temporarySql = Join-Path $localRoot 'mysql\setup.sql'
    try {
        $sql | Set-Content -LiteralPath $temporarySql -Encoding UTF8
        Get-Content -LiteralPath $temporarySql -Raw -Encoding UTF8 | & $mysqlClient "--defaults-extra-file=$rootClientPath" --batch --raw
        if ($LASTEXITCODE -ne 0) {
            throw "MySQL SQL command failed with exit code $LASTEXITCODE"
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporarySql) {
            Remove-Item -LiteralPath $temporarySql -Force
        }
    }
}

Assert-Administrator
if (-not (Test-Path -LiteralPath $receiptPath) -or -not (Test-Path -LiteralPath $secretPath)) {
    throw 'Prepared deployment files are missing. Run Prepare-LocalDeployment.ps1 first.'
}

$receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
$secrets = Get-Content -LiteralPath $secretPath -Raw -Encoding UTF8 | ConvertFrom-Json
$mysqldPath = Join-Path $receipt.mysqlHome 'bin\mysqld.exe'
$mysqlClient = Join-Path $receipt.mysqlHome 'bin\mysql.exe'
$mysqlDump = Join-Path $receipt.mysqlHome 'bin\mysqldump.exe'
$dotnetPath = Join-Path $projectRoot '.dotnet\dotnet.exe'
$preparedApiUpgrade = Join-Path $localRoot 'api-next'
$preparedClientUpgrade = Join-Path $localRoot 'staged-client'
$preparedAddinUpgrade = Join-Path $localRoot 'staged-solidworks-addin'
$hasPreparedApiUpgrade = Test-Path -LiteralPath (Join-Path $preparedApiUpgrade 'Pdm.Api.dll')
$hasPreparedClientUpgrade = Test-Path -LiteralPath (Join-Path $preparedClientUpgrade 'Upton.Pdm.Desktop.exe')
$hasPreparedAddinUpgrade = Test-Path -LiteralPath (Join-Path $preparedAddinUpgrade 'Upton.Pdm.SolidWorks.Addin.dll')
$hasPreparedUpgrade = $hasPreparedApiUpgrade -or $hasPreparedClientUpgrade -or $hasPreparedAddinUpgrade

if (-not (Test-Path -LiteralPath $mysqldPath) -or -not (Test-Path -LiteralPath $mysqlClient) -or -not (Test-Path -LiteralPath $mysqlDump)) {
    throw 'MySQL binaries are missing from the prepared runtime.'
}

if ($hasPreparedUpgrade -and -not ($hasPreparedApiUpgrade -and $hasPreparedClientUpgrade -and $hasPreparedAddinUpgrade)) {
    throw 'The PDM upgrade is incomplete. API, Windows client and SolidWorks add-in must be staged together.'
}

if ($hasPreparedUpgrade) {
    $blockingProcesses = Get-Process -Name 'SLDWORKS', 'Upton.Pdm.Desktop' -ErrorAction SilentlyContinue
    if ($blockingProcesses) {
        $names = ($blockingProcesses | Select-Object -ExpandProperty ProcessName -Unique) -join ', '
        throw "Close the PDM Windows client and SolidWorks before the three-part upgrade. Running: $names"
    }
}

$mysqlService = Get-Service -Name $mysqlServiceName -ErrorAction SilentlyContinue
if ($null -eq $mysqlService) {
    & $mysqldPath --install $mysqlServiceName "--defaults-file=$myIniPath"
    if ($LASTEXITCODE -ne 0) {
        throw "MySQL service installation failed with exit code $LASTEXITCODE"
    }
}
& sc.exe config $mysqlServiceName start= auto | Out-Null
& sc.exe description $mysqlServiceName 'UPTON PDM isolated MySQL 8.4 instance on 127.0.0.1:3308' | Out-Null
& sc.exe failure $mysqlServiceName reset= 86400 actions= restart/5000/restart/15000/restart/30000 | Out-Null
Start-Service -Name $mysqlServiceName
Wait-Port 3308 60

if (-not (Test-Path -LiteralPath $migrationMarker)) {
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & $mysqlClient "--defaults-extra-file=$rootClientPath" --execute 'SELECT 1' 2>$null
    $rootClientExitCode = $LASTEXITCODE
    $ErrorActionPreference = $previousErrorActionPreference
    if ($rootClientExitCode -ne 0) {
        $setupSql = @"
ALTER USER 'root'@'localhost' IDENTIFIED BY '$($secrets.mysqlRootPassword)';
CREATE DATABASE IF NOT EXISTS pdm CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
CREATE USER IF NOT EXISTS 'pdm_app'@'127.0.0.1' IDENTIFIED BY '$($secrets.databasePassword)';
ALTER USER 'pdm_app'@'127.0.0.1' IDENTIFIED BY '$($secrets.databasePassword)';
GRANT ALL PRIVILEGES ON pdm.* TO 'pdm_app'@'127.0.0.1';
FLUSH PRIVILEGES;
"@
        $setupSql | & $mysqlClient --protocol=TCP --host=127.0.0.1 --port=3308 --user=root --batch --raw
        if ($LASTEXITCODE -ne 0) {
            throw "Initial MySQL account setup failed with exit code $LASTEXITCODE"
        }
    }
    New-Item -ItemType File -Path $migrationMarker -Force | Out-Null
}

$apiBinaryPath = '"{0}" "{1}"' -f $dotnetPath, $receipt.apiPath
$apiService = Get-Service -Name $apiServiceName -ErrorAction SilentlyContinue
if ($hasPreparedUpgrade) {
    $backupRoot = Join-Path $localRoot (Join-Path 'backup' ([DateTimeOffset]::Now.ToString('yyyyMMdd-HHmmss')))
    $databaseBackupPath = Join-Path $backupRoot 'pdm.sql'
    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
    foreach ($component in @('api', 'client', 'solidworks-addin')) {
        $source = Join-Path $localRoot $component
        if (Test-Path -LiteralPath $source) {
            Copy-Item -LiteralPath $source -Destination (Join-Path $backupRoot $component) -Recurse -Force
        }
    }
    & $mysqlDump "--defaults-extra-file=$rootClientPath" --single-transaction --routines --triggers --hex-blob "--result-file=$databaseBackupPath" pdm
    if ($LASTEXITCODE -ne 0) {
        throw "PDM database backup failed with exit code $LASTEXITCODE. Upgrade files were not switched."
    }
    $dataBackupRoot = Join-Path $backupRoot 'data'
    foreach ($dataName in @('vault', 'release')) {
        $dataSource = Join-Path $localRoot $dataName
        if (Test-Path -LiteralPath $dataSource) {
            Copy-Item -LiteralPath $dataSource -Destination (Join-Path $dataBackupRoot $dataName) -Recurse -Force
        }
    }
}

if ($hasPreparedApiUpgrade -and $null -ne $apiService -and $apiService.Status -ne 'Stopped') {
    & sc.exe failure $apiServiceName reset= 0 actions= '' | Out-Null
    try {
        Stop-Service -Name $apiServiceName -Force -ErrorAction Stop
        $apiService.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    catch {
        $serviceProcess = Get-CimInstance Win32_Service -Filter "Name='$apiServiceName'"
        if ($null -ne $serviceProcess -and $serviceProcess.ProcessId -gt 0) {
            Stop-Process -Id $serviceProcess.ProcessId -Force
            Start-Sleep -Seconds 2
        }
        $apiService.Refresh()
        if ($apiService.Status -ne 'Stopped') {
            throw "Unable to stop $apiServiceName for an in-place update."
        }
    }
}

if ($hasPreparedApiUpgrade) {
    Copy-Item -Path (Join-Path $preparedApiUpgrade '*') -Destination (Join-Path $localRoot 'api') -Recurse -Force
    Remove-Item -LiteralPath $preparedApiUpgrade -Recurse -Force
    Copy-Item -Path (Join-Path $preparedClientUpgrade '*') -Destination (Join-Path $localRoot 'client') -Recurse -Force
    Remove-Item -LiteralPath $preparedClientUpgrade -Recurse -Force
    Copy-Item -Path (Join-Path $preparedAddinUpgrade '*') -Destination (Join-Path $localRoot 'solidworks-addin') -Recurse -Force
    Remove-Item -LiteralPath $preparedAddinUpgrade -Recurse -Force
}

if ($null -eq $apiService) {
    New-Service -Name $apiServiceName -BinaryPathName $apiBinaryPath -DisplayName 'UPTON PDM API' -Description 'UPTON PDM API on 127.0.0.1:5080' -StartupType Automatic | Out-Null
}
else {
    & sc.exe config $apiServiceName "binPath= $apiBinaryPath" start= auto | Out-Null
}
& sc.exe config $apiServiceName depend= $mysqlServiceName | Out-Null
& sc.exe failure $apiServiceName reset= 86400 actions= restart/5000/restart/15000/restart/30000 | Out-Null

$apiEnvironment = @(
    'ASPNETCORE_ENVIRONMENT=Production',
    "PDM_DB_PASSWORD=$($secrets.databasePassword)",
    "PDM_JWT_SIGNING_KEY=$($secrets.jwtSigningKey)",
    "PDM_BOOTSTRAP_ADMIN_PASSWORD=$($secrets.bootstrapAdminPassword)",
    "Pdm__Storage__UploadTempRoot=$(Join-Path $localRoot 'uploads')"
)
$apiRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$apiServiceName"
New-ItemProperty -LiteralPath $apiRegistryPath -Name Environment -PropertyType MultiString -Value $apiEnvironment -Force | Out-Null

Start-Service -Name $apiServiceName
$health = Wait-Health 90

if (-not (Test-Path -LiteralPath $seedMarker)) {
    $vaultLocation = (Join-Path $localRoot 'vault\PRJ-2026-018').Replace('\', '\\')
    $releaseLocation = (Join-Path $localRoot 'release\PRJ-2026-018').Replace('\', '\\')
    $rootJson = '{"nodeId":"33333333-3333-3333-3333-333333333334","documentId":"22222222-2222-2222-2222-222222222222","instancePath":"A01-000","fileName":"A01-000.SLDASM","displayName":"Demo Assembly","kind":0,"configuration":"Default","quantity":1,"status":0,"revision":{"baseRevision":null,"workIteration":1,"isReleased":false},"checkedOutBy":null,"children":[]}'
    $seedSql = @"
USE pdm;
INSERT INTO project(id, code, name, owner, vault_location, release_location, is_active, created_at, updated_at)
VALUES(UUID_TO_BIN('11111111-1111-1111-1111-111111111111'), 'PRJ-2026-018', 'Automatic Assembly Line', 'Administrator', '$vaultLocation', '$releaseLocation', 1, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE name=VALUES(name), vault_location=VALUES(vault_location), release_location=VALUES(release_location), updated_at=UTC_TIMESTAMP(6);
INSERT INTO document(id, project_id, drawing_number, name, file_name, kind, lifecycle_state, revision_label, created_at, updated_at)
VALUES
(UUID_TO_BIN('22222222-2222-2222-2222-222222222222'), UUID_TO_BIN('11111111-1111-1111-1111-111111111111'), 'A01-000', 'Main Assembly', 'A01-000.SLDASM', 'Assembly', 'Work', 'W1', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)),
(UUID_TO_BIN('22222222-2222-2222-2222-222222222223'), UUID_TO_BIN('11111111-1111-1111-1111-111111111111'), 'A01-100', 'Frame Assembly', 'A01-100.SLDASM', 'Assembly', 'Released', 'A', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)),
(UUID_TO_BIN('22222222-2222-2222-2222-222222222224'), UUID_TO_BIN('11111111-1111-1111-1111-111111111111'), 'A01-101', 'Base Plate', 'A01-101.SLDPRT', 'Part', 'Released', 'A', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)),
(UUID_TO_BIN('22222222-2222-2222-2222-222222222225'), UUID_TO_BIN('11111111-1111-1111-1111-111111111111'), 'A01-100', 'Frame Drawing', 'A01-100.SLDDRW', 'Drawing', 'Released', 'A', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE name=VALUES(name), lifecycle_state=VALUES(lifecycle_state), revision_label=VALUES(revision_label), updated_at=UTC_TIMESTAMP(6);
INSERT INTO reference_snapshot(id, project_id, root_document_id, captured_at, captured_by, sha256, root_json)
SELECT UUID_TO_BIN('33333333-3333-3333-3333-333333333333'), UUID_TO_BIN('11111111-1111-1111-1111-111111111111'), UUID_TO_BIN('22222222-2222-2222-2222-222222222222'), UTC_TIMESTAMP(6), 'system', REPEAT('0', 64), '$rootJson'
WHERE NOT EXISTS (SELECT 1 FROM reference_snapshot WHERE project_id=UUID_TO_BIN('11111111-1111-1111-1111-111111111111'));
INSERT INTO bom_item(id, project_id, bom_kind, sequence_no, drawing_number, name, quantity, unit, material, specification, revision_label, is_complete, updated_at)
VALUES
(UUID_TO_BIN('55555555-5555-5555-5555-555555555551'), UUID_TO_BIN('11111111-1111-1111-1111-111111111111'), 'Standard', 1, 'A01-100', 'Frame Assembly', 1, 'set', NULL, 'Assembly', 'A', 1, UTC_TIMESTAMP(6)),
(UUID_TO_BIN('55555555-5555-5555-5555-555555555552'), UUID_TO_BIN('11111111-1111-1111-1111-111111111111'), 'NonStandard', 1, 'A01-101', 'Base Plate', 2, 'piece', 'Q235B', '12mm', 'A', 1, UTC_TIMESTAMP(6)),
(UUID_TO_BIN('55555555-5555-5555-5555-555555555553'), UUID_TO_BIN('11111111-1111-1111-1111-111111111111'), 'Electrical', 1, 'E01-001', 'Control Cabinet', 1, 'set', NULL, 'Standard', 'A', 1, UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE name=VALUES(name), quantity=VALUES(quantity), material=VALUES(material), specification=VALUES(specification), revision_label=VALUES(revision_label), is_complete=VALUES(is_complete), updated_at=UTC_TIMESTAMP(6);
"@
    Run-RootSql $seedSql
    New-Item -ItemType File -Path $seedMarker -Force | Out-Null
}

$apiEnvironmentWithoutBootstrap = $apiEnvironment | Where-Object { $_ -notlike 'PDM_BOOTSTRAP_ADMIN_PASSWORD=*' }
Set-ItemProperty -LiteralPath $apiRegistryPath -Name Environment -Value $apiEnvironmentWithoutBootstrap

$regAsmPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'
$addinPath = $receipt.addinPath
$tlbPath = [IO.Path]::ChangeExtension($addinPath, '.tlb')
$solidWorksInstallDir = 'C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS'
if (-not (Test-Path -LiteralPath $regAsmPath) -or -not (Test-Path -LiteralPath $addinPath) -or -not (Test-Path -LiteralPath $solidWorksInstallDir)) {
    throw 'RegAsm, the prepared SolidWorks add-in, or the SolidWorks install directory was not found.'
}

$addinDirectory = Split-Path -Parent $addinPath
$solidWorksInteropFiles = @(
    'SolidWorks.Interop.sldworks.dll',
    'SolidWorks.Interop.swconst.dll',
    'SolidWorks.Interop.swpublished.dll'
)
foreach ($interopFile in $solidWorksInteropFiles) {
    $sourcePath = Join-Path $solidWorksInstallDir $interopFile
    if (-not (Test-Path -LiteralPath $sourcePath)) {
        throw "Required SolidWorks interop assembly was not found: $sourcePath"
    }

    Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $addinDirectory $interopFile) -Force
}

& $regAsmPath $addinPath /codebase "/tlb:$tlbPath"
if ($LASTEXITCODE -ne 0) {
    throw "SolidWorks add-in registration failed with exit code $LASTEXITCODE"
}

$interactiveUser = (Get-CimInstance Win32_ComputerSystem).UserName
if ([string]::IsNullOrWhiteSpace($interactiveUser)) {
    $interactiveUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
}
$interactiveUserSid = ([Security.Principal.NTAccount]::new($interactiveUser)).Translate([Security.Principal.SecurityIdentifier]).Value
$addinStartupRegistryPath = "HKU\$interactiveUserSid\Software\SolidWorks\AddInsStartup\{BCFD8A8A-472B-42E2-AC62-58BC17773650}"
& reg.exe add $addinStartupRegistryPath /ve /t REG_DWORD /d 1 /f | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "SolidWorks add-in startup registration failed with exit code $LASTEXITCODE"
}

$status = [ordered]@{
    installedAt = [DateTimeOffset]::Now.ToString('O')
    mysqlService = (Get-Service -Name $mysqlServiceName).Status.ToString()
    apiService = (Get-Service -Name $apiServiceName).Status.ToString()
    healthStatus = $health.status
    database = $health.database
    apiPort = $health.apiPort
    mysqlPort = $health.mysqlPort
    addinRegistered = Test-Path -LiteralPath 'HKLM:\SOFTWARE\SOLIDWORKS\Addins\{BCFD8A8A-472B-42E2-AC62-58BC17773650}'
    addinStartupUserSid = $interactiveUserSid
}
$status | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $localRoot 'installation-status.json') -Encoding UTF8
Write-Host 'UPTON PDM local services and SolidWorks add-in are installed.'
Stop-Transcript | Out-Null
