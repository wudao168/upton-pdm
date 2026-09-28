[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$LanBaseUrl,
    [string]$InstallRoot = 'C:\UPLM\pdm'
)

$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '请使用管理员 PowerShell 执行服务器首装。' }
try { $lanUri = [Uri]$LanBaseUrl.TrimEnd('/') } catch { throw "内网访问地址无效：$LanBaseUrl" }
if (-not $lanUri.IsAbsoluteUri -or $lanUri.Scheme -notin @('http', 'https')) { throw 'LanBaseUrl 必须是完整 HTTP/HTTPS 地址，例如 http://10.7.7.62:5173。' }
if ($lanUri.Port -ne 5173) { throw 'LanBaseUrl 必须使用 UPLM 网页端口 5173。' }
$LanBaseUrl = $lanUri.AbsoluteUri.TrimEnd('/')

$serverOs = Get-CimInstance Win32_OperatingSystem
if ($serverOs.Caption -notmatch 'Windows Server' -or [version]$serverOs.Version -lt [version]'10.0.17763') { throw '此首装包仅支持 Windows Server 2019 x64 或更高版本。' }
$serverRoot = Split-Path -Parent $PSCommandPath
$packageRoot = Split-Path -Parent $serverRoot
$manifestPath = Join-Path $packageRoot 'manifest.json'
$appSource = Join-Path $serverRoot 'app'
$mysqlZip = Join-Path $serverRoot 'mysql-8.4.11-winx64.zip'
$vcRedist = Join-Path $serverRoot 'vc_redist.x64.exe'
$clientRoot = Join-Path $packageRoot 'Client'
foreach ($required in @($manifestPath, (Join-Path $appSource 'Pdm.Api.exe'), (Join-Path $appSource 'Pdm.Api.dll'), $mysqlZip, $vcRedist, (Join-Path $clientRoot 'Install-UPLMClient.ps1'), (Join-Path $clientRoot 'Uninstall-UPLMClient.ps1'))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "部署包不完整：$required" }
}
if (Get-Service -Name 'UptonPdmApi' -ErrorAction SilentlyContinue) { throw '已检测到 UptonPdmApi 服务；新服务器首装包不会覆盖已有部署。' }
if (Get-Service -Name 'UptonPdmMySQL' -ErrorAction SilentlyContinue) { throw '已检测到 UptonPdmMySQL 服务；请先完成旧部署迁移或清理。' }
$installRootFull = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
if (Test-Path -LiteralPath (Join-Path $installRootFull 'app\Pdm.Api.exe')) { throw "目标目录已经包含 UPLM 应用：$installRootFull" }

foreach ($port in @(3308, 5080, 5173)) {
    if (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue | Select-Object -First 1) { throw "端口 $port 已被占用，首装已停止。" }
}

$vcProcess = Start-Process -FilePath $vcRedist -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
if ($vcProcess.ExitCode -notin @(0, 1638, 3010)) { throw "VC++ x64 运行库安装失败，退出码：$($vcProcess.ExitCode)" }
$rebootRequired = $vcProcess.ExitCode -eq 3010

$runtimeRoot = Join-Path $installRootFull 'runtime'
$mysqlHome = Join-Path $runtimeRoot 'mysql-8.4.11-winx64'
$localRoot = Join-Path $installRootFull 'local'
$secretsRoot = Join-Path $localRoot 'secrets'
$appTarget = Join-Path $installRootFull 'app'
$dataProtectionRoot = Join-Path $installRootFull 'secrets\data-protection-keys'
foreach ($directory in @($installRootFull, $runtimeRoot, $localRoot, $secretsRoot, $dataProtectionRoot, $appTarget,
    (Join-Path $localRoot 'mysql\data'), (Join-Path $localRoot 'mysql\logs'), (Join-Path $localRoot 'mysql\tmp'),
    (Join-Path $localRoot 'vault'), (Join-Path $localRoot 'release'), (Join-Path $localRoot 'uploads'), (Join-Path $localRoot 'program-templates'))) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}
if (-not (Test-Path -LiteralPath (Join-Path $mysqlHome 'bin\mysqld.exe') -PathType Leaf)) { Expand-Archive -LiteralPath $mysqlZip -DestinationPath $runtimeRoot -Force }
$mysqld = Join-Path $mysqlHome 'bin\mysqld.exe'
$mysql = Join-Path $mysqlHome 'bin\mysql.exe'
if (-not (Test-Path -LiteralPath $mysqld -PathType Leaf)) { throw 'MySQL 解压后缺少 mysqld.exe。' }

