# UPTON PDM

面向非标自动化设计团队的独立PDM。一期交付范围包括SolidWorks插件、Windows工程客户端和它们依赖的PDM API、MySQL数据模型与文件库。

## 端口与系统隔离

| 服务 | PDM默认端口 | CRM保留端口 |
| --- | ---: | ---: |
| MySQL | 3308 | 3306 |
| API | 5080 | 8080 |
| 客户端UI开发服务 | 5173 | 5174 |

PDM使用独立数据库`pdm`、独立服务名和独立存储目录。不要将PDM配置指向CRM数据库或CRM文件目录。

## 本地构建

```powershell
$dotnet = Join-Path $PWD '.dotnet\dotnet.exe'
& $dotnet restore Pdm.slnx
& $dotnet build Pdm.slnx --no-restore
pnpm.cmd install
pnpm.cmd ui:build
```

开发环境API默认监听`http://127.0.0.1:5080`，客户端UI默认监听`http://127.0.0.1:5173`。生产环境必须通过环境变量提供数据库密码、管理员初始密码和JWT签名密钥。

## 目录

- `src/Pdm.Api`：认证、项目、图档、BOM、审批、发包与审计API。
- `src/Pdm.Infrastructure`：MySQL持久化、迁移、文件存储与发布包生成。
- `src/Pdm.SolidWorks.Addin`：SolidWorks 2022–2025插件。
- `src/Pdm.Desktop`：WPF + WebView2客户端壳层。
- `src/pdm-ui`：Vue 3客户端界面。
- `docs/acceptance`：一期验收说明。
