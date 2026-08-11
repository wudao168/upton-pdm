[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnetPath = Join-Path $projectRoot '.dotnet\dotnet.exe'

if (-not (Test-Path -LiteralPath $dotnetPath)) {
    throw "Project-local .NET SDK was not found: $dotnetPath"
}

Push-Location $projectRoot
try {
    pnpm.cmd install --frozen-lockfile
    pnpm.cmd ui:build
    & $dotnetPath restore Pdm.slnx
    & $dotnetPath build Pdm.slnx --no-restore --configuration $Configuration --nologo
    & $dotnetPath test Pdm.slnx --no-build --no-restore --configuration $Configuration --nologo

    $desktopPath = Join-Path $projectRoot "src\Pdm.Desktop\bin\$Configuration\net48\Upton.Pdm.Desktop.exe"
    $addinPath = Join-Path $projectRoot "src\Pdm.SolidWorks.Addin\bin\$Configuration\net48\Upton.Pdm.SolidWorks.Addin.dll"
    if (-not (Test-Path -LiteralPath $desktopPath) -or -not (Test-Path -LiteralPath $addinPath)) {
        throw 'Phase 1 build artifacts are incomplete.'
    }

    Get-FileHash -Algorithm SHA256 -LiteralPath $desktopPath, $addinPath |
        Select-Object Path, Hash |
        Format-Table -AutoSize
}
finally {
    Pop-Location
}
