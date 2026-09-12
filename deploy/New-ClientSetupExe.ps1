[CmdletBinding()]
param([string]$Version = '2026.09.12-test1')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root ".artifacts\uplm-client-test-$Version"
$stage = Join-Path $root ".artifacts\client-exe-stage-$Version"
$payloadStage = Join-Path $stage 'payload-source'
$setupContent = Join-Path $stage 'setup-content'
$payloadArchive = Join-Path $setupContent 'ClientPayload.zip'
$setupArchive = Join-Path $stage 'SetupContent.zip'
$bootstrapExe = Join-Path $stage 'UPLM-Client-Bootstrap.exe'
$bootstrapManifest = Join-Path $stage 'UPLM-Client-Bootstrap.manifest'
$outputExe = Join-Path $root ".artifacts\UPLM-Client-Setup-$Version.exe"
$serverPublishZip = Join-Path $root ".artifacts\UPLM-Client-ServerPublish-$Version.zip"
$cscCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$csc = $cscCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
$bootstrapSource = Join-Path $PSScriptRoot 'ClientSetupBootstrap.cs'

foreach ($required in @($csc, $bootstrapSource, (Join-Path $source 'Install-ClientTestPackage.ps1'), (Join-Path $source 'manifest.json'), (Join-Path $source 'payload'), (Join-Path $source 'prerequisites'), (Join-Path $source 'server-publish'))) {
    if ([string]::IsNullOrWhiteSpace($required) -or -not (Test-Path -LiteralPath $required)) { throw "缺少 EXE 打包输入：$required" }
}

if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $payloadStage -Force | Out-Null
New-Item -ItemType Directory -Path $setupContent -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $source 'Install-ClientTestPackage.ps1') -Destination $payloadStage
Copy-Item -LiteralPath (Join-Path $source 'manifest.json') -Destination $payloadStage
Copy-Item -LiteralPath (Join-Path $source 'payload') -Destination (Join-Path $payloadStage 'payload') -Recurse
Copy-Item -LiteralPath (Join-Path $source 'prerequisites') -Destination (Join-Path $payloadStage 'prerequisites') -Recurse
$utf8Bom = New-Object Text.UTF8Encoding($true)
$clientInstaller = Join-Path $payloadStage 'Install-ClientTestPackage.ps1'
[IO.File]::WriteAllText($clientInstaller, (Get-Content -LiteralPath $clientInstaller -Raw -Encoding UTF8), $utf8Bom)
Compress-Archive -Path (Join-Path $payloadStage '*') -DestinationPath $payloadArchive -CompressionLevel Optimal
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Run-ClientSetup.ps1') -Destination $setupContent
$setupRunner = Join-Path $setupContent 'Run-ClientSetup.ps1'
[IO.File]::WriteAllText($setupRunner, (Get-Content -LiteralPath $setupRunner -Raw -Encoding UTF8), $utf8Bom)
Compress-Archive -Path (Join-Path $setupContent '*') -DestinationPath $setupArchive -CompressionLevel NoCompression

$manifestXml = @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
    <security>
      <requestedPrivileges>
        <requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
      </requestedPrivileges>
    </security>
  </trustInfo>
</assembly>
'@
[IO.File]::WriteAllText($bootstrapManifest, $manifestXml, [Text.UTF8Encoding]::new($false))

& $csc /nologo /target:winexe /optimize+ /platform:anycpu /win32manifest:$bootstrapManifest /out:$bootstrapExe /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /reference:System.Windows.Forms.dll $bootstrapSource
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $bootstrapExe)) { throw "客户端自解压启动器编译失败，退出码：$LASTEXITCODE" }

if (Test-Path -LiteralPath $outputExe) { Remove-Item -LiteralPath $outputExe -Force }
$bootstrapLength = (Get-Item -LiteralPath $bootstrapExe).Length
$archiveLength = (Get-Item -LiteralPath $setupArchive).Length
$output = [IO.File]::Open($outputExe, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    foreach ($inputPath in @($bootstrapExe, $setupArchive)) {
        $input = [IO.File]::OpenRead($inputPath)
        try { $input.CopyTo($output) } finally { $input.Dispose() }
    }
    $writer = [IO.BinaryWriter]::new($output, [Text.Encoding]::ASCII, $true)
    try {
        $writer.Write([Text.Encoding]::ASCII.GetBytes('UPLMSFX1'))
        $writer.Write([long]$bootstrapLength)
        $writer.Write([long]$archiveLength)
    }
    finally { $writer.Dispose() }
}
finally { $output.Dispose() }

$serverStage = Join-Path $stage 'server-publish-package'
New-Item -ItemType Directory -Path $serverStage -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $source 'Publish-ClientUpdatesOnServer.ps1') -Destination $serverStage
Copy-Item -LiteralPath (Join-Path $source 'server-publish') -Destination (Join-Path $serverStage 'server-publish') -Recurse
$serverPublisher = Join-Path $serverStage 'Publish-ClientUpdatesOnServer.ps1'
[IO.File]::WriteAllText($serverPublisher, (Get-Content -LiteralPath $serverPublisher -Raw -Encoding UTF8), $utf8Bom)
if (Test-Path -LiteralPath $serverPublishZip) { Remove-Item -LiteralPath $serverPublishZip -Force }
Compress-Archive -Path (Join-Path $serverStage '*') -DestinationPath $serverPublishZip -CompressionLevel Optimal

[pscustomobject]@{
    ClientSetupExe = $outputExe
    ClientSetupExeLength = (Get-Item -LiteralPath $outputExe).Length
    ClientSetupExeSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $outputExe).Hash
    ServerPublishZip = $serverPublishZip
    ServerPublishZipSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $serverPublishZip).Hash
} | Format-List
