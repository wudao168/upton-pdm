# 独立转图电脑部署（PDF / STEP）——按步骤操作

正式发布（非标件BOM+图纸、标准件/电气/非标件正式发布与增补）审批通过后，系统会把 2D 工程图转 PDF、零件与装配转 STEP。
转换位置由 **系统管理 → 设置 → 图纸转换** 决定：可以用 API 服务器本机，也可以交给一台独立的“转图电脑”。改完立即生效，不需要重启服务。

下文里的“服务器”指**运行 UPLM API 的那台机器**（本机部署时就是有这个仓库目录 `F:\codex file\pdm` 的电脑）；“转图电脑”指装着 SolidWorks、专门做转换的另一台电脑。

## 0. 原理（先看一遍再动手）

```
最终审批通过
  → API 服务器把引用树源文件打包 (zip: manifest.json + sources/**)
  → POST http://<转图电脑IP>:5199/convert   （请求头 X-Pdm-Agent-Token）
  → 转图代理解包 → 调本机 SolidWorks 转换程序（静默打开、另存）→ 生成 PDF(2D) / STEP(3D)
  → 打包回传 (zip: result.json + outputs/**)
  → API 落盘：D:\PDM\Vault\<项目>\.release-previews\<发布包Id>\、发布暂存 previews\、发布目录 <Release>\<发布包号>\
```

源文件和结果都走 HTTP，所以**转图电脑不需要映射 PDM 共享目录、不需要连数据库**，只要网络能通。

## 方式A（推荐）：一键安装程序

服务器上已生成安装程序 **`.local\UplmPreviewAgentSetup.exe`**（约 3.3 MB，内嵌代理与转换程序，双击即用）。

1. 把 `UplmPreviewAgentSetup.exe` 拷到**转图电脑**（U盘/共享/远程桌面均可，只有一个文件）。
2. 在转图电脑上双击运行（会自动请求管理员权限）。
3. 在窗口里填写：**PLM 服务器地址**、**管理员账号/密码**（需要有系统设置权限）、**本机代理端口**（默认 5199）、**访问令牌**（点"生成令牌"自动生成）、**转换超时**（默认 30）。

   > **PLM 服务器地址填什么**：填**浏览器里打开 PLM 的那个地址**。局域网一般是 `http://<服务器IP>:5173`（API 在服务器上同时监听 `0.0.0.0:5173`）；
   > `5080` 只绑定在服务器本机回环（`127.0.0.1`），**从别的电脑连不上 5080**。
   > 如果端口填错，安装程序会自动在 5173 / 5080 之间重试并提示实际使用的地址。
4. 点 **一键安装并登记**。程序会自动完成：

   | 步骤 | 自动执行的内容 |
   | --- | --- |
   | 1 | 把转图代理与 SolidWorks 转换程序释放到 `C:\Program Files\UPLM Preview Agent` |
   | 2 | 生成 `PdmPreviewAgent.json`（端口、访问令牌、超时） |
   | 3 | 添加防火墙入站规则 `UPLM Preview Agent`（TCP 端口） |
   | 4 | 注册开机自启计划任务 `UPLM Preview Agent`（以当前账号运行，最高权限） |
   | 5 | 启动代理并自检 `http://127.0.0.1:5199/health` |
   | 6 | 用管理员账号登录 PLM |
   | 7 | 把本机登记为 PLM 的转图服务器（自动填入地址/令牌/超时并切换为"远程转图服务器"），并做一次服务器侧连通性测试 |

   安装程序在动手之前会先探测 PLM 是否可达（不可达直接报错退出，不会在本机留下半成品）；第 7 步如果服务器无法回连本机代理，会自动把 PLM 切回"本机转换"并提示检查网络/防火墙，避免发布被卡住。

5. 看到"安装完成，本机已登记为PLM的转图服务器。"即完成；到 PLM 的 **系统管理 → 设置 → 图纸转换** 可直接看到已填好的地址与令牌。
6. 需要卸载/迁移时，再次运行同一安装程序，点 **卸载本机代理**（会停止代理、删除计划任务与防火墙规则、删除安装目录，并尝试把 PLM 切回"本机转换"）。

批量部署可用静默模式（IT 常用，返回值 0=成功）：

```powershell
UplmPreviewAgentSetup.exe /install /silent /server http://192.168.2.8:5080 /user admin /password 密码 /port 5199 /timeout 30
UplmPreviewAgentSetup.exe /uninstall /silent /server http://192.168.2.8:5080 /user admin /password 密码
```

静默模式日志：`%TEMP%\uplm-preview-agent-setup.log`。

## 方式B（备用）：手工部署

### 步骤 1：在 API 服务器上准备代理包

