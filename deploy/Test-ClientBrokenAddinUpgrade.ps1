$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $root ('tmp\broken-addin-upgrade-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$addinSource = Join-Path $fixture 'payload'
New-Item -ItemType Directory -Path $addinSource -Force | Out-Null
$fakeRegAsm = Join-Path $fixture 'RegAsm.ps1'
@'
param([string]$Assembly, [string]$Operation)
Add-Content -LiteralPath (Join-Path $PSScriptRoot 'calls.txt') -Value "$Assembly|$Operation"
if ($Assembly.EndsWith('broken.dll') -or (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'deny'))) {
    Write-Error 'RA0000: missing SolidWorks.Interop.swpublished'
    exit 100
}
Write-Output 'RegAsm success'
exit 0
'@ | Set-Content -LiteralPath $fakeRegAsm -Encoding UTF8
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Install-UPLMClient.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw '安装脚本存在语法错误。' }
$functionAst = $ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Unregister-PreviousUplmAddin'}, $true)
Invoke-Expression $functionAst.Extent.Text
# Redirect diagnostic files into the isolated fixture; do not touch installed software or registry.
function Join-Path {
    param([string]$Path, [string]$ChildPath)
    if ($ChildPath -eq 'UPLM\logs') { return Microsoft.PowerShell.Management\Join-Path $fixture 'logs' }
    return Microsoft.PowerShell.Management\Join-Path $Path $ChildPath
}
$calls = Join-Path $fixture 'calls.txt'
Unregister-PreviousUplmAddin $fakeRegAsm (Join-Path $fixture 'healthy.dll')
if (@(Get-Content -LiteralPath $calls).Count -ne 1) { throw '完整旧版插件不应触发替代注销。' }
Clear-Content -LiteralPath $calls
$status = @(Unregister-PreviousUplmAddin $fakeRegAsm (Join-Path $fixture 'broken.dll'))
$invocations = @(Get-Content -LiteralPath $calls)
if ($invocations.Count -ne 2 -or $invocations[1] -ne ((Join-Path $addinSource 'Upton.Pdm.SolidWorks.Addin.dll') + '|/unregister')) { throw '残缺旧版插件未使用安装包内插件恢复注销。' }
if ($status -match 'RA0000') { throw '安装状态不应展示第三方原始异常。' }
Set-Content -LiteralPath (Join-Path $fixture 'deny') -Value 'deny'
$blocked = $false
try { Unregister-PreviousUplmAddin $fakeRegAsm (Join-Path $fixture 'broken.dll') }
catch { $blocked = $_.Exception.Message.Contains('尚未删除旧安装文件') }
if (-not $blocked) { throw '替代注销失败时必须停止安装，保留旧安装。' }
if (-not (Get-ChildItem -LiteralPath (Join-Path $fixture 'logs') -File | Where-Object {(Get-Content -LiteralPath $_.FullName -Raw).Contains('RA0000')})) { throw '未保留原始诊断日志。' }
Write-Output 'PASS：完整旧版正常注销；缺失依赖时替代注销；双重失败时停止并保留旧安装；原始异常仅写诊断日志。'

