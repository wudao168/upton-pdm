[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $repo '.dotnet\dotnet.exe'
$fixture = Join-Path $repo ('tmp\native-update-test-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $fixture 'payload'
$version = '2026.11.13.120000'
New-Item -ItemType Directory -Path (Join-Path $payload 'ui') -Force | Out-Null
$rsa = [Security.Cryptography.RSACryptoServiceProvider]::new(3072)
$rsa.PersistKeyInCsp = $false
try {
    $key = Join-Path $fixture 'public.xml'
    [IO.File]::WriteAllText($key, $rsa.ToXmlString($false))
    $project = Join-Path $repo 'tools\Pdm.Updater.Acceptance\Pdm.Updater.Acceptance.csproj'
    & $dotnet build $project -c Release --nologo -p:NuGetAudit=false "-p:UplmUpdatePublicKeyFile=$key" "-p:InformationalVersion=$version"
    if ($LASTEXITCODE) { throw '更新回归工具构建失败。' }
    $exe = Join-Path (Split-Path -Parent $project) 'bin\Release\net48\Pdm.Updater.Acceptance.exe'
    Copy-Item -LiteralPath $exe -Destination (Join-Path $payload 'Upton.Pdm.SolidWorks.Addin.dll')
    [IO.File]::WriteAllText((Join-Path $payload '.uplm-version'), $version)
    foreach ($page in @('index.html', 'review-overlay.html', 'server-settings.html', 'client-bootstrap.json')) {
        [IO.File]::WriteAllText((Join-Path $payload "ui\$page"), 'fixture')
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = Join-Path $fixture 'package.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $archive)
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    $bytes = [Text.Encoding]::UTF8.GetBytes("UPLM-UPDATE-V1`nsolidworks-addin`n$version`n$hash")
    $signature = $rsa.SignData($bytes, [Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'))
    $signaturePath = Join-Path $fixture 'signature.txt'
    [IO.File]::WriteAllText($signaturePath, [Convert]::ToBase64String($signature))
    $desktopPayload = Join-Path $fixture 'desktop-payload'
    New-Item -ItemType Directory -Path $desktopPayload -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $payload 'ui') -Destination $desktopPayload -Recurse
    Copy-Item -LiteralPath (Join-Path $payload '.uplm-version') -Destination $desktopPayload
    Copy-Item -LiteralPath $exe -Destination (Join-Path $desktopPayload 'Upton.Pdm.Desktop.exe')
    Copy-Item -LiteralPath $exe -Destination (Join-Path $desktopPayload 'UPLM.Updater.exe')
    $desktopArchive = Join-Path $fixture 'desktop.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($desktopPayload, $desktopArchive)
    $hash = (Get-FileHash -LiteralPath $desktopArchive -Algorithm SHA256).Hash
    $bytes = [Text.Encoding]::UTF8.GetBytes("UPLM-UPDATE-V1`ndesktop`n$version`n$hash")
    $desktopSignaturePath = Join-Path $fixture 'desktop-signature.txt'
    [IO.File]::WriteAllText($desktopSignaturePath, [Convert]::ToBase64String($rsa.SignData($bytes, [Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'))))
    & $exe $fixture $exe $signaturePath $desktopSignaturePath
    if ($LASTEXITCODE) { throw '独立更新器回归失败。' }
} finally { $rsa.Dispose() }
