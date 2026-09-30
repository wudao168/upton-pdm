[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$assemblyPath = Join-Path $projectRoot 'src\Pdm.SolidWorks.Addin\bin\Release\net48\Upton.Pdm.SolidWorks.Addin.dll'
$assembly = [Reflection.Assembly]::LoadFile($assemblyPath)
$updaterType = $assembly.GetType('Upton.Pdm.ClientShared.ClientPackageUpdater', $true)
$buildArguments = $updaterType.GetMethod('BuildUpdaterArguments', [Reflection.BindingFlags]'NonPublic,Static')
if (-not $buildArguments) { throw 'Build the updated plugin before running this test.' }
$root = Join-Path $projectRoot ('tmp\handoff-test-' + [Guid]::NewGuid().ToString('N'))
foreach ($scenario in @('success', 'version', 'hash', 'rollback')) {
    $invalidVersion = $scenario -eq 'version'
    $caseRoot = Join-Path $root ($scenario + "-quote'-unicode-" + [char]0x4E2D)
    $target = Join-Path $caseRoot 'target'
    $payload = Join-Path $caseRoot 'payload'
    New-Item -ItemType Directory -Path $target, $payload -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $target '.uplm-version'), '2026.09.30.1417')
    $payloadVersion = if ($invalidVersion) { '2026.09.30.1400' } else { '2026.09.30.1459' }
    [IO.File]::WriteAllText((Join-Path $payload '.uplm-version'), $payloadVersion)
    [IO.File]::WriteAllText((Join-Path $payload 'updated.txt'), 'new payload')
    [IO.File]::WriteAllText((Join-Path $target 'uplm-bootstrap.json'), '{"BootstrapUrl":"http://192.168.2.8:5173/client-bootstrap.json"}')
    $pendingPath = Join-Path $caseRoot 'pending.json'
    # Simulate state readable inside the plugin but unreadable by the installer.
    [IO.File]::WriteAllBytes($pendingPath, [byte[]]@(0, 1, 2, 255))
    $hashes = @{}
    Get-ChildItem -LiteralPath $payload -File | ForEach-Object { $hashes[$_.Name] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    if ($scenario -eq 'hash') { [IO.File]::WriteAllText((Join-Path $payload 'updated.txt'), 'corrupted download') }
    $pending = @{Component='desktop'; Version='2026.09.30.1459'; PayloadDirectory=$payload; TargetDirectory=$target; FileHashes=$hashes} | ConvertTo-Json -Compress
    $arguments = $buildArguments.Invoke($null, [object[]]@(0, $pendingPath.ToString(), ($pendingPath + '.launched'), '', $pending.ToString()))
    if ($scenario -eq 'rollback') {
        # Fault injection: candidate hashes pass, installed-file hashes fail after the directory swap.
        $bootstrap = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String(($arguments -split ' ')[-1]))
        $installedFile = (Join-Path $target 'updated.txt').Replace("'", "''")
        $mock = "Import-Module Microsoft.PowerShell.Utility; function Get-FileHash { param([string]`$LiteralPath,[string]`$Algorithm); if (`$LiteralPath -eq '$installedFile') { [pscustomobject]@{Hash='invalid'} } else { Microsoft.PowerShell.Utility\Get-FileHash -LiteralPath `$LiteralPath -Algorithm `$Algorithm } }; "
        $arguments = '-NoProfile -ExecutionPolicy Bypass -EncodedCommand ' + [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($mock + $bootstrap))
    }
    $start = [Diagnostics.ProcessStartInfo]::new('powershell.exe', $arguments)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($start)
    if (-not $process.WaitForExit(30000)) { throw 'Installer timed out.' }
    if ($process.ExitCode -ne 0) { throw "Installer exited with $($process.ExitCode)" }
    $installed = (Get-Content -LiteralPath (Join-Path $target '.uplm-version') -Raw).Trim()
    if ($scenario -ne 'success') {
        if ($installed -ne '2026.09.30.1417' -or -not (Test-Path -LiteralPath ($pendingPath + '.error.txt'))) { throw 'Invalid package was not rejected with an error.' }
        if ($scenario -eq 'rollback' -and (Test-Path -LiteralPath (Join-Path $target 'updated.txt'))) { throw 'Failed new files remained after rollback.' }
        Write-Output "PASS: $scenario failure retains/restores the old installation and records an error."
    } else {
        if ($installed -ne '2026.09.30.1459' -or (Test-Path -LiteralPath $pendingPath)) { throw 'Update did not finish.' }
        if ((Get-Content -LiteralPath (Join-Path $target 'uplm-bootstrap.json') -Raw) -notmatch '192.168.2.8:5173') { throw 'Server address was lost.' }
        if (-not (Test-Path -LiteralPath (Join-Path $target 'updated.txt'))) { throw 'Payload was not installed.' }
        Write-Output 'PASS: in-memory handoff updates despite unreadable pending JSON, without a disk script; quoted Unicode paths and empty restart path work.'
    }
    $process.Dispose()
}
$launch = $updaterType.GetMethod('TryLaunchPendingUpdate', [Reflection.BindingFlags]'Public,Static')
if ($launch.Invoke($null, [object[]]@('solidworks-addin', 0, '', $false))) { throw 'Silent plugin installation was allowed.' }
Write-Output 'PASS: silent plugin installation is blocked.'
