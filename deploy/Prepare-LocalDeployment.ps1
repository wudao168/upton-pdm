[CmdletBinding()]
param(
    [string]$MySqlVersion = '8.4.11',
    [string]$LanBaseUrl = 'http://192.168.2.8:5173',
    [string]$ReleaseVersion = '',
    [switch]$ServerOnly
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$projectRoot = Split-Path -Parent $PSScriptRoot
$runtimeRoot = Join-Path $projectRoot '.runtime'
$localRoot = Join-Path $projectRoot '.local'
$downloadRoot = Join-Path $runtimeRoot 'downloads'
$mysqlArchiveName = "mysql-$MySqlVersion-winx64.zip"
$mysqlArchive = Join-Path $downloadRoot $mysqlArchiveName
$mysqlHome = Join-Path $runtimeRoot "mysql-$MySqlVersion-winx64"
$mysqlUrl = "https://cdn.mysql.com/Downloads/MySQL-8.4/$mysqlArchiveName"
$expectedArchiveLength = 281191914
$dotnetPath = Join-Path $projectRoot '.dotnet\dotnet.exe'
$secretRoot = Join-Path $localRoot 'secrets'
$secretPath = Join-Path $secretRoot 'pdm-secrets.json'
$rootClientPath = Join-Path $secretRoot 'mysql-root-client.ini'
$myIniPath = Join-Path $localRoot 'mysql\my.ini'
$isUpgrade = $null -ne (Get-Service -Name 'UptonPdmApi' -ErrorAction SilentlyContinue)
if ($isUpgrade) {
    $apiOutput = Join-Path $localRoot 'api-next'
    if (-not $ServerOnly) {
        $clientOutput = Join-Path $localRoot 'staged-client'
        $addinOutput = Join-Path $localRoot 'staged-solidworks-addin'
        $previewWorkerOutput = Join-Path $localRoot 'staged-preview-worker'
    }
}
else {
    $apiOutput = Join-Path $localRoot 'api'
    if (-not $ServerOnly) {
        $clientOutput = Join-Path $localRoot 'client'
        $addinOutput = Join-Path $localRoot 'solidworks-addin'
        $previewWorkerOutput = Join-Path $localRoot 'preview-worker'
    }
}

function New-RandomText([int]$byteCount) {
    $bytes = New-Object byte[] $byteCount
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $generator.GetBytes($bytes)
    }
    finally {
        $generator.Dispose()
    }

    return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function Write-Utf8File([string]$path, [string[]]$lines) {
    $encoding = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllLines($path, $lines, $encoding)
}

function Protect-SecretFile([string]$path) {
    $account = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    & icacls.exe $path /inheritance:r /grant:r "${account}:(F)" 'SYSTEM:(F)' 'Administrators:(F)' | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to protect secret file: $path"
    }
}

