[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][ValidatePattern('^\d{4}\.\d{2}\.\d{2}\.\d{6}$')][string]$Version,
    [Parameter(Mandatory)][uri]$ServerAddress,
    [Parameter(Mandatory)][string]$ReleaseNote,
    [switch]$Activate
)
$ErrorActionPreference = 'Stop'
$releaseMutex = [Threading.Mutex]::new($false, 'Global\UPLM.LocalUiDeployment')
$releaseLockHeld = $false
try {
    try { $releaseLockHeld = $releaseMutex.WaitOne([TimeSpan]::FromSeconds(60)) }
    catch [Threading.AbandonedMutexException] { $releaseLockHeld = $true }
    if (!$releaseLockHeld) { throw '其他本机发布仍在进行，未更改插件发布清单。' }
. (Join-Path $PSScriptRoot 'SystemReleaseHistory.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repo = Split-Path -Parent $PSScriptRoot
$web = Join-Path $repo '.local/api/wwwroot'
$manifest = Join-Path $web 'client-bootstrap.json'
$packagePath = (Resolve-Path -LiteralPath $Package).Path
$hash = (Get-FileHash -LiteralPath $packagePath).Hash
$relative = "/updates/uplm-solidworks-addin-$Version-$($hash.Substring(0,12)).zip"
$url = [uri]::new($ServerAddress,$relative)
$target = Join-Path $web $relative.TrimStart('/')
$zip = [IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    foreach ($name in @('.uplm-version','Upton.Pdm.SolidWorks.Addin.dll','uplm-ui.zip','uplm-bootstrap.json')) {
        if (!$zip.GetEntry($name)) { throw "更新包缺少 $name" }
    }
    $reader = [IO.StreamReader]::new($zip.GetEntry('.uplm-version').Open())
    try { if ($reader.ReadToEnd().Trim() -ne $Version) { throw '更新包标记与发布版本不一致' } } finally { $reader.Dispose() }
    $assemblyCheck = [IO.Path]::GetTempFileName()
    try {
        $inputStream = $zip.GetEntry('Upton.Pdm.SolidWorks.Addin.dll').Open()
        $outputStream = [IO.File]::Create($assemblyCheck)
        try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose(); $inputStream.Dispose() }
        $compiledVersion = ([Diagnostics.FileVersionInfo]::GetVersionInfo($assemblyCheck).ProductVersion -split '\+')[0]
        if ($compiledVersion -ne $Version) { throw '插件程序集实际版本与发布标记不一致，未发布更新。' }
    } finally { Remove-Item -LiteralPath $assemblyCheck -ErrorAction SilentlyContinue }
} finally { $zip.Dispose() }
if (!(Test-Path -LiteralPath $target)) {
    $partial = "$target.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::Copy($packagePath,$partial)
        [IO.File]::Move($partial,$target)
    } finally { if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial } }
}
$download = [IO.Path]::GetTempFileName()
try {
    Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $download -TimeoutSec 120
    if ((Get-FileHash -LiteralPath $download).Hash -ne $hash) { throw '内网下载校验失败，未切换插件版本' }
} finally { Remove-Item -LiteralPath $download }
if (!$Activate) { return [pscustomobject]@{PackageUrl=$url.AbsoluteUri;Sha256=$hash;Activated=$false} }
& (Join-Path $PSScriptRoot 'Sync-LocalUiManifest.ps1') -ProjectRoot $repo -VerifyOnly | Out-Null
$baseline = [IO.File]::ReadAllText($manifest)
$config = $baseline | ConvertFrom-Json
if ([version]$config.SolidWorksAddin.Version -ge [version]$Version) { throw '已有相同或更新的插件发布，请核对当前在线版本。' }
$configurationVersion = (Get-Date).ToString('yyyy.MM.dd.HHmmss')
if ([version]$configurationVersion -le [version]$config.ConfigurationVersion) { throw '配置版本尚未递增，请稍后发布。' }
$history = Get-UplmReleaseHistory -CurrentVersion $configurationVersion -CurrentReleaseNote $ReleaseNote -CurrentDesktopVersion $config.Desktop.Version -CurrentSolidWorksAddinVersion $Version -SourceBootstraps @($config)
$config.SolidWorksAddin = [ordered]@{Version=$Version;PackageUrl=$relative;Sha256=$hash}
$config.ConfigurationVersion = $configurationVersion
$config.ReleasedAt = [DateTimeOffset]::Now.ToString('o')
$config.ReleaseNote = $ReleaseNote
$config.ReleaseHistory = $history
$newJson = $config | ConvertTo-Json -Depth 12
$pending = "$manifest.$([guid]::NewGuid().ToString('N')).tmp"
try {
    [IO.File]::WriteAllText($pending,$newJson,[Text.UTF8Encoding]::new($false))
    if ([IO.File]::ReadAllText($manifest) -cne $baseline) { throw '检测到其他发布，未覆盖版本清单。' }
    [IO.File]::Replace($pending,$manifest,[NullString]::Value)
} finally { if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath $pending } }
try {
    & (Join-Path $PSScriptRoot 'Sync-LocalUiManifest.ps1') -ProjectRoot $repo | Out-Null
    $ui = & (Join-Path $PSScriptRoot 'Sync-LocalUiManifest.ps1') -ProjectRoot $repo -VerifyOnly
    $online = Invoke-RestMethod -Uri ([uri]::new($ServerAddress,'/client-bootstrap.json'))
    if ($online.SolidWorksAddin.Version -ne $Version -or $online.SolidWorksAddin.Sha256 -ne $hash -or $online.SolidWorksAddin.PackageUrl -ne $relative) { throw '在线插件清单与已校验更新包不一致' }
} catch {
    if ([IO.File]::ReadAllText($manifest) -ceq $newJson) {
        [IO.File]::WriteAllText($pending,$baseline,[Text.UTF8Encoding]::new($false))
        [IO.File]::Replace($pending,$manifest,[NullString]::Value)
        & (Join-Path $PSScriptRoot 'Sync-LocalUiManifest.ps1') -ProjectRoot $repo | Out-Null
        & (Join-Path $PSScriptRoot 'Sync-LocalUiManifest.ps1') -ProjectRoot $repo -VerifyOnly | Out-Null
    }
    throw
}
[pscustomobject]@{Version=$Version;Sha256=$hash;Activated=$true;Ui=$ui}
} finally {
    if ($releaseLockHeld) { $releaseMutex.ReleaseMutex() }
    $releaseMutex.Dispose()
}
