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