function New-RandomText([int]$byteCount) {
    $bytes = New-Object byte[] $byteCount
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}
$secrets = [ordered]@{
    mysqlRootPassword = 'MysqlRoot!' + (New-RandomText 24); databasePassword = 'PdmDb!' + (New-RandomText 24)
    bootstrapAdminUsername = 'admin'; bootstrapAdminPassword = 'PdmAdmin!' + (New-RandomText 24)
    jwtSigningKey = New-RandomText 48; createdAt = [DateTimeOffset]::Now.ToString('O')
}
$secretPath = Join-Path $secretsRoot 'pdm-secrets.json'
$secrets | ConvertTo-Json | Set-Content -LiteralPath $secretPath -Encoding UTF8
& icacls.exe $secretPath /inheritance:r /grant:r 'Administrators:(F)' 'SYSTEM:(F)' | Out-Null
if ($LASTEXITCODE -ne 0) { throw '无法保护服务器密钥文件。' }
$rootClient = Join-Path $secretsRoot 'mysql-root-client.ini'
[IO.File]::WriteAllLines($rootClient, @('[client]', 'user=root', "password=$($secrets.mysqlRootPassword)", 'host=127.0.0.1', 'port=3308', 'protocol=TCP'), [Text.UTF8Encoding]::new($false))
& icacls.exe $rootClient /inheritance:r /grant:r 'Administrators:(F)' 'SYSTEM:(F)' | Out-Null
if ($LASTEXITCODE -ne 0) { throw '无法保护 MySQL 管理凭据。' }

$myIni = Join-Path $localRoot 'mysql\my.ini'
[IO.File]::WriteAllLines($myIni, @('[mysqld]', "basedir=$($mysqlHome.Replace('\','/'))", "datadir=$((Join-Path $localRoot 'mysql\data').Replace('\','/'))", "tmpdir=$((Join-Path $localRoot 'mysql\tmp').Replace('\','/'))", 'port=3308', 'bind-address=127.0.0.1', 'mysqlx=0', 'character-set-server=utf8mb4', 'collation-server=utf8mb4_0900_ai_ci', 'skip-log-bin', "log-error=$((Join-Path $localRoot 'mysql\logs\mysql-error.log').Replace('\','/'))", 'pid-file=upton-pdm-mysql.pid'), [Text.UTF8Encoding]::new($false))
if (-not (Test-Path -LiteralPath (Join-Path $localRoot 'mysql\data\auto.cnf') -PathType Leaf)) {
    & $mysqld "--defaults-file=$myIni" --initialize-insecure --console
    if ($LASTEXITCODE -ne 0) { throw 'MySQL 初始化失败。' }
}
& $mysqld --install UptonPdmMySQL "--defaults-file=$myIni"
if ($LASTEXITCODE -ne 0) { throw 'MySQL 服务注册失败。' }
& sc.exe config UptonPdmMySQL start= auto | Out-Null
& sc.exe failure UptonPdmMySQL reset= 86400 actions= restart/5000/restart/15000/restart/30000 | Out-Null
Start-Service UptonPdmMySQL
for ($index = 0; $index -lt 60; $index++) { if (Test-NetConnection 127.0.0.1 -Port 3308 -InformationLevel Quiet) { break }; Start-Sleep -Seconds 1 }
if (-not (Test-NetConnection 127.0.0.1 -Port 3308 -InformationLevel Quiet)) { throw 'MySQL 未能在端口 3308 启动。' }
@("ALTER USER 'root'@'localhost' IDENTIFIED BY '$($secrets.mysqlRootPassword)';", 'CREATE DATABASE pdm CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;', "CREATE USER 'pdm_app'@'127.0.0.1' IDENTIFIED BY '$($secrets.databasePassword)';", "GRANT ALL PRIVILEGES ON pdm.* TO 'pdm_app'@'127.0.0.1';", 'FLUSH PRIVILEGES;') | & $mysql --protocol=TCP --host=127.0.0.1 --port=3308 --user=root
if ($LASTEXITCODE -ne 0) { throw 'MySQL 应用账号初始化失败。' }