foreach ($directory in @(
    $runtimeRoot,
    $downloadRoot,
    $localRoot,
    $secretRoot,
    (Join-Path $localRoot 'mysql\data'),
    (Join-Path $localRoot 'mysql\logs'),
    (Join-Path $localRoot 'mysql\tmp'),
    (Join-Path $localRoot 'api'),
    (Join-Path $localRoot 'program-templates'),
    (Join-Path $localRoot 'vault\PRJ-2026-018'),
    (Join-Path $localRoot 'release\PRJ-2026-018'),
    (Join-Path $localRoot 'uploads')
)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

if (-not $ServerOnly) {
    foreach ($directory in @(
        (Join-Path $localRoot 'client'),
        (Join-Path $localRoot 'solidworks-addin'),
        (Join-Path $localRoot 'preview-worker')
    )) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
}

if ($isUpgrade) {
    $upgradeOutputs = @($apiOutput)
    if (-not $ServerOnly) { $upgradeOutputs += @($clientOutput, $addinOutput, $previewWorkerOutput) }
    foreach ($directory in $upgradeOutputs) {
        $resolvedLocal = [IO.Path]::GetFullPath($localRoot).TrimEnd('\') + '\'
        $resolvedTarget = [IO.Path]::GetFullPath($directory)
        if (-not $resolvedTarget.StartsWith($resolvedLocal, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Upgrade staging path escaped .local: $resolvedTarget"
        }
        if (Test-Path -LiteralPath $resolvedTarget) { Remove-Item -LiteralPath $resolvedTarget -Recurse -Force }
        New-Item -ItemType Directory -Path $resolvedTarget -Force | Out-Null
    }
}

if ($isUpgrade -and -not (Test-Path -LiteralPath $secretPath)) {
    throw 'Existing PLM service is missing its protected deployment secret file.'
}

if (-not $isUpgrade -and -not (Test-Path -LiteralPath $secretPath)) {
    $secrets = [ordered]@{
        mysqlRootPassword = 'MysqlRoot!' + (New-RandomText 24)
        databasePassword = 'PdmDb!' + (New-RandomText 24)
        bootstrapAdminUsername = 'admin'
        bootstrapAdminPassword = 'PdmAdmin!' + (New-RandomText 24)
        jwtSigningKey = New-RandomText 48
        createdAt = [DateTimeOffset]::Now.ToString('O')
    }
    $secrets | ConvertTo-Json | Set-Content -LiteralPath $secretPath -Encoding UTF8
    Protect-SecretFile $secretPath
}

if (-not $isUpgrade) {
    $secrets = Get-Content -LiteralPath $secretPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Write-Utf8File $rootClientPath @(
        '[client]',
        'user=root',
        "password=$($secrets.mysqlRootPassword)",
        'host=127.0.0.1',
        'port=3308',
        'protocol=TCP'
    )
    Protect-SecretFile $rootClientPath
}

if (-not (Test-Path -LiteralPath (Join-Path $mysqlHome 'bin\mysqld.exe'))) {
    $needsDownload = -not (Test-Path -LiteralPath $mysqlArchive)
    if (-not $needsDownload -and (Get-Item -LiteralPath $mysqlArchive).Length -ne $expectedArchiveLength) {
        Remove-Item -LiteralPath $mysqlArchive -Force
        $needsDownload = $true
    }

    if ($needsDownload) {
        $temporaryArchive = "$mysqlArchive.partial"
        if (Test-Path -LiteralPath $temporaryArchive) {
            Remove-Item -LiteralPath $temporaryArchive -Force
        }
        Write-Host "Downloading $mysqlArchiveName from the official MySQL CDN..."
        Invoke-WebRequest -UseBasicParsing -Uri $mysqlUrl -OutFile $temporaryArchive
        if ((Get-Item -LiteralPath $temporaryArchive).Length -ne $expectedArchiveLength) {
            throw 'Downloaded MySQL archive length does not match the official response.'
        }
        Move-Item -LiteralPath $temporaryArchive -Destination $mysqlArchive
    }

    Write-Host "Extracting $mysqlArchiveName..."
    Expand-Archive -LiteralPath $mysqlArchive -DestinationPath $runtimeRoot -Force
}

if (-not (Test-Path -LiteralPath (Join-Path $mysqlHome 'bin\mysqld.exe'))) {
    throw "MySQL server executable was not found after extraction: $mysqlHome"
}

$mysqlHomeOption = $mysqlHome.Replace('\', '/')
$mysqlDataOption = (Join-Path $localRoot 'mysql\data').Replace('\', '/')
$mysqlLogOption = (Join-Path $localRoot 'mysql\logs\mysql-error.log').Replace('\', '/')
$mysqlTmpOption = (Join-Path $localRoot 'mysql\tmp').Replace('\', '/')
Write-Utf8File $myIniPath @(
    '[mysqld]',
    "basedir=$mysqlHomeOption",
    "datadir=$mysqlDataOption",
    "tmpdir=$mysqlTmpOption",
    'port=3308',
    'bind-address=127.0.0.1',
    'mysqlx=0',
    'character-set-server=utf8mb4',
    'collation-server=utf8mb4_0900_ai_ci',
    'default-time-zone=+00:00',
    'local-infile=0',
    'skip-log-bin',
    'max-connections=100',
    "log-error=$mysqlLogOption",
    'pid-file=upton-pdm-mysql.pid',
    '',
    '[client]',
    'port=3308',
    'host=127.0.0.1',
    'default-character-set=utf8mb4'
)

$mysqlDataRoot = Join-Path $localRoot 'mysql\data'
if (-not (Test-Path -LiteralPath (Join-Path $mysqlDataRoot 'auto.cnf'))) {
    Write-Host 'Initializing the isolated MySQL data directory...'
    $mysqldPath = Join-Path $mysqlHome 'bin\mysqld.exe'
    & $mysqldPath "--defaults-file=$myIniPath" --initialize-insecure --console
    if ($LASTEXITCODE -ne 0) {
        throw "MySQL initialization failed with exit code $LASTEXITCODE"
    }
}

if (-not (Test-Path -LiteralPath $dotnetPath)) {
    throw "Project-local .NET SDK was not found: $dotnetPath"
}

Push-Location $projectRoot
try {
    pnpm.cmd install --frozen-lockfile
    if ($LASTEXITCODE -ne 0) {
        throw 'Frontend dependency restore failed.'
    }
    pnpm.cmd ui:build
    if ($LASTEXITCODE -ne 0) {
        throw 'Frontend production build failed.'
    }
    # Local deployments use the locked dependency graph and may run on an isolated factory network.
    # NuGet vulnerability auditing is a separate online gate; do not let its network call block deployment.
    $restoreTarget = if ($ServerOnly) { 'src\Pdm.Api\Pdm.Api.csproj' } else { 'Pdm.slnx' }
    & $dotnetPath restore $restoreTarget --nologo -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) {
        throw 'Solution restore failed.'
    }
    if (-not $ServerOnly) {
        & $dotnetPath build Pdm.slnx --configuration Release --no-restore --nologo
        if ($LASTEXITCODE -ne 0) {
            throw 'Release build failed.'
        }

        & $dotnetPath test Pdm.slnx --configuration Release --no-build --no-restore --nologo
        if ($LASTEXITCODE -ne 0) {
            throw 'Release tests failed.'
        }
    }

    & $dotnetPath publish 'src\Pdm.Api\Pdm.Api.csproj' --configuration Release --no-restore --output $apiOutput --nologo
    if ($LASTEXITCODE -ne 0) {
        throw 'API publish failed.'
    }
}
finally {
    Pop-Location
}

if ($ServerOnly) {
    $webRoot = Join-Path $apiOutput 'wwwroot'
    New-Item -ItemType Directory -Path $webRoot -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src\pdm-ui\dist') -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $webRoot $_.Name) -Recurse -Force
    }
    $receipt = [ordered]@{
        deploymentMode = 'server-only'
        preparedAt = [DateTimeOffset]::Now.ToString('O')
        mysqlVersion = $MySqlVersion
        mysqlArchive = $mysqlArchive
        mysqlArchiveSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $mysqlArchive).Hash
        mysqlHome = $mysqlHome
        localRoot = $localRoot
        apiPath = Join-Path $localRoot 'api\Pdm.Api.dll'
        lanBaseUrl = $LanBaseUrl.TrimEnd('/')
    }
    $receipt | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $localRoot 'deployment-receipt.json') -Encoding UTF8
    Write-Host 'UPLM server-only deployment files are prepared.'
    Write-Host "Receipt: $(Join-Path $localRoot 'deployment-receipt.json')"
    return
}

$clientBuildOutput = Join-Path $projectRoot 'src\Pdm.Desktop\bin\Release\net48'
Get-ChildItem -LiteralPath $clientBuildOutput |
    Where-Object { $_.Name -ne 'Upton.Pdm.Desktop.exe.WebView2' } |
    Copy-Item -Destination $clientOutput -Recurse -Force
Copy-Item -Path (Join-Path $projectRoot 'src\Pdm.SolidWorks.Addin\bin\Release\net48\*') -Destination $addinOutput -Recurse -Force
Copy-Item -Path (Join-Path $projectRoot 'src\Pdm.SolidWorks.PreviewWorker\bin\Release\net48\*') -Destination $previewWorkerOutput -Recurse -Force

if ([string]::IsNullOrWhiteSpace($ReleaseVersion)) {
    $ReleaseVersion = [DateTimeOffset]::Now.ToString('yyyy.MM.dd.HHmm')
}
$lanBase = $LanBaseUrl.TrimEnd('/')
$bootstrapUrl = "$lanBase/client-bootstrap.json"
$locator = [ordered]@{ BootstrapUrl = $bootstrapUrl }
foreach ($output in @($clientOutput, $addinOutput)) {
    Write-Utf8File (Join-Path $output 'uplm-bootstrap.json') @(($locator | ConvertTo-Json))
    Write-Utf8File (Join-Path $output '.uplm-version') @($ReleaseVersion)
}

$webRoot = Join-Path $apiOutput 'wwwroot'
$updatesRoot = Join-Path $webRoot 'updates'
New-Item -ItemType Directory -Path $updatesRoot -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src\pdm-ui\dist') -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $webRoot $_.Name) -Recurse -Force
}
$desktopArchive = Join-Path $updatesRoot "uplm-desktop-$ReleaseVersion.zip"
$addinArchive = Join-Path $updatesRoot "uplm-solidworks-addin-$ReleaseVersion.zip"
Compress-Archive -Path (Join-Path $clientOutput '*') -DestinationPath $desktopArchive -CompressionLevel Optimal -Force
Compress-Archive -Path (Join-Path $addinOutput '*') -DestinationPath $addinArchive -CompressionLevel Optimal -Force
$bootstrap = [ordered]@{
    SchemaVersion = 1
    ConfigurationVersion = $ReleaseVersion
    ApiBaseUrl = "$lanBase/"
    UiBaseUrl = "$lanBase/"
    PollSeconds = 30
    Desktop = [ordered]@{
        Version = $ReleaseVersion
        PackageUrl = "/updates/$([IO.Path]::GetFileName($desktopArchive))"
        Sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $desktopArchive).Hash
    }
    SolidWorksAddin = [ordered]@{
        Version = $ReleaseVersion
        PackageUrl = "/updates/$([IO.Path]::GetFileName($addinArchive))"
        Sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $addinArchive).Hash
    }
}
Write-Utf8File (Join-Path $webRoot 'client-bootstrap.json') @(($bootstrap | ConvertTo-Json -Depth 5))

