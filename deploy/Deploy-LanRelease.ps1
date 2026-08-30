[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot '.local'))
$apiSource = Join-Path $localRoot 'api-next'
$apiTarget = Join-Path $localRoot 'api'
$clientSource = Join-Path $localRoot 'staged-client'
$clientTarget = Join-Path $localRoot 'client'
$addinSource = Join-Path $localRoot 'staged-solidworks-addin'
$addinTarget = Join-Path $localRoot 'solidworks-addin'
$previewSource = Join-Path $localRoot 'staged-preview-worker'
$previewTarget = Join-Path $localRoot 'preview-worker'
$serviceName = 'UptonPdmApi'
$timestamp = [DateTimeOffset]::Now.ToString('yyyyMMdd-HHmmss')
$backupRoot = Join-Path $localRoot (Join-Path 'backup' "lan-release-$timestamp")
$resultPath = Join-Path $localRoot 'lan-deploy-result.json'
$errorPath = Join-Path $localRoot 'lan-deploy-error.txt'
Remove-Item -LiteralPath $errorPath -Force -ErrorAction SilentlyContinue
trap {
    ($_ | Out-String) | Set-Content -LiteralPath $errorPath -Encoding UTF8
    exit 1
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated Administrator PowerShell session.'
}

foreach ($path in @($apiSource, $apiTarget, $clientSource, $clientTarget, $addinSource, $addinTarget, $previewSource, $previewTarget, $backupRoot)) {
    $resolved = [IO.Path]::GetFullPath($path)
    if (-not $resolved.StartsWith($localRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Deployment path escaped .local: $resolved"
    }
}
foreach ($source in @($apiSource, $clientSource, $addinSource, $previewSource)) {
    if (-not (Test-Path -LiteralPath $source)) { throw "Deployment staging directory does not exist: $source" }
}

$bootstrap = Get-Content -LiteralPath (Join-Path $apiSource 'wwwroot\client-bootstrap.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$releaseVersion = [string]$bootstrap.ConfigurationVersion
if ([string]::IsNullOrWhiteSpace($releaseVersion)) { throw 'Staged client bootstrap has no release version.' }

New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
foreach ($component in @(
    @{ Source = $apiTarget; Name = 'api' },
    @{ Source = $clientTarget; Name = 'client' },
    @{ Source = $addinTarget; Name = 'solidworks-addin' },
    @{ Source = $previewTarget; Name = 'preview-worker' }
)) {
    $destination = Join-Path $backupRoot $component.Name
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    if (Test-Path -LiteralPath $component.Source) {
        Get-ChildItem -LiteralPath $component.Source -Force |
            Where-Object { $_.Name -ne 'Upton.Pdm.Desktop.exe.WebView2' } |
            Copy-Item -Destination $destination -Recurse -Force
    }
}

$desktopWasRunning = $false
$serviceStopped = $false
$deploymentStarted = $false
$addinDeployedImmediately = $false
$pendingAddin = $null
$deferredHelperArguments = $null
try {
    $portOwner = Get-NetTCPConnection -State Listen -LocalPort 5173 -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($portOwner) {
        $ownerProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$($portOwner.OwningProcess)" -ErrorAction SilentlyContinue
        if ($ownerProcess -and $ownerProcess.Name -eq 'node.exe' -and $ownerProcess.CommandLine -like '*vite*') {
            Stop-Process -Id $portOwner.OwningProcess -Force
        }
        elseif ($portOwner.OwningProcess -ne (Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue).ProcessId) {
            throw "Port 5173 is occupied by an unrelated process (PID $($portOwner.OwningProcess))."
        }
    }

    $desktopProcesses = @(Get-Process -Name 'Upton.Pdm.Desktop' -ErrorAction SilentlyContinue)
    $desktopWasRunning = $desktopProcesses.Count -gt 0
    foreach ($process in $desktopProcesses) { Stop-Process -Id $process.Id -Force }

    Stop-Service -Name $serviceName -Force
    $serviceStopped = $true
    (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    $deploymentStarted = $true

    Copy-Item -Path (Join-Path $apiSource '*') -Destination $apiTarget -Recurse -Force
    Copy-Item -Path (Join-Path $clientSource '*') -Destination $clientTarget -Recurse -Force
    Copy-Item -Path (Join-Path $previewSource '*') -Destination $previewTarget -Recurse -Force

    $addinState = 'deployed'
    $addinResultPath = Join-Path $localRoot 'solidworks-addin-deferred-result.json'
    if (Get-Process -Name 'SLDWORKS' -ErrorAction SilentlyContinue) {
        $pendingAddin = Join-Path $localRoot "pending-solidworks-addin-$timestamp"
        New-Item -ItemType Directory -Path $pendingAddin -Force | Out-Null
        Copy-Item -Path (Join-Path $addinSource '*') -Destination $pendingAddin -Recurse -Force
        $addinBackup = Join-Path $backupRoot 'solidworks-addin-deferred'
        $helperScript = Join-Path $PSScriptRoot 'Apply-DeferredSolidWorksAddin.ps1'
        $deferredHelperArguments = "-NoProfile -ExecutionPolicy Bypass -File `"$helperScript`" -Source `"$pendingAddin`" -Target `"$addinTarget`" -Backup `"$addinBackup`" -ResultPath `"$addinResultPath`" -Version `"$releaseVersion`""
        $addinState = 'scheduled-after-solidworks-exit'
    }
    else {
        Copy-Item -Path (Join-Path $addinSource '*') -Destination $addinTarget -Recurse -Force
        $addinDeployedImmediately = $true
    }

    foreach ($assemblyName in @('Pdm.Api.dll', 'Pdm.Domain.dll', 'Pdm.Application.dll', 'Pdm.Infrastructure.dll')) {
        $sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $apiSource $assemblyName)).Hash
        $targetHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $apiTarget $assemblyName)).Hash
        if ($sourceHash -ne $targetHash) { throw "$assemblyName hash mismatch after deployment." }
    }

    $existingRule = Get-NetFirewallRule -DisplayName 'UPLM LAN UI (5173)' -ErrorAction SilentlyContinue
    if ($existingRule) { Remove-NetFirewallRule -DisplayName 'UPLM LAN UI (5173)' }
    New-NetFirewallRule -DisplayName 'UPLM LAN UI (5173)' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5173 -RemoteAddress LocalSubnet -Profile Domain,Private | Out-Null

    Start-Service -Name $serviceName
    $serviceStopped = $false
    $health = $null
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        try {
            $health = Invoke-RestMethod -Uri 'http://127.0.0.1:5173/health' -TimeoutSec 3
            if ($health.status -eq 'ok') { break }
        }
        catch { }
        Start-Sleep -Milliseconds 750
    }
    if ($null -eq $health -or $health.status -ne 'ok') { throw 'LAN health check failed after deployment.' }

    $bootstrapProbeUrl = "http://127.0.0.1:5173/client-bootstrap.json?deployment=$([Uri]::EscapeDataString($releaseVersion))"
    $bootstrapResponse = Invoke-WebRequest -UseBasicParsing -Uri $bootstrapProbeUrl -Headers @{ 'Cache-Control' = 'no-cache' } -TimeoutSec 5
    $bootstrapJson = ([string]$bootstrapResponse.Content).TrimStart([char]0xFEFF)
    $misdecodedBom = ([char]0x00EF) + ([char]0x00BB) + ([char]0x00BF)
    if ($bootstrapJson.StartsWith($misdecodedBom)) { $bootstrapJson = $bootstrapJson.Substring(3) }
    $activeBootstrap = $bootstrapJson | ConvertFrom-Json
    $activeVersion = [string]$activeBootstrap.ConfigurationVersion
    if ($activeVersion -ne $releaseVersion) { throw "Active bootstrap version mismatch. Expected '$releaseVersion', received '$activeVersion'." }
    $uiResponse = Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:5173/' -TimeoutSec 5
    if ($uiResponse.StatusCode -ne 200) { throw 'Server UI did not return HTTP 200.' }

    if ($desktopWasRunning) {
        Start-Process -FilePath (Join-Path $clientTarget 'Upton.Pdm.Desktop.exe') -WorkingDirectory $clientTarget -WindowStyle Hidden
    }

    $result = [ordered]@{
        status = 'passed'
        deployedAt = [DateTimeOffset]::Now.ToString('O')
        releaseVersion = $releaseVersion
        lanUrl = [string]$bootstrap.UiBaseUrl
        componentBackup = $backupRoot
        health = $health.status
        database = $health.database
        apiAssemblies = [ordered]@{}
        desktopSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $clientTarget 'Upton.Pdm.Desktop.exe')).Hash
        solidWorksAddin = $addinState
        firewallScope = 'LocalSubnet Domain,Private'
    }
    foreach ($assemblyName in @('Pdm.Api.dll', 'Pdm.Domain.dll', 'Pdm.Application.dll', 'Pdm.Infrastructure.dll')) {
        $result.apiAssemblies[$assemblyName] = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $apiTarget $assemblyName)).Hash
    }
    $resultJson = $result | ConvertTo-Json -Depth 5
    $resultJson | Set-Content -LiteralPath $resultPath -Encoding UTF8
    if ($deferredHelperArguments) {
        Start-Process -FilePath 'powershell.exe' -ArgumentList $deferredHelperArguments -WindowStyle Hidden
    }
    $resultJson
}
catch {
    Remove-Item -LiteralPath $resultPath -Force -ErrorAction SilentlyContinue
    if ($deploymentStarted) {
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($service -and $service.Status -ne 'Stopped') {
            Stop-Service -Name $serviceName -Force
            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        }
        Copy-Item -Path (Join-Path $backupRoot 'api\*') -Destination $apiTarget -Recurse -Force
        Copy-Item -Path (Join-Path $backupRoot 'client\*') -Destination $clientTarget -Recurse -Force
        Copy-Item -Path (Join-Path $backupRoot 'preview-worker\*') -Destination $previewTarget -Recurse -Force
        if ($addinDeployedImmediately) {
            Copy-Item -Path (Join-Path $backupRoot 'solidworks-addin\*') -Destination $addinTarget -Recurse -Force
        }
        if ($pendingAddin -and (Test-Path -LiteralPath $pendingAddin)) {
            Remove-Item -LiteralPath $pendingAddin -Recurse -Force
        }
        Start-Service -Name $serviceName
    }
    elseif ($serviceStopped) {
        Start-Service -Name $serviceName
    }
    ($_ | Out-String) | Set-Content -LiteralPath $errorPath -Encoding UTF8
    throw
}
