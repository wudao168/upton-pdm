[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$WorkerPath)
$ErrorActionPreference = 'Stop'
$parent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$root = Join-Path $parent ('pdm-cad-roots-' + [Guid]::NewGuid().ToString('N'))
$process = $null
function Wait-ForFile([string]$Path) {
    $until = [DateTimeOffset]::Now.AddSeconds(15)
    while (-not (Test-Path -LiteralPath $Path) -and [DateTimeOffset]::Now -lt $until) {
        if ($process -and $process.HasExited) { throw "网页存档后台提前退出，退出码：$($process.ExitCode)；$(Get-Content -LiteralPath (Join-Path $root 'stderr.log') -Raw -ErrorAction SilentlyContinue)" }
        Start-Sleep -Milliseconds 100
    }
    if (-not (Test-Path -LiteralPath $Path)) { throw "网页存档后台未读取最新目录配置：$Path" }
}
try {
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    $config = Join-Path $root 'roots.txt'
    $first = Join-Path $root 'current'
    $legacy = Join-Path $root 'legacy'
    [IO.File]::WriteAllLines($config, @($first, $legacy))
    $process = Start-Process -FilePath $WorkerPath -ArgumentList @('--watch-config', ('"{0}"' -f $config)) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $root 'stdout.log') -RedirectStandardError (Join-Path $root 'stderr.log')
    Wait-ForFile (Join-Path $first 'worker-heartbeat.txt')
    Wait-ForFile (Join-Path $legacy 'worker-heartbeat.txt')
    [IO.File]::WriteAllText(($config + '.pause'), 'backup')
    Wait-ForFile ($config + '.paused')
    $next = Join-Path $root 'next'
    [IO.File]::WriteAllLines($config, @($next, $first, $legacy))
    Remove-Item -LiteralPath ($config + '.pause')
    Wait-ForFile (Join-Path $next 'worker-heartbeat.txt')
    Write-Output '通过：同一后台读取当前及历史目录；备份暂停确认；配置变更后读取新目录。未提交CAD任务，未启动SolidWorks。'
} finally {
    if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
    $absolute = [IO.Path]::GetFullPath($root)
    if (-not $absolute.StartsWith($parent + '\', [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($absolute) -notlike 'pdm-cad-roots-*') { throw '隔离测试清理路径无效。' }
    if (Test-Path -LiteralPath $absolute) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}
