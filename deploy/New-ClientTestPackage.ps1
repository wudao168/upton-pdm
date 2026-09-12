[CmdletBinding()]
param(
    [string]$ServerBaseUrl = 'http://10.7.7.62:5173',
    [string]$Version = ([DateTimeOffset]::Now.ToString('yyyy.MM.dd.HHmm'))
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $root '.dotnet\dotnet.exe'
$prerequisiteSource = Join-Path $root '.artifacts\client-prerequisites'
$webViewInstaller = Join-Path $prerequisiteSource 'MicrosoftEdgeWebView2RuntimeInstallerX64.exe'
$net48Installer = Join-Path $prerequisiteSource 'NDP48-x86-x64-AllOS-ENU.exe'
$output = Join-Path $root ".artifacts\uplm-client-test-$Version"
$payload = Join-Path $output 'payload'
$desktopOutput = Join-Path $payload 'desktop'
$addinOutput = Join-Path $payload 'solidworks-addin'
$serverPublish = Join-Path $output 'server-publish'
$updates = Join-Path $serverPublish 'updates'
$prerequisites = Join-Path $output 'prerequisites'
foreach ($required in @($dotnet, $webViewInstaller, $net48Installer)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "缺少客户端构建依赖：$required" }
}
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Path $desktopOutput,$addinOutput,$updates,$prerequisites -Force | Out-Null

Push-Location $root
try {
    pnpm.cmd install --frozen-lockfile
    if ($LASTEXITCODE -ne 0) { throw '前端依赖还原失败。' }
    pnpm.cmd ui:build
    if ($LASTEXITCODE -ne 0) { throw '前端构建失败。' }
    & $dotnet restore 'src\Pdm.Desktop\Pdm.Desktop.csproj' --nologo -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw '桌面客户端依赖还原失败。' }
    & $dotnet restore 'src\Pdm.SolidWorks.Addin\Pdm.SolidWorks.Addin.csproj' --nologo -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'SolidWorks 插件依赖还原失败。' }
    & $dotnet build 'src\Pdm.Desktop\Pdm.Desktop.csproj' --configuration Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw '桌面客户端构建失败。' }
    & $dotnet build 'src\Pdm.SolidWorks.Addin\Pdm.SolidWorks.Addin.csproj' --configuration Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'SolidWorks 插件构建失败。' }
}
finally { Pop-Location }

$desktopBuild = Join-Path $root 'src\Pdm.Desktop\bin\Release\net48'
$addinBuild = Join-Path $root 'src\Pdm.SolidWorks.Addin\bin\Release\net48'
Get-ChildItem -LiteralPath $desktopBuild | Where-Object { $_.Name -ne 'Upton.Pdm.Desktop.exe.WebView2' } | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $desktopOutput $_.Name) -Recurse -Force
}
Get-ChildItem -LiteralPath $addinBuild | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $addinOutput $_.Name) -Recurse -Force
}

$serverBase = $ServerBaseUrl.TrimEnd('/')
$locatorJson = [ordered]@{ BootstrapUrl = "$serverBase/client-bootstrap.json" } | ConvertTo-Json
$encoding = New-Object Text.UTF8Encoding($false)
foreach ($componentOutput in @($desktopOutput, $addinOutput)) {
    [IO.File]::WriteAllText((Join-Path $componentOutput 'uplm-bootstrap.json'), $locatorJson, $encoding)
    [IO.File]::WriteAllText((Join-Path $componentOutput '.uplm-version'), $Version, $encoding)
}

$desktopArchive = Join-Path $updates "uplm-desktop-$Version.zip"
$addinArchive = Join-Path $updates "uplm-solidworks-addin-$Version.zip"
Compress-Archive -Path (Join-Path $desktopOutput '*') -DestinationPath $desktopArchive -CompressionLevel Optimal -Force
Compress-Archive -Path (Join-Path $addinOutput '*') -DestinationPath $addinArchive -CompressionLevel Optimal -Force
$bootstrap = [ordered]@{
    SchemaVersion = 1
    ConfigurationVersion = $Version
    ApiBaseUrl = "$serverBase/"
    UiBaseUrl = "$serverBase/"
    PollSeconds = 30
    Desktop = [ordered]@{
        Version = $Version
        PackageUrl = "/updates/$([IO.Path]::GetFileName($desktopArchive))"
        Sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $desktopArchive).Hash
    }
    SolidWorksAddin = [ordered]@{
        Version = $Version
        PackageUrl = "/updates/$([IO.Path]::GetFileName($addinArchive))"
        Sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $addinArchive).Hash
    }
}
[IO.File]::WriteAllText((Join-Path $serverPublish 'client-bootstrap.json'), ($bootstrap | ConvertTo-Json -Depth 5), $encoding)

Copy-Item -LiteralPath $webViewInstaller -Destination $prerequisites
Copy-Item -LiteralPath $net48Installer -Destination $prerequisites
$utf8Bom = New-Object Text.UTF8Encoding($true)
foreach ($scriptName in @('Install-ClientTestPackage.ps1', 'Publish-ClientUpdatesOnServer.ps1')) {
    $scriptSource = Join-Path $PSScriptRoot $scriptName
    $scriptTarget = Join-Path $output $scriptName
    [IO.File]::WriteAllText($scriptTarget, (Get-Content -LiteralPath $scriptSource -Raw -Encoding UTF8), $utf8Bom)
}
$manifest = [ordered]@{
    version = $Version
    createdAt = [DateTimeOffset]::Now.ToString('O')
    serverBaseUrl = $serverBase
    desktopExeSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $desktopOutput 'Upton.Pdm.Desktop.exe')).Hash
    addinDllSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $addinOutput 'Upton.Pdm.SolidWorks.Addin.dll')).Hash
    webView2Sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $prerequisites 'MicrosoftEdgeWebView2RuntimeInstallerX64.exe')).Hash
    netFramework48Sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $prerequisites 'NDP48-x86-x64-AllOS-ENU.exe')).Hash
    desktopUpdateSha256 = $bootstrap.Desktop.Sha256
    solidWorksAddinUpdateSha256 = $bootstrap.SolidWorksAddin.Sha256
}
[IO.File]::WriteAllText((Join-Path $output 'manifest.json'), ($manifest | ConvertTo-Json), $encoding)

$zip = "$output.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $output '*') -DestinationPath $zip -CompressionLevel Optimal
Get-FileHash -Algorithm SHA256 -LiteralPath $zip | Format-List
