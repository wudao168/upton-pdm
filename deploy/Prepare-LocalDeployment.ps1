[CmdletBinding()]
param(
    [string]$MySqlVersion = '8.4.11'
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
    (Join-Path $localRoot 'client'),
    (Join-Path $localRoot 'solidworks-addin'),
    (Join-Path $localRoot 'vault\PRJ-2026-018'),
    (Join-Path $localRoot 'release\PRJ-2026-018'),
    (Join-Path $localRoot 'uploads')
)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

if (-not (Test-Path -LiteralPath $secretPath)) {
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
    pnpm.cmd ui:build
    & $dotnetPath restore Pdm.slnx --nologo
    if ($LASTEXITCODE -ne 0) {
        throw 'Solution restore failed.'
    }
    & $dotnetPath build Pdm.slnx --configuration Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) {
        throw 'Release build failed.'
    }

    & $dotnetPath test Pdm.slnx --configuration Release --no-build --no-restore --nologo
    if ($LASTEXITCODE -ne 0) {
        throw 'Release tests failed.'
    }

    & $dotnetPath publish 'src\Pdm.Api\Pdm.Api.csproj' --configuration Release --no-restore --output (Join-Path $localRoot 'api') --nologo
    if ($LASTEXITCODE -ne 0) {
        throw 'API publish failed.'
    }
}
finally {
    Pop-Location
}

Copy-Item -Path (Join-Path $projectRoot 'src\Pdm.Desktop\bin\Release\net48\*') -Destination (Join-Path $localRoot 'client') -Recurse -Force
Copy-Item -Path (Join-Path $projectRoot 'src\Pdm.SolidWorks.Addin\bin\Release\net48\*') -Destination (Join-Path $localRoot 'solidworks-addin') -Recurse -Force

$desktopDirectory = [Environment]::GetFolderPath('Desktop')
$shortcutPath = Join-Path $desktopDirectory 'UPTON PDM.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $localRoot 'client\Upton.Pdm.Desktop.exe'
$shortcut.WorkingDirectory = Join-Path $localRoot 'client'
$shortcut.Description = 'UPTON PDM engineering client'
$shortcut.Save()

$receipt = [ordered]@{
    preparedAt = [DateTimeOffset]::Now.ToString('O')
    mysqlVersion = $MySqlVersion
    mysqlArchive = $mysqlArchive
    mysqlArchiveSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $mysqlArchive).Hash
    mysqlHome = $mysqlHome
    localRoot = $localRoot
    apiPath = Join-Path $localRoot 'api\Pdm.Api.dll'
    clientPath = Join-Path $localRoot 'client\Upton.Pdm.Desktop.exe'
    addinPath = Join-Path $localRoot 'solidworks-addin\Upton.Pdm.SolidWorks.Addin.dll'
    shortcutPath = $shortcutPath
}
$receipt | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $localRoot 'deployment-receipt.json') -Encoding UTF8

Write-Host 'Local deployment files are prepared.'
Write-Host "Receipt: $(Join-Path $localRoot 'deployment-receipt.json')"