$desktopDirectory = [Environment]::GetFolderPath('Desktop')
$shortcutPath = Join-Path $desktopDirectory 'UPLM.lnk'
$previousShortcutPath = Join-Path $desktopDirectory 'UPTON PLM.lnk'
$legacyShortcutPath = Join-Path $desktopDirectory 'UPTON PDM.lnk'
$clientPath = Join-Path $localRoot 'client\Upton.Pdm.Desktop.exe'
$clientIconPath = Join-Path $localRoot 'client\UPTON-PLM.ico'
if (-not (Test-Path -LiteralPath $clientIconPath)) {
    throw "UPLM desktop icon was not found: $clientIconPath"
}
$clientIconHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $clientIconPath).Hash.Substring(0, 12)
$shortcutIconPath = Join-Path $localRoot "client\UPTON-PLM-$clientIconHash.ico"
Copy-Item -LiteralPath $clientIconPath -Destination $shortcutIconPath -Force
if (-not $isUpgrade) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $clientPath
    $shortcut.WorkingDirectory = Join-Path $localRoot 'client'
    $shortcut.IconLocation = "$shortcutIconPath,0"
    $shortcut.Description = 'UPLM engineering client'
    $shortcut.Save()
    if (Test-Path -LiteralPath $previousShortcutPath) { Remove-Item -LiteralPath $previousShortcutPath -Force }
    if (Test-Path -LiteralPath $legacyShortcutPath) { Remove-Item -LiteralPath $legacyShortcutPath -Force }
}

