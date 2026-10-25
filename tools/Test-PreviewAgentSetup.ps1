[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$SetupPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms,System.Drawing,System.IO.Compression,System.IO.Compression.FileSystem
$root = Split-Path -Parent $PSScriptRoot
$setup = [IO.Path]::GetFullPath($SetupPath)
$directory = Join-Path $root ('.artifacts\preview-agent-setup-1.2.0\verify-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$assembly = [Reflection.Assembly]::LoadFrom($setup)
$flags = [Reflection.BindingFlags]'Public,NonPublic,Instance,Static'
$script:checks = 0
function Check([bool]$condition, [string]$message) { if(-not $condition){throw "FAIL $message"};$script:checks++;Write-Output "PASS $message" }
function StreamHash($stream) { $hash=[Security.Cryptography.SHA256]::Create();try{[BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-','')}finally{$hash.Dispose()} }
function GuiSubsystem([string]$path) { $stream=[IO.File]::OpenRead($path);$reader=[IO.BinaryReader]::new($stream);try{$stream.Position=0x3c;$offset=$reader.ReadInt32();$stream.Position=$offset+24+68;$reader.ReadUInt16()}finally{$reader.Dispose()} }
Check ($assembly.GetName().Version.ToString() -eq '1.2.0.0') 'complete installer version is 1.2.0.0'
Check ((GuiSubsystem $setup) -eq 2) 'installer is a Windows GUI executable'
$resources=@($assembly.GetManifestResourceNames())
Check (@($resources|Where-Object{$_ -like 'payload.*'}).Count -eq 7) 'complete installer embeds all seven application payload files'
Check (@($resources|Where-Object{$_ -match 'PdmPreviewAgent\.json|settings\.json|pdm-secrets|\.pdb$'}).Count -eq 0) 'payload excludes development configuration, credentials and PDBs'
$stream=$assembly.GetManifestResourceStream('upgrade.files.json');$reader=[IO.StreamReader]::new($stream);try{$manifest=$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()}
Check ($manifest.Version -eq '1.2.0.0' -and $manifest.Files.Count -eq 7) 'embedded upgrade manifest matches both program versions'
foreach($file in $manifest.Files){
 $name=if($file.Path.StartsWith('preview-worker\')){'payload.worker.'+$file.Path.Substring('preview-worker\'.Length)}else{'payload.agent.'+$file.Path}
 $stream=$assembly.GetManifestResourceStream($name);if(-not $stream){throw "Missing embedded resource $name"};try{$hash=StreamHash $stream}finally{$stream.Dispose()}
 Check ($hash -eq $file.Sha256) ('embedded bytes verified: '+$file.Path)
}
$stream=$assembly.GetManifestResourceStream('upgrade.Update-PreviewAgent.ps1');try{$scriptHash=StreamHash $stream}finally{$stream.Dispose()}
Check ($scriptHash -eq (Get-FileHash -LiteralPath (Join-Path $root 'deploy\Update-PreviewAgent.ps1')).Hash) 'installer carries the tested upgrade and rollback script'
$optionsType=$assembly.GetType('Upton.Pdm.PreviewAgent.Setup.SetupOptions',$true)
$options=[Activator]::CreateInstance($optionsType,$true)
$extract=Join-Path $directory 'extracted'
$options.InstallDirectory=$extract
$runnerType=$assembly.GetType('Upton.Pdm.PreviewAgent.Setup.SetupRunner',$true)
$runner=$runnerType.GetConstructors($flags)[0].Invoke(@($options,$null))
$runnerType.GetMethod('ExtractPayload',$flags).Invoke($runner,[object[]]@([string]$extract))
Check (@(Get-ChildItem -LiteralPath $extract -File -Recurse).Count -eq 7) 'installer extraction produces the complete runtime without external downloads'
Check ((GuiSubsystem (Join-Path $extract 'Upton.Pdm.PreviewAgent.exe')) -eq 2) 'installed agent has no console window'
Check ((Get-Item -LiteralPath (Join-Path $extract 'Upton.Pdm.PreviewAgent.exe')).VersionInfo.FileVersion -eq '1.2.0.0' -and
 (Get-Item -LiteralPath (Join-Path $extract 'preview-worker\Upton.Pdm.SolidWorks.PreviewWorker.exe')).VersionInfo.FileVersion -eq '1.2.0.0') 'installed agent and worker are the same release'
$badRoot=$options.InstallDirectory;$options.InstallDirectory=[IO.Path]::GetPathRoot($directory);$blocked=$false
try{$runnerType.GetMethod('ValidateInstallDirectory',$flags).Invoke($runner,@($true))}catch{$blocked=$_.Exception.InnerException.Message -match '独立安装文件夹|公共目录'}
Check $blocked 'disk roots are rejected before install or uninstall operations'
$options.InstallDirectory=$badRoot
$preferences=$assembly.GetType('Upton.Pdm.PreviewAgent.Setup.SetupPreferences',$true)
$preferences.GetField('FilePath',$flags).SetValue($null,(Join-Path $directory 'setup-preferences.json'))
[IO.File]::WriteAllText((Join-Path $extract 'PdmPreviewAgent.json'),'{}')
$form=[Activator]::CreateInstance($assembly.GetType('Upton.Pdm.PreviewAgent.Setup.SetupForm',$true),$flags,$null,@($options),$null)
try{
 $form.StartPosition=[Windows.Forms.FormStartPosition]::Manual;$form.Left=-20000;$form.Top=-20000;$form.Show();[Windows.Forms.Application]::DoEvents()
 $upgrade=$form.GetType().GetField('upgradeButton',$flags).GetValue($form)
 Check ($upgrade.Enabled -and $upgrade.Text.Contains('保留配置')) 'existing installations expose the configuration-preserving upgrade action'
 Check ($form.GetType().GetField('passwordBox',$flags).GetValue($form).UseSystemPasswordChar) 'administrator password is masked in the installer'
 $bitmap=[Drawing.Bitmap]::new($form.Width,$form.Height);try{$form.DrawToBitmap($bitmap,[Drawing.Rectangle]::new(0,0,$bitmap.Width,$bitmap.Height));$bitmap.Save((Join-Path $directory 'installer.png'))}finally{$bitmap.Dispose()}
 $form.Close();Check $form.IsDisposed 'closing the installer exits instead of leaving a second tray manager'
}finally{$form.Dispose()}
$listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$listener.Start();$port=$listener.LocalEndpoint.Port;$listener.Stop()
@{Name='安装包隔离验收';Port=$port;Token='fixture-only';BindAddress='127.0.0.1';ServerUrl="http://127.0.0.1:$port";ShowMainWindow=$true;StartWithWindows=$false;CloseToTray=$true;WorkRoot=(Join-Path $directory 'work')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $extract 'PdmPreviewAgent.json') -Encoding UTF8
$exe=Join-Path $extract 'Upton.Pdm.PreviewAgent.exe'
$process=Start-Process -FilePath $exe -ArgumentList @('--tray','--dataroot',('"'+(Join-Path $directory 'agent-data')+'"')) -WindowStyle Hidden -PassThru
try{
 $health=$null;for($attempt=0;$attempt -lt 40;$attempt++){try{$health=Invoke-RestMethod "http://127.0.0.1:$port/health" -TimeoutSec 2;if($health.windowCreated){break}}catch{};Start-Sleep -Milliseconds 250}
 Check ($health.status -eq 'ok' -and $health.agentVersion -eq '1.2.0.0' -and $health.workerVersion -eq '1.2.0.0') 'actual extracted installer runtime starts and reports both embedded versions'
 Check ($health.windowCreated -and -not $health.windowVisible) 'packaged runtime creates the three-page GUI and supports an explicit tray launch'
}finally{if(-not $process.HasExited){Stop-Process -Id $process.Id;$process.WaitForExit(5000)|Out-Null}}
[pscustomobject]@{Status='passed';Checks=$script:checks;Version='1.2.0.0';Setup=$setup;SizeBytes=(Get-Item $setup).Length;Sha256=(Get-FileHash -LiteralPath $setup).Hash;Evidence=$directory;InstalledOnDevelopmentMachine=$false}|
 ConvertTo-Json|Tee-Object -FilePath (Join-Path $root '.artifacts\preview-agent-setup-1.2.0\verification.json')