foreach ($name in @('Stop-PreviousDesktopClient', 'Test-UplmInstallRoot', 'Resolve-UplmProgramPath', 'Backup-PreviousUplmPrograms', 'Restore-PreviousUplmPrograms')) {
    $definition = $ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name}, $true)
    Invoke-Expression $definition.Extent.Text
}
function New-UpgradeFixture([string]$Name) {
    $install = Join-Path $fixture ($Name + '\UPLM')
    foreach ($folder in @('client', 'solidworks-addin', 'Diagnostics', '用户图纸')) { New-Item -ItemType Directory -Path (Join-Path $install $folder) -Force | Out-Null }
    Set-Content -LiteralPath (Join-Path $install 'client\Upton.Pdm.Desktop.exe') -Value 'old-client'
    Set-Content -LiteralPath (Join-Path $install 'solidworks-addin\Upton.Pdm.SolidWorks.Addin.dll') -Value 'old-addin'
    Set-Content -LiteralPath (Join-Path $install 'client\用户零件.SLDPRT') -Value 'source-in-program-directory'
    Set-Content -LiteralPath (Join-Path $install 'Diagnostics\unsent.jsonl') -Value 'unsent-log'
    Set-Content -LiteralPath (Join-Path $install '用户图纸\原始图纸.SLDDRW') -Value 'original-drawing'
    Set-Content -LiteralPath (Join-Path $install 'server-settings.json') -Value '{"ServerAddress":"http://old-server","CustomValue":"retained"}'
    return $install
}
function Assert-UserData([string]$Install) {
    foreach ($file in @('Diagnostics\unsent.jsonl', '用户图纸\原始图纸.SLDDRW', 'server-settings.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Install $file) -PathType Leaf)) { throw "用户文件丢失：$file" }
    }
}
Remove-Item -LiteralPath (Join-Path $fixture 'deny') -Force
$install = New-UpgradeFixture 'normal'
$script:previousProgramBackups = New-Object System.Collections.Generic.List[object]
$script:previousInstallRoots = @($install)
$settingsBefore = Get-Content -LiteralPath (Join-Path $install 'server-settings.json') -Raw
Backup-PreviousUplmPrograms $install
Assert-UserData $install
if ((Get-Content -LiteralPath (Join-Path $install 'server-settings.json') -Raw) -ne $settingsBefore) { throw '备份程序时更改了用户设置。' }
$backup = $script:previousProgramBackups[0].BackupRoot
if ((Get-Content -LiteralPath (Join-Path $backup 'client\用户零件.SLDPRT') -Raw).Trim() -ne 'source-in-program-directory') { throw '程序目录内的用户源文件未保留。' }
foreach ($folder in @('client', 'solidworks-addin')) { New-Item -ItemType Directory -Path (Join-Path $install $folder) -Force | Out-Null }
Set-Content -LiteralPath (Join-Path $install 'client\Upton.Pdm.Desktop.exe') -Value 'partial-new-client'
$rollback = Restore-PreviousUplmPrograms $fakeRegAsm
if ($rollback -notmatch '插件注册已恢复') { throw '回滚后没有报告原插件注册结果。' }
if ((Get-Content -LiteralPath (Join-Path $install 'client\Upton.Pdm.Desktop.exe') -Raw).Trim() -ne 'old-client') { throw '未恢复原客户端目录。' }
if (-not (Test-Path -LiteralPath (Join-Path $backup 'failed-new\client\Upton.Pdm.Desktop.exe'))) { throw '失败的新程序文件未保留。' }
Assert-UserData $install
Write-Output 'PASS：仅备份两处程序目录；日志、设置及用户图纸原位保留；程序目录内源文件可恢复；失败新文件保留，旧程序及COM注册恢复。'

$install = New-UpgradeFixture 'partial-move'
$script:previousProgramBackups = New-Object System.Collections.Generic.List[object]
$script:previousInstallRoots = @($install)
$script:failSecondMove = $true
function Move-Item {
    param([string]$LiteralPath, [string]$Destination, [string]$ErrorAction)
    if ($script:failSecondMove -and $LiteralPath.EndsWith('\solidworks-addin')) { $script:failSecondMove = $false; throw '模拟第二个目录被占用。' }
    Microsoft.PowerShell.Management\Move-Item -LiteralPath $LiteralPath -Destination $Destination -ErrorAction Stop
}
$moveFailed = $false
try { Backup-PreviousUplmPrograms $install } catch { $moveFailed = $true }
if (-not $moveFailed) { throw '未触发移动中断验收。' }
$null = Restore-PreviousUplmPrograms $fakeRegAsm
Remove-Item Function:\Move-Item
if ((Get-Content -LiteralPath (Join-Path $install 'client\Upton.Pdm.Desktop.exe') -Raw).Trim() -ne 'old-client' -or
    (Get-Content -LiteralPath (Join-Path $install 'solidworks-addin\Upton.Pdm.SolidWorks.Addin.dll') -Raw).Trim() -ne 'old-addin') { throw '部分目录移动失败后原目录未恢复。' }
Assert-UserData $install
$escaped = $false
try { $null = Resolve-UplmProgramPath $install '..\outside' } catch { $escaped = $true }
if (-not $escaped) { throw '未阻止安装根目录外的移动目标。' }
Write-Output 'PASS：第二个目录移动失败时恢复已移动目录；移动前阻止越过UPLM根目录。'

$script:previousProgramBackups = New-Object System.Collections.Generic.List[object]
Set-Content -LiteralPath (Join-Path $fixture 'deny') -Value 'deny'
$registrationFailure = Restore-PreviousUplmPrograms $fakeRegAsm
if ($registrationFailure -notmatch '插件注册恢复未完成' -or $registrationFailure -match 'RA0000') { throw '注册恢复失败未以中文单独报告。' }
if (-not (Test-Path -LiteralPath (Join-Path $install 'client\Upton.Pdm.Desktop.exe'))) { throw '注册恢复失败影响已恢复程序目录。' }
Remove-Item -LiteralPath (Join-Path $fixture 'deny') -Force
$serverSettingsPath = Join-Path $install 'server-settings.json'
Set-Content -LiteralPath $serverSettingsPath -Value '{"ServerAddress":"http://old-server","CustomValue":"retained","Nested":{"More":{"Value":"unchanged"}}}'
$serverUri = [Uri]'http://new-server:5173'
$scriptText = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Install-UPLMClient.ps1'))
$settingsStart = $scriptText.IndexOf('$serverSettingsValues = @{}')
$settingsEnd = $scriptText.IndexOf('[IO.File]::WriteAllText((Join-Path $serverSettingsRoot', $settingsStart)
Invoke-Expression $scriptText.Substring($settingsStart, $settingsEnd - $settingsStart)
$settingsResult = $serverSettings | ConvertFrom-Json
if ($settingsResult.ServerAddress -ne 'http://new-server:5173' -or $settingsResult.CustomValue -ne 'retained' -or $settingsResult.Nested.More.Value -ne 'unchanged') { throw '安装选择服务器地址时未保留其他设置。' }
Write-Output 'PASS：注册恢复失败单独报告且保留程序；服务器地址采用安装参数并保留其他嵌套设置。'

$script:swRunning = $false
$script:desktopCloseCalled = $false
$fakeDesktop = [pscustomobject]@{ Id = 123456 }
$fakeDesktop | Add-Member ScriptMethod CloseMainWindow { $script:desktopCloseCalled = $true; return $true }
$fakeDesktop | Add-Member ScriptMethod WaitForExit { param($Milliseconds) return $false }
function Get-Process {
    param([string]$Name, [string]$ErrorAction)
    if ($Name -eq 'SLDWORKS') { if ($script:swRunning) { return [pscustomobject]@{ Id = 234567 } }; return }
    if ($Name -eq 'Upton.Pdm.Desktop') { return $fakeDesktop }
}
$blocked = $false
try { Stop-PreviousDesktopClient } catch { $blocked = $_.Exception.Message.Contains('未强制结束进程') }
if (-not $blocked -or -not $script:desktopCloseCalled) { throw '客户端拒绝关闭时未安全停止升级。' }
$script:swRunning = $true; $script:desktopCloseCalled = $false
try { Stop-PreviousDesktopClient; throw 'SolidWorks运行中仍允许升级。' } catch { if (-not $_.Exception.Message.Contains('SolidWorks 正在运行')) { throw } }
if ($script:desktopCloseCalled) { throw 'SolidWorks运行中不应先关闭客户端。' }
Remove-Item Function:\Get-Process
if (@($ast.FindAll({param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Stop-Process'}, $true)).Count -ne 0) { throw '安装脚本仍包含强制结束进程。' }
Write-Output 'PASS：客户端无法正常退出则停止；SolidWorks运行时先停止升级；安装脚本不强杀进程。'
