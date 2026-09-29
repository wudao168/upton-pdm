[CmdletBinding()]
param(
    [switch]$NoElevation,
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'UPLM')
)

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $NoElevation -and -not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $PSCommandPath), '-NoElevation', '-InstallRoot', ('"{0}"' -f $InstallRoot))
    $process = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $arguments -Wait -PassThru
    exit $process.ExitCode
}
$installRootFull = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
if ([IO.Path]::GetFileName($installRootFull) -ine 'UPLM' -or $installRootFull -eq [IO.Path]::GetPathRoot($installRootFull).TrimEnd('\')) {
    throw "仅允许卸载名称为 UPLM 的安装目录：$installRootFull"
}
$desktopExe = Join-Path $installRootFull 'client\Upton.Pdm.Desktop.exe'
$addinDll = Join-Path $installRootFull 'solidworks-addin\Upton.Pdm.SolidWorks.Addin.dll'
$solidWorksAddinGuid = '{BCFD8A8A-472B-42E2-AC62-58BC17773650}'

function Remove-UplmRegistrationResidue {
    foreach ($registryPath in @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\UPLMClient',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\UPLMClient',
        'HKCU:\Software\UPTON\PDM Desktop',
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
}

if (-not (Test-Path -LiteralPath $desktopExe -PathType Leaf)) {
    Write-Host '客户端安装目录已不存在，正在清理历史版本的注册表和用户残留。'
}
$desktopProcesses = @(Get-Process -Name 'Upton.Pdm.Desktop' -ErrorAction SilentlyContinue)
foreach ($process in $desktopProcesses) { $null = $process.CloseMainWindow() }
foreach ($process in $desktopProcesses) {
    if (-not $process.WaitForExit(10000)) { Stop-Process -Id $process.Id -Force -ErrorAction Stop }
}
if ((Test-Path -LiteralPath $addinDll -PathType Leaf) -and @(Get-Process -Name 'SLDWORKS' -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'SolidWorks 正在运行。为避免丢失未保存图纸，请先退出 SolidWorks 后再次卸载。'
}

$receiptPath = Join-Path $installRootFull 'client-install-receipt.json'
$receipt = if (Test-Path -LiteralPath $receiptPath) { Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
$regAsm = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'
if ((Test-Path -LiteralPath $addinDll -PathType Leaf) -and (Test-Path -LiteralPath $regAsm -PathType Leaf)) {
    & $regAsm $addinDll /unregister
    if ($LASTEXITCODE -ne 0) { throw "SolidWorks 插件注销失败，退出码：$LASTEXITCODE" }
}
Remove-UplmRegistrationResidue
$shortcutPath = if ($null -ne $receipt -and -not [string]::IsNullOrWhiteSpace([string]$receipt.shortcutPath)) { [string]$receipt.shortcutPath } else { Join-Path ([Environment]::GetFolderPath('Desktop')) 'UPLM.lnk' }
if (Test-Path -LiteralPath $shortcutPath -PathType Leaf) { Remove-Item -LiteralPath $shortcutPath -Force }
if (Test-Path -LiteralPath $installRootFull) { Remove-Item -LiteralPath $installRootFull -Recurse -Force }
Write-Host 'UPLM Windows 客户端和 SolidWorks 插件已卸载。' -ForegroundColor Green
