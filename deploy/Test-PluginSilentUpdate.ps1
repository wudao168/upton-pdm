[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$root = Join-Path $repo ('tmp/plugin-silent-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
# Compile the production updater with private test storage and a private fake host process name.
# No live updater state, installation directory or SolidWorks process is touched.
$source = [IO.File]::ReadAllText((Join-Path $repo 'src/Pdm.Client.Shared/ClientBootstrap.cs'))
$source = $source.Replace('Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)', ('@"' + $root.Replace('"','""') + '"'))
$source = $source.Replace("[Environment]::GetFolderPath('LocalApplicationData')", ("'" + $root.Replace("'","''") + "'"))
$source = $source.Replace("Get-Process -Name 'SLDWORKS'", "Get-Process -Name 'UPLM.TestSolidWorks'")
$source = $source.Replace('Start-Sleep -Seconds 30', 'Start-Sleep -Seconds 1')
[IO.File]::WriteAllText((Join-Path $root 'ClientBootstrap.cs'), $source)
[IO.File]::WriteAllText((Join-Path $root 'test.csproj'), @'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net48</TargetFramework><LangVersion>latest</LangVersion><OutputType>Exe</OutputType><AssemblyName>UPLM.TestSolidWorks</AssemblyName></PropertyGroup><ItemGroup><Reference Include="System.Net.Http"/><Reference Include="System.IO.Compression"/><Reference Include="System.IO.Compression.FileSystem"/><Reference Include="System.Web.Extensions"/><PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies.net48" Version="1.0.3" PrivateAssets="all"/></ItemGroup></Project>
'@)
[IO.File]::WriteAllText((Join-Path $root 'Program.cs'), @'
using System;
class Program { static void Main(string[] args) { System.Threading.Thread.Sleep(int.Parse(args[0])); } }
'@)
& dotnet build (Join-Path $root 'test.csproj') -c Release -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Fixture build failed.' }
$assemblyPath = Join-Path $root 'bin/Release/net48/UPLM.TestSolidWorks.exe'
$assembly = [Reflection.Assembly]::LoadFile($assemblyPath)
$updater = $assembly.GetType('Upton.Pdm.ClientShared.ClientPackageUpdater', $true)
$packageType = $assembly.GetType('Upton.Pdm.ClientShared.ClientPackageConfiguration', $true)
$stage = $updater.GetMethod('StagePluginInBackground', [Reflection.BindingFlags]'Public,Static')
$schedule = $updater.GetMethod('SchedulePluginUpdate', [Reflection.BindingFlags]'Public,Static')
$pending = Join-Path $root 'UPLM/updates/solidworks-addin-pending.json'
$settings = Join-Path $root 'UPLM/solidworks-addin-settings.json'
$payload = Join-Path $root 'payload'
$target = Join-Path $root 'target'
New-Item -ItemType Directory -Path $payload,$target -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $target '.uplm-version'),'2026.11.11.010000')
[IO.File]::WriteAllText((Join-Path $target 'uplm-bootstrap.json'),'{"BootstrapUrl":"http://192.168.2.8:5173/client-bootstrap.json"}')
[IO.File]::WriteAllText((Join-Path $payload '.uplm-version'),'2026.11.12.010000')
[IO.File]::WriteAllText((Join-Path $payload 'Upton.Pdm.SolidWorks.Addin.dll'),'verified fixture')
[IO.File]::WriteAllText((Join-Path $payload 'uplm-bootstrap.json'),'{"BootstrapUrl":"http://wrong-server"}')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$ui = Join-Path $root 'ui'
New-Item -ItemType Directory -Path $ui -Force | Out-Null
foreach ($entry in @('index.html','review-overlay.html','server-settings.html','client-bootstrap.json')) {
    [IO.File]::WriteAllText((Join-Path $ui $entry),'fixture UI')
}
[IO.Compression.ZipFile]::CreateFromDirectory($ui,(Join-Path $payload 'uplm-ui.zip'))
$zipPath = Join-Path $root 'package.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($payload,$zipPath)
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
$server = Start-Job -ArgumentList $port,$zipPath -ScriptBlock {
    param($port,$zipPath)
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,$port)
    $listener.Start()
    try {
      for ($requestNumber = 0; $requestNumber -lt 2; $requestNumber++) {
        $deadline = [DateTime]::UtcNow.AddSeconds(45)
        while (!$listener.Pending() -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
        if (!$listener.Pending()) { throw 'No download request arrived.' }
        $client = $listener.AcceptTcpClient()
        try {
            $stream = $client.GetStream()
            $buffer = New-Object byte[] 8192
            [void]$stream.Read($buffer,0,$buffer.Length)
            if ($requestNumber -eq 0) {
                $unavailable = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 503 Service Unavailable`r`nContent-Length: 0`r`nConnection: close`r`n`r`n")
                $stream.Write($unavailable,0,$unavailable.Length)
                continue
            }
            Start-Sleep -Seconds 5
            $bytes = [IO.File]::ReadAllBytes($zipPath)
            $header = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 200 OK`r`nContent-Length: $($bytes.Length)`r`nConnection: close`r`n`r`n")
            $stream.Write($header,0,$header.Length)
            $stream.Write($bytes,0,$bytes.Length)
        } finally { $client.Dispose() }
      }
    } finally { $listener.Stop() }
}
$host1 = Start-Process -FilePath $assemblyPath -ArgumentList '3000' -WindowStyle Hidden -PassThru
$host2 = Start-Process -FilePath $assemblyPath -ArgumentList '16000' -WindowStyle Hidden -PassThru
try {
    $package = [Activator]::CreateInstance($packageType,$true)
    foreach ($pair in @{Version='2026.11.12.010000';PackageUrl="http://127.0.0.1:$port/package.zip";Sha256=(Get-FileHash -LiteralPath $zipPath).Hash}.GetEnumerator()) {
        $packageType.GetProperty($pair.Key).SetValue($package,$pair.Value,$null)
    }
    if (!$stage.Invoke($null,[object[]]@($package,$target.ToString(),[int]$host1.Id,$true))) { throw 'Background start failed.' }
    $marker = "$pending.downloading"
    $pid1 = [IO.File]::ReadAllText($marker)
    [void]$stage.Invoke($null,[object[]]@($package,$target.ToString(),[int]$host1.Id,$true))
    if ([IO.File]::ReadAllText($marker) -ne $pid1) { throw 'Duplicate download worker started.' }
    Write-Output 'PASS: repeated checks reuse the hidden download worker.'
    if (!$host1.WaitForExit(10000)) { throw 'Fake host did not exit.' }
    for ($attempt=0; $attempt -lt 100 -and !(Test-Path -LiteralPath $pending); $attempt++) { Start-Sleep -Milliseconds 100 }
    if (!(Test-Path -LiteralPath $pending)) { throw 'Download did not survive host exit.' }
    if ((Get-Content -LiteralPath (Join-Path $target '.uplm-version') -Raw) -ne '2026.11.11.010000') { throw 'Installed while another host was open.' }
    Write-Output 'PASS: transient download failure retries automatically, survives host exit and waits for the remaining host.'
    [IO.File]::WriteAllText($settings,'{"AutomaticUpdatesEnabled":false}')
    if (!$host2.WaitForExit(20000)) { throw 'Remaining fake host did not exit.' }
    for ($attempt=0; $attempt -lt 100 -and (Test-Path -LiteralPath "$pending.launched"); $attempt++) { Start-Sleep -Milliseconds 100 }
    if ((Get-Content -LiteralPath (Join-Path $target '.uplm-version') -Raw) -ne '2026.11.11.010000') { throw 'Disabled automatic updates were installed.' }
    Write-Output 'PASS: turning off automatic updates while waiting preserves the existing installation.'
    [IO.File]::WriteAllText($settings,'{"AutomaticUpdatesEnabled":true}')
    if (!$schedule.Invoke($null,[object[]]@(0,$true))) { throw 'Silent update scheduling failed.' }
    for ($attempt=0; $attempt -lt 200 -and (Test-Path -LiteralPath $pending); $attempt++) { Start-Sleep -Milliseconds 100 }
    if (Test-Path -LiteralPath $pending) { throw 'Silent installation did not complete.' }
    if ((Get-Content -LiteralPath (Join-Path $target '.uplm-version') -Raw) -ne '2026.11.12.010000') { throw 'Installed version mismatch.' }
    if ((Get-Content -LiteralPath (Join-Path $target 'uplm-bootstrap.json') -Raw) -notmatch '192.168.2.8:5173') { throw 'Server address was changed.' }
    if ((Get-Content -LiteralPath (Join-Path $target 'Upton.Pdm.SolidWorks.Addin.dll') -Raw) -ne 'verified fixture') { throw 'Payload was not replaced.' }
    foreach ($entry in @('index.html','review-overlay.html','server-settings.html','client-bootstrap.json')) {
        if ((Get-Content -LiteralPath (Join-Path $target ('ui/' + $entry)) -Raw) -ne 'fixture UI') { throw 'Offline UI was not fully installed.' }
    }
    Write-Output 'PASS: hidden update installs verified files and preserves the configured server without user interaction.'
    Write-Output "Evidence: $root"
} catch {
    Write-Output ($_ | Out-String)
    throw
} finally {
    $host1.Dispose(); $host2.Dispose()
    Stop-Job $server -ErrorAction SilentlyContinue
    Remove-Job $server -Force -ErrorAction SilentlyContinue
}
