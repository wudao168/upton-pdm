[CmdletBinding()]
param([string]$Version = '2026.09.12-test3')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root '.artifacts'
$clientSource = Join-Path $artifacts "UPLM-Client-Setup-$Version.exe"
$releaseSource = Join-Path $artifacts "uplm-client-test-$Version\server-publish"
$webUiSource = Join-Path $artifacts "uplm-client-test-$Version\payload\desktop\ui"
$stage = Join-Path $artifacts "uplm-client-plugin-upgrade-$Version"
$outputZip = Join-Path $artifacts "UPLM-Client-Plugin-Upgrade-$Version.zip"
foreach ($required in @($clientSource, $releaseSource, (Join-Path $webUiSource 'index.html'), (Join-Path $webUiSource 'assets'))) {
    if (-not (Test-Path -LiteralPath $required)) { throw "缺少统一升级包输入：$required" }
}

if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
$serverDirectory = Join-Path $stage 'Server'
$clientDirectory = Join-Path $stage 'Client'
New-Item -ItemType Directory -Path $serverDirectory,$clientDirectory -Force | Out-Null
Copy-Item -LiteralPath $clientSource -Destination $clientDirectory
Copy-Item -LiteralPath $releaseSource -Destination (Join-Path $serverDirectory 'server-publish') -Recurse
Copy-Item -LiteralPath $webUiSource -Destination (Join-Path $serverDirectory 'web-ui') -Recurse

$utf8Bom = [Text.UTF8Encoding]::new($true)
foreach ($script in @(
    @{ Source = 'Install-ClientPluginUpgradeOnServer.ps1'; Target = (Join-Path $serverDirectory 'Install-ClientPluginUpgradeOnServer.ps1') },
    @{ Source = 'Verify-ClientPluginUpgradePackage.ps1'; Target = (Join-Path $stage 'Verify-ClientPluginUpgradePackage.ps1') }
)) {
    $sourcePath = Join-Path $PSScriptRoot $script.Source
    [IO.File]::WriteAllText($script.Target, (Get-Content -LiteralPath $sourcePath -Raw -Encoding UTF8), $utf8Bom)
}

$readme = @"
UPLM 客户端和 SolidWorks 插件统一升级包
版本：$Version

范围：
1. 发布桌面客户端自动升级包。
2. 发布 SolidWorks 插件自动升级包。
3. 同步带版本显示的服务器 Web UI。
4. 提供修正后的客户端完整安装 EXE。
5. 不修改 MySQL、API 程序或业务数据。

服务器操作（管理员 PowerShell）：
Set-Location 'C:\UPLM\upgrade-$Version'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File '.\Verify-ClientPluginUpgradePackage.ps1'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File '.\Server\Install-ClientPluginUpgradeOnServer.ps1' -InstallRoot 'C:\UPLM\pdm'
`$result = Invoke-RestMethod 'http://127.0.0.1:5173/client-bootstrap.json'
`$result.ConfigurationVersion

客户端操作：
1. 正常退出 SolidWorks 和 UPLM；安装程序不会强制关闭它们。
2. 运行 Client\UPLM-Client-Setup-$Version.exe。
3. 以管理员身份安装。
4. 浏览器访问 http://10.7.7.62:5173/（不要使用 https）。

如果客户端安装失败，日志位于：C:\ProgramData\UPLM\logs
"@
[IO.File]::WriteAllText((Join-Path $stage '升级说明.txt'), $readme, $utf8Bom)

$files = Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object { $_.Name -ne 'manifest.json' }
$manifest = [ordered]@{
    version = $Version
    createdAt = [DateTimeOffset]::Now.ToString('O')
    scope = 'desktop-client-and-solidworks-addin'
    serverBaseUrl = 'http://10.7.7.62:5173/'
    files = @($files | ForEach-Object {
        [ordered]@{
            path = $_.FullName.Substring($stage.Length + 1)
            length = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
}
[IO.File]::WriteAllText((Join-Path $stage 'manifest.json'), ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))

& (Join-Path $stage 'Verify-ClientPluginUpgradePackage.ps1') -PackageRoot $stage
if (Test-Path -LiteralPath $outputZip) { Remove-Item -LiteralPath $outputZip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $outputZip -CompressionLevel Optimal

[pscustomobject]@{
    Package = $outputZip
    Length = (Get-Item -LiteralPath $outputZip).Length
    Sha256 = (Get-FileHash -LiteralPath $outputZip -Algorithm SHA256).Hash
} | Format-List
