[CmdletBinding()]
param([string]$InstallRoot = 'C:\UPLM\pdm')

$ErrorActionPreference = 'Stop'
$serviceName = 'UptonPdmApi'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请使用管理员 PowerShell 执行 API 运行时修复。'
}

$packageRoot = Split-Path -Parent $PSCommandPath
$payload = Join-Path $packageRoot 'app'
$installRootFull = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$appTarget = Join-Path $installRootFull 'app'
foreach ($required in @($payload, (Join-Path $payload 'Pdm.Api.exe'), (Join-Path $payload 'hostfxr.dll'), (Join-Path $payload 'coreclr.dll'), $appTarget)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "修复包或服务器文件不完整：$required" }
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backup = Join-Path $installRootFull "backup\api-runtime-repair-$stamp"
New-Item -ItemType Directory -Path $backup -Force | Out-Null
$service = Get-Service -Name $serviceName -ErrorAction Stop
if ($service.Status -ne 'Stopped') {
    Stop-Service -Name $serviceName -Force
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

$preservedDirectories = @('wwwroot', 'preview-worker')
Get-ChildItem -LiteralPath $appTarget -Force |
    Where-Object { $_.Name -notin $preservedDirectories } |
    Copy-Item -Destination $backup -Recurse -Force
Get-ChildItem -LiteralPath $payload -Force |
    Where-Object { $_.Name -notin $preservedDirectories } |
    Copy-Item -Destination $appTarget -Recurse -Force

Start-Service -Name $serviceName
$health = $null
for ($index = 0; $index -lt 60; $index++) {
    try {
        $health = Invoke-RestMethod 'http://127.0.0.1:5080/health' -TimeoutSec 3
        if ($health.status -eq 'ok' -and $health.database -eq 'MySql') { break }
    }
    catch { }
    Start-Sleep -Seconds 1
}
if ($null -eq $health -or $health.status -ne 'ok' -or $health.database -ne 'MySql') {
    throw "API 未恢复。原 API 文件已备份至：$backup"
}

[pscustomobject]@{ status = $health.status; database = $health.database; apiRuntimeBackup = $backup } | ConvertTo-Json
