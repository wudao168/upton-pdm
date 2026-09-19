[CmdletBinding()]
param([switch]$Elevated)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$deployScript = Join-Path $PSScriptRoot 'Deploy-WebApiOnly.ps1'
$resultPath = Join-Path $projectRoot '.local\deploy-webapi-only-result.json'
$errorPath = Join-Path $projectRoot '.local\deploy-webapi-only-error.txt'

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host '当前不是管理员，正在申请管理员权限重新执行...'
    $arguments = "-NoProfile -NoExit -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Elevated"
    Start-Process pwsh -Verb RunAs -ArgumentList $arguments
    return
}

Write-Host '以管理员身份执行 Web API 部署...'
try {
    & $deployScript -DeferDesktopClient
}
finally {
    if (Test-Path -LiteralPath $resultPath) { Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 }
    if (Test-Path -LiteralPath $errorPath) { Get-Content -LiteralPath $errorPath -Raw -Encoding UTF8 }
}
