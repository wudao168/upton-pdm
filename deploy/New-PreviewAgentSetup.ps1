[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $root '.dotnet\dotnet.exe'
$version = '1.2.3'
$stage = Join-Path $root ".artifacts\preview-agent-setup-$version"
$payload = Join-Path $stage 'payload'
$output = Join-Path $root "output\UPLM-PreviewAgent-$version"
foreach ($path in @($stage, $payload, $output)) {
    if (-not [IO.Path]::GetFullPath($path).StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '安装包目录越界。' }
    New-Item -ItemType Directory -Path $path -Force | Out-Null
}
foreach ($component in @('agent','worker','setup')) { New-Item -ItemType Directory -Path (Join-Path $stage $component) -Force | Out-Null }
& $dotnet build (Join-Path $root 'src\Pdm.PreviewAgent\Pdm.PreviewAgent.csproj') -c Release --no-restore -o (Join-Path $stage 'agent') -v quiet
if ($LASTEXITCODE -ne 0) { throw '前台代理编译失败。' }
& $dotnet build (Join-Path $root 'src\Pdm.SolidWorks.PreviewWorker\Pdm.SolidWorks.PreviewWorker.csproj') -c Release --no-restore -o (Join-Path $stage 'worker') -v quiet
if ($LASTEXITCODE -ne 0) { throw '转换程序编译失败。' }
foreach ($component in @('agent','preview-worker','upgrade')) { New-Item -ItemType Directory -Path (Join-Path $payload $component) -Force | Out-Null }
$files = [Collections.Generic.List[object]]::new()
foreach ($name in @('Upton.Pdm.PreviewAgent.exe','Upton.Pdm.PreviewAgent.exe.config')) {
    Copy-Item -LiteralPath (Join-Path (Join-Path $stage 'agent') $name) -Destination (Join-Path $payload 'agent') -Force
    $files.Add([pscustomobject]@{Path=$name;Sha256=(Get-FileHash -LiteralPath (Join-Path (Join-Path $payload 'agent') $name)).Hash})
}
foreach ($name in @('Upton.Pdm.SolidWorks.PreviewWorker.exe','Upton.Pdm.SolidWorks.PreviewWorker.exe.config','QRCoder.dll')) {
    Copy-Item -LiteralPath (Join-Path (Join-Path $stage 'worker') $name) -Destination (Join-Path $payload 'preview-worker') -Force
}
foreach ($name in @('SolidWorks.Interop.sldworks.dll','SolidWorks.Interop.swconst.dll')) {
    Copy-Item -LiteralPath (Join-Path 'C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS' $name) -Destination (Join-Path $payload 'preview-worker') -Force
}
foreach ($file in Get-ChildItem (Join-Path $payload 'preview-worker') -File) { $files.Add([pscustomobject]@{Path=('preview-worker\'+$file.Name);Sha256=(Get-FileHash -LiteralPath $file.FullName).Hash}) }
$agentFiles=@(Get-ChildItem (Join-Path $payload 'agent') -File)
$workerFiles=@(Get-ChildItem (Join-Path $payload 'preview-worker') -File)
if ($agentFiles.Count -ne 2 -or $workerFiles.Count -ne 5) { throw '载荷包含预期之外的文件，未生成安装包。' }
foreach ($path in @((Join-Path $payload 'agent\Upton.Pdm.PreviewAgent.exe'),(Join-Path $payload 'preview-worker\Upton.Pdm.SolidWorks.PreviewWorker.exe'))) {
    if ((Get-Item -LiteralPath $path).VersionInfo.FileVersion -ne "$version.0") { throw '代理与转换程序版本不一致。' }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Update-PreviewAgent.ps1') -Destination (Join-Path $payload 'upgrade\Update-PreviewAgent.ps1') -Force
@{Version="$version.0";Files=@($files)} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $payload 'upgrade\files.json') -Encoding UTF8
& $dotnet build (Join-Path $root 'src\Pdm.PreviewAgent.Setup\Pdm.PreviewAgent.Setup.csproj') -c Release --no-restore `
    "-p:PreviewAgentPayloadRoot=$payload" -o (Join-Path $stage 'setup') -v quiet
if ($LASTEXITCODE -ne 0) { throw '完整安装程序编译失败。' }
$setup = Join-Path $output "UPLM-PreviewAgent-Setup-$version.exe"
Copy-Item -LiteralPath (Join-Path $stage 'setup\Upton.Pdm.PreviewAgent.Setup.exe') -Destination $setup -Force
@'
UPLM 转图服务代理 1.2.3 完整安装程序

请将安装EXE复制到其他需要转图、且已安装SolidWorks的Windows服务器上运行。
本EXE已包含前台代理、转换程序、互操作程序集及全部应用依赖，不需要另行复制payload文件夹。

默认安装目录：C:\Program Files\UPLM Preview Agent
主程序：Upton.Pdm.PreviewAgent.exe

新服务器：双击安装EXE，填写PLM服务器地址、管理员账号密码和本机代理端口，点击“一键安装并登记”。
已有服务器：选择原安装目录，点击“升级现有代理（保留配置）”，无需重复登录PLM；原地址、端口和令牌保留，失败自动回退。

本次修复：专用转图机自动记录并关闭残留的SolidWorks，独立转图会话不再被旧会话阻塞；转换工作进程异常退出时自动回收其新建的SolidWorks。
1.2.3 修复日志文件无法创建的启动弹窗：每次启动使用独立可写日志目录，启动完成恢复原设置；启动失败不再对同批每个文件重复启动。专用机升级时可恢复已卡住超过一分钟的1.2.2启动请求，正常转图任务仍须完成后升级。
安装器默认勾选“专用转图机”，适用于PC-07等不用于人工设计的电脑；普通设计电脑请取消勾选。
日志会自动脱敏、压缩上传到PLM“客户端日志→转图代理”，断网后补传；升级后会补传近7天处理历史。服务端需同时更新到支持转图代理日志的版本。

安装后直接显示带“首页、处理记录、代理设置”的前台主窗口，可以收起到Windows右下角托盘，双击图标恢复。
安装窗口完成后退出，代理自己负责显示状态和托盘，不再另起独立管理窗口。
若有正在处理或排队的任务，请等任务完成后再升级；暂停接收但已空闲时可以升级。

图档原存档不会因代理界面更新被改写。目标转图服务器安装完成后，请重新发起一批转图并确认实际结果。
'@ | Set-Content -LiteralPath (Join-Path $output '安装说明.txt') -Encoding UTF8
[pscustomobject]@{Version="$version.0";Setup=$setup;InstallDirectory='C:\Program Files\UPLM Preview Agent';PayloadFiles=$files.Count;SizeBytes=(Get-Item $setup).Length;Sha256=(Get-FileHash -LiteralPath $setup).Hash} |
    ConvertTo-Json | Tee-Object -FilePath (Join-Path $stage 'package.json')
