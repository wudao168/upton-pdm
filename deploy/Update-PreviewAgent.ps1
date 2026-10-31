[CmdletBinding()]
param([string]$InstallRoot = 'C:\Program Files\UPLM Preview Agent', [int]$PreserveProcessId = 0, [switch]$EnableDedicatedRecovery, [switch]$RecoverBlockedStartup)

[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
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
$foreground = [version]$manifest.Version -ge [version]'1.2.0.0'
if ($foreground) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $identity = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes(($InstallRoot + '\').ToLowerInvariant()))).Replace('-','').Substring(0,16) }
    finally { $hash.Dispose() }
    $preferences = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "UPLM\preview-agent\$identity\settings.json"
    if (Test-Path -LiteralPath $preferences) {
        $saved = Get-Content -LiteralPath $preferences -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($saved.Port) { $port = [int]$saved.Port }
    }
}
$healthHost = if ($config.BindAddress -and $config.BindAddress -notin @('0.0.0.0','::','*','+')) { [string]$config.BindAddress } else { '127.0.0.1' }
if ($healthHost.Contains(':') -and -not $healthHost.StartsWith('[')) { $healthHost = "[$healthHost]" }
$healthUrl = "http://${healthHost}:$port/health"
$workerPath = [IO.Path]::GetFullPath((Join-Path $InstallRoot 'preview-worker\Upton.Pdm.SolidWorks.PreviewWorker.exe'))
if ($config.WorkerPath -and [IO.Path]::GetFullPath($config.WorkerPath) -ne $workerPath) { throw '转换程序配置位于自定义目录，请确认部署路径后再升级。' }
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
$health = $null
try { $health = Invoke-RestMethod $healthUrl -TimeoutSec 5 }
catch {
    $agentPaths = @((Join-Path $InstallRoot 'Upton.Pdm.PreviewAgent.exe'), $workerPath)
    $active = @(Get-CimInstance Win32_Process | Where-Object {
        ($_.ExecutablePath -and $agentPaths -contains $_.ExecutablePath) -or $_.Name -in @('Upton.Pdm.PreviewAgent.exe','Upton.Pdm.SolidWorks.PreviewWorker.exe')
    })
    if ($active.Count -gt 0 -or [string]$task.State -eq 'Running') {
        throw "转图代理仍在运行，但无法读取状态（$healthUrl）。请先等待当前任务完成，并检查代理窗口和监听地址，再重新升级；尚未替换任何程序文件。"
    }
    Write-Host '旧转图代理已停止，且没有代理或转换进程；将直接更新程序并启动代理。' -ForegroundColor Yellow
}
if ($null -ne $health) {
    $processing = if ($null -ne $health.processing) { [bool]$health.processing } else { [bool]$health.busy }
    if ($processing -or $health.queued -gt 0) {
        $lastProgress = [DateTime]::MinValue
        $blocked = ($RecoverBlockedStartup -and $EnableDedicatedRecovery -and [version]$manifest.Version -ge [version]'1.2.3.0' -and
            $health.agentVersion -eq '1.2.2.0' -and $null -ne $health.solidWorksPid -and [int]$health.solidWorksPid -eq 0 -and
            $health.total -gt 0 -and $health.completed -lt $health.total -and
            [string]$health.conversionStage -match '^(启动或连接SolidWorks|已处理文件：)' -and
            [DateTime]::TryParse([string]$health.progressUpdatedAt, [ref]$lastProgress) -and
            ([DateTime]::Now - $lastProgress).TotalSeconds -ge 60)
        if (-not $blocked) { throw '转图任务仍在执行或排队，请完成后再升级。' }
        Write-Host '检测到旧版SolidWorks启动卡住，自动结束当前预览请求后升级；源图档保持不变，更新后请重试原任务。' -ForegroundColor Yellow
    }
    if ([IO.Path]::GetFullPath($health.workerPath) -ne $workerPath) { throw '当前工作进程位于自定义目录，请确认部署路径后再升级。' }
}
$taskXml = $null
$managerTask = $null
$managerTaskXml = $null
if ($foreground) {
    if (-not $task.Principal.UserId) { throw '无法核对原代理运行账号，未替换任何文件，请检查计划任务配置。' }
    $taskXml = Export-ScheduledTask -TaskName $taskName
    $managerTask = Get-ScheduledTask -TaskName "$taskName Manager" -ErrorAction SilentlyContinue
    if ($managerTask -and ($managerTask.Actions.Execute | Where-Object { $_.Trim('"') -eq (Join-Path $InstallRoot 'UplmPreviewAgentSetup.exe') })) {
        $managerTaskXml = Export-ScheduledTask -TaskName "$taskName Manager"
    } else { $managerTask = $null }
}
$backup = Join-Path $InstallRoot ('backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $backup -Force | Out-Null
Copy-Item -LiteralPath $configPath -Destination (Join-Path $backup 'PdmPreviewAgent.json')
if ($EnableDedicatedRecovery -and $preferences -and (Test-Path -LiteralPath $preferences)) {
    Copy-Item -LiteralPath $preferences -Destination (Join-Path $backup 'agent-settings.json')
}
if ($taskXml) { $taskXml | Set-Content -LiteralPath (Join-Path $backup 'agent-task.xml') -Encoding Unicode }
if ($managerTaskXml) { $managerTaskXml | Set-Content -LiteralPath (Join-Path $backup 'manager-task.xml') -Encoding Unicode }
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
    if ($managerTask) {
        Stop-ScheduledTask -TaskName "$taskName Manager"
        $paths += Join-Path $InstallRoot 'UplmPreviewAgentSetup.exe'
    }
    foreach ($process in Get-CimInstance Win32_Process) {
        if ($process.ProcessId -ne $PreserveProcessId -and $process.ExecutablePath -and $paths -contains $process.ExecutablePath) {
            Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
        }
    }
}
$taskMigrationApplied = $false
try {
    Stop-AgentProcesses
    foreach ($copy in $copies) {
        New-Item -ItemType Directory -Path (Split-Path $copy.Target) -Force | Out-Null
        Copy-Item -LiteralPath $copy.Source -Destination $copy.Target -Force
        if ((Get-FileHash -LiteralPath $copy.Target).Hash -ne $copy.Hash) { throw "安装文件校验失败：$($copy.Relative)" }
    }
    if ((Get-FileHash -LiteralPath $configPath).Hash -ne $configHash) { throw '原有连接配置发生变化，已停止升级。' }
    if ($EnableDedicatedRecovery) {
        $config | Add-Member -NotePropertyName DedicatedConversionHost -NotePropertyValue $true -Force
        [IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
        if ($preferences -and (Test-Path -LiteralPath $preferences)) {
            $savedPreferences = Get-Content -LiteralPath $preferences -Raw -Encoding UTF8 | ConvertFrom-Json
            $savedPreferences | Add-Member -NotePropertyName DedicatedConversionHost -NotePropertyValue $true -Force
            [IO.File]::WriteAllText($preferences, ($savedPreferences | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
        }
    }
    if ($foreground) {
        $taskMigrationApplied = $true
        $action = New-ScheduledTaskAction -Execute (Join-Path $InstallRoot 'Upton.Pdm.PreviewAgent.exe') -Argument '--startup' -WorkingDirectory $InstallRoot
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $task.Principal.UserId
        $principal = New-ScheduledTaskPrincipal -UserId $task.Principal.UserId -LogonType Interactive -RunLevel Highest
        Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $task.Settings -Force | Out-Null
        if ($managerTask) { Disable-ScheduledTask -TaskName "$taskName Manager" | Out-Null }
    }
    if ($foreground) {
        Start-Process -FilePath (Join-Path $InstallRoot 'Upton.Pdm.PreviewAgent.exe') -WorkingDirectory $InstallRoot -WindowStyle Normal `
            -RedirectStandardOutput (Join-Path $backup 'agent-startup.log') -RedirectStandardError (Join-Path $backup 'agent-startup-error.log') | Out-Null
    } else { Start-ScheduledTask -TaskName $taskName }
    $verified = $false
    for ($attempt=0; $attempt -lt 20; $attempt++) {
        Start-Sleep -Seconds 1
        try {
            $health = Invoke-RestMethod $healthUrl -TimeoutSec 2
            if ($health.agentVersion -eq $manifest.Version -and $health.workerVersion -eq $manifest.Version) { $verified=$true; break }
        } catch { }
    }
    if (-not $verified) { throw '新版代理未通过健康或版本检查。' }
    [pscustomobject]@{ Result='升级成功'; Version=$manifest.Version; Backup=$backup; ConfigurationPreserved=$true; DedicatedRecoveryEnabled=[bool]$EnableDedicatedRecovery } |
        ConvertTo-Json | Tee-Object -FilePath (Join-Path $backup 'upgrade-result.json')
    Write-Host '升级完成，请重新加载装配预览或重试原转图任务，并返回聊天告知结果。' -ForegroundColor Green
} catch {
    $failure = $_
    Stop-AgentProcesses
    foreach ($copy in $copies) {
        $saved = Join-Path $backup $copy.Relative
        if (Test-Path -LiteralPath $saved) { Copy-Item -LiteralPath $saved -Destination $copy.Target -Force }
    }
    if ($EnableDedicatedRecovery) {
        Copy-Item -LiteralPath (Join-Path $backup 'PdmPreviewAgent.json') -Destination $configPath -Force
        if (Test-Path -LiteralPath (Join-Path $backup 'agent-settings.json')) {
            Copy-Item -LiteralPath (Join-Path $backup 'agent-settings.json') -Destination $preferences -Force
        }
    }
    if ($taskMigrationApplied) {
        Register-ScheduledTask -TaskName $taskName -Xml $taskXml -Force | Out-Null
        if ($managerTaskXml) {
            Register-ScheduledTask -TaskName "$taskName Manager" -Xml $managerTaskXml -Force | Out-Null
            if ([string]$managerTask.State -eq 'Running') { Start-ScheduledTask -TaskName "$taskName Manager" }
        }
    }
    Start-ScheduledTask -TaskName $taskName
    throw "升级失败，已恢复备份：$backup。原因：$failure"
}
