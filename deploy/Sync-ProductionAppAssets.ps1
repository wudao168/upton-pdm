param([string]$ProjectRoot=(Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference='Stop'
$root=(Resolve-Path -LiteralPath $ProjectRoot).Path
$source=Join-Path $root 'src/pdm-ui/public/production-app'
$web=Join-Path $root '.local/api/wwwroot'
$targets=@($web,"$root/.local/client/ui","$root/.local/solidworks-addin/ui","$env:LOCALAPPDATA/UPLM/client/ui","$env:LOCALAPPDATA/UPLM/solidworks-addin/ui") | Where-Object {Test-Path -LiteralPath $_}
$mutex=[Threading.Mutex]::new($false,'Global\UPLM.LocalUiDeployment')
$locked=$false
$backup=Join-Path $root ('.local/backup/production-app-assets-'+(Get-Date -Format 'yyyyMMddHHmmss'))
$saved=[Collections.Generic.List[object]]::new()
try {
    $locked=$mutex.WaitOne(0)
    if(!$locked){throw '已有本机发布正在运行。'}
    & "$root/deploy/Sync-LocalUiManifest.ps1" -VerifyOnly | Out-Null
    New-Item -ItemType Directory -Force $backup | Out-Null
    foreach($target in $targets) {
        foreach($relative in @('client-bootstrap.json','production-app/index.html','production-app/app.js','production-app/app.css','production-app/logo.png')) {
            $path=Join-Path $target $relative
            if(Test-Path -LiteralPath $path){$copy=Join-Path $backup ($saved.Count.ToString()+'.bak');Copy-Item -LiteralPath $path -Destination $copy;$saved.Add([pscustomobject]@{Path=$path;Backup=$copy})}
        }
        New-Item -ItemType Directory -Force (Join-Path $target 'production-app') | Out-Null
        foreach($file in Get-ChildItem -LiteralPath $source -File) {
            $path=Join-Path $target ('production-app/'+$file.Name)
            [IO.File]::Copy($file.FullName,$path+'.stage',$true)
            [IO.File]::Move($path+'.stage',$path,$true)
        }
    }
    $manifestPath=Join-Path $web 'client-bootstrap.json'
    $manifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $now=[DateTimeOffset]::UtcNow.ToOffset([TimeSpan]::FromHours(8))
    $manifest.ConfigurationVersion=$now.ToString('yyyy.MM.dd.HHmmss')
    $manifest.ReleasedAt=$now.ToString('O')
    $manifest.ReleaseNote='生产执行 App 合并为扫码确认单页，其他工序无打印操作；包装扫码暂作预留，可在电脑端完成包装与打印，App 不要求配置打印机。'
    $manifest.ReleaseHistory=@([pscustomobject]@{Version=$manifest.ConfigurationVersion;ReleasedAt=$manifest.ReleasedAt;ReleaseNote=$manifest.ReleaseNote;DesktopVersion=$manifest.Desktop.Version;SolidWorksAddinVersion=$manifest.SolidWorksAddin.Version})+@($manifest.ReleaseHistory)
    [IO.File]::WriteAllText($manifestPath,($manifest|ConvertTo-Json -Depth 60),[Text.UTF8Encoding]::new($false))
    & "$root/deploy/Sync-LocalUiManifest.ps1" | Out-Null
    $verified=& "$root/deploy/Sync-LocalUiManifest.ps1" -VerifyOnly
    foreach($target in $targets) { foreach($file in Get-ChildItem -LiteralPath $source -File) {
        if((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $target ('production-app/'+$file.Name))).Hash){throw '生产 App 资源副本不一致。'}
    } }
    if((Invoke-RestMethod 'http://192.168.2.8:5173/client-bootstrap.json' -NoProxy -TimeoutSec 10).ConfigurationVersion -ne $manifest.ConfigurationVersion){throw '局域网在线版本不一致。'}
    [pscustomobject]@{Success=$true;Version=$manifest.ConfigurationVersion;AppCopies=$targets.Count;Verification=$verified;Backup=$backup} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$root/.local/production-app-assets-result.json" -Encoding utf8
} catch {
    foreach($file in $saved){Copy-Item -LiteralPath $file.Backup -Destination $file.Path -Force}
    & "$root/deploy/Sync-LocalUiManifest.ps1" | Out-Null
    & "$root/deploy/Sync-LocalUiManifest.ps1" -VerifyOnly | Out-Null
    throw
} finally {if($locked){$mutex.ReleaseMutex()};$mutex.Dispose()}
