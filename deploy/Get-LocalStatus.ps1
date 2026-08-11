[CmdletBinding()]
param()

$projectRoot = Split-Path -Parent $PSScriptRoot
$localRoot = Join-Path $projectRoot '.local'
$ports = 3306, 3308, 5080, 5173, 5174, 8080
$listeners = netstat -ano -p TCP | Select-String -Pattern ($ports.ForEach({ ':' + $_ + '\s' })) | Where-Object { $_.Line -match 'LISTENING' } | ForEach-Object { $_.Line.Trim() }
$health = $null
try {
    $health = Invoke-RestMethod -Uri 'http://127.0.0.1:5080/health' -TimeoutSec 5
}
catch {
}

$mysqlService = Get-Service -Name 'UptonPdmMySQL' -ErrorAction SilentlyContinue
$apiService = Get-Service -Name 'UptonPdmApi' -ErrorAction SilentlyContinue

[ordered]@{
    mysqlService = if ($null -eq $mysqlService) { 'NotInstalled' } else { $mysqlService.Status.ToString() }
    apiService = if ($null -eq $apiService) { 'NotInstalled' } else { $apiService.Status.ToString() }
    health = $health
    listeners = $listeners
    clientExists = Test-Path -LiteralPath (Join-Path $localRoot 'client\Upton.Pdm.Desktop.exe')
    addinRegistered = Test-Path -LiteralPath 'HKLM:\SOFTWARE\SOLIDWORKS\Addins\{BCFD8A8A-472B-42E2-AC62-58BC17773650}'
} | ConvertTo-Json -Depth 5
