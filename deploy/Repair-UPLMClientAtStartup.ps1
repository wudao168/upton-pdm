[CmdletBinding()]
param(
    [string]$ServerBaseUrl = 'http://10.7.7.62:5173',
    [string]$UsersRoot = (Join-Path $env:SystemDrive 'Users'),
    [string]$RecoveryRoot = (Join-Path $env:ProgramData 'UPLM\fleet-recovery')
)

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this recovery script as Administrator or as a computer-startup SYSTEM task.'
}

$running = @(Get-Process -Name 'SLDWORKS', 'Upton.Pdm.Desktop' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    throw 'UPLM or SolidWorks is still running. Use this script as a computer startup task before users sign in.'
}

function Convert-ToReleaseVersion([string]$Value) {
    $normalized = ([string]$Value).Trim().TrimStart('V', 'v')
    $qualifierIndex = $normalized.IndexOf('-')
    if ($qualifierIndex -ge 0) { $normalized = $normalized.Substring(0, $qualifierIndex) }
    $parsed = $null
    if (-not [version]::TryParse($normalized, [ref]$parsed)) { return $null }
    return $parsed
}

function Test-UpdateRequired([string]$TargetDirectory, [string]$AvailableVersion) {
    $available = Convert-ToReleaseVersion $AvailableVersion
    if ($null -eq $available) { throw "Invalid server release version: $AvailableVersion" }
    $versionFile = Join-Path $TargetDirectory '.uplm-version'
    if (-not (Test-Path -LiteralPath $versionFile -PathType Leaf)) { return $true }
    $installedText = (Get-Content -LiteralPath $versionFile -Raw -Encoding UTF8).Trim()
    $installed = Convert-ToReleaseVersion $installedText
    return $null -eq $installed -or $available -gt $installed
}

function Copy-DirectoryWithRetry([string]$Source, [string]$Destination) {
    $lastError = $null
    for ($attempt = 1; $attempt -le 30; $attempt++) {
        try {
            New-Item -ItemType Directory -Path $Destination -Force | Out-Null
            Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {
                Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force -ErrorAction Stop
            }
            return
        }
        catch {
            $lastError = $_
            Start-Sleep -Seconds 1
        }
    }
    throw $lastError
}

