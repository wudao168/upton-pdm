[CmdletBinding()]
param([string]$InstallRoot = 'C:\UPLM\pdm')

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run as Administrator.' }
$packageRoot = Split-Path -Parent $PSCommandPath
$payload = Join-Path $packageRoot 'payload'
$appSource = Join-Path $payload 'app'
$mysqlZip = Join-Path $payload 'mysql-8.4.11-winx64.zip'
if (-not (Test-Path -LiteralPath (Join-Path $appSource 'Pdm.Api.dll')) -or -not (Test-Path -LiteralPath $mysqlZip)) { throw 'Package payload is incomplete.' }
if (Get-Service -Name 'UptonPdmApi' -ErrorAction SilentlyContinue) { throw 'UPLM is already installed. This first-install package must not overwrite an existing server.' }
if (Get-Service -Name 'UptonPdmMySQL' -ErrorAction SilentlyContinue) { throw 'A previous UptonPdmMySQL service exists. Remove the failed test installation before retrying.' }
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { throw '.NET 10 Hosting Bundle is missing.' }
$runtime = Join-Path $InstallRoot 'runtime'
$mysqlHome = Join-Path $runtime 'mysql-8.4.11-winx64'
$local = Join-Path $InstallRoot 'local'
$secrets = Join-Path $local 'secrets'
foreach ($dir in @($InstallRoot,$runtime,$local,$secrets,(Join-Path $local 'mysql\data'),(Join-Path $local 'mysql\logs'),(Join-Path $local 'mysql\tmp'),(Join-Path $local 'vault'),(Join-Path $local 'release'),(Join-Path $local 'uploads'),(Join-Path $local 'program-templates'))) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
if (-not (Test-Path -LiteralPath (Join-Path $mysqlHome 'bin\mysqld.exe'))) { Expand-Archive -LiteralPath $mysqlZip -DestinationPath $runtime -Force }
$random = { $bytes = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes); [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_') }
$secret = [ordered]@{ mysqlRootPassword = "MysqlRoot!$(& $random)"; databasePassword = "PdmDb!$(& $random)"; bootstrapAdminPassword = "PdmAdmin!$(& $random)"; jwtSigningKey = & $random }
$secretPath = Join-Path $secrets 'pdm-secrets.json'; $secret | ConvertTo-Json | Set-Content -LiteralPath $secretPath -Encoding UTF8
& icacls.exe $secretPath /inheritance:r /grant:r 'Administrators:(F)' 'SYSTEM:(F)' | Out-Null
$ini = Join-Path $local 'mysql\my.ini'
$iniLines = @('[mysqld]',"basedir=$($mysqlHome.Replace('\','/'))","datadir=$((Join-Path $local 'mysql\data').Replace('\','/'))",'port=3308','bind-address=127.0.0.1','mysqlx=0','character-set-server=utf8mb4','collation-server=utf8mb4_0900_ai_ci','skip-log-bin',"log-error=$((Join-Path $local 'mysql\logs\mysql-error.log').Replace('\','/'))",'pid-file=upton-pdm-mysql.pid')
[IO.File]::WriteAllLines($ini, $iniLines, (New-Object Text.UTF8Encoding($false)))
$mysqld = Join-Path $mysqlHome 'bin\mysqld.exe'; $mysqlClient = Join-Path $mysqlHome 'bin\mysql.exe'
if (-not (Test-Path -LiteralPath (Join-Path $local 'mysql\data\auto.cnf'))) { & $mysqld "--defaults-file=$ini" --initialize-insecure --console; if ($LASTEXITCODE -ne 0) { throw 'MySQL initialization failed.' } }
& $mysqld --install UptonPdmMySQL "--defaults-file=$ini"; if ($LASTEXITCODE -ne 0) { throw 'MySQL service installation failed.' }
& sc.exe config UptonPdmMySQL start= auto | Out-Null; Start-Service UptonPdmMySQL
for ($i=0; $i -lt 60; $i++) { if (Test-NetConnection 127.0.0.1 -Port 3308 -InformationLevel Quiet) { break }; Start-Sleep -Seconds 1 }; if (-not (Test-NetConnection 127.0.0.1 -Port 3308 -InformationLevel Quiet)) { throw 'MySQL did not start.' }
@("ALTER USER 'root'@'localhost' IDENTIFIED BY '$($secret.mysqlRootPassword)';","CREATE DATABASE pdm CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;","CREATE USER 'pdm_app'@'127.0.0.1' IDENTIFIED BY '$($secret.databasePassword)';","GRANT ALL PRIVILEGES ON pdm.* TO 'pdm_app'@'127.0.0.1';",'FLUSH PRIVILEGES;') | & $mysqlClient --protocol=TCP --host=127.0.0.1 --port=3308 --user=root
if ($LASTEXITCODE -ne 0) { throw 'MySQL application account setup failed.' }
$app = Join-Path $InstallRoot 'app'; Copy-Item -LiteralPath $appSource -Destination $app -Recurse -Force
$bin = '"{0}" "{1}"' -f $dotnet,(Join-Path $app 'Pdm.Api.dll'); New-Service -Name UptonPdmApi -BinaryPathName $bin -DisplayName 'UPLM API' -StartupType Automatic | Out-Null; & sc.exe config UptonPdmApi depend= UptonPdmMySQL | Out-Null; & sc.exe failure UptonPdmApi reset= 86400 actions= restart/5000/restart/15000/restart/30000 | Out-Null
$envs = @('ASPNETCORE_ENVIRONMENT=Production',"PDM_DB_PASSWORD=$($secret.databasePassword)","PDM_JWT_SIGNING_KEY=$($secret.jwtSigningKey)","PDM_BOOTSTRAP_ADMIN_PASSWORD=$($secret.bootstrapAdminPassword)","Pdm__Storage__UploadTempRoot=$(Join-Path $local 'uploads')","Pdm__Storage__ProgramTemplateRoot=$(Join-Path $local 'program-templates')")
New-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\UptonPdmApi' -Name Environment -PropertyType MultiString -Value $envs -Force | Out-Null; Start-Service UptonPdmApi
for ($i=0; $i -lt 90; $i++) { try { $health = Invoke-RestMethod 'http://127.0.0.1:5080/health' -TimeoutSec 3; if ($health.status -eq 'ok') { break } } catch {}; Start-Sleep -Seconds 1 }; if ($null -eq $health -or $health.status -ne 'ok') { throw 'UPLM API health check failed.' }; $envs | Where-Object { $_ -notlike 'PDM_BOOTSTRAP_ADMIN_PASSWORD=*' } | Set-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\UptonPdmApi' -Name Environment; $health | ConvertTo-Json
