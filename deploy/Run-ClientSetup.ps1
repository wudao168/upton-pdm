[CmdletBinding()]
param([switch]$Elevated)

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', ('"{0}"' -f $PSCommandPath),
        '-Elevated'
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
    $installer = Join-Path $temporaryRoot 'Install-ClientTestPackage.ps1'
    if (-not (Test-Path -LiteralPath $installer)) { throw '没有找到客户端安装脚本。' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer
    if ($LASTEXITCODE -ne 0) { throw "客户端安装失败，退出码：$LASTEXITCODE" }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}