function Get-VerifiedPackage([string]$Name, $Package, [string]$CacheRoot) {
    if ([string]::IsNullOrWhiteSpace([string]$Package.Version) -or
        [string]::IsNullOrWhiteSpace([string]$Package.PackageUrl) -or
        [string]::IsNullOrWhiteSpace([string]$Package.Sha256)) {
        throw "Server package metadata is incomplete: $Name"
    }

    $packageUri = [Uri]::new([Uri]($ServerBaseUrl.TrimEnd('/') + '/'), [string]$Package.PackageUrl)
    $archivePath = Join-Path $CacheRoot ($Name + '.zip')
    Invoke-WebRequest -Uri $packageUri.AbsoluteUri -OutFile $archivePath -UseBasicParsing -TimeoutSec 600
    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    if (-not [string]::Equals($actualHash, [string]$Package.Sha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Package hash validation failed: $Name"
    }

    $payloadPath = Join-Path $CacheRoot ($Name + '-payload')
    if (Test-Path -LiteralPath $payloadPath) { Remove-Item -LiteralPath $payloadPath -Recurse -Force }
    Expand-Archive -LiteralPath $archivePath -DestinationPath $payloadPath -Force
    $payloadVersionFile = Join-Path $payloadPath '.uplm-version'
    if (-not (Test-Path -LiteralPath $payloadVersionFile -PathType Leaf)) {
        throw "Package version marker is missing: $Name"
    }
    $payloadVersion = (Get-Content -LiteralPath $payloadVersionFile -Raw -Encoding UTF8).Trim()
    if (-not [string]::Equals($payloadVersion, [string]$Package.Version, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Package version validation failed: $Name"
    }
    return $payloadPath
}

function Move-StaleUpdateState([string]$LocalRoot, [string]$Component, [string]$QuarantineRoot) {
    $updateRoot = Join-Path $LocalRoot 'updates'
    $pendingPath = Join-Path $updateRoot ($Component + '-pending.json')
    foreach ($path in @($pendingPath, $pendingPath + '.launched', $pendingPath + '.error.txt')) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        New-Item -ItemType Directory -Path $QuarantineRoot -Force | Out-Null
        Move-Item -LiteralPath $path -Destination (Join-Path $QuarantineRoot ([IO.Path]::GetFileName($path))) -Force
    }
}

$bootstrapUrl = $ServerBaseUrl.TrimEnd('/') + '/client-bootstrap.json?t=' + [DateTimeOffset]::Now.ToUnixTimeSeconds()
$bootstrap = Invoke-RestMethod -Uri $bootstrapUrl -TimeoutSec 30
$configurationVersion = [string]$bootstrap.ConfigurationVersion
if ([string]::IsNullOrWhiteSpace($configurationVersion)) { throw 'Server bootstrap has no configuration version.' }

$cacheRoot = Join-Path $RecoveryRoot $configurationVersion
New-Item -ItemType Directory -Path $cacheRoot -Force | Out-Null
$desktopPayload = Get-VerifiedPackage 'desktop' $bootstrap.Desktop $cacheRoot
$addinPayload = Get-VerifiedPackage 'solidworks-addin' $bootstrap.SolidWorksAddin $cacheRoot
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$results = @()

foreach ($profile in @(Get-ChildItem -LiteralPath $UsersRoot -Directory -ErrorAction Stop)) {
    $localRoot = Join-Path $profile.FullName 'AppData\Local\UPLM'
    if (-not (Test-Path -LiteralPath $localRoot -PathType Container)) { continue }

    foreach ($component in @(
        [pscustomobject]@{ Name = 'desktop'; Target = (Join-Path $localRoot 'client'); Package = $bootstrap.Desktop; Payload = $desktopPayload },
        [pscustomobject]@{ Name = 'solidworks-addin'; Target = (Join-Path $localRoot 'solidworks-addin'); Package = $bootstrap.SolidWorksAddin; Payload = $addinPayload }
    )) {
        if (-not (Test-Path -LiteralPath $component.Target -PathType Container)) { continue }
        $pendingStatePath = Join-Path (Join-Path $localRoot 'updates') ($component.Name + '-pending.json')
        $hasPendingState = Test-Path -LiteralPath $pendingStatePath -PathType Leaf
        $updateRequired = Test-UpdateRequired $component.Target ([string]$component.Package.Version)
        if (-not $updateRequired -and -not $hasPendingState) {
            $results += [pscustomobject]@{ Profile = $profile.Name; Component = $component.Name; Status = 'Current'; Version = [string]$component.Package.Version }
            continue
        }

        $profileRecoveryRoot = Join-Path $RecoveryRoot ('profiles\' + $profile.Name)
        $backupRoot = Join-Path $profileRecoveryRoot ("backup-$($component.Name)-$stamp")
        $quarantineRoot = Join-Path $profileRecoveryRoot ("stale-state-$($component.Name)-$stamp")
        Copy-DirectoryWithRetry $component.Target $backupRoot
        Move-StaleUpdateState $localRoot $component.Name $quarantineRoot
        Copy-DirectoryWithRetry $component.Payload $component.Target

        $installedVersion = (Get-Content -LiteralPath (Join-Path $component.Target '.uplm-version') -Raw -Encoding UTF8).Trim()
        if (-not [string]::Equals($installedVersion, [string]$component.Package.Version, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Installed version validation failed: $($profile.Name)/$($component.Name)"
        }
        $status = if ($hasPendingState) { 'Repaired' } else { 'Updated' }
        $results += [pscustomobject]@{ Profile = $profile.Name; Component = $component.Name; Status = $status; Version = $installedVersion }
    }
}

$receipt = [ordered]@{
    completedAt = [DateTimeOffset]::Now.ToString('O')
    serverBaseUrl = $ServerBaseUrl.TrimEnd('/')
    configurationVersion = $configurationVersion
    results = $results
}
$receiptPath = Join-Path $RecoveryRoot ("fleet-recovery-$stamp.json")
[IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
$results | Format-Table -AutoSize
Write-Host "UPLM client recovery completed. Receipt: $receiptPath" -ForegroundColor Green
