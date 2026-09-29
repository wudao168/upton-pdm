[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ServerBaseUrl,
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'UPLM')
)

$ErrorActionPreference = 'Stop'
function Write-InstallStep([int]$Number, [string]$Message) {
    Write-Output ("[UPLM_STEP:{0}/6] {1}" -f $Number, $Message)
}
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请在实际使用人的 Windows 账号中，以管理员身份运行客户端安装脚本。'
}

try { $serverUri = [Uri]$ServerBaseUrl.TrimEnd('/') }
catch { throw "服务器地址无效：$ServerBaseUrl" }
if (-not $serverUri.IsAbsoluteUri -or $serverUri.Scheme -notin @('http', 'https')) {
    throw '服务器地址必须是完整的 HTTP 或 HTTPS 地址，例如 http://10.7.7.62:5173。'
}
$ServerBaseUrl = $serverUri.AbsoluteUri.TrimEnd('/')
Write-InstallStep 1 '正在检查安装包和客户端运行环境…'

$packageRoot = Split-Path -Parent $PSCommandPath
$manifestPath = Join-Path $packageRoot 'manifest.json'
$uninstallerSource = Join-Path $packageRoot 'Uninstall-UPLMClient.ps1'
$uninstallerExeSource = Join-Path $packageRoot 'UPLM-Client-Uninstall.exe'
$desktopSource = Join-Path $packageRoot 'payload\desktop'
$addinSource = Join-Path $packageRoot 'payload\solidworks-addin'
$webViewInstaller = Join-Path $packageRoot 'prerequisites\MicrosoftEdgeWebView2RuntimeInstallerX64.exe'
$net48Installer = Join-Path $packageRoot 'prerequisites\NDP48-x86-x64-AllOS-ENU.exe'
foreach ($required in @($manifestPath, $uninstallerSource, $uninstallerExeSource, (Join-Path $desktopSource 'Upton.Pdm.Desktop.exe'), (Join-Path $addinSource 'Upton.Pdm.SolidWorks.Addin.dll'), $webViewInstaller, $net48Installer)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "客户端安装包不完整：$required" }
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
function Assert-Hash([string]$Path, [string]$Expected) {
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
    if (-not [string]::Equals($actual, $Expected, [StringComparison]::OrdinalIgnoreCase)) { throw "文件校验失败：$Path" }
}

function Get-WebView2RuntimeVersion {
    $appId = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    foreach ($key in @(
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\$appId",
        "HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$appId",
        "HKCU:\Software\Microsoft\EdgeUpdate\Clients\$appId"
    )) {
        try {
            $version = [string](Get-ItemPropertyValue -LiteralPath $key -Name 'pv' -ErrorAction Stop)
            if (-not [string]::IsNullOrWhiteSpace($version)) { return $version }
        }
        catch { }
    }
    return ''
}
Assert-Hash (Join-Path $desktopSource 'Upton.Pdm.Desktop.exe') $manifest.desktopExeSha256
Assert-Hash (Join-Path $addinSource 'Upton.Pdm.SolidWorks.Addin.dll') $manifest.addinDllSha256
Assert-Hash $webViewInstaller $manifest.webView2Sha256
Assert-Hash $net48Installer $manifest.netFramework48Sha256

function Stop-PreviousDesktopClient {
    $desktopProcesses = @(Get-Process -Name 'Upton.Pdm.Desktop' -ErrorAction SilentlyContinue)
    foreach ($process in $desktopProcesses) { $null = $process.CloseMainWindow() }
    foreach ($process in $desktopProcesses) {
        if (-not $process.WaitForExit(10000)) { Stop-Process -Id $process.Id -Force -ErrorAction Stop }
    }
    if (@(Get-Process -Name 'SLDWORKS' -ErrorAction SilentlyContinue).Count -gt 0) {
        throw 'SolidWorks 正在运行。为避免丢失未保存图纸，请先退出 SolidWorks 后再次运行升级安装包。'
    }
}

