[CmdletBinding()]
param(
    [switch]$Elevated,
    [string]$ServerBaseUrl = '',
    [ValidateSet('Install', 'Repair', 'Uninstall')][string]$Mode = 'Install',
    [Parameter(Mandatory = $true)][string]$InstallRoot
)

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', ('"{0}"' -f $PSCommandPath),
        '-Elevated',
        '-ServerBaseUrl', ('"{0}"' -f $ServerBaseUrl),
        '-Mode', $Mode,
        '-InstallRoot', ('"{0}"' -f $InstallRoot)
    )
    $process = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $arguments -Wait -PassThru
    exit $process.ExitCode
}
$archive = Join-Path $PSScriptRoot 'ClientPayload.zip'
if (-not (Test-Path -LiteralPath $archive)) { throw '客户端安装数据不完整。' }
$temporaryRoot = Join-Path $env:TEMP ("UPLM-Client-Setup-{0}" -f [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
try {
    Expand-Archive -LiteralPath $archive -DestinationPath $temporaryRoot -Force
    $scriptName = if ($Mode -eq 'Uninstall') { 'Uninstall-UPLMClient.ps1' } else { 'Install-UPLMClient.ps1' }
    $script = Join-Path $temporaryRoot $scriptName
    if (-not (Test-Path -LiteralPath $script)) { throw "没有找到 $scriptName。" }
    if ($Mode -eq 'Uninstall') {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script -NoElevation -InstallRoot $InstallRoot
    }
    else {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script -ServerBaseUrl $ServerBaseUrl -InstallRoot $InstallRoot
    }
    if ($LASTEXITCODE -ne 0) { throw "客户端$Mode失败，退出码：$LASTEXITCODE" }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}
