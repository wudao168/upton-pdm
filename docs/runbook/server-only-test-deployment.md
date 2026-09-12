# UPLM 测试服务器部署（不安装 SolidWorks）

本流程适用于只有 `C:` 盘的 Windows Server 2016 测试服务器。它只安装 MySQL、UPLM API 和网页；不安装工程客户端、SolidWorks 插件或预览转换程序。

## 前提

1. 以管理员身份安装 `VC_redist.x64.exe` 和 `dotnet-hosting-10.0.11-win.exe`，重启并确认 `C:\Program Files\dotnet\dotnet.exe --list-runtimes` 显示 10.0.11。
2. 将完整仓库（包括隐藏的 `.dotnet` 目录）复制到 `C:\UPLM\pdm`。
3. 将离线 MySQL ZIP 复制到 `C:\UPLM\pdm\.runtime\downloads\mysql-8.4.11-winx64.zip`。
4. 此脚本会构建前端和 API，所以服务器还需要可用的 `node`、`pnpm.cmd`、NuGet 和 npm 依赖源。隔离环境应由发布机生成部署产物后再安装，不能直接运行本脚本。

## 首次安装

普通 PowerShell：

```powershell
Set-Location 'C:\UPLM\pdm'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\deploy\Prepare-LocalDeployment.ps1 -ServerOnly -LanBaseUrl 'http://服务器内网IP:5173'
```

管理员 PowerShell：

```powershell
Set-Location 'C:\UPLM\pdm'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\deploy\Install-LocalServices.ps1 -ServerOnly
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\deploy\Get-LocalStatus.ps1
```

验收地址为 `http://服务器内网IP:5173/`，健康检查为 `http://127.0.0.1:5080/health`。MySQL 仅监听 `127.0.0.1:3308`。

## 限制

- 最终审批后的 STEP/PDF 自动转换在本服务器不可用；必须待独立转换机服务部署完成后启用。
- 此测试模式仍使用 HTTP 5173。正式多公司使用前必须改为 VPN 内 HTTPS 入口，并限制防火墙到 VPN 网段。