Copy-Item -Path (Join-Path $appSource '*') -Destination $appTarget -Recurse -Force
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$desktopPayload = Join-Path $clientRoot 'payload\desktop'
$addinPayload = Join-Path $clientRoot 'payload\solidworks-addin'
$locator = [ordered]@{ BootstrapUrl = "$LanBaseUrl/client-bootstrap.json" } | ConvertTo-Json
$utf8 = New-Object Text.UTF8Encoding($false)
foreach ($payload in @($desktopPayload, $addinPayload)) { [IO.File]::WriteAllText((Join-Path $payload 'uplm-bootstrap.json'), $locator, $utf8) }
$updatesRoot = Join-Path $appTarget 'wwwroot\updates'
if (Test-Path -LiteralPath $updatesRoot) { Remove-Item -LiteralPath $updatesRoot -Recurse -Force }
New-Item -ItemType Directory -Path $updatesRoot -Force | Out-Null
$desktopUpdate = Join-Path $updatesRoot "uplm-desktop-$($manifest.desktopVersion).zip"
$addinUpdate = Join-Path $updatesRoot "uplm-solidworks-addin-$($manifest.solidWorksAddinVersion).zip"
Compress-Archive -Path (Join-Path $desktopPayload '*') -DestinationPath $desktopUpdate -CompressionLevel Optimal
Compress-Archive -Path (Join-Path $addinPayload '*') -DestinationPath $addinUpdate -CompressionLevel Optimal
$bootstrapPath = Join-Path $appTarget 'wwwroot\client-bootstrap.json'
$bootstrap = Get-Content -LiteralPath $bootstrapPath -Raw -Encoding UTF8 | ConvertFrom-Json
$bootstrap.ConfigurationVersion = $manifest.version
$bootstrap.ApiBaseUrl = "$LanBaseUrl/"
$bootstrap.UiBaseUrl = "$LanBaseUrl/"
$bootstrap.Desktop.Version = $manifest.desktopVersion
$bootstrap.Desktop.PackageUrl = "/updates/$([IO.Path]::GetFileName($desktopUpdate))"
$bootstrap.Desktop.Sha256 = (Get-FileHash -LiteralPath $desktopUpdate -Algorithm SHA256).Hash
$bootstrap.SolidWorksAddin.Version = $manifest.solidWorksAddinVersion
$bootstrap.SolidWorksAddin.PackageUrl = "/updates/$([IO.Path]::GetFileName($addinUpdate))"
$bootstrap.SolidWorksAddin.Sha256 = (Get-FileHash -LiteralPath $addinUpdate -Algorithm SHA256).Hash
[IO.File]::WriteAllText($bootstrapPath, ($bootstrap | ConvertTo-Json -Depth 8), $utf8)

$apiBin = '"{0}"' -f (Join-Path $appTarget 'Pdm.Api.exe')
New-Service -Name UptonPdmApi -BinaryPathName $apiBin -DisplayName 'UPLM API' -Description 'UPLM API on ports 5080 and 5173' -StartupType Automatic | Out-Null
& sc.exe config UptonPdmApi depend= UptonPdmMySQL | Out-Null
& sc.exe failure UptonPdmApi reset= 86400 actions= restart/5000/restart/15000/restart/30000 | Out-Null
$apiRegistry = 'HKLM:\SYSTEM\CurrentControlSet\Services\UptonPdmApi'
$apiEnvironment = @('ASPNETCORE_ENVIRONMENT=Production', "PDM_DB_PASSWORD=$($secrets.databasePassword)", "PDM_JWT_SIGNING_KEY=$($secrets.jwtSigningKey)", "PDM_BOOTSTRAP_ADMIN_PASSWORD=$($secrets.bootstrapAdminPassword)", "Pdm__Storage__UploadTempRoot=$(Join-Path $localRoot 'uploads')", "Pdm__Storage__ProgramTemplateRoot=$(Join-Path $localRoot 'program-templates')")
New-ItemProperty -LiteralPath $apiRegistry -Name Environment -PropertyType MultiString -Value $apiEnvironment -Force | Out-Null
Start-Service UptonPdmApi
for ($index = 0; $index -lt 90; $index++) { try { $health = Invoke-RestMethod 'http://127.0.0.1:5080/health' -TimeoutSec 3; if ($health.status -eq 'ok' -and $health.database -eq 'MySql') { break } } catch { }; Start-Sleep -Seconds 1 }
if ($null -eq $health -or $health.status -ne 'ok') { throw 'UPLM API 健康检查失败。' }
Set-ItemProperty -LiteralPath $apiRegistry -Name Environment -Value ($apiEnvironment | Where-Object { $_ -notlike 'PDM_BOOTSTRAP_ADMIN_PASSWORD=*' })
Get-NetFirewallRule -DisplayName 'UPLM LAN UI (5173)' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName 'UPLM LAN UI (5173)' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5173 -RemoteAddress LocalSubnet -Profile Domain,Private | Out-Null
$onlineBootstrap = Invoke-RestMethod 'http://127.0.0.1:5173/client-bootstrap.json' -TimeoutSec 5
if ($onlineBootstrap.ConfigurationVersion -ne $manifest.version -or $onlineBootstrap.Desktop.Version -ne $manifest.desktopVersion -or $onlineBootstrap.SolidWorksAddin.Version -ne $manifest.solidWorksAddinVersion) { throw '在线客户端升级配置与部署包版本不一致。' }
$status = [ordered]@{ installedAt = [DateTimeOffset]::Now.ToString('O'); version = $manifest.version; lanBaseUrl = $LanBaseUrl; mysqlService = 'UptonPdmMySQL'; apiService = 'UptonPdmApi'; health = $health.status; bootstrapAdminSecret = $secretPath; clientTools = $clientRoot; rebootRequired = $rebootRequired }
$status | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $localRoot 'installation-status.json') -Encoding UTF8
Write-Host "UPLM Windows Server 2019 首装完成：$($manifest.version)" -ForegroundColor Green
Write-Host "客户端工具目录：$clientRoot"
Write-Host "首次管理员密码受保护地保存在：$secretPath"
if ($rebootRequired) { Write-Warning 'VC++ 运行库要求重启；请重启服务器后复核服务状态。' }
