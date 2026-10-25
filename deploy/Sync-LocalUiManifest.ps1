[CmdletBinding()]
param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot), [switch]$VerifyOnly)
$ErrorActionPreference = 'Stop'
$web = Join-Path $ProjectRoot '.local\api\wwwroot'
$manifest = Join-Path $web 'client-bootstrap.json'
$targets = @((Join-Path $ProjectRoot '.local\client\ui'), (Join-Path $ProjectRoot '.local\solidworks-addin\ui'), (Join-Path $env:LOCALAPPDATA 'UPLM\client\ui'), (Join-Path $env:LOCALAPPDATA 'UPLM\solidworks-addin\ui')) | Where-Object { Test-Path -LiteralPath $_ -PathType Container }
$files = @('index.html','review-overlay.html','server-settings.html','favicon.ico')
foreach ($entry in @('index.html','review-overlay.html','server-settings.html')) {
    $html = [IO.File]::ReadAllText((Join-Path $web $entry))
    $files += @([regex]::Matches($html, '(?:src|href)="\./(assets/[^"?]+\.(?:js|css|png|svg|ico))"') | ForEach-Object { $_.Groups[1].Value })
}
$files = @($files | Select-Object -Unique)
foreach ($target in $targets) {
    foreach ($file in $files) {
        $local = Join-Path $target $file
        if (-not (Test-Path -LiteralPath $local -PathType Leaf) -or (Get-FileHash -LiteralPath $local).Hash -ne (Get-FileHash -LiteralPath (Join-Path $web $file)).Hash) {
            throw "客户端界面构建与服务器不一致，请先发布同一份构建产物：$target（$file）"
        }
    }
}
for ($attempt = 0; $attempt -lt 3; $attempt++) {
    $json = [IO.File]::ReadAllText($manifest)
    $version = ($json | ConvertFrom-Json).ConfigurationVersion
    foreach ($target in $targets) {
        $path = Join-Path $target 'client-bootstrap.json'
        if ($VerifyOnly) {
            if ((Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).ConfigurationVersion -ne $version) { throw "客户端配置版本与服务器不一致：$target" }
        } else { [IO.File]::WriteAllText($path, $json, [Text.UTF8Encoding]::new($false)) }
    }
    if ([IO.File]::ReadAllText($manifest) -eq $json) {
        [pscustomobject]@{Version=$version;MatchedClientUiDirectories=$targets.Count;VerifiedFilesPerDirectory=$files.Count;VerifiedOnly=[bool]$VerifyOnly}
        return
    }
}
throw '存在并发发布，服务器版本清单持续变化，请待发布完成后重新核对。'
