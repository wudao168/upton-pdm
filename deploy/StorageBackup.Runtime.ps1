$ErrorActionPreference = 'Stop'

function Get-PdmBackupTrustedDotnetHosts([string]$ProjectRoot) {
    @((Join-Path $ProjectRoot '.dotnet\dotnet.exe'), (Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'dotnet\dotnet.exe'))
}
function Get-PdmBackupRuntimePaths([string]$ProjectRoot, $Service) {
    $root = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\', '/')
    $apiCandidates = @((Join-Path $root '.local\api\Pdm.Api.dll'), (Join-Path $root 'app\Pdm.Api.dll'))
    foreach ($receiptPath in @((Join-Path $root '.local\deployment-receipt.json'), (Join-Path $root 'local\deployment-receipt.json'))) {
        if (Test-Path -LiteralPath $receiptPath -PathType Leaf) {
            $receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($receipt.apiPath) {
                $recorded = [IO.Path]::GetFullPath([string]$receipt.apiPath)
                if ($recorded.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($recorded) -ieq 'Pdm.Api.dll') { $apiCandidates += $recorded }
            }
        }
    }
    $apiDll = $apiCandidates[0]
    if ($Service) {
        $parts = Get-PdmBackupCommandParts $Service.PathName
        $serviceApi = if ($parts.Argument) { $parts.Argument } else { [IO.Path]::ChangeExtension($parts.Executable, '.dll') }
        $selected = @($apiCandidates | Where-Object { Test-PdmBackupRuntimePath $serviceApi $_ } | Select-Object -Unique)
        if ($selected.Count -ne 1) { throw '已安装API服务的程序路径不属于本仓库或正式安装记录，已停止备份。' }
        $apiDll = $selected[0]
    }
    $apiDirectory = [IO.Path]::GetDirectoryName($apiDll)
    $paths = [pscustomobject]@{
        Root = $root; Dotnet = Join-Path $root '.dotnet\dotnet.exe'; ApiDll = $apiDll; ApiDirectory = $apiDirectory
        AppHost = Join-Path $apiDirectory 'Pdm.Api.exe'; Startup = Join-Path $root 'deploy\Start-LocalPdmRuntime.ps1'
        LocalRoot = if (Test-PdmBackupRuntimePath $apiDirectory (Join-Path $root 'app')) { Join-Path $root 'local' } else { Join-Path $root '.local' }
        TrustedDotnetHosts = @(Get-PdmBackupTrustedDotnetHosts $root); Ports = @(5080)
    }
    foreach ($path in @($paths.ApiDll)) {
        Assert-PdmOrdinaryBackupPath $path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'API运行文件不完整，不能安全暂停或恢复备份。' }
    }
    $settings = Get-Content -LiteralPath (Join-Path $apiDirectory 'appsettings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $ports = @(5080)
    foreach ($endpoint in @($settings.Kestrel.Endpoints.PSObject.Properties)) {
        if ($endpoint.Value.Url) { $ports += ([Uri]$endpoint.Value.Url).Port }
    }
    $paths.Ports = @($ports | Sort-Object -Unique)
    return $paths
}
function Test-PdmBackupRuntimePath([string]$Actual, [string]$Expected) {
    if ([string]::IsNullOrWhiteSpace($Actual)) { return $false }
    try { return [IO.Path]::GetFullPath($Actual).Equals([IO.Path]::GetFullPath($Expected), [StringComparison]::OrdinalIgnoreCase) } catch { return $false }
}
function Get-PdmBackupCommandParts([string]$CommandLine) {
    $match = [regex]::Match($CommandLine, '^\s*(?:"(?<exe>[^"]+)"|(?<exe>[^\s"]+))(?:\s+(?:"(?<dll>[^"]+)"|(?<dll>[^\s"]+)))?\s*$')
    if (!$match.Success) { throw 'API启动参数无法完整核验，请使用本仓库的正式启动方式并以管理员身份重试。' }
    return [pscustomobject]@{ Executable = $match.Groups['exe'].Value; Argument = $match.Groups['dll'].Value }
}
function Assert-PdmBackupApiCommand([string]$CommandLine, $Paths) {
    $parts = Get-PdmBackupCommandParts $CommandLine
    $hosts = @($Paths.TrustedDotnetHosts | Where-Object { Test-PdmBackupRuntimePath $parts.Executable $_ })
    $expected = if ($hosts.Count -eq 1 -and (Test-PdmBackupRuntimePath $parts.Argument $Paths.ApiDll)) { $hosts[0] }
        elseif ((Test-PdmBackupRuntimePath $parts.Executable $Paths.AppHost) -and [string]::IsNullOrWhiteSpace($parts.Argument)) { $Paths.AppHost }
        else { throw 'API启动路径不属于本仓库或正式安装的受信任宿主，已拒绝停止进程。' }
    Assert-PdmOrdinaryBackupPath $expected
    if (!(Test-Path -LiteralPath $expected -PathType Leaf)) { throw 'API宿主文件不存在，不能核验或恢复运行状态。' }
    return $expected
}
function Assert-PdmBackupLocalRecovery($Paths, [string]$ExecutablePath) {
    if (!(Test-PdmBackupRuntimePath $Paths.ApiDll (Join-Path $Paths.Root '.local\api\Pdm.Api.dll')) -or
        ($ExecutablePath -and !(Test-PdmBackupRuntimePath $ExecutablePath $Paths.Dotnet))) { throw '独立API不是本仓库启动脚本管理的实例，已拒绝停止。' }
    foreach ($path in @($Paths.Dotnet, $Paths.Startup, (Join-Path $Paths.LocalRoot 'secrets\pdm-secrets.json'))) {
        Assert-PdmOrdinaryBackupPath $path
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw '本机独立API恢复所需的启动文件或私有配置缺失，未停止API。' }
    }
}

