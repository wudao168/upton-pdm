[CmdletBinding()]
param([string]$Version = ([DateTimeOffset]::Now.ToString('yyyy.MM.dd.HHmm')))

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $root '.dotnet\dotnet.exe'
$mysql = Join-Path $root 'server-deployment-packages\windows-server-2016-required-2026-09-12\mysql-8.4.11-winx64.zip'
$output = Join-Path $root ".artifacts\server-only-test-$Version"
$payload = Join-Path $output 'payload'
if (-not (Test-Path -LiteralPath $dotnet)) { throw 'Project-local .NET SDK is missing.' }
if (-not (Test-Path -LiteralPath $mysql)) { throw 'Offline MySQL package is missing.' }
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Path $payload -Force | Out-Null

Push-Location $root
try {
    pnpm.cmd install --frozen-lockfile
    if ($LASTEXITCODE -ne 0) { throw 'Frontend dependency restore failed.' }
    pnpm.cmd ui:build
    if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed.' }
    & $dotnet restore 'src\Pdm.Api\Pdm.Api.csproj' --nologo -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'API restore failed.' }
    & $dotnet publish 'src\Pdm.Api\Pdm.Api.csproj' --configuration Release --no-restore --output (Join-Path $payload 'app') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'API publish failed.' }
    $distRoot = Join-Path $root 'src\pdm-ui\dist'
    $wwwroot = Join-Path $payload 'app\wwwroot'
    Get-ChildItem -LiteralPath $distRoot -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $wwwroot $_.Name) -Recurse -Force
    }
}
finally { Pop-Location }

Copy-Item -LiteralPath $mysql -Destination (Join-Path $payload 'mysql-8.4.11-winx64.zip')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-ServerOnlyTestPackage.ps1') -Destination $output
$manifest = [ordered]@{ version = $Version; createdAt = [DateTimeOffset]::Now.ToString('O'); apiSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $payload 'app\Pdm.Api.dll')).Hash; mysqlSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $payload 'mysql-8.4.11-winx64.zip')).Hash }
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'manifest.json') -Encoding UTF8
$zip = "$output.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $output '*') -DestinationPath $zip -CompressionLevel Optimal
Get-FileHash -Algorithm SHA256 -LiteralPath $zip | Format-List
