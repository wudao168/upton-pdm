$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$source = Get-Content -LiteralPath (Join-Path $projectRoot 'src/Pdm.Client.Shared/ClientBootstrap.cs') -Raw -Encoding UTF8
$match = [regex]::Match($source, '(?s)private const string ApplyScript = @"(.*?)";')
if (-not $match.Success) { throw 'Updater script not found.' }
$root = Join-Path $projectRoot ('tmp/locator-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$script = Join-Path $root 'apply.ps1'
[IO.File]::WriteAllText($script, $match.Groups[1].Value.Replace('""', '"'), [Text.UTF8Encoding]::new($true))
foreach ($component in @('desktop', 'solidworks-addin')) {
    $target = Join-Path $root $component
    $payload = Join-Path $root ($component + '-payload')
    New-Item -ItemType Directory -Path $target, $payload -Force | Out-Null
    $original = '{"BootstrapUrl":"http://192.168.2.8/client-bootstrap.json"}'
    [IO.File]::WriteAllText((Join-Path $target 'uplm-bootstrap.json'), $original)
    [IO.File]::WriteAllText((Join-Path $payload 'uplm-bootstrap.json'), '{"BootstrapUrl":"http://127.0.0.1:5173/client-bootstrap.json"}')
    [IO.File]::WriteAllText((Join-Path $target '.uplm-version'), '1.0.0')
    [IO.File]::WriteAllText((Join-Path $payload '.uplm-version'), '1.0.1')
    $pending = Join-Path $root ($component + '-pending.json')
    @{ Component = $component; Version = '1.0.1'; TargetDirectory = $target; PayloadDirectory = $payload } |
        ConvertTo-Json | Set-Content -LiteralPath $pending -Encoding UTF8
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script -ProcessId 0 -PendingPath $pending -LaunchMarker ($pending + '.launched')
    if ($LASTEXITCODE -ne 0) { throw "$component updater failed." }
    if (Test-Path -LiteralPath ($pending + '.error.txt')) { throw (Get-Content -LiteralPath ($pending + '.error.txt') -Raw) }
    if ((Get-Content -LiteralPath (Join-Path $target 'uplm-bootstrap.json') -Raw) -cne $original) { throw "$component lost its server address." }
    if ((Get-Content -LiteralPath (Join-Path $target '.uplm-version') -Raw) -ne '1.0.1') { throw "$component did not update." }
    Write-Output "PASS: $component updated and retained its installed server address."
}
