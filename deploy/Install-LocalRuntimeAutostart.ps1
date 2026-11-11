Start-Transcript -Path 'G:\codex file\pdm\.local\logs\autostart-install.log' -Append
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$action=New-ScheduledTaskAction -Execute "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -Argument ('-NoProfile -ExecutionPolicy Bypass -File "'+$root+'\deploy\Start-LocalPdmRuntime.ps1"') -WorkingDirectory $root
$triggers=@((New-ScheduledTaskTrigger -AtStartup),(New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(2) -RepetitionInterval (New-TimeSpan -Minutes 2)))
$settings=New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 8) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName 'UPLM-LocalRuntime-Autostart' -Action $action -Trigger $triggers -Settings $settings -User SYSTEM -RunLevel Highest -Description 'Local PDM runtime startup and recovery' -Force | Out-Null
Start-ScheduledTask -TaskName 'UPLM-LocalRuntime-Autostart'

Get-ScheduledTask -TaskName 'UPLM-LocalRuntime-Autostart' | Select-Object TaskName,State | ConvertTo-Json | Set-Content 'G:\codex file\pdm\.local\logs\autostart-task-status.json'
