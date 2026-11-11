[CmdletBinding()]
param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $ProjectRoot).Path
Set-Location -LiteralPath $root
$resultPath = Join-Path $root '.local/production-app-deploy-result.json'
$logPath = Join-Path $root '.local/production-app-deploy.log'
$api = Join-Path $root '.local/api'
$apiStage = Join-Path $root '.local/production-app-api-stage'
$uiStage = Join-Path $root '.local/production-app-ui-stage'
$web = Join-Path $api 'wwwroot'
$targets = @($web, "$root/.local/client/ui", "$root/.local/solidworks-addin/ui", "$env:LOCALAPPDATA/UPLM/client/ui", "$env:LOCALAPPDATA/UPLM/solidworks-addin/ui") | Where-Object { Test-Path -LiteralPath $_ }
$dlls = @('Pdm.Api.dll','Pdm.Application.dll','Pdm.Domain.dll','Pdm.Infrastructure.dll','Pdm.Api.pdb','Pdm.Application.pdb','Pdm.Domain.pdb','Pdm.Infrastructure.pdb')
$backup = Join-Path $root ('.local/backup/production-app-' + (Get-Date -Format 'yyyyMMddHHmmss'))
$mutex = [Threading.Mutex]::new($false,'Global\UPLM.LocalUiDeployment')
$locked = $false; $changed = $false; $stopped = $false; $saved = [Collections.Generic.List[object]]::new()
function AtomicCopy([string]$source,[string]$target) {
    [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
    [IO.File]::Copy($source,$target+'.production-stage',$true)
    [IO.File]::Move($target+'.production-stage',$target,$true)
}
function BackupFile([string]$path) {
    if (!(Test-Path -LiteralPath $path)) { return }
    $to = Join-Path $backup ($saved.Count.ToString('D5') + '.bak')
    Copy-Item -LiteralPath $path -Destination $to
    $saved.Add([pscustomobject]@{Target=$path;Backup=$to})
}
function StopApi {
    $listener = Get-NetTCPConnection -LocalPort 5080 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if (!$listener) { return }
    $instance = Get-CimInstance Win32_Process -Filter "ProcessId=$($listener.OwningProcess)"
    if ($instance.Name -ne 'dotnet.exe' -or $instance.CommandLine -notlike ('*' + $api + '\Pdm.Api.dll*')) { throw '本机 API 进程路径不匹配，未停止任何进程。' }
    $process = Get-Process -Id $instance.ProcessId
    Stop-Process -InputObject $process -Force
    if (!$process.WaitForExit(20000)) { throw '本机 API 未能及时停止。' }
}
function StartApi {
    # A redirected native shell can retain inherited pipe handles in the background API.
    # Launch a separate hidden window and verify health instead of waiting for that pipe.
    $runner = Start-Process -FilePath (Get-Command pwsh).Source -WindowStyle Hidden -PassThru -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $root 'deploy/Start-LocalPdmRuntime.ps1') + '"')
    for($attempt=0;$attempt -lt 30;$attempt++) {
        try { if((Invoke-RestMethod 'http://127.0.0.1:5080/health' -TimeoutSec 2 -NoProxy).status -eq 'ok'){return} } catch { }
        Start-Sleep -Seconds 1
    }
    throw '本机 API 启动后健康检查未通过，请查看 autostart-api 日志。'
}
try {
    $principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '更新正在运行的本机 API 需要 Windows 管理员权限。' }
    $locked=$mutex.WaitOne(0)
    if (!$locked) { throw '已有本机发布正在运行，本次尚未修改文件。' }
    $baseline = Get-Content -LiteralPath "$root/.local/production-app-deploy-baseline.json" -Raw | ConvertFrom-Json
    foreach($file in $baseline) { if((Get-FileHash -LiteralPath $file.Path).Hash -ne $file.Hash) { throw '检测到并发发布，请重新准备当前版本。' } }
    $uiBuilt=(Get-Item -LiteralPath "$uiStage/index.html").LastWriteTimeUtc
    if (Get-ChildItem -LiteralPath "$root/src/pdm-ui/src" -Recurse -File | Where-Object LastWriteTimeUtc -gt $uiBuilt | Select-Object -First 1) { throw '网页源码在构建后有变化，请重新构建。' }
    foreach($project in @('Pdm.Api','Pdm.Domain','Pdm.Application','Pdm.Infrastructure')) {
        $built=(Get-Item -LiteralPath "$apiStage/$project.dll").LastWriteTimeUtc
        if (Get-ChildItem -LiteralPath "$root/src/$project" -File -Recurse -Filter '*.cs' | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' -and $_.LastWriteTimeUtc -gt $built } | Select-Object -First 1) { throw "服务端 $project 源码在构建后有变化，请重新构建。" }
    }
    & "$root/deploy/Sync-LocalUiManifest.ps1" -VerifyOnly | Out-Null
    New-Item -ItemType Directory -Force $backup | Out-Null
    foreach($name in $dlls) { BackupFile (Join-Path $api $name) }
    $inventory=@(Get-ChildItem -LiteralPath $uiStage -Recurse -File | Where-Object { $_.Extension -ne '.map' -and $_.Name -ne 'client-bootstrap.json' })
    $updates=[Collections.Generic.List[object]]::new()
    foreach($target in $targets) {
        BackupFile (Join-Path $target 'client-bootstrap.json')
        foreach($source in $inventory) {
            $destination=Join-Path $target ([IO.Path]::GetRelativePath($uiStage,$source.FullName))
            if ((Test-Path -LiteralPath $destination) -and (Get-FileHash -LiteralPath $destination).Hash -eq (Get-FileHash -LiteralPath $source.FullName).Hash) { continue }
            BackupFile $destination
            $updates.Add([pscustomobject]@{Source=$source.FullName;Target=$destination})
        }
    }
    $saved | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath "$backup/files.json" -Encoding utf8
    StopApi; $stopped=$true; $changed=$true
    foreach($name in $dlls) { AtomicCopy "$apiStage/$name" "$api/$name" }
    foreach($update in $updates) { AtomicCopy $update.Source $update.Target }
    $manifestPath=Join-Path $web 'client-bootstrap.json'
    $manifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $now=[DateTimeOffset]::UtcNow.ToOffset([TimeSpan]::FromHours(8))
    $manifest.ConfigurationVersion=$now.ToString('yyyy.MM.dd.HHmmss')
    $manifest.ReleasedAt=$now.ToString('O')
    $manifest.ReleaseNote='增加 Android 9 生产执行 App、发布批次二维码、分批包装标签与打印工作站；新增权限默认拒绝，支持图档作废拦截。'
    $manifest.ReleaseHistory=@([pscustomobject]@{Version=$manifest.ConfigurationVersion;ReleasedAt=$manifest.ReleasedAt;ReleaseNote=$manifest.ReleaseNote;DesktopVersion=$manifest.Desktop.Version;SolidWorksAddinVersion=$manifest.SolidWorksAddin.Version})+@($manifest.ReleaseHistory)
    [IO.File]::WriteAllText($manifestPath,($manifest|ConvertTo-Json -Depth 60),[Text.UTF8Encoding]::new($false))
    & "$root/deploy/Sync-LocalUiManifest.ps1" | Out-Null
    $verified=& "$root/deploy/Sync-LocalUiManifest.ps1" -VerifyOnly
    StartApi
    $health=Invoke-RestMethod 'http://127.0.0.1:5080/health' -TimeoutSec 10 -NoProxy
    if ($health.status -ne 'ok') { throw 'API 健康检查未通过。' }
    $online=Invoke-RestMethod 'http://127.0.0.1:5080/client-bootstrap.json' -TimeoutSec 10 -NoProxy
    if ($online.ConfigurationVersion -ne $manifest.ConfigurationVersion) { throw '本机在线版本不一致。' }
    foreach($name in $dlls) { if((Get-FileHash -LiteralPath "$api/$name").Hash -ne (Get-FileHash -LiteralPath "$apiStage/$name").Hash) { throw 'API 文件校验失败。' } }
    [pscustomobject]@{Success=$true;Version=$manifest.ConfigurationVersion;Backup=$backup;Verification=$verified;ChangedUiFiles=$updates.Count} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resultPath -Encoding utf8
} catch {
    $failure=$_
    if ($changed) {
        try { StopApi; foreach($file in $saved) { AtomicCopy $file.Backup $file.Target }; & "$root/deploy/Sync-LocalUiManifest.ps1" | Out-Null; & "$root/deploy/Sync-LocalUiManifest.ps1" -VerifyOnly | Out-Null; StartApi }
        catch { $_ | Out-String | Add-Content -LiteralPath $logPath }
    } elseif($stopped) { StartApi }
    [pscustomobject]@{Success=$false;Error=$failure.Exception.Message;Backup=$backup} | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding utf8
    throw $failure
} finally { if($locked){$mutex.ReleaseMutex()};$mutex.Dispose() }
