$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
Set-Location $root
$logs=Join-Path $root '.local/logs'
function PortReady([int]$port) {
 $tcp=[Net.Sockets.TcpClient]::new()
 try { $a=$tcp.BeginConnect('127.0.0.1',$port,$null,$null); if(-not $a.AsyncWaitHandle.WaitOne(1000)){return $false}; $tcp.EndConnect($a); return $true } catch {return $false} finally {$tcp.Dispose()}
}
if(-not (PortReady 3308)) {
 if(-not (Get-Process mysqld -ErrorAction SilentlyContinue)) {
 Start-Process (Join-Path $root '.runtime/mysql-8.4.11-winx64/bin/mysqld.exe') -ArgumentList ('--defaults-file="'+$root+'\.local\mysql\recovery-my.ini"') -WindowStyle Hidden
 }
 for($i=0;$i -lt 120;$i++){if(PortReady 3308){break};Start-Sleep -Seconds 2}
 if(-not (PortReady 3308)){throw 'Database startup timed out'}
}
if(-not (PortReady 5080)) {
 $existing=Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" | Where-Object {$_.CommandLine -like ('*'+$root+'\.local\api\Pdm.Api.dll*')}
 if(-not $existing) {
 $s=Get-Content (Join-Path $root '.local/secrets/pdm-secrets.json') -Raw -Encoding UTF8|ConvertFrom-Json
 $env:ASPNETCORE_ENVIRONMENT='Production'
 $env:PDM_DB_PASSWORD=$s.databasePassword
 $env:PDM_JWT_SIGNING_KEY=$s.jwtSigningKey
 $env:Pdm__Storage__UploadTempRoot=Join-Path $root '.local/uploads'
 $env:Pdm__Storage__ProgramTemplateRoot=Join-Path $root '.local/program-templates'
 $env:Pdm__PreviewWorker__AllowLocalExecution='false'
 $env:PDM_PREVIEW_WORKER_PATH=Join-Path $root '.local/preview-worker/Upton.Pdm.SolidWorks.PreviewWorker.exe'
 Start-Process (Join-Path $root '.dotnet/dotnet.exe') -ArgumentList ('"'+$root+'\.local\api\Pdm.Api.dll"') -WorkingDirectory (Join-Path $root '.local/api') -WindowStyle Hidden -RedirectStandardOutput (Join-Path $logs 'autostart-api.stdout.log') -RedirectStandardError (Join-Path $logs 'autostart-api.stderr.log')
 }
}
for($i=0;$i -lt 90;$i++){try{$h=Invoke-RestMethod http://127.0.0.1:5080/health -TimeoutSec 3;if($h.status -eq 'ok'){exit 0}}catch{};Start-Sleep -Seconds 2}
throw 'API startup timed out'
