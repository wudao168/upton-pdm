# 一期本地部署与端口隔离

## 固定隔离边界

PDM 与现有 CRM 完全独立：

| 资源 | PDM | CRM 保留 |
| --- | --- | --- |
| MySQL 监听端口 | `3308` | `3306` |
| API 监听端口 | `5080` | `8080` |
| UI 开发端口 | `5173` | `5174` |
| 数据库 | `pdm` | 不读取、不修改 |
| MySQL 运行时/服务 | `.runtime\mysql-8.4.11-winx64` / `UptonPdmMySQL` | 不复用 |
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

## 独立 MySQL

正式本地部署不依赖 Docker。`Prepare-LocalDeployment.ps1` 准备项目私有的 MySQL 8.4 运行时，`Install-LocalServices.ps1` 将其安装为 `UptonPdmMySQL`，配置文件、数据、日志和临时目录都位于 `.local\mysql`。它只监听 `127.0.0.1:3308`，不得改为 CRM 的 `3306`。

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

客户端通过 WebView2 访问内置静态资源，并只连接 `http://127.0.0.1:5080`。目标电脑需安装 Microsoft Edge WebView2 Runtime 和 eDrawings Professional。选择图档并点击“在客户端内预览”后，eDrawings ActiveX 直接显示在主页面图纸预览区，不会打开独立预览窗口。

## 日常图档操作

1. 在 SolidWorks 插件中登录并选择项目。
2. 选中未入库图档后点击“获取权限”，系统先登记图档再取得独占编辑权；已入库图档直接取得独占编辑权。
3. 在 SolidWorks 中保存图档，再点击“提交存档”，填写变更说明。首次生成 `W1`，之后生成 `W2/W3/...`；未保存、被其他进程占用或引用缺失时不会推进版本。
4. 无实际文件变化时可结束编辑而不产生新版本；也可“放弃编辑”释放权限，系统不会覆盖本地文件。
5. 在“版本记录”页签可打开当前版本、从独立只读临时目录打开历史版本，或选择两个版本唤起 Windows 客户端对比。
6. 从历史版本恢复只创建新的工作版本，不修改历史。例如当前最新为 `W3` 时恢复 `W1` 会生成 `W4`；正式 `A` 之后修改生成 `A-W1`，再次审批发布为 `B`。

## BOM、审批与生产发包

1. Windows 客户端的机械 BOM 读取结构快照；电气 BOM 可手工维护或导入标准 XLSX，并可导出。
2. 创建发布包后依次提交工艺审核和批准。驳回后修改并重新提交，历史审批记录保留。
3. 最终批准成功后，系统才把 PDF、DWG、两类 BOM、清单、审批记录和 SHA-256 文件原子投放到项目 `ReleaseLocation`。
4. 任一文件准备、哈希、审批或投放失败时，发布包不进入已发布状态，生产目录不会出现半成品。

## 全量备份与恢复演练

备份包含 `pdm` 数据库、vault、release、表数量和逐文件 SHA-256 清单，只允许写入 `.local\backup`：

```powershell
Set-Location 'F:\codex file\pdm'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\deploy\Backup-LocalPdm.ps1
```

恢复验收必须使用以 `_qa` 结尾的隔离数据库，不会覆盖正式 `pdm`：

```powershell
$backup = Get-ChildItem .\.local\backup -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName 'manifest.json') } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\deploy\Test-BackupRestore.ps1 `
    -BackupPath $backup.FullName `
    -Database pdm_restore_qa
```

脚本会核对数据库迁移、关键表行数以及全部恢复文件的长度和 SHA-256；任一不一致即失败。

## 一期自动验收入口

```powershell
# Release 构建和 .NET 测试
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\deploy\Build-Phase1.ps1 -Configuration Release

# 前端单元与 125%/150% 布局回归
pnpm.cmd --dir .\src\pdm-ui test
pnpm.cmd --dir .\src\pdm-ui test:e2e

# 隔离 API、版本、审批、发包和断点续传
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\deploy\Test-Phase1Api.ps1 `
    -Database pdm_phase1_qa -ApiPort 5180
```

SolidWorks 真实验收必须关闭生产编辑会话，仅对指定装配执行只读打开；当前结果见 `docs\acceptance\phase-1-checklist.md`。

## 文件存放地点

项目的 `VaultLocation` 与 `ReleaseLocation` 支持本地绝对路径和 UNC 共享路径，例如：

```text
D:\PDM\Vault\PRJ-2026-018
\\nas01\engineering\pdm\release\PRJ-2026-018
```

系统拒绝磁盘根目录、相对路径以及逃逸出项目目录的路径。发布包使用临时目录完成后原子切换到目标目录，避免生产部门读取到半成品。
