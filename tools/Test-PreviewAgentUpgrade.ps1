[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repository 'deploy\Update-PreviewAgent.ps1'
$code = [IO.File]::ReadAllText($source).Replace("`r`n", "`n")
$guard = @'
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请以管理员身份打开 PowerShell，再执行本升级脚本。'
}
'@
if (-not $code.Contains($guard)) { throw '未找到正式脚本的管理员检查，未执行隔离测试。' }
# Only the sandbox copy omits elevation; every service/process/network command is mocked below.
$code = $code.Replace($guard, '# 隔离测试不操作真实服务，无需管理员权限。')
$testRoot = Join-Path $repository ('tmp\preview-upgrade-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$passed = 0
foreach ($scenario in @('stopped', 'active-unreachable', 'running-task', 'busy', 'custom-bind', 'startup-failure', 'tampered', 'foreground', 'foreground-rollback', 'paused-idle', 'preserve-installer')) {
    $version = if ($scenario -like 'foreground*' -or $scenario -in @('paused-idle','preserve-installer')) { '1.2.0.0' } else { '1.1.4.0' }
    $directory = Join-Path $testRoot $scenario
    $package = Join-Path $directory 'package'
    $install = Join-Path $directory 'install'
    New-Item -ItemType Directory -Path (Join-Path $package 'payload\preview-worker'), (Join-Path $install 'preview-worker') -Force | Out-Null
    $scriptFile = Join-Path $package 'Update-PreviewAgent.ps1'
    [IO.File]::WriteAllText($scriptFile, $code, [Text.UTF8Encoding]::new($true))
    $files = foreach ($relative in @('Upton.Pdm.PreviewAgent.exe', 'preview-worker\Upton.Pdm.SolidWorks.PreviewWorker.exe')) {
        [IO.File]::WriteAllText((Join-Path $install $relative), 'old-program')
        $payload = Join-Path (Join-Path $package 'payload') $relative
        [IO.File]::WriteAllText($payload, 'new-program')
        [pscustomobject]@{ Path = $relative; Sha256 = (Get-FileHash -LiteralPath $payload).Hash }
    }
    @{ Version = $version; Files = @($files) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $package 'files.json') -Encoding UTF8
    $bind = if ($scenario -eq 'custom-bind') { '10.7.7.177' } else { '0.0.0.0' }
    $configuration = Join-Path $install 'PdmPreviewAgent.json'
    @{ Port = 5199; BindAddress = $bind; Token = 'preserve-test-configuration' } | ConvertTo-Json | Set-Content -LiteralPath $configuration -Encoding UTF8
    $configurationHash = (Get-FileHash -LiteralPath $configuration).Hash
    if ($scenario -eq 'tampered') { [IO.File]::WriteAllText((Join-Path $package 'payload\Upton.Pdm.PreviewAgent.exe'), 'corrupt') }
    $state = [pscustomobject]@{ Started = $false; Starts = 0; Stops = 0; Migrated = $false; ManagerDisabled = $false; Restored = 0; Calls = [Collections.Generic.List[string]]::new() }
    function Get-ScheduledTask {
        param($TaskName)
        if ($TaskName -like '* Manager') {
            if ($version -eq '1.2.0.0') { [pscustomobject]@{ State = 'Ready'; Actions = [pscustomobject]@{ Execute = (Join-Path $install 'UplmPreviewAgentSetup.exe') } } }
            return
        }
        [pscustomobject]@{ State = $(if ($scenario -in @('active-unreachable','running-task')) { 'Running' } else { 'Ready' });
            Actions = [pscustomobject]@{ Execute = (Join-Path $install 'Upton.Pdm.PreviewAgent.exe') }; Principal = [pscustomobject]@{ UserId = 'test-user' }; Settings = [pscustomobject]@{} }
    }
    function Invoke-RestMethod {
        param($Uri, $TimeoutSec)
        $state.Calls.Add([string]$Uri)
        if (-not $state.Started -and $scenario -notin @('busy','custom-bind','paused-idle','preserve-installer')) { throw '模拟代理已停止' }
        [pscustomobject]@{ busy = ($scenario -in @('busy','paused-idle')); processing = ($scenario -eq 'busy'); queued = 0; workerPath = (Join-Path $install 'preview-worker\Upton.Pdm.SolidWorks.PreviewWorker.exe');
            agentVersion = $(if ($scenario -in @('startup-failure','foreground-rollback')) { '1.1.3.0' } else { $version }); workerVersion = $version }
    }
    function Get-CimInstance {
        param($ClassName)
        if ($scenario -eq 'active-unreachable') { [pscustomobject]@{ Name = 'Upton.Pdm.PreviewAgent.exe'; ExecutablePath = (Join-Path $install 'Upton.Pdm.PreviewAgent.exe'); ProcessId = 123 } }
        if ($scenario -eq 'preserve-installer') { [pscustomobject]@{ Name = 'UplmPreviewAgentSetup.exe'; ExecutablePath = (Join-Path $install 'UplmPreviewAgentSetup.exe'); ProcessId = 321 } }
    }
    function Stop-ScheduledTask { param($TaskName) $state.Stops++ }
    function Start-ScheduledTask { param($TaskName) $state.Started = $true; $state.Starts++ }
    function Start-Process { param($FilePath, $WorkingDirectory, $WindowStyle, $RedirectStandardOutput, $RedirectStandardError) if($WindowStyle -ne 'Normal'){throw '前台代理启动方式不正确'};$state.Started=$true;$state.Starts++ }
    function Stop-Process { param($Id, [switch]$Force, $ErrorAction) throw '不应在隔离测试中停止任何真实进程。' }
    function Start-Sleep { param($Seconds) }
    function Export-ScheduledTask { param($TaskName) '<Task />' }
    function New-ScheduledTaskAction { param($Execute, $Argument, $WorkingDirectory) [pscustomobject]@{ Execute = $Execute; Argument = $Argument } }
    function New-ScheduledTaskTrigger { param([switch]$AtLogOn, $User) [pscustomobject]@{ AtLogOn = $AtLogOn; User = $User } }
    function New-ScheduledTaskPrincipal { param($UserId, $LogonType, $RunLevel) [pscustomobject]@{ UserId = $UserId; LogonType = $LogonType } }
    function Disable-ScheduledTask { param($TaskName) $state.ManagerDisabled = $true }
    function Register-ScheduledTask {
        param($TaskName, $Action, $Trigger, $Principal, $Settings, $Xml, [switch]$Force)
        if ($Xml) { $state.Restored++; if ($TaskName -like '* Manager') { $state.ManagerDisabled = $false }; return }
        if ($Principal.LogonType -ne 'Interactive' -or -not $Trigger.AtLogOn -or $Action.Argument -ne '--startup') { throw '代理任务未切换为前台交互登录启动' }
        $state.Migrated = $true
    }
    $failure = $null
    try { & $scriptFile -InstallRoot $install -PreserveProcessId 321 | Out-Null } catch { $failure = $_.Exception.Message }
    $success = $scenario -in @('stopped','custom-bind','foreground','paused-idle','preserve-installer')
    if ($success -and $failure) { throw "$scenario 升级不应失败：$failure" }
    if (-not $success -and -not $failure) { throw "$scenario 未阻止预期的失败" }
    if ((Get-FileHash -LiteralPath $configuration).Hash -ne $configurationHash) { throw "$scenario 改写了配置" }
    foreach ($relative in $files.Path) {
        $expected = if ($success) { 'new-program' } else { 'old-program' }
        if ([IO.File]::ReadAllText((Join-Path $install $relative)) -ne $expected) { throw "$scenario 程序文件状态不正确" }
    }
    if ($success -or $scenario -in @('startup-failure','foreground-rollback')) {
        $backups = @(Get-ChildItem -LiteralPath $install -Directory -Filter 'backup-*')
        if ($backups.Count -ne 1 -or [IO.File]::ReadAllText((Join-Path $backups[0].FullName $files[0].Path)) -ne 'old-program') { throw "$scenario 未备份原程序" }
    } elseif ($state.Stops -ne 0 -or $state.Starts -ne 0) { throw "$scenario 在校验失败时操作了任务" }
    if ($scenario -eq 'startup-failure' -and ($failure -notmatch '已恢复备份' -or $state.Starts -ne 2)) { throw '启动失败未恢复原程序并重启任务' }
    if ($scenario -eq 'custom-bind' -and @($state.Calls | Where-Object { $_ -ne 'http://10.7.7.177:5199/health' }).Count) { throw '未使用配置的监听地址' }
    if ($scenario -eq 'foreground' -and (-not $state.Migrated -or -not $state.ManagerDisabled)) { throw '未切换前台任务或停用旧管理窗口任务' }
    if ($scenario -eq 'foreground-rollback' -and ($state.Restored -ne 2 -or $state.ManagerDisabled)) { throw '升级失败未恢复原任务配置' }
    Write-Output "PASS $scenario"
    $passed++
}
Write-Output "升级脚本隔离验收：$passed 项通过；测试文件保留于 $testRoot"
