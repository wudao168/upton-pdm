[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$assemblyPath = Join-Path $projectRoot "src\Pdm.SolidWorks.Addin\bin\$Configuration\net48\Upton.Pdm.SolidWorks.Addin.dll"
$regAsmPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script as Administrator.'
}

if (-not (Test-Path -LiteralPath $assemblyPath)) {
    throw "Add-in was not found: $assemblyPath"
}

& $regAsmPath $assemblyPath /unregister
if ($LASTEXITCODE -ne 0) {
    throw "SolidWorks add-in unregister failed. RegAsm exit code: $LASTEXITCODE"
}

Write-Host 'UPTON PDM SolidWorks add-in unregistered.'
