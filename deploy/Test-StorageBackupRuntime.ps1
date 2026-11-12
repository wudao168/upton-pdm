[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'StorageBackup.Common.ps1')
$repo = Split-Path -Parent $PSScriptRoot
$testParent = [IO.Path]::GetFullPath((Join-Path $repo 'tmp/storage-backup-runtime'))
$fixture = Join-Path $testParent ([Guid]::NewGuid().ToString('N'))
$checks = [Collections.Generic.List[string]]::new()
function Assert-Test([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Expect-Refusal([scriptblock]$Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true; Assert-Test (!$_.Exception.Message.Contains('sim-private-value')) '拒绝提示泄露了模拟私密值。' }
    Assert-Test $rejected '本应拒绝身份异常，但操作继续了。'
}
function Get-PdmBackupTrustedDotnetHosts([string]$ProjectRoot) { @((Join-Path $ProjectRoot '.dotnet/dotnet.exe'), (Join-Path $ProjectRoot 'official-dotnet/dotnet.exe')) }
function Get-PdmBackupApiService { $script:sim.Service }
function Get-PdmBackupApiProcesses { @($script:sim.Processes) }
function Get-PdmBackupApiListeners($Ports) { @($script:sim.Listeners) }
function Get-PdmBackupAutostartTask { if ($script:sim.Task) { $script:sim.Task } }
function Get-PdmBackupCurrentUserSid { 'S-1-5-21-TEST-1001' }
function Get-PdmBackupProcessOwnerSid($Process) { $script:sim.OwnerSid }
function Get-PdmBackupServiceArchiveSource { [pscustomobject]@{ImagePath=$script:sim.Service.PathName;ObjectName='LocalSystem';Environment=@('PDM_DB_PASSWORD=sim-private-value','ASPNETCORE_ENVIRONMENT=Production')} }
function Disable-PdmBackupAutostartTask { $script:sim.Task.Settings.Enabled=$false; $script:sim.Events.Add('task-disable') }
function Enable-PdmBackupAutostartTask { $script:sim.Task.Settings.Enabled=$true; $script:sim.Events.Add('task-enable') }
function Stop-PdmBackupAutostartTask {
    $script:sim.Task.State='Ready'; $script:sim.Events.Add('task-stop')
    if ($script:sim.TaskKillsApi) { $script:sim.Processes=@(); $script:sim.Listeners=@() }
    if ($script:sim.TaskStopThrows) { throw '模拟任务停止后异常。' }
}
function Start-PdmBackupAutostartTask { $script:sim.Events.Add('task-start') }
function Enter-PdmBackupRuntimeLock($Runtime) { $Runtime.MutexAcquired=$true; $script:sim.Events.Add('lock') }
function Exit-PdmBackupRuntimeLock($Runtime) { $Runtime.MutexAcquired=$false; $script:sim.Events.Add('unlock') }
function Test-PdmBackupApiHealth { $true }
function Stop-PdmVerifiedLocalApi($Runtime) {
    $script:sim.Events.Add('api-stop'); $script:sim.Processes=@(); $script:sim.Listeners=@()
    if ($script:sim.StopThrows) { throw '模拟API停止后异常。' }
}
function Stop-PdmBackupApiService {
    $script:sim.Events.Add('service-stop'); $script:sim.Service.State='Stopped'; $script:sim.Service.ProcessId=0; $script:sim.Processes=@(); $script:sim.Listeners=@()
}
function Set-SimRunning([string]$Kind) {
    $api=Join-Path $script:sim.ApiDirectory 'Pdm.Api.dll'
    $exe=switch($Kind) {'apphost' {Join-Path $script:sim.ApiDirectory 'Pdm.Api.exe'} 'official' {Join-Path $fixture 'official-dotnet/dotnet.exe'} default {Join-Path $fixture '.dotnet/dotnet.exe'}}
    $command=if($Kind -eq 'apphost') {'"'+$exe+'"'} else {'"'+$exe+'" "'+$api+'"'}
    $script:sim.Processes=@([pscustomobject]@{ProcessId=4242;Name=[IO.Path]::GetFileName($exe);ExecutablePath=$exe;CommandLine=$command;CreationDate=[DateTime]'2026-11-12T01:00:00Z'})
    $script:sim.Listeners=@([pscustomobject]@{OwningProcess=4242;LocalPort=5080},[pscustomobject]@{OwningProcess=4242;LocalPort=5173})
    if ($script:sim.Service -and $Kind -ne 'local') {$script:sim.Service.State='Running';$script:sim.Service.ProcessId=4242}
}
function Start-PdmBackupLocalApi($Runtime) {
    $script:sim.Events.Add('api-start')
    if($script:sim.StartThrows){throw '模拟API启动失败。'}
    Set-SimRunning 'local'
}
function Start-PdmBackupApiService { $script:sim.Events.Add('service-start'); Set-SimRunning $script:sim.Kind }
function Reset-Sim([string]$Kind='local', [bool]$Running=$true, [bool]$Task=$false) {
    $script:sim=[pscustomobject]@{Kind=$Kind;ApiDirectory=Join-Path $fixture '.local/api';Service=$null;Processes=@();Listeners=@();Task=$null;Events=[Collections.Generic.List[string]]::new();TaskKillsApi=$false;TaskStopThrows=$false;StopThrows=$false;StartThrows=$false;OwnerSid='S-1-5-21-TEST-1001'}
    if($Kind -in @('service','apphost','official')) {
        $exe=switch($Kind){'apphost'{Join-Path $fixture '.local/api/Pdm.Api.exe'} 'official'{Join-Path $fixture 'official-dotnet/dotnet.exe'} default{Join-Path $fixture '.dotnet/dotnet.exe'}}
        $command=if($Kind -eq 'apphost'){'"'+$exe+'"'}else{'"'+$exe+'" "'+(Join-Path $fixture '.local/api/Pdm.Api.dll')+'"'}
        $script:sim.Service=[pscustomobject]@{Name='UptonPdmApi';State='Stopped';ProcessId=0;PathName=$command;StartName='LocalSystem'}
    }
    if($Running){Set-SimRunning $Kind}
    if($Task){$script:sim.Task=[pscustomobject]@{TaskName='UPLM-LocalRuntime-Autostart';TaskPath='\';State='Ready';Settings=[pscustomobject]@{Enabled=$true};Principal=[pscustomobject]@{UserId='SYSTEM';GroupId='';LogonType='ServiceAccount';RunLevel='Highest'};Actions=@([pscustomobject]@{Execute=Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe';Arguments='-NoProfile -ExecutionPolicy Bypass -File "'+(Join-Path $fixture 'deploy/Start-LocalPdmRuntime.ps1')+'"';WorkingDirectory=$fixture})}}
}
function Invoke-SimBackup([scriptblock]$Body={}) {
    $runtime=Get-PdmBackupApiRuntime $fixture
    try {Suspend-PdmBackupApiRuntime $runtime; Assert-PdmBackupApiQuiescent $runtime; & $Body}
    finally {Restore-PdmBackupApiRuntime $runtime}
    return $runtime
}
try {
    foreach($relative in @('.dotnet/dotnet.exe','.local/api/Pdm.Api.dll','.local/api/Pdm.Api.exe','official-dotnet/dotnet.exe','deploy/Start-LocalPdmRuntime.ps1','.local/secrets/pdm-secrets.json')) {
        $path=Join-Path $fixture $relative; New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($path)) -Force|Out-Null
        [IO.File]::WriteAllText($path, $(if($relative -like '*secrets.json'){'{"databasePassword":"sim-private-value"}'}else{'仅用于隔离测试，不执行'}))
    }
    [IO.File]::WriteAllText((Join-Path $fixture '.local/api/appsettings.json'),'{"Kestrel":{"Endpoints":{"Http":{"Url":"http://127.0.0.1:5080"},"Lan":{"Url":"http://0.0.0.0:5173"}}}}')
    foreach($kind in @('service','apphost','official')) {
        Reset-Sim $kind
        if($kind -ne 'service'){Move-Item -LiteralPath (Join-Path $fixture '.dotnet/dotnet.exe') -Destination (Join-Path $fixture '.dotnet/unused.exe'); Move-Item -LiteralPath (Join-Path $fixture 'deploy/Start-LocalPdmRuntime.ps1') -Destination (Join-Path $fixture 'deploy/unused.ps1')}
        try {$runtime=Invoke-SimBackup; Assert-Test ($sim.Events.Contains('service-stop') -and $sim.Events.Contains('service-start') -and !$runtime.MutexAcquired) '服务暂停和恢复失败。'}
        finally{if($kind -ne 'service'){Move-Item -LiteralPath (Join-Path $fixture '.dotnet/unused.exe') -Destination (Join-Path $fixture '.dotnet/dotnet.exe'); Move-Item -LiteralPath (Join-Path $fixture 'deploy/unused.ps1') -Destination (Join-Path $fixture 'deploy/Start-LocalPdmRuntime.ps1')}}
        $checks.Add("服务宿主${kind}暂停与恢复，非本地宿主不依赖本地启动文件")
    }
    foreach($location in @('app','recorded-api')) {
        $apiDirectory=Join-Path $fixture $location;New-Item -ItemType Directory -Path $apiDirectory -Force|Out-Null
        foreach($file in @('Pdm.Api.dll','Pdm.Api.exe','appsettings.json')){Copy-Item -LiteralPath (Join-Path $fixture ('.local/api/'+$file)) -Destination (Join-Path $apiDirectory $file)}
        $receiptPath=Join-Path $fixture '.local/deployment-receipt.json'
        if($location -eq 'recorded-api'){@{apiPath=Join-Path $apiDirectory 'Pdm.Api.dll'}|ConvertTo-Json|Set-Content -LiteralPath $receiptPath -Encoding UTF8}
        Reset-Sim 'apphost';$sim.ApiDirectory=$apiDirectory;$sim.Service.PathName='"'+(Join-Path $apiDirectory 'Pdm.Api.exe')+'"';Set-SimRunning 'apphost'
        try{Invoke-SimBackup|Out-Null;Assert-Test $sim.Events.Contains('service-start') '正式安装API路径未能恢复。'}finally{if(Test-Path -LiteralPath $receiptPath){Remove-Item -LiteralPath $receiptPath}}
        $checks.Add("正式安装${location}路径按受控位置或安装记录核验并恢复")
    }
    Reset-Sim 'service' $false;$sim.Kind='local';Set-SimRunning 'local';Invoke-SimBackup|Out-Null
    Assert-Test ($sim.Service.State -eq 'Stopped' -and $sim.Events.Contains('api-start') -and !$sim.Events.Contains('service-start')) '独立API恢复时不应启动原停止的服务。'
    $checks.Add('已安装但停止的服务与独立API并存时保留原模式')
    Reset-Sim 'local' $true $true; $runtime=Invoke-SimBackup
    Assert-Test ($sim.Events.Contains('api-stop') -and $sim.Events.Contains('api-start') -and $sim.Task.Settings.Enabled -and !$runtime.MutexAcquired) '独立进程或自动拉起任务未恢复。'
    $checks.Add('独立API暂停、恢复及自动拉起任务启用状态恢复')
    foreach($kind in @('local','service')){Reset-Sim $kind $false $true; Invoke-SimBackup|Out-Null; Assert-Test (!$sim.Events.Contains('api-start') -and !$sim.Events.Contains('service-start')) '原未运行的API不应被启动。';$checks.Add("原未运行的${kind}保持未运行")}
    Reset-Sim 'local' $true $true; $sim.Task.Settings.Enabled=$false; Invoke-SimBackup|Out-Null; Assert-Test (!$sim.Task.Settings.Enabled) '原禁用任务被意外启用。';$checks.Add('原禁用的自动启动任务保持禁用')
    Reset-Sim 'local' $true $true; Expect-Refusal {Invoke-SimBackup {throw '模拟复制失败。'}}; Assert-Test ($sim.Events.Contains('api-start') -and $sim.Task.Settings.Enabled) '复制异常后API未恢复。';$checks.Add('备份复制异常仍恢复API和自动启动任务')
    Reset-Sim 'local' $true $true; $sim.StopThrows=$true; Expect-Refusal {Invoke-SimBackup}; Assert-Test $sim.Events.Contains('api-start') '停止后异常未恢复API。';$checks.Add('API停止后抛错仍执行恢复')
    foreach($throws in @($false,$true)){
        Reset-Sim 'local' $true $true; $sim.Task.State='Running';$sim.TaskKillsApi=$true;$sim.TaskStopThrows=$throws
        if($throws){Expect-Refusal {Invoke-SimBackup}}else{Invoke-SimBackup|Out-Null}
        Assert-Test ($sim.Events.Contains('api-start') -and $sim.Task.Settings.Enabled -and $sim.Events.Contains('task-start')) '任务连带停止API后未恢复原运行状态。'
        $checks.Add("启动任务连带结束API后的恢复，任务停止异常=$throws")
    }
    Reset-Sim 'local' $true $true; $sim.StartThrows=$true; Expect-Refusal {Invoke-SimBackup}; Assert-Test ($sim.Task.Settings.Enabled -and $sim.Events.Contains('unlock')) '恢复失败后任务或互斥未释放。';$checks.Add('恢复失败仍恢复任务设置并释放互斥')
    foreach($field in @('ExecutablePath','CommandLine','CreationDate')){Reset-Sim; $sim.Processes[0].$field=$null; Expect-Refusal {Get-PdmBackupApiRuntime $fixture}; Assert-Test ($sim.Events.Count -eq 0) '身份不可读时执行了停机。';$checks.Add("身份字段${field}不可读时拒绝且无停机动作")}
    Reset-Sim; $sim.Processes[0].CommandLine += ' --password sim-private-value'; Expect-Refusal {Get-PdmBackupApiRuntime $fixture}; $checks.Add('不接受额外启动参数且错误提示不回显参数')
    Reset-Sim; $sim.Processes[0].ExecutablePath=Join-Path $fixture 'foreign/dotnet.exe';Expect-Refusal {Get-PdmBackupApiRuntime $fixture};$checks.Add('可执行路径不匹配时拒绝')
    Reset-Sim; $sim.Listeners[1].OwningProcess=9999;Expect-Refusal {Get-PdmBackupApiRuntime $fixture};$checks.Add('不同进程占用配置端口时拒绝')
    Reset-Sim 'local' $true $true; $state=Get-PdmBackupApiRuntime $fixture; $sim.Processes[0].CreationDate=$sim.Processes[0].CreationDate.AddMinutes(1)
    try{Expect-Refusal {Suspend-PdmBackupApiRuntime $state}}finally{Restore-PdmBackupApiRuntime $state}
    Assert-Test (!$sim.Events.Contains('api-stop') -and !$sim.Events.Contains('api-start')) '进程身份变化后仍停止或重启API。';$checks.Add('PID相同但创建时间变化时拒绝且不改API')
    Reset-Sim 'local' $true $true;$sim.Task.Actions[0].Arguments='-File "C:\unrelated.ps1"';Expect-Refusal {Get-PdmBackupApiRuntime $fixture};$checks.Add('不暂停其他安装目录的自动启动任务')
    foreach($change in @('Principal','Enabled','Running','Shell')) {
        Reset-Sim 'local' $true $true;$state=Get-PdmBackupApiRuntime $fixture
        switch($change){'Principal'{$sim.Task.Principal.UserId='ChangedAccount'} 'Enabled'{$sim.Task.Settings.Enabled=$false} 'Running'{$sim.Task.State='Running'} 'Shell'{$sim.Task.Actions[0].Execute=Join-Path $PSHOME 'pwsh.exe'}}
        try{Expect-Refusal {Suspend-PdmBackupApiRuntime $state}}finally{Restore-PdmBackupApiRuntime $state}
        Assert-Test (!$sim.Events.Contains('task-disable') -and !$sim.Events.Contains('api-stop')) '任务身份变化后仍修改了任务或API。'
        $checks.Add("任务${change}在计划后变化时停机前拒绝，不修改新状态")
    }
    Reset-Sim;$sim.OwnerSid='S-1-5-18';Expect-Refusal {Get-PdmBackupApiRuntime $fixture};Assert-Test ($sim.Events.Count -eq 0) '跨运行账户发生停机。';$checks.Add('独立API运行账户不同于备份账户时停机前拒绝')
    Reset-Sim; $state=Get-PdmBackupApiRuntime $fixture; $out=Join-Path $fixture 'local-config';New-Item -ItemType Directory -Path $out|Out-Null
    $returned=@(Copy-PdmBackupApiConfiguration $state $out)
    Assert-Test ($returned.Count -eq 0 -and (Get-FileHash -LiteralPath (Join-Path $out 'api-private-secrets.json')).Hash -eq (Get-FileHash -LiteralPath (Join-Path $fixture '.local/secrets/pdm-secrets.json')).Hash) '本机私有配置未原样归档或被输出到日志。'
    Assert-Test ((Test-Path -LiteralPath (Join-Path $out 'api-appsettings.json')) -and (Test-Path -LiteralPath (Join-Path $out 'api-start-local-runtime.ps1'))) '本机恢复所需配置不完整。';$checks.Add('独立模式私有配置、API设置和启动脚本归档，返回不含凭据')
    Reset-Sim 'service';$state=Get-PdmBackupApiRuntime $fixture;$out=Join-Path $fixture 'service-config';New-Item -ItemType Directory -Path $out|Out-Null
    $returned=@(Copy-PdmBackupApiConfiguration $state $out);$archive=Get-Content -LiteralPath (Join-Path $out 'api-service.json') -Raw|ConvertFrom-Json
    Assert-Test ($returned.Count -eq 0 -and $archive.imagePath -eq $sim.Service.PathName -and $archive.account -eq 'LocalSystem' -and $archive.environment -contains 'PDM_DB_PASSWORD=sim-private-value') '已有服务归档字段未保留。';$checks.Add('服务归档兼容原imagePath/account/environment且不输出凭据')
    Remove-Item -LiteralPath (Join-Path $fixture '.local/secrets/pdm-secrets.json')
    $out=Join-Path $fixture 'service-no-private-config';New-Item -ItemType Directory -Path $out|Out-Null
    Copy-PdmBackupApiConfiguration $state $out
    $acl=Get-Acl -LiteralPath $out; $allowed=@('S-1-5-18','S-1-5-32-544',[Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
    $unexpected=@($acl.Access | Where-Object {$_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -notin $allowed})
    Assert-Test ($acl.AreAccessRulesProtected -and $unexpected.Count -eq 0 -and (Test-Path -LiteralPath (Join-Path $out 'api-service.json'))) '没有私有文件的服务归档未保持受限ACL。'
    $checks.Add('纯服务无私有secret文件时仍先建立受限ACL再归档服务凭据')
    [pscustomobject]@{Status='passed';Checks=$checks.Count;Cases=$checks;Isolation='所有主机服务、进程、端口、任务及账户调用均为隔离替身；只在GUID测试目录写文件';RealApiStopped=$false}|ConvertTo-Json -Depth 5
} finally {
    $resolved=[IO.Path]::GetFullPath($fixture)
    if(!$resolved.StartsWith($testParent+'\',[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notmatch '^[a-f0-9]{32}$'){throw '测试清理范围无效。'}
    if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
