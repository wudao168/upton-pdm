# 一期本地部署与端口隔离

## 固定隔离边界

PDM 与现有 CRM 完全独立：

| 资源 | PDM | CRM 保留 |
| --- | --- | --- |
| MySQL 监听端口 | `3308` | `3306` |
| API 监听端口 | `5080` | `8080` |
| UI 开发端口 | `5173` | `5174` |
| 数据库 | `pdm` | 不读取、不修改 |
| MySQL 容器/卷 | `upton-pdm-mysql` / `upton_pdm_mysql_data` | 不复用 |
| 文件库 | 项目指定的本地绝对路径或 UNC 路径 | 不复用 CRM 文件目录 |

任何端口已被占用时必须停止部署并排查，禁止临时改用 CRM 端口。

## 当前电脑的一键本地部署

本机采用独立的 Windows 服务部署，不依赖 CRM 的 MySQL，也不需要 Docker：

```powershell
Set-Location 'F:\codex file\pdm'

# 普通 PowerShell：准备 MySQL、构建并发布一期产物
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\deploy\Prepare-LocalDeployment.ps1

# 管理员 PowerShell：安装两个自动启动服务并注册 SolidWorks 插件
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\deploy\Install-LocalServices.ps1

# 查看服务、端口、API 健康状态和插件注册状态
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\deploy\Get-LocalStatus.ps1
```

安装后的固定资源：

- MySQL 服务：`UptonPdmMySQL`，只监听 `127.0.0.1:3308`。
- API 服务：`UptonPdmApi`，只监听 `127.0.0.1:5080`，并依赖 MySQL 服务。
- 客户端：`.local\client\Upton.Pdm.Desktop.exe`，同时创建桌面快捷方式 `UPTON PDM.lnk`。
- SolidWorks 插件：`.local\solidworks-addin\Upton.Pdm.SolidWorks.Addin.dll`。
- 首次管理员：用户名 `admin`，随机密码保存在 `.local\secrets\pdm-secrets.json`；该文件受本机用户 ACL 保护，禁止复制到代码仓库。
- 部署结果：`.local\installation-status.json`；准备清单：`.local\deployment-receipt.json`。

插件注册时会从本机现有 SolidWorks 安装目录复制其 3 个 Interop 依赖到插件私有目录；不会修改 SolidWorks 安装目录。重新打开 SolidWorks 后，在“工具 > 插件”中确认 `UPTON PDM` 已勾选。

## 构建一期产物

```powershell
Set-Location 'F:\codex file\pdm'
powershell.exe -ExecutionPolicy Bypass -File .\deploy\Build-Phase1.ps1 -Configuration Release
```

关键产物：

- `src\Pdm.Desktop\bin\Release\net48\Upton.Pdm.Desktop.exe`
- `src\Pdm.SolidWorks.Addin\bin\Release\net48\Upton.Pdm.SolidWorks.Addin.dll`
- `src\Pdm.Api\bin\Release\net10.0\Pdm.Api.dll`

## 启动独立 MySQL

主机需要 Docker Desktop 或等价的 Docker Engine。不要连接 CRM 的 `3306` 实例。

```powershell
Set-Location 'F:\codex file\pdm'
Copy-Item -LiteralPath .env.example -Destination .env
# 编辑 .env，替换为真实的 PDM 专用密码；不要提交 .env。
docker compose up -d mysql
docker compose ps
```

`compose.yaml` 只将容器 MySQL 的 `3306` 映射到主机 `127.0.0.1:3308`。

## 启动 API

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Production'
$env:PDM_DB_PASSWORD = '<PDM专用数据库密码>'
$env:PDM_BOOTSTRAP_ADMIN_PASSWORD = '<首次管理员密码>'
$env:PDM_JWT_SIGNING_KEY = '<至少32字符的随机密钥>'
Set-Location 'F:\codex file\pdm\src\Pdm.Api\bin\Release\net10.0'
& 'F:\codex file\pdm\.dotnet\dotnet.exe' .\Pdm.Api.dll
```

验收：

```powershell
Invoke-RestMethod -Uri 'http://127.0.0.1:5080/health'
```

响应中的 `apiPort` 必须为 `5080`，`mysqlPort` 必须为 `3308`。

## 安装 SolidWorks 插件

关闭所有 SolidWorks 进程，以管理员 PowerShell 执行：

```powershell
Set-Location 'F:\codex file\pdm'
powershell.exe -ExecutionPolicy Bypass -File .\deploy\Register-SolidWorksAddin.ps1 -Configuration Release
```

重新打开 SolidWorks，在“工具 > 插件”确认 `UPTON PDM` 已启用。卸载时使用 `Unregister-SolidWorksAddin.ps1`。

## 启动 Windows 客户端

```powershell
& 'F:\codex file\pdm\src\Pdm.Desktop\bin\Release\net48\Upton.Pdm.Desktop.exe'
```

客户端通过 WebView2 访问内置静态资源，并只连接 `http://127.0.0.1:5080`。目标电脑需安装 Microsoft Edge WebView2 Runtime。

## 文件存放地点

项目的 `VaultLocation` 与 `ReleaseLocation` 支持本地绝对路径和 UNC 共享路径，例如：

```text
D:\PDM\Vault\PRJ-2026-018
\\nas01\engineering\pdm\release\PRJ-2026-018
```

系统拒绝磁盘根目录、相对路径以及逃逸出项目目录的路径。发布包使用临时目录完成后原子切换到目标目录，避免生产部门读取到半成品。