function Test-UplmInstallRoot([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    return ([IO.Path]::GetFileName($full) -ieq 'UPLM' -and $full -ne [IO.Path]::GetPathRoot($full).TrimEnd('\'))
}

function Test-UplmInstallation([string]$Path) {
    return (Test-Path -LiteralPath (Join-Path $Path 'client\Upton.Pdm.Desktop.exe') -PathType Leaf) -or
        (Test-Path -LiteralPath (Join-Path $Path 'solidworks-addin\Upton.Pdm.SolidWorks.Addin.dll') -PathType Leaf)
}

function Remove-UplmRegistrationResidue {
    $solidWorksAddinGuid = '{BCFD8A8A-472B-42E2-AC62-58BC17773650}'
    foreach ($registryPath in @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\UPLMClient',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\UPLMClient',
        "HKCU:\Software\SOLIDWORKS\AddInsStartup\$solidWorksAddinGuid",
        "HKLM:\SOFTWARE\SOLIDWORKS\Addins\$solidWorksAddinGuid",
        "HKCU:\Software\Classes\CLSID\$solidWorksAddinGuid",
        "HKLM:\SOFTWARE\Classes\CLSID\$solidWorksAddinGuid",
        'HKCU:\Software\Classes\Upton.Pdm.SolidWorks.Addin',
        'HKLM:\SOFTWARE\Classes\Upton.Pdm.SolidWorks.Addin'
    )) {
        Remove-Item -LiteralPath $registryPath -Recurse -Force -ErrorAction SilentlyContinue
    }
    Remove-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name UPLM -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath 'HKCU:\Software\UPTON\PDM Desktop' -Recurse -Force -ErrorAction SilentlyContinue
}

function Remove-PreviousUplmInstallation {
    Stop-PreviousDesktopClient
    $candidateRoots = New-Object System.Collections.Generic.List[string]
    foreach ($candidate in @($InstallRoot, (Join-Path $env:LOCALAPPDATA 'UPLM'))) {
        if ((Test-UplmInstallRoot $candidate) -and (Test-UplmInstallation $candidate)) { $candidateRoots.Add(([IO.Path]::GetFullPath($candidate).TrimEnd('\'))) }
    }
    foreach ($registryPath in @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\UPLMClient', 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\UPLMClient')) {
        try {
            $location = [string](Get-ItemPropertyValue -LiteralPath $registryPath -Name InstallLocation -ErrorAction Stop)
            if ((Test-UplmInstallRoot $location) -and (Test-UplmInstallation $location)) { $candidateRoots.Add(([IO.Path]::GetFullPath($location).TrimEnd('\'))) }
        } catch { }
    }
    $regAsm = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'
    foreach ($candidate in @($candidateRoots | Select-Object -Unique)) {
        $oldAddin = Join-Path $candidate 'solidworks-addin\Upton.Pdm.SolidWorks.Addin.dll'
        if ((Test-Path -LiteralPath $oldAddin -PathType Leaf) -and (Test-Path -LiteralPath $regAsm -PathType Leaf)) {
            & $regAsm $oldAddin /unregister
            if ($LASTEXITCODE -ne 0) { throw "旧版 SolidWorks 插件注销失败，退出码：$LASTEXITCODE" }
        }
        if (Test-Path -LiteralPath $candidate) { Remove-Item -LiteralPath $candidate -Recurse -Force }
    }
    Remove-UplmRegistrationResidue
    $oldShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'UPLM.lnk'
    Remove-Item -LiteralPath $oldShortcut -Force -ErrorAction SilentlyContinue
}

$rebootRequired = $false
$net48Release = 0
try { $net48Release = [int](Get-ItemPropertyValue 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -Name Release -ErrorAction Stop) } catch { }
if ($net48Release -lt 528040) {
    Write-InstallStep 2 '正在安装 .NET Framework 4.8…'
    $process = Start-Process -FilePath $net48Installer -ArgumentList '/q', '/norestart' -Wait -PassThru
    if ($process.ExitCode -notin @(0, 1641, 3010)) { throw ".NET Framework 4.8 安装失败，退出码：$($process.ExitCode)" }
    if ($process.ExitCode -in @(1641, 3010)) { $rebootRequired = $true }
}

$webView2Version = Get-WebView2RuntimeVersion
if ([string]::IsNullOrWhiteSpace($webView2Version)) {
    Write-InstallStep 2 '正在从安装包内置的离线 Microsoft Edge WebView2 Runtime 安装组件…'
    $process = Start-Process -FilePath $webViewInstaller -ArgumentList '/silent', '/install' -Wait -PassThru
    if ($process.ExitCode -notin @(0, 1641, 3010)) { throw "离线 WebView2 Runtime 安装失败，退出码：$($process.ExitCode)" }
    if ($process.ExitCode -in @(1641, 3010)) { $rebootRequired = $true }
    for ($attempt = 0; $attempt -lt 30 -and [string]::IsNullOrWhiteSpace($webView2Version); $attempt++) {
        Start-Sleep -Seconds 2
        $webView2Version = Get-WebView2RuntimeVersion
    }
    if ([string]::IsNullOrWhiteSpace($webView2Version)) { throw '离线 WebView2 Runtime 安装后未检测到运行时，请重启电脑后选择“修复”重试。' }
}
else { Write-InstallStep 2 "已检测到 Microsoft Edge WebView2 Runtime $webView2Version，跳过离线安装。" }

Write-InstallStep 3 '正在注销并清理旧版客户端和 SolidWorks 插件…'
Remove-PreviousUplmInstallation

Write-InstallStep 4 '正在复制新版客户端文件…'
$desktopTarget = Join-Path $InstallRoot 'client'
$addinTarget = Join-Path $InstallRoot 'solidworks-addin'
New-Item -ItemType Directory -Path $desktopTarget, $addinTarget -Force | Out-Null
Copy-Item -Path (Join-Path $desktopSource '*') -Destination $desktopTarget -Recurse -Force
Copy-Item -Path (Join-Path $addinSource '*') -Destination $addinTarget -Recurse -Force
$uninstallerTarget = Join-Path $InstallRoot 'Uninstall-UPLMClient.ps1'
Copy-Item -LiteralPath $uninstallerSource -Destination $uninstallerTarget -Force
$uninstallerToolRoot = Join-Path $env:ProgramData 'UPLM\ClientUninstall'
New-Item -ItemType Directory -Path $uninstallerToolRoot -Force | Out-Null
$uninstallerExeTarget = Join-Path $uninstallerToolRoot 'UPLM-Client-Uninstall.exe'
$uninstallerScriptTarget = Join-Path $uninstallerToolRoot 'Uninstall-UPLMClient.ps1'
Copy-Item -LiteralPath $uninstallerExeSource -Destination $uninstallerExeTarget -Force
Copy-Item -LiteralPath $uninstallerSource -Destination $uninstallerScriptTarget -Force
$locator = [ordered]@{ BootstrapUrl = "$ServerBaseUrl/client-bootstrap.json" } | ConvertTo-Json
$utf8 = New-Object Text.UTF8Encoding($false)
foreach ($target in @($desktopTarget, $addinTarget)) { [IO.File]::WriteAllText((Join-Path $target 'uplm-bootstrap.json'), $locator, $utf8) }

$regAsm = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'
$addinDll = Join-Path $addinTarget 'Upton.Pdm.SolidWorks.Addin.dll'
$addinRegistered = $false
Write-InstallStep 5 '正在注册 SolidWorks 插件…'
if (Test-Path -LiteralPath $regAsm -PathType Leaf) {
    $addinTlb = [IO.Path]::ChangeExtension($addinDll, '.tlb')
    & $regAsm $addinDll /codebase "/tlb:$addinTlb"
    if ($LASTEXITCODE -ne 0) { throw "SolidWorks 插件注册失败，退出码：$LASTEXITCODE" }
    $addinRegistered = $true
}
else { Write-Warning '未找到 64 位 RegAsm；已安装 Windows 客户端，但未注册 SolidWorks 插件。' }

$desktopExe = Join-Path $desktopTarget 'Upton.Pdm.Desktop.exe'
$icon = Join-Path $desktopTarget 'UPTON-PLM.ico'
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Desktop')) 'UPLM.lnk'
$shell = New-Object -ComObject WScript.Shell
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

$uninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\UPLMClient'
$uninstallCommand = '"{0}" --install-root "{1}"' -f $uninstallerExeTarget, $InstallRoot
New-Item -Path $uninstallKey -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayName -PropertyType String -Value 'UPLM 客户端和 SolidWorks 插件' -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayVersion -PropertyType String -Value ([string]$manifest.version) -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name Publisher -PropertyType String -Value 'UPTON' -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name InstallLocation -PropertyType String -Value $InstallRoot -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayIcon -PropertyType String -Value $desktopExe -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name UninstallString -PropertyType String -Value $uninstallCommand -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name NoModify -PropertyType DWord -Value 1 -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name NoRepair -PropertyType DWord -Value 1 -Force | Out-Null

$receipt = [ordered]@{
    installedAt = [DateTimeOffset]::Now.ToString('O'); releaseVersion = $manifest.version
    desktopVersion = $manifest.desktopVersion; solidWorksAddinVersion = $manifest.solidWorksAddinVersion
    serverBaseUrl = $ServerBaseUrl; desktopPath = $desktopExe; addinPath = $addinDll
    addinRegistered = $addinRegistered; shortcutPath = $shortcutPath; uninstallerPath = $uninstallerExeTarget; rebootRequired = $rebootRequired
}
$receipt | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $InstallRoot 'client-install-receipt.json') -Encoding UTF8
Write-InstallStep 6 '客户端和插件安装完成，正在创建 Windows 卸载入口。'
Write-Host "UPLM 客户端安装完成：桌面端 $($manifest.desktopVersion)，SolidWorks 插件 $($manifest.solidWorksAddinVersion)" -ForegroundColor Green
if ($rebootRequired) { Write-Warning '系统运行环境已更新，请重启电脑后再打开 UPLM 和 SolidWorks。' }
