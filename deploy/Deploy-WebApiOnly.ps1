[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot '.local'))
$apiSource = [IO.Path]::GetFullPath((Join-Path $localRoot 'api-next'))
$apiTarget = [IO.Path]::GetFullPath((Join-Path $localRoot 'api'))
$clientSource = [IO.Path]::GetFullPath((Join-Path $localRoot 'staged-client'))
$clientTarget = [IO.Path]::GetFullPath((Join-Path $localRoot 'client'))
$previewSource = [IO.Path]::GetFullPath((Join-Path $localRoot 'staged-preview-worker'))
$previewTarget = [IO.Path]::GetFullPath((Join-Path $localRoot 'preview-worker'))
$programTemplateRoot = [IO.Path]::GetFullPath((Join-Path $localRoot 'program-templates'))
$resultPath = Join-Path $localRoot 'deploy-webapi-only-result.json'
$errorPath = Join-Path $localRoot 'deploy-webapi-only-error.txt'
$serviceName = 'UptonPdmApi'
trap {
    ($_ | Out-String) | Set-Content -LiteralPath $errorPath -Encoding UTF8
    exit 1
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated Administrator PowerShell session.'
}

Remove-Item -LiteralPath $resultPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $errorPath -Force -ErrorAction SilentlyContinue

foreach ($path in @($apiSource, $apiTarget, $clientSource, $clientTarget, $previewSource, $previewTarget, $programTemplateRoot)) {
    if (-not $path.StartsWith($localRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Deployment path escaped .local: $path"
    }
}
New-Item -ItemType Directory -Path $programTemplateRoot -Force | Out-Null
foreach ($source in @($apiSource, $clientSource, $previewSource)) {
    if (-not (Test-Path -LiteralPath $source)) { throw "Deployment staging directory does not exist: $source" }
}

$backupRoot = Join-Path $localRoot (Join-Path 'backup' ('webapi-' + [DateTimeOffset]::Now.ToString('yyyyMMdd-HHmmss')))
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
foreach ($component in @(
    @{ Source = $apiTarget; Name = 'api' },
    @{ Source = $clientTarget; Name = 'client' },
    @{ Source = $previewTarget; Name = 'preview-worker' }
)) {
    $backupTarget = Join-Path $backupRoot $component.Name
    New-Item -ItemType Directory -Path $backupTarget -Force | Out-Null
    Copy-Item -Path (Join-Path $component.Source '*') -Destination $backupTarget -Recurse -Force
}

$serviceStopped = $false
try {
    $serviceProcessId = [int](Get-CimInstance Win32_Service -Filter "Name='$serviceName'").ProcessId
    Stop-Service -Name $serviceName -Force
    (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    $serviceStopped = $true
    if ($serviceProcessId -gt 0) {
        $serviceProcess = Get-Process -Id $serviceProcessId -ErrorAction SilentlyContinue
        if ($null -ne $serviceProcess -and -not $serviceProcess.WaitForExit(30000)) {
            throw "API service process did not exit within 30 seconds: $serviceProcessId"
        }
    }

    Copy-Item -Path (Join-Path $apiSource '*') -Destination $apiTarget -Recurse -Force
    Copy-Item -Path (Join-Path $clientSource '*') -Destination $clientTarget -Recurse -Force
    Copy-Item -Path (Join-Path $previewSource '*') -Destination $previewTarget -Recurse -Force

    $apiRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
    $serviceEnvironment = @((Get-ItemProperty -LiteralPath $apiRegistryPath -Name Environment -ErrorAction SilentlyContinue).Environment)
    $serviceEnvironment = @($serviceEnvironment | Where-Object { $_ -notlike 'Pdm__Storage__ProgramTemplateRoot=*' })
    $serviceEnvironment += "Pdm__Storage__ProgramTemplateRoot=$programTemplateRoot"
    New-ItemProperty -LiteralPath $apiRegistryPath -Name Environment -PropertyType MultiString -Value $serviceEnvironment -Force | Out-Null

    $stagedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $apiSource 'Pdm.Api.dll')).Hash
    $activeHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $apiTarget 'Pdm.Api.dll')).Hash
    if ($stagedHash -ne $activeHash) { throw 'Pdm.Api.dll hash mismatch after deployment.' }

    Start-Service -Name $serviceName
    $serviceStopped = $false
    $health = $null
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        try {
            $health = Invoke-RestMethod -Uri 'http://127.0.0.1:5080/health' -TimeoutSec 3
            if ($health.status -eq 'ok') { break }
        }
        catch {
        }
        Start-Sleep -Milliseconds 750
    }
    if ($null -eq $health -or $health.status -ne 'ok') { throw 'API health check failed after deployment.' }

    $result = [ordered]@{
        status = 'passed'
        deployedAt = [DateTimeOffset]::Now.ToString('O')
        componentBackup = $backupRoot
        apiSha256 = $activeHash
        health = $health.status
        database = $health.database
        solidWorksAddin = 'not-switched-while-solidworks-is-running'
    }
    $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $resultPath -Encoding UTF8
    $result | ConvertTo-Json -Depth 4
}
catch {
    ($_ | Out-String) | Set-Content -LiteralPath $errorPath -Encoding UTF8
    if ($serviceStopped) {
        Copy-Item -Path (Join-Path $backupRoot 'api\*') -Destination $apiTarget -Recurse -Force
        Start-Service -Name $serviceName
    }
    throw
}