代理包已经在服务器上打好了：`F:\codex file\pdm\.local\preview-agent\`（12 个文件，约 3.2 MB）：

| 文件 | 作用 |
| --- | --- |
| `Upton.Pdm.PreviewAgent.exe` | 转图代理（net48，Windows 自带运行时） |
| `PdmPreviewAgent.json` | 代理配置（端口/令牌/超时等） |
| `preview-worker\` | SolidWorks 转换程序 + 互操作程序集（必须与 exe 保持相对位置） |
| `Run-PreviewAgent.ps1` | 前台启动（首次测试用） |
| `Install-PreviewAgentTask.ps1` | 注册开机自启计划任务 |

需要重新生成（改了代理代码后）：

```powershell
cd 'F:\codex file\pdm'
& .\.dotnet\dotnet.exe build src\Pdm.PreviewAgent\Pdm.PreviewAgent.csproj -c Release --no-restore
& .\.local\Build-PreviewAgentPackage-20260920.ps1
```

## 步骤 2：复制到转图电脑

在**服务器**上执行（把 `192.168.2.50` 换成转图电脑的 IP / 主机名；`D$` 表示该机的 D 盘共享，需有管理员权限）：

```powershell
robocopy 'F:\codex file\pdm\.local\preview-agent' '\\192.168.2.50\D$\UPLM\preview-agent' /E /R:1 /W:1
```

或者远程桌面到转图电脑，直接从服务器共享/移动硬盘复制整个 `preview-agent` 文件夹到 `D:\UPLM\preview-agent`。

复制完成后，转图电脑上应有：`D:\UPLM\preview-agent\Upton.Pdm.PreviewAgent.exe`、`PdmPreviewAgent.json`、`preview-worker\Upton.Pdm.SolidWorks.PreviewWorker.exe` 等。

## 步骤 3：确认转图电脑的运行条件

- Windows（x64），已安装 **SolidWorks**（建议与设计端同版本），能正常打开图纸、无弹窗、许可可用。
- .NET Framework 4.8（Windows 10/11 自带）。
- 建议用一个**专用 Windows 账号**运行代理（SolidWorks 需要用户配置文件），不要用 SYSTEM。

## 步骤 4：配置代理（转图电脑上）

用记事本/VS Code 打开 `D:\UPLM\preview-agent\PdmPreviewAgent.json`：

```json
{
  "Port": 5199,
  "BindAddress": "0.0.0.0",
  "Token": "换成一段口令",
  "WorkerPath": "",
  "WorkRoot": "",
  "TimeoutMinutes": 30
}
```

| 字段 | 说明 |
| --- | --- |
| `Port` | 代理监听端口，默认 5199，与设置页里填的端口一致 |
| `BindAddress` | 默认 `0.0.0.0`（监听所有网卡）；只想本机测试可改 `127.0.0.1` |
| `Token` | 访问口令，需与 PLM 设置页的“访问令牌”一致；留空 = 不校验（不建议） |
| `WorkerPath` | 留空 = 用 `preview-worker\Upton.Pdm.SolidWorks.PreviewWorker.exe` |
| `WorkRoot` | 留空 = 用系统临时目录；可改成磁盘空间充足的自定义目录 |
| `TimeoutMinutes` | 单批转换超时（分钟），与设置页保持一致 |

## 步骤 5：放通端口并启动

在**转图电脑**上用管理员 PowerShell：

```powershell
# 5.1 放通入站端口（只需一次）
New-NetFirewallRule -DisplayName 'UPLM Preview Agent' -Direction Inbound -Protocol TCP -LocalPort 5199 -Action Allow

# 5.2 先前台运行，确认没有报错（Ctrl+C 结束）
cd D:\UPLM\preview-agent
.\Run-PreviewAgent.ps1
```

前台运行时应看到：

```
转图代理已启动：http://0.0.0.0:5199/
工作目录：C:\Users\<账号>\AppData\Local\Temp\UPTON-PLM\preview-agent；转换程序：D:\UPLM\preview-agent\preview-worker\Upton.Pdm.SolidWorks.PreviewWorker.exe；超时：30 分钟；访问令牌：已设置
```

## 步骤 6：验证代理可达（在服务器上做）

```powershell
Invoke-RestMethod -Uri 'http://192.168.2.50:5199/health' -TimeoutSec 10
```

应返回：

```json
{ "status": "ok", "machine": "SW-PC", "workerPath": "D:\\UPLM\\preview-agent\\preview-worker\\Upton.Pdm.SolidWorks.PreviewWorker.exe", "workerExists": true, "timeoutMinutes": 30 }
```

- 连不上：检查代理是否在运行、IP/端口、防火墙（步骤 5.1）。
- `workerExists=false`：`preview-worker` 目录被移动或缺失，请恢复目录结构或修改 `WorkerPath`。

## 步骤 7：注册开机自启（转图电脑上，可选但推荐）

```powershell
cd D:\UPLM\preview-agent
.\Install-PreviewAgentTask.ps1
```

它会注册名为 `UPLM Preview Agent` 的计划任务（开机启动、以当前账号运行、失败自动重试）并立即启动；查看状态：

```powershell
Get-ScheduledTask -TaskName 'UPLM Preview Agent' | Select-Object TaskName,State
Get-NetTCPConnection -LocalPort 5199 -State Listen
```

## 步骤 8：在 PLM 里选择这台转图服务器

1. 用管理员登录 PLM（网页或客户端）。
2. 打开 **系统管理 → 设置 → 图纸转换**。
3. 转换方式选 **远程转图服务器（推荐）**。
4. 转图服务器地址：`http://192.168.2.50:5199`（不要带结尾斜杠）。
5. 访问令牌：与步骤 4 的 `Token` 一致。
6. 转换超时（分钟）：建议 30（1–120）。
7. 点 **测试连接** —— 会**用你当前页面填写的内容**去探测（不需要先保存），成功提示：“已连接转图服务器 SW-PC，SolidWorks转换程序就绪。”
8. 点 **保存图纸转换设置**，立即生效，无需重启服务。

