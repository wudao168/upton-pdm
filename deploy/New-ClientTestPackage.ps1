[CmdletBinding()]
param(
    [string]$ServerBaseUrl = 'http://10.7.7.62:5173',
    [string]$Version = ([DateTimeOffset]::Now.ToString('yyyy.MM.dd.HHmm')),
    [string]$DesktopVersion = '',
    [string]$SolidWorksAddinVersion = '',
    [string]$ReleaseNote = '',
    [string]$ClientUiSource = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'SystemReleaseHistory.ps1')
$ReleaseNote = Get-UplmReleaseNote -Version $Version -ReleaseNote $ReleaseNote -Fallback '本次发布未填写版本说明。'
if ([string]::IsNullOrWhiteSpace($DesktopVersion) -or [string]::IsNullOrWhiteSpace($SolidWorksAddinVersion)) {
    throw '必须分别指定 DesktopVersion 和 SolidWorksAddinVersion；未变化的组件请填写服务器当前组件版本。'
}
$DesktopVersion = $DesktopVersion.Trim()
$SolidWorksAddinVersion = $SolidWorksAddinVersion.Trim()
$releasedAt = Get-UplmReleasedAt -Version $Version
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
$clientUiSourceFull = ''
if (-not [string]::IsNullOrWhiteSpace($ClientUiSource)) {
    $clientUiSourceFull = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $ClientUiSource).Path)
    foreach ($requiredUiFile in @('index.html', 'review-overlay.html')) {
        if (-not (Test-Path -LiteralPath (Join-Path $clientUiSourceFull $requiredUiFile) -PathType Leaf)) {
            throw "指定的客户端 UI 快照不完整：$requiredUiFile"
        }
    }
}
foreach ($required in @($dotnet, $webViewInstaller, $net48Installer)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "缺少客户端构建依赖：$required" }
}
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Path $desktopOutput,$addinOutput,$updates,$prerequisites -Force | Out-Null

Push-Location $root
try {
    if ([string]::IsNullOrWhiteSpace($clientUiSourceFull)) {
        pnpm.cmd install --frozen-lockfile
        if ($LASTEXITCODE -ne 0) { throw '前端依赖还原失败。' }
        pnpm.cmd ui:build
        if ($LASTEXITCODE -ne 0) { throw '前端构建失败。' }
    }
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
if (-not [string]::IsNullOrWhiteSpace($clientUiSourceFull)) {
    $desktopUiOutput = Join-Path $desktopOutput 'ui'
    if (Test-Path -LiteralPath $desktopUiOutput) { Remove-Item -LiteralPath $desktopUiOutput -Recurse -Force }
    New-Item -ItemType Directory -Path $desktopUiOutput -Force | Out-Null
    Get-ChildItem -LiteralPath $clientUiSourceFull -Recurse -File | Where-Object { $_.Extension -ne '.map' } | ForEach-Object {
        $relativePath = $_.FullName.Substring($clientUiSourceFull.Length + 1)
        $destination = Join-Path $desktopUiOutput $relativePath
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
    }
}
Get-ChildItem -LiteralPath $addinBuild | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $addinOutput $_.Name) -Recurse -Force
}

$serverBase = $ServerBaseUrl.TrimEnd('/')
$existingBootstrap = $null
try { $existingBootstrap = Get-UplmJsonUtf8 -Uri "$serverBase/client-bootstrap.json" }
catch { Write-Warning "Server release history could not be read; the retained baseline will be used: $($_.Exception.Message)" }
$releaseHistory = Get-UplmReleaseHistory `
    -CurrentVersion $Version `
    -CurrentReleaseNote $ReleaseNote `
    -CurrentDesktopVersion $DesktopVersion `
    -CurrentSolidWorksAddinVersion $SolidWorksAddinVersion `
    -SourceBootstraps @($existingBootstrap) `
    -BaselinePath (Join-Path $PSScriptRoot 'system-release-history.json')
$locatorJson = [ordered]@{ BootstrapUrl = "$serverBase/client-bootstrap.json" } | ConvertTo-Json
$encoding = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $desktopOutput 'uplm-bootstrap.json'), $locatorJson, $encoding)
[IO.File]::WriteAllText((Join-Path $desktopOutput '.uplm-version'), $DesktopVersion, $encoding)
[IO.File]::WriteAllText((Join-Path $addinOutput 'uplm-bootstrap.json'), $locatorJson, $encoding)
[IO.File]::WriteAllText((Join-Path $addinOutput '.uplm-version'), $SolidWorksAddinVersion, $encoding)

$desktopArchive = Join-Path $updates "uplm-desktop-$DesktopVersion.zip"
$addinArchive = Join-Path $updates "uplm-solidworks-addin-$SolidWorksAddinVersion.zip"
Compress-Archive -Path (Join-Path $desktopOutput '*') -DestinationPath $desktopArchive -CompressionLevel Optimal -Force
Compress-Archive -Path (Join-Path $addinOutput '*') -DestinationPath $addinArchive -CompressionLevel Optimal -Force
$bootstrap = [ordered]@{
    SchemaVersion = 2
    ConfigurationVersion = $Version
    ReleasedAt = $releasedAt
    ReleaseNote = $ReleaseNote
    ReleaseHistory = $releaseHistory
    ApiBaseUrl = "$serverBase/"
    UiBaseUrl = "$serverBase/"
    PollSeconds = 30
    Desktop = [ordered]@{
        Version = $DesktopVersion
        PackageUrl = "/updates/$([IO.Path]::GetFileName($desktopArchive))"
        Sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $desktopArchive).Hash
    }
    SolidWorksAddin = [ordered]@{
        Version = $SolidWorksAddinVersion
        PackageUrl = "/updates/$([IO.Path]::GetFileName($addinArchive))"
        Sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $addinArchive).Hash
    }
}
if ($null -ne $existingBootstrap -and
    [string]::Equals([string]$existingBootstrap.SolidWorksAddin.Version, $SolidWorksAddinVersion, [StringComparison]::OrdinalIgnoreCase)) {
    $bootstrap['SolidWorksAddin'] = $existingBootstrap.SolidWorksAddin
}
[IO.File]::WriteAllText((Join-Path $serverPublish 'client-bootstrap.json'), ($bootstrap | ConvertTo-Json -Depth 8), $encoding)

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
    desktopVersion = $DesktopVersion
    solidWorksAddinVersion = $SolidWorksAddinVersion
    releaseNote = $ReleaseNote
    releaseHistoryCount = $releaseHistory.Count
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
