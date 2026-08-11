[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$assemblyPath = Join-Path $projectRoot "src\Pdm.SolidWorks.Addin\bin\$Configuration\net48\Upton.Pdm.SolidWorks.Addin.dll"
$regAsmPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'
$tlbPath = [System.IO.Path]::ChangeExtension($assemblyPath, '.tlb')

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script as Administrator. SolidWorks add-in registration writes to HKLM.'
}

if (-not (Test-Path -LiteralPath $assemblyPath)) {
    throw "Add-in was not found: $assemblyPath. Run deploy\Build-Phase1.ps1 first."
}

if (-not (Test-Path -LiteralPath $regAsmPath)) {
    throw "64-bit RegAsm was not found: $regAsmPath"
}

& $regAsmPath $assemblyPath /codebase "/tlb:$tlbPath"
if ($LASTEXITCODE -ne 0) {
    throw "SolidWorks add-in registration failed. RegAsm exit code: $LASTEXITCODE"
}

Write-Host "UPTON PDM SolidWorks add-in registered: $assemblyPath"
Write-Host 'Restart SolidWorks and verify UPTON PDM under Tools > Add-ins.'
