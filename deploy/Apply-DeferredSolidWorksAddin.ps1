[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Target,
    [Parameter(Mandatory = $true)][string]$Backup,
    [Parameter(Mandatory = $true)][string]$ResultPath,
    [Parameter(Mandatory = $true)][string]$Version
)

$ErrorActionPreference = 'Stop'
try {
    while (Get-Process -Name 'SLDWORKS' -ErrorAction SilentlyContinue) {
        Start-Sleep -Seconds 5
    }

    foreach ($path in @($Source, $Target, $Backup)) {
        $resolved = [IO.Path]::GetFullPath($path)
        if (-not $resolved.StartsWith([IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $PSScriptRoot) '.local')) + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Deferred add-in path escaped .local: $resolved"
        }
    }

    New-Item -ItemType Directory -Path $Backup -Force | Out-Null
    if (Test-Path -LiteralPath $Target) {
        Copy-Item -Path (Join-Path $Target '*') -Destination $Backup -Recurse -Force
    }
    New-Item -ItemType Directory -Path $Target -Force | Out-Null
    Copy-Item -Path (Join-Path $Source '*') -Destination $Target -Recurse -Force
    $sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $Source 'Upton.Pdm.SolidWorks.Addin.dll')).Hash
    $targetHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $Target 'Upton.Pdm.SolidWorks.Addin.dll')).Hash
    if ($sourceHash -ne $targetHash) { throw 'SolidWorks add-in hash mismatch after deferred deployment.' }
    [ordered]@{
        status = 'passed'
        appliedAt = [DateTimeOffset]::Now.ToString('O')
        version = $Version
        backup = $Backup
        sha256 = $targetHash
    } | ConvertTo-Json | Set-Content -LiteralPath $ResultPath -Encoding UTF8
}
catch {
    ($_ | Out-String) | Set-Content -LiteralPath ($ResultPath + '.error.txt') -Encoding UTF8
    exit 1
}