# 将主机调用集中在这些小函数中，隔离回归可替换它们而不停止任何真实服务。
function Get-PdmBackupApiService { Get-CimInstance Win32_Service -Filter "Name='UptonPdmApi'" -ErrorAction Stop }
function Get-PdmBackupApiProcesses { @(Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='Pdm.Api.exe'" -ErrorAction Stop) }
function Get-PdmBackupCurrentUserSid { [Security.Principal.WindowsIdentity]::GetCurrent().User.Value }
function Get-PdmBackupProcessOwnerSid($Process) {
    $owner = Invoke-CimMethod -InputObject $Process -MethodName GetOwnerSid -ErrorAction Stop
    if ($owner.ReturnValue -ne 0 -or [string]::IsNullOrWhiteSpace($owner.Sid)) { throw '无法核验独立API的运行账户，未停止进程。' }
    return [string]$owner.Sid
}
function Get-PdmBackupServiceArchiveSource { Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\UptonPdmApi' -ErrorAction Stop }
function Get-PdmBackupApiListeners($Ports) { @(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object { $Ports -contains $_.LocalPort }) }
function Get-PdmBackupAutostartTask { @(Get-ScheduledTask -ErrorAction Stop | Where-Object { $_.TaskName -eq 'UPLM-LocalRuntime-Autostart' -and $_.TaskPath -eq '\' }) }
function Disable-PdmBackupAutostartTask { Disable-ScheduledTask -TaskName 'UPLM-LocalRuntime-Autostart' -TaskPath '\' -ErrorAction Stop | Out-Null }
function Enable-PdmBackupAutostartTask { Enable-ScheduledTask -TaskName 'UPLM-LocalRuntime-Autostart' -TaskPath '\' -ErrorAction Stop | Out-Null }
function Stop-PdmBackupAutostartTask { Stop-ScheduledTask -TaskName 'UPLM-LocalRuntime-Autostart' -TaskPath '\' -ErrorAction Stop }
function Start-PdmBackupAutostartTask { Start-ScheduledTask -TaskName 'UPLM-LocalRuntime-Autostart' -TaskPath '\' -ErrorAction Stop }
function Stop-PdmBackupApiService { Stop-Service -Name 'UptonPdmApi' -ErrorAction Stop }
function Start-PdmBackupApiService { Start-Service -Name 'UptonPdmApi' -ErrorAction Stop }
function Test-PdmBackupApiHealth {
    try { return (Invoke-RestMethod 'http://127.0.0.1:5080/health' -TimeoutSec 3).status -eq 'ok' } catch { return $false }
}
function Get-PdmBackupTaskIdentity($Task, $Paths) {
    if (!$Task) { return $null }
    $actions = @($Task.Actions)
    $shells = @((Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'), (Join-Path $PSHOME 'pwsh.exe'))
    $allowedShell = @($shells | Where-Object { Test-PdmBackupRuntimePath $actions[0].Execute $_ }).Count -gt 0
    $expected = '^\s*-NoProfile\s+-ExecutionPolicy\s+Bypass\s+-File\s+"' + [regex]::Escape($Paths.Startup) + '"\s*$'
    if ($actions.Count -ne 1 -or !$allowedShell -or $actions[0].Arguments -notmatch $expected) {
        throw '自动启动任务不属于本仓库或启动参数已变化，不能安全暂停备份。'
    }
    if ((!$Task.Principal.UserId -and !$Task.Principal.GroupId) -or [string]::IsNullOrWhiteSpace([string]$Task.Principal.LogonType) -or [string]::IsNullOrWhiteSpace([string]$Task.Principal.RunLevel)) { throw '自动启动任务的运行账户或登录权限不可读，不能安全暂停备份。' }
    $signature = @([string]$actions[0].Execute, [string]$actions[0].Arguments, [string]$actions[0].WorkingDirectory,
        [string]$Task.Principal.UserId, [string]$Task.Principal.GroupId, [string]$Task.Principal.LogonType, [string]$Task.Principal.RunLevel) -join '|'
    return [pscustomobject]@{ Enabled = [bool]$Task.Settings.Enabled; WasRunning = [string]$Task.State -eq 'Running'; Signature = $signature }
}
function Test-PdmBackupTaskSnapshot($Current, $Original) {
    if (!$Current -or !$Original) { return ($null -eq $Current -and $null -eq $Original) }
    return $Current.Signature -ceq $Original.Signature -and $Current.Enabled -eq $Original.Enabled -and $Current.WasRunning -eq $Original.WasRunning
}
function Get-PdmBackupApiRuntime([string]$ProjectRoot) {
    try { $service = Get-PdmBackupApiService } catch { throw '无法读取API服务身份，请以系统管理员身份重试；未停止任何实例。' }
    $paths = Get-PdmBackupRuntimePaths $ProjectRoot $service
    try { $processes = @(Get-PdmBackupApiProcesses); $listeners = @(Get-PdmBackupApiListeners $paths.Ports); $tasks = @(Get-PdmBackupAutostartTask) }
    catch { throw '无法读取API进程、端口、服务或自动启动任务身份，请以系统管理员身份运行备份；未停止任何实例。' }
    if ($tasks.Count -gt 1) { throw '检测到重复的本机自动启动任务，已停止备份。' }
    $task = if ($tasks.Count) { Get-PdmBackupTaskIdentity $tasks[0] $paths } else { $null }
    if ($service) {
        $serviceExecutable = Assert-PdmBackupApiCommand $service.PathName $paths
        if ($service.State -notin @('Running','Stopped')) { throw 'API服务正在启动或停止，请待状态稳定后重试。' }
    }
    $owners = @($listeners.OwningProcess | Sort-Object -Unique)
    if ($owners.Count -gt 1) { throw 'API配置端口由多个进程占用，已拒绝暂停；请先核对端口冲突。' }
    $candidates = @($processes | Where-Object {
        ($_.CommandLine -and $_.CommandLine.Replace('/','\').IndexOf($paths.ApiDll, [StringComparison]::OrdinalIgnoreCase) -ge 0) -or
        (Test-PdmBackupRuntimePath $_.ExecutablePath $paths.AppHost) -or
        ((Test-PdmBackupRuntimePath $_.ExecutablePath $paths.Dotnet) -and [string]::IsNullOrWhiteSpace($_.CommandLine))
    })
    if ($owners.Count -eq 0) {
        $unreadable = @($processes | Where-Object { [string]::IsNullOrWhiteSpace($_.ExecutablePath) -or [string]::IsNullOrWhiteSpace($_.CommandLine) -or !$_.CreationDate })
        if ($candidates.Count -gt 0 -or $unreadable.Count -gt 0 -or ($service -and $service.State -eq 'Running')) { throw 'API进程与监听端口状态不一致或进程身份不可读，不能确认停机状态；请以管理员身份核对后重试。' }
        $process = $null
    } else {
        $matches = @($processes | Where-Object { $_.ProcessId -eq $owners[0] })
        if ($matches.Count -ne 1 -or $candidates.Count -ne 1 -or $matches[0].ProcessId -ne $candidates[0].ProcessId) { throw '无法唯一确认API端口所属进程，已拒绝停止进程。' }
        $process = $matches[0]
        if ([string]::IsNullOrWhiteSpace($process.ExecutablePath) -or [string]::IsNullOrWhiteSpace($process.CommandLine) -or !$process.CreationDate) { throw 'API可执行路径、启动参数或启动时间不可读，请以系统管理员身份重试；未停止进程。' }
        $expectedExe = Assert-PdmBackupApiCommand $process.CommandLine $paths
        if (!(Test-PdmBackupRuntimePath $process.ExecutablePath $expectedExe) -or @($listeners | Where-Object LocalPort -eq 5080).Count -eq 0) { throw 'API可执行路径或健康端口归属不一致，已拒绝停止进程。' }
        if ($service -and $service.State -eq 'Running' -and [int]$service.ProcessId -ne [int]$process.ProcessId) { throw 'API服务与端口进程身份不一致，已拒绝停止。' }
        if ($service -and $service.State -eq 'Running' -and !(Test-PdmBackupRuntimePath $process.ExecutablePath $serviceExecutable)) { throw 'API服务配置的宿主与实际进程不一致，已拒绝停止。' }
    }
    $mode = if ($service -and ($service.State -eq 'Running' -or !$process)) { 'service' } else { 'local-process' }
    if ($mode -eq 'local-process') { Assert-PdmBackupLocalRecovery $paths $(if ($process) { $process.ExecutablePath } else { $null }) }
    $ownerSid = $null
    if ($mode -eq 'local-process' -and $process) {
        try { $ownerSid = Get-PdmBackupProcessOwnerSid $process } catch { throw '无法核验独立API的运行账户，请以原运行账户的管理员权限重试；未停止进程。' }
        if ($ownerSid -ne (Get-PdmBackupCurrentUserSid)) { throw '当前备份账户与独立API运行账户不同，请使用原运行账户的管理员权限执行；未停止进程或更改任务账户。' }
    }
    return [pscustomobject]@{
        Paths = $paths; Mode = $mode; WasRunning = ($null -ne $process); ServiceInstalled = ($null -ne $service)
        ServiceAccount = if ($service) { [string]$service.StartName } else { $null }
        ProcessId = if ($process) { [int]$process.ProcessId } else { 0 }
        StartedAtUtc = if ($process) { ([DateTime]$process.CreationDate).ToUniversalTime() } else { $null }
        ExecutablePath = if ($process) { [string]$process.ExecutablePath } else { $null }
        CommandLine = if ($process) { [string]$process.CommandLine } else { $null }
        OwnerSid = $ownerSid
        AutoTask = $task; TaskChanged = $false; StopRequested = $false; Mutex = $null; MutexAcquired = $false
    }
}
function Enter-PdmBackupRuntimeLock($Runtime) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $key = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Runtime.Paths.Root.ToLowerInvariant())))).Replace('-','') } finally { $sha.Dispose() }
    $Runtime.Mutex = [Threading.Mutex]::new($false, ('Global\UPLM-StorageBackup-' + $key))
    try { $Runtime.MutexAcquired = $Runtime.Mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $Runtime.MutexAcquired = $true }
    if (!$Runtime.MutexAcquired) { $Runtime.Mutex.Dispose(); $Runtime.Mutex = $null; throw '本仓库已有备份正在暂停或恢复API，请待其完成后重试。' }
}
function Exit-PdmBackupRuntimeLock($Runtime) {
    if ($Runtime.Mutex) { try { if ($Runtime.MutexAcquired) { $Runtime.Mutex.ReleaseMutex() } } finally { $Runtime.Mutex.Dispose(); $Runtime.Mutex = $null; $Runtime.MutexAcquired = $false } }
}
function Stop-PdmVerifiedLocalApi($Runtime) {
    $process = [Diagnostics.Process]::GetProcessById($Runtime.ProcessId)
    try {
        $null = $process.Handle
        if ([Math]::Abs(($process.StartTime.ToUniversalTime() - $Runtime.StartedAtUtc).TotalMilliseconds) -ge 1) { throw 'API进程标识已被复用，已拒绝停止。' }
        $process.Kill()
        if (!$process.WaitForExit(30000)) { throw 'API进程未在30秒内停止，未开始备份。' }
    } finally { $process.Dispose() }
}
function Start-PdmBackupLocalApi($Runtime) {
    $shell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $Runtime.Paths.Startup + '"'
    $process = Start-Process -FilePath $shell -ArgumentList $arguments -WorkingDirectory $Runtime.Paths.Root -WindowStyle Hidden -PassThru
    try {
        if (!$process.WaitForExit(60000)) { $process.Kill(); throw '独立API恢复启动超过60秒，请检查本机启动日志。' }
        if ($process.ExitCode -ne 0) { throw '独立API启动脚本未成功完成，请检查本机启动日志。' }
    } finally { $process.Dispose() }
}
function Assert-PdmBackupApiQuiescent($Runtime) {
    $current = Get-PdmBackupApiRuntime $Runtime.Paths.Root
    if ($current.WasRunning) { throw '备份期间API被重新启动，不能确认数据一致性，本次备份不算完成。' }
}
function Suspend-PdmBackupApiRuntime($Runtime) {
    Enter-PdmBackupRuntimeLock $Runtime
    $initial = Get-PdmBackupApiRuntime $Runtime.Paths.Root
    if ($initial.WasRunning -ne $Runtime.WasRunning -or $initial.Mode -ne $Runtime.Mode -or $initial.ProcessId -ne $Runtime.ProcessId -or $initial.StartedAtUtc -ne $Runtime.StartedAtUtc) { throw 'API身份在暂停前发生变化，已拒绝停止，请重新生成备份计划。' }
    if (!(Test-PdmBackupTaskSnapshot $initial.AutoTask $Runtime.AutoTask)) { throw '自动启动任务的账户、命令或启停状态在暂停前发生变化，未修改任务，请重新生成备份计划。' }
    if ($Runtime.AutoTask) {
        $Runtime.TaskChanged = $true
        Disable-PdmBackupAutostartTask
        if ($Runtime.AutoTask.WasRunning) {
            $beforeTaskStop = Get-PdmBackupApiRuntime $Runtime.Paths.Root
            if ($beforeTaskStop.WasRunning -ne $Runtime.WasRunning -or $beforeTaskStop.ProcessId -ne $Runtime.ProcessId -or $beforeTaskStop.StartedAtUtc -ne $Runtime.StartedAtUtc -or !$beforeTaskStop.AutoTask -or $beforeTaskStop.AutoTask.Signature -cne $Runtime.AutoTask.Signature -or $beforeTaskStop.AutoTask.Enabled) { throw 'API或任务身份在停止自动启动任务前发生变化，已拒绝继续停止。' }
            # 结束启动任务可能连带结束其API子进程；先承担恢复责任，异常路径也必须恢复。
            $Runtime.StopRequested = $Runtime.WasRunning
            Stop-PdmBackupAutostartTask
        }
    }
    $current = Get-PdmBackupApiRuntime $Runtime.Paths.Root
    if ($Runtime.StopRequested -and $Runtime.AutoTask.WasRunning -and !$current.WasRunning) { Assert-PdmBackupApiQuiescent $Runtime; return }
    if ($current.WasRunning -ne $Runtime.WasRunning -or $current.Mode -ne $Runtime.Mode -or $current.ProcessId -ne $Runtime.ProcessId -or $current.StartedAtUtc -ne $Runtime.StartedAtUtc) {
        throw 'API身份在暂停前发生变化，已拒绝停止，请重新生成备份计划。'
    }
    if ($Runtime.WasRunning) {
        $Runtime.StopRequested = $true
        if ($Runtime.Mode -eq 'service') { Stop-PdmBackupApiService } else { Stop-PdmVerifiedLocalApi $Runtime }
        $deadline = [DateTimeOffset]::Now.AddSeconds(30)
        do {
            try { Assert-PdmBackupApiQuiescent $Runtime; return } catch { if ([DateTimeOffset]::Now -ge $deadline) { throw 'API未完全停止，未开始备份；将尝试恢复原运行状态。' } }
            Start-Sleep -Milliseconds 200
        } while ($true)
    }
    Assert-PdmBackupApiQuiescent $Runtime
}
function Restore-PdmBackupApiRuntime($Runtime) {
    if (!$Runtime) { return }
    try {
        if ($Runtime.StopRequested -and $Runtime.WasRunning) {
            $stableDeadline = [DateTimeOffset]::Now.AddSeconds(30)
            do {
                try { $current = Get-PdmBackupApiRuntime $Runtime.Paths.Root; break } catch { if ([DateTimeOffset]::Now -ge $stableDeadline) { throw 'API停止后的身份仍不可确认，请人工核对运行状态；自动启动任务将恢复原设置。' } }
                Start-Sleep -Milliseconds 200
            } while ($true)
            if (!$current.WasRunning) {
                if ($Runtime.Mode -eq 'service') { Start-PdmBackupApiService } else { Start-PdmBackupLocalApi $Runtime }
            }
            $deadline = [DateTimeOffset]::Now.AddSeconds(30)
            do {
                try { $current = Get-PdmBackupApiRuntime $Runtime.Paths.Root; if ($current.WasRunning -and $current.Mode -eq $Runtime.Mode -and $current.OwnerSid -eq $Runtime.OwnerSid -and (Test-PdmBackupApiHealth)) { break } } catch { }
                if ([DateTimeOffset]::Now -ge $deadline) { throw '备份结束后API未恢复原运行模式或健康状态，请检查本机启动日志。' }
                Start-Sleep -Milliseconds 200
            } while ($true)
        }
    } finally {
        try {
            if ($Runtime.TaskChanged) {
                $tasks = @(Get-PdmBackupAutostartTask)
                if ($tasks.Count -ne 1 -or (Get-PdmBackupTaskIdentity $tasks[0] $Runtime.Paths).Signature -cne $Runtime.AutoTask.Signature) { throw '自动启动任务在备份期间发生变化，未覆盖该任务；请核对后恢复。' }
                if ($Runtime.AutoTask.Enabled -or $Runtime.AutoTask.WasRunning) { Enable-PdmBackupAutostartTask }
                if ($Runtime.AutoTask.WasRunning) { Start-PdmBackupAutostartTask }
                if (!$Runtime.AutoTask.Enabled) { Disable-PdmBackupAutostartTask }
                $Runtime.TaskChanged = $false
            }
        } finally { Exit-PdmBackupRuntimeLock $Runtime }
    }
}
function Get-PdmBackupApiRuntimeConfiguration($Runtime) {
    # 只归档运行方式和可信路径，凭据仍由受保护的服务环境或本机私有配置提供。
    return [ordered]@{
        mode = $Runtime.Mode; wasRunning = $Runtime.WasRunning; serviceInstalled = $Runtime.ServiceInstalled
        serviceName = if ($Runtime.ServiceInstalled) { 'UptonPdmApi' } else { $null }; serviceAccount = $Runtime.ServiceAccount
        executablePath = $Runtime.ExecutablePath; apiAssemblyPath = $Runtime.Paths.ApiDll; startupScript = if ($Runtime.Mode -eq 'local-process') { $Runtime.Paths.Startup } else { $null }
        processStartedAtUtc = $Runtime.StartedAtUtc; ports = $Runtime.Paths.Ports
        processOwnerSid = $Runtime.OwnerSid
        credentialsIncluded = $false; credentialSource = if ($Runtime.Mode -eq 'service') { '受保护的Windows服务环境配置' } else { '.local/secrets/pdm-secrets.json' }
        autostartWasEnabled = if ($Runtime.AutoTask) { $Runtime.AutoTask.Enabled } else { $null }
    }
}

function Copy-PdmBackupApiConfiguration($Runtime, [string]$ConfigurationRoot) {
    Assert-PdmOrdinaryBackupPath $ConfigurationRoot
    if (@(Get-ChildItem -LiteralPath $ConfigurationRoot -Force).Count -ne 0) { throw '本次配置归档目录必须为空，未覆盖既有备份。' }
    # 在写入任何凭据之前先限制空目录ACL。纯服务安装没有私有secret文件时同样保护归档。
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $security.SetOwner($owner)
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544', $owner.Value) | Select-Object -Unique) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),
            [Security.AccessControl.FileSystemRights]::FullControl,
            ([Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit),
            [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)
        $security.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $ConfigurationRoot -AclObject $security
    $metadata = Get-PdmBackupApiRuntimeConfiguration $Runtime
    if ($Runtime.Mode -eq 'service') {
        $service = Get-PdmBackupServiceArchiveSource
        $null = Assert-PdmBackupApiCommand ([string]$service.ImagePath) $Runtime.Paths
        # 保留已有服务恢复格式；私密值仅写入用户已配置的备份文件，不返回到日志。
        $metadata['imagePath'] = $service.ImagePath; $metadata['account'] = $service.ObjectName; $metadata['environment'] = @($service.Environment)
        $metadata['credentialsIncluded'] = $true
        $runtimeName = 'api-service.json'
    } else { $runtimeName = 'api-local-runtime.json'; $metadata['credentialsIncluded'] = $true; $metadata['privateConfigurationFile'] = 'api-private-secrets.json' }
    $metadata | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ConfigurationRoot $runtimeName) -Encoding UTF8
    $sources = @(
        @{ Source = Join-Path $Runtime.Paths.ApiDirectory 'appsettings.json'; Name = 'api-appsettings.json'; Required = $true },
        @{ Source = Join-Path $Runtime.Paths.ApiDirectory 'appsettings.Production.json'; Name = 'api-appsettings.Production.json'; Required = $false },
        @{ Source = Join-Path $Runtime.Paths.LocalRoot 'secrets\pdm-secrets.json'; Name = 'api-private-secrets.json'; Required = ($Runtime.Mode -eq 'local-process') }
    )
    if ($Runtime.Mode -eq 'local-process') { $sources += @{ Source = $Runtime.Paths.Startup; Name = 'api-start-local-runtime.ps1'; Required = $true } }
    foreach ($source in $sources) {
        if (!(Test-Path -LiteralPath $source.Source -PathType Leaf)) { if ($source.Required) { throw 'API恢复所需的配置文件缺失，本次备份不算完成。' }; continue }
        Assert-PdmOrdinaryBackupPath $source.Source
        $destination = Join-Path $ConfigurationRoot $source.Name
        Copy-Item -LiteralPath $source.Source -Destination $destination
    }
}
