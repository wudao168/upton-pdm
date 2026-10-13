[CmdletBinding()]
param([string]$InstallRoot = 'C:\Program Files\UPLM Preview Agent')

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请以管理员身份打开 PowerShell，再执行本升级脚本。'
}
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'files.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$configPath = Join-Path $InstallRoot 'PdmPreviewAgent.json'
if (-not (Test-Path -LiteralPath $configPath)) { throw '未找到现有转图代理配置，请确认安装目录。' }
$configHash = (Get-FileHash -LiteralPath $configPath).Hash
$config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
$port = if ($config.Port) { [int]$config.Port } else { 5199 }
$healthUrl = "http://127.0.0.1:$port/health"
$health = Invoke-RestMethod $healthUrl -TimeoutSec 10
if ($health.busy -or $health.queued -gt 0) { throw '转图任务仍在执行或排队，请完成后再升级。' }
$workerPath = [IO.Path]::GetFullPath((Join-Path $InstallRoot 'preview-worker\Upton.Pdm.SolidWorks.PreviewWorker.exe'))
if ([IO.Path]::GetFullPath($health.workerPath) -ne $workerPath) { throw '当前工作进程位于自定义目录，请确认部署路径后再升级。' }
$copies = foreach ($file in $manifest.Files) {
    $source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('payload\' + $file.Path)))
    $target = [IO.Path]::GetFullPath((Join-Path $InstallRoot $file.Path))
    if (-not $source.StartsWith(([IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'payload')) + '\'), [StringComparison]::OrdinalIgnoreCase) -or
        -not $target.StartsWith($InstallRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '升级文件路径不合法。' }
    if ((Get-FileHash -LiteralPath $source).Hash -ne $file.Sha256) { throw "升级包文件校验失败：$($file.Path)" }
    [pscustomobject]@{ Source=$source; Target=$target; Relative=$file.Path; Hash=$file.Sha256 }
}
$taskName = 'UPLM Preview Agent'
$task = Get-ScheduledTask -TaskName $taskName
if (-not ($task.Actions.Execute | Where-Object { $_.Trim('"') -eq (Join-Path $InstallRoot 'Upton.Pdm.PreviewAgent.exe') })) { throw '现有计划任务路径与安装目录不一致。' }
$backup = Join-Path $InstallRoot ('backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $backup -Force | Out-Null
Copy-Item -LiteralPath $configPath -Destination (Join-Path $backup 'PdmPreviewAgent.json')
foreach ($copy in $copies) {
    if (Test-Path -LiteralPath $copy.Target) {
        $saved = Join-Path $backup $copy.Relative
        New-Item -ItemType Directory -Path (Split-Path $saved) -Force | Out-Null
        Copy-Item -LiteralPath $copy.Target -Destination $saved
    }
}
function Stop-AgentProcesses {
    Stop-ScheduledTask -TaskName $taskName
    $paths = @((Join-Path $InstallRoot 'Upton.Pdm.PreviewAgent.exe'), $workerPath)
    foreach ($process in Get-CimInstance Win32_Process) {
        if ($process.ExecutablePath -and $paths -contains $process.ExecutablePath) {
            Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
        }
    }
}
try {
    Stop-AgentProcesses
    foreach ($copy in $copies) {
        New-Item -ItemType Directory -Path (Split-Path $copy.Target) -Force | Out-Null
        Copy-Item -LiteralPath $copy.Source -Destination $copy.Target -Force
        if ((Get-FileHash -LiteralPath $copy.Target).Hash -ne $copy.Hash) { throw "安装文件校验失败：$($copy.Relative)" }
    }
    if ((Get-FileHash -LiteralPath $configPath).Hash -ne $configHash) { throw '原有连接配置发生变化，已停止升级。' }
    Start-ScheduledTask -TaskName $taskName
    $verified = $false
    for ($attempt=0; $attempt -lt 20; $attempt++) {
        Start-Sleep -Seconds 1
        try {
            $health = Invoke-RestMethod $healthUrl -TimeoutSec 2
            if ($health.agentVersion -eq $manifest.Version -and $health.workerVersion -eq $manifest.Version) { $verified=$true; break }
        } catch { }
    }
    if (-not $verified) { throw '新版代理未通过健康或版本检查。' }
    [pscustomobject]@{ Result='升级成功'; Version=$manifest.Version; Backup=$backup; ConfigurationPreserved=$true } |
        ConvertTo-Json | Tee-Object -FilePath (Join-Path $backup 'upgrade-result.json')
    Write-Host '升级完成，请返回聊天告知；随后将由服务器重试失败批次。' -ForegroundColor Green
} catch {
    $failure = $_
    Stop-AgentProcesses
    foreach ($copy in $copies) {
        $saved = Join-Path $backup $copy.Relative
        if (Test-Path -LiteralPath $saved) { Copy-Item -LiteralPath $saved -Destination $copy.Target -Force }
    }
    Start-ScheduledTask -TaskName $taskName
    throw "升级失败，已恢复备份：$backup。原因：$failure"
}