> 想改回服务器本机转换：转换方式选“本机（API服务器上的SolidWorks）”保存即可，此时 API 服务器必须装 SolidWorks，转换程序需位于 `<API目录>\preview-worker\`。

## 步骤 9：首次发布验证

1. 提交并走完一个“非标件BOM+图纸·正式发布”（最后一位主管点通过）。
2. 观察转图电脑的控制台/计划任务历史，应出现：
   ```
   [HH:mm:ss] 收到转换任务 N 项，来源 192.168.2.8:xxxxx
   [HH:mm:ss] 转换完成 N 项
   ```
3. 到服务器上检查三处产物：
   - `D:\PDM\Release\<项目号>\<发布包号>\previews\`（每个 Drawing 一个 `.pdf`、每个 Part/Assembly 一个 `.step`）
   - `D:\PDM\Vault\<项目号>\.release-previews\<发布包Id>\`（同内容）
   - 网页/客户端里图档版本的 PDF / STEP 下载可用

## 运行与失败处理

- 转换期间审批请求会保持等待（等待上限 = 设置里的超时时间），属正常现象。
- 失败（连不上 / 代理报错 / SolidWorks 打开或另存失败 / 超时）→ 发布中止，发布包状态变 **发布失败** 并在页面显示原因；修正后点 **重新提交审批** 会重走审批，并在最后一步重试转换。
- 每次转换前会校验源文件大小与 SHA256；发布包创建后源图档被改会直接中止并提示重建发布包。
- 转图代理只需被 API 服务器访问（入站方向），自身不主动外连。

## 常见排查

| 现象 | 处理 |
| --- | --- |
| 安装程序提示"无法访问PLM服务器…连接超时/拒绝连接" | 地址填成了 `:5080`（只对本机开放）。改成浏览器里打开PLM的地址，局域网一般是 `http://<服务器IP>:5173`；确认服务器防火墙放通 5173 |
| 安装程序提示"PLM服务器无法访问本机代理" | 服务器到转图电脑的 5199 端口不通：检查转图电脑防火墙（安装程序会自动加规则）、网段/路由、代理是否在运行；处理后重新运行安装程序 |
| 测试连接提示无法连接 | 代理是否在运行、端口/防火墙、IP 是否正确（先用步骤 6 的 health 验证） |
| 提示 401 令牌不正确 | 设置页令牌与 `PdmPreviewAgent.json` 的 `Token` 不一致（或含空格） |
| 提示未找到转换程序 | `preview-worker\` 缺失，或 `WorkerPath` 配错 |
| 打开/另存失败或超时 | 在转图电脑上手工打开该图纸确认正常；确认无许可/弹窗/文件被占用；必要时调大超时 |
| 提示“发布引用树包含重名源文件” | 项目引用树里有同名文件，需先在设计端改名 |

## 相关文件

- 代理源码：`src/Pdm.PreviewAgent/`；转换程序：`src/Pdm.SolidWorks.PreviewWorker/`
- 一键安装程序源码：`src/Pdm.PreviewAgent.Setup/`（WinForms + 内嵌载荷）
- API 侧转换与协议：`src/Pdm.Infrastructure/SolidWorksServerPreviewConverter.cs`
- 设置项：`PreviewConversionSettings`（存于 `pdm_system_setting` 的 `preview_conversion` 键）
- 代理打包脚本：`.local/Build-PreviewAgentPackage-20260920.ps1`
- 安装程序打包脚本：`.local/Build-PreviewAgentSetup-20260920.ps1`（生成 `.local/UplmPreviewAgentSetup.exe`）