$receipt = [ordered]@{
    preparedAt = [DateTimeOffset]::Now.ToString('O')
    mysqlVersion = $MySqlVersion
    mysqlArchive = $mysqlArchive
    mysqlArchiveSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $mysqlArchive).Hash
    mysqlHome = $mysqlHome
    localRoot = $localRoot
    apiPath = Join-Path $localRoot 'api\Pdm.Api.dll'
    clientPath = $clientPath
    clientIconPath = $clientIconPath
    shortcutIconPath = $shortcutIconPath
    addinPath = Join-Path $localRoot 'solidworks-addin\Upton.Pdm.SolidWorks.Addin.dll'
    previewWorkerPath = Join-Path $localRoot 'preview-worker\Upton.Pdm.SolidWorks.PreviewWorker.exe'
    lanBaseUrl = $lanBase
    releaseVersion = $ReleaseVersion
    bootstrapPath = Join-Path $webRoot 'client-bootstrap.json'
    desktopPackageSha256 = $bootstrap.Desktop.Sha256
    solidWorksAddinPackageSha256 = $bootstrap.SolidWorksAddin.Sha256
    shortcutPath = $shortcutPath
}
$receipt | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $localRoot 'deployment-receipt.json') -Encoding UTF8

Write-Host 'Local deployment files are prepared.'
Write-Host "Receipt: $(Join-Path $localRoot 'deployment-receipt.json')"
