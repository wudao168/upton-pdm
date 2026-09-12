[CmdletBinding()]
param([string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'UPLM'))

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请在实际使用人的 Windows 账号中，以管理员身份运行本脚本。'
}

$packageRoot = Split-Path -Parent $PSCommandPath
$manifestPath = Join-Path $packageRoot 'manifest.json'
$desktopSource = Join-Path $packageRoot 'payload\desktop'
$addinSource = Join-Path $packageRoot 'payload\solidworks-addin'
$webViewInstaller = Join-Path $packageRoot 'prerequisites\MicrosoftEdgeWebView2RuntimeInstallerX64.exe'
$net48Installer = Join-Path $packageRoot 'prerequisites\NDP48-x86-x64-AllOS-ENU.exe'
foreach ($required in @($manifestPath, (Join-Path $desktopSource 'Upton.Pdm.Desktop.exe'), (Join-Path $addinSource 'Upton.Pdm.SolidWorks.Addin.dll'), $webViewInstaller, $net48Installer)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "客户端安装包不完整：$required" }
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
function Assert-Hash([string]$Path, [string]$Expected) {
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
    if (-not [string]::Equals($actual, $Expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "文件校验失败：$Path"
    }
}
Assert-Hash (Join-Path $desktopSource 'Upton.Pdm.Desktop.exe') $manifest.desktopExeSha256
Assert-Hash (Join-Path $addinSource 'Upton.Pdm.SolidWorks.Addin.dll') $manifest.addinDllSha256
Assert-Hash $webViewInstaller $manifest.webView2Sha256
Assert-Hash $net48Installer $manifest.netFramework48Sha256

$running = @(Get-Process -Name 'SLDWORKS', 'Upton.Pdm.Desktop' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    throw '请让使用者正常退出 SolidWorks 和 UPLM 客户端后再安装；安装程序不会强制关闭它们。'
}

$rebootRequired = $false
$net48Release = 0
try { $net48Release = [int](Get-ItemPropertyValue 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -Name Release -ErrorAction Stop) } catch { }
if ($net48Release -lt 528040) {
    $process = Start-Process -FilePath $net48Installer -ArgumentList '/q','/norestart' -Wait -PassThru
    if ($process.ExitCode -notin @(0, 1641, 3010)) { throw ".NET Framework 4.8 安装失败，退出码：$($process.ExitCode)" }
    if ($process.ExitCode -in @(1641, 3010)) { $rebootRequired = $true }
}

$webViewKey = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
if (-not (Test-Path -LiteralPath $webViewKey)) {
    $process = Start-Process -FilePath $webViewInstaller -ArgumentList '/silent','/install' -Wait -PassThru
    if ($process.ExitCode -notin @(0, 1641, 3010)) { throw "WebView2 Runtime 安装失败，退出码：$($process.ExitCode)" }
    if ($process.ExitCode -in @(1641, 3010)) { $rebootRequired = $true }
}

$desktopTarget = Join-Path $InstallRoot 'client'
$addinTarget = Join-Path $InstallRoot 'solidworks-addin'
New-Item -ItemType Directory -Path $desktopTarget,$addinTarget -Force | Out-Null
Copy-Item -Path (Join-Path $desktopSource '*') -Destination $desktopTarget -Recurse -Force
Copy-Item -Path (Join-Path $addinSource '*') -Destination $addinTarget -Recurse -Force

$regAsm = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'
if (-not (Test-Path -LiteralPath $regAsm)) { throw '未找到 64 位 .NET Framework RegAsm。' }
$addinDll = Join-Path $addinTarget 'Upton.Pdm.SolidWorks.Addin.dll'
$addinTlb = [IO.Path]::ChangeExtension($addinDll, '.tlb')
& $regAsm $addinDll /codebase "/tlb:$addinTlb"
if ($LASTEXITCODE -ne 0) { throw "SolidWorks 插件注册失败，退出码：$LASTEXITCODE" }

$desktopExe = Join-Path $desktopTarget 'Upton.Pdm.Desktop.exe'
$icon = Join-Path $desktopTarget 'UPTON-PLM.ico'
$shell = New-Object -ComObject WScript.Shell
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Desktop')) 'UPLM.lnk'
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $desktopExe
$shortcut.WorkingDirectory = $desktopTarget
if (Test-Path -LiteralPath $icon) { $shortcut.IconLocation = "$icon,0" }
$shortcut.Description = 'UPLM engineering client'
$shortcut.Save()

$preferenceKey = 'HKCU:\Software\UPTON\PDM Desktop'
New-Item -Path $preferenceKey -Force | Out-Null
New-ItemProperty -Path $preferenceKey -Name StartWithWindows -PropertyType DWord -Value 1 -Force | Out-Null
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
New-ItemProperty -Path $runKey -Name UPLM -PropertyType String -Value ('"{0}" --startup' -f $desktopExe) -Force | Out-Null

$receipt = [ordered]@{
    installedAt = [DateTimeOffset]::Now.ToString('O')
    version = $manifest.version
    serverBaseUrl = $manifest.serverBaseUrl
    desktopPath = $desktopExe
    addinPath = $addinDll
    shortcutPath = $shortcutPath
    rebootRequired = $rebootRequired
}
$receipt | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $InstallRoot 'client-install-receipt.json') -Encoding UTF8

Write-Host "UPLM 客户端和 SolidWorks 插件安装完成：$($manifest.version)" -ForegroundColor Green
Write-Host "桌面快捷方式：$shortcutPath"
if ($rebootRequired) { Write-Host '系统运行环境已更新，请重启电脑后再打开 UPLM 和 SolidWorks。' -ForegroundColor Yellow }
else { Write-Host '现在可以从桌面打开 UPLM，并在 SolidWorks 工具 > 插件中确认 UPLM。' }
