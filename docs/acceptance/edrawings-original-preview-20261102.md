# eDrawings 原图预览及性能验收

## 规则和范围

用户确认优先直接读取原图，减少预览前置环节。客户端不再为普通装配预览强制调用 `/assembly-preview`；继续通过原有权限检查获取不可变存档版本，校验 SHA-256，存入独立只读预览缓存。

原图显示父装配该次存档保存的显示数据，不自动把后续子件变化重建进父装配；打开最新、编辑、受控引用和正式发布流程未在本次改动中调整。虚拟组件保持嵌入父装配，不要求独立登记或导出。

官方依据：

- [PDM Preview Tab](https://help.solidworks.com/2022/english/EnterprisePDM/fileexplorer/r_File_Preview_Tab.htm)：使用 eDrawings 显示 SOLIDWORKS 零件和装配。
- [File Type Properties](https://help.solidworks.com/2023/English/EnterprisePDM/Admin/IDD_FILE_TYPE_PROPS.htm)：预览是否缓存引用可配置，支持相应显示数据的预览程序无需先获得完整引用树。
- [eDrawings 加载完成事件](https://help.solidworks.com/2025/English/api/emodelapi/eDrawings.Interop.EModelViewControl~eDrawings.Interop.EModelViewControl._IEModelViewControlEvents_OnFinishedLoadingDocumentEventHandler.html)：OpenDoc 可在加载完成前返回，后续模型操作须等待完成事件。

“先重建最新引用组合再导出 EASM”是 UPLM 的自定义原生预览前置步骤，并非上述 PDM 原图预览的必要条件。日志中原有失败发生于该步骤，尚未进入原生加载。

## 修复

- 移除客户端预览路径中的强制 EASM 生成；装配、零件和工程图使用原始存档文件。
- 文件校验移到后台，下载和排队请求可取消，过期选择不覆盖当前预览；取消后清理本次下载暂存文件。
- 文件版本、内容校验及服务器批注均未变化时，复用仍在控件中加载的场景；保留视角和批注状态，显式重新加载仍会关闭并重开。
- 原生加载超时从 OpenDoc 接受文件后开始计算，避免同步初始化阶段挤占异步加载预算。失败后的迟到完成事件不再把状态改成成功。
- 在诊断日志分别记录元数据、文件、批注、原生加载和总耗时。

## 实际样本与结果

- P700022.10.SLDASM，4,204,103 字节，SHA-256 校验通过，直接加载并导出图像。
- HMI组件-7寸.SLDASM，14,462,031 字节；HMI组件-9寸10寸.SLDASM，17,652,024 字节；均直接加载并导出图像。
- 10寸屏 MT8106iE.SLDPRT 及 FGB280X40X3.3-UPTON01.SLDDRW，加上故意损坏的工程图，执行 15 次加载、失败和恢复检查，全部通过。
- 隔离客户端完整请求链：有效缓存不重复下载、同长度损坏缓存重新校验下载、历史版本参数保留、旧请求取消、最新选择生效；装配转图请求数为 0。
- 基础直接原图版本的同场景重复解析约 9.326 秒；场景复用后隔离请求测试约 0.078 秒，切换取消回归约 0.060 秒。此项使用模拟网络验证客户端开销。
- 真实本机服务：已有只读文件缓存、首次加载引擎场景约 9.463 秒，随后重复预览约 0.596 秒，文件再次下载数为 0，装配转图请求数为 0。首次图形解析仍受模型复杂度及 eDrawings 初始化影响，不承诺即时完成。
- 客户端构建通过。隔离副本的实际 MainWindow、审核 WebView2、五种主题和延迟状态同步通过；界面读取当时服务器同版构建。

证据目录：`.local/assembly-original-qa`。关键结果：`native-flow-final.json`、`native-live-result.json`、`native-ui-final.json`、`baseline-native` 和 `more-assemblies` 图像。

首轮原图预览修复已在用户确认保存并允许重启 UPLM 后部署；API 权限读取优化部署版本为 `2026.11.02.165256`。SolidWorks 未关闭。

## 大装配首次加载和界面响应优化

本轮继续保留原图、固定版本、完整 SHA-256、只读缓存及实时权限规则。没有调整 CAD 文件、子件引用、模型树、测量或批注功能。

- 桌面客户端每个服务器的连接上限从实际运行时默认 2 条调整为 8 条，避免预览与后台文件请求争用少量连接。SolidWorks 插件原有连接设置未改动。
- 下载时在后台流内计算 SHA-256 和长度，按 256KB 合并写盘，减少 TLS 小块读取带来的频繁写入；异常、截断及取消仍拒绝提交缓存文件。已有缓存仍完整读取校验。
- 批注请求与文件准备并行，校验同一版本批注后再进入可审核状态。
- eDrawings 在专用 STA 线程内创建和操作。完整原生操作串行化，避免 COM 关闭过程泵消息时与下一次打开重入；损坏缓存替换前等待原生关闭完成。
- 嵌入窗口与容器保持相同 DPI 上下文，在显示后绑定并核对父窗口；大小和主题更新发送到所属线程。主窗口不等待原生解析完成才处理消息。

专用线程和窗口托管是 UPLM 的实现，未将其表述为 PDM 原生架构。官方依据还包括 [WinForms 控件线程规则](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/controls/how-to-make-thread-safe-calls) 和 [SetParent 的窗口样式及 DPI 规则](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setparent)。模型操作仍等待上述 eDrawings 完成事件。

### 本机结果

- 真实样本 `CW0020002.00.SLDASM`，57,888,158 字节。使用唯一测试缓存文件触发完整下载，测试后仅清理该文件，不清除用户原有缓存。
- 同环境旧客户端冷加载对照：总计 13.687 秒，主界面最大消息间隔 9.686 秒。最终线程方案的 HTTPS 冷下载和预览样本约 15.013 秒，重复预览约 0.642 秒，主界面最大消息间隔 0.964 秒。
- 测试期间总耗时受系统磁盘、文件页缓存和图形初始化状态影响明显，观察到约 13–43 秒；不据此宣称固定的总耗时加速比例。主要可重复改善是将长解析从主窗口线程分离，以及减少串行准备和重复读写。
- 58MB 解析中隐藏预览耗时低于 1 毫秒，迟到事件未恢复已关闭预览，随后可重新打开。最大主窗口消息间隔约 0.750 秒。
- 真实 HTTP 和 HTTPS 原图获取、装配体、零件、工程图、完整模式、测量、五种主题、父窗口关系、缩放尺寸及图形导出通过。图像已人工检查。
- 20MB 传输测试的完整内容、错误 SHA、截断和传输中取消四项通过。缓存破坏修复、历史版本、旧请求取消和零次 EASM 请求通过。
- 最终交付构建追加缓存替换等待后，缓存及完整界面回归再次通过；同一模型测试主窗口最大消息间隔约 0.120 秒。审核页面检查等待真实页面就绪，并输出 URL、readyState 和脚本列表，不仅按导航 URL 判定成功。

### 部署

本机配置版本 `2026.11.02.180012`，原生程序 SHA-256 为 `9535DA93BE7872B3E9AA50A351A623C92C33D0C0775B36B52216BDD987BD5257`。`.local/client` 与已安装客户端副本一致；已执行 `Sync-LocalUiManifest.ps1` 和 `-VerifyOnly`，4 处客户端界面副本每处 57 个关联文件一致，API 健康。未生成安装或全量升级包，未关闭 SolidWorks。

主要证据：`.local/large-preview-deployment.json`、`.local/assembly-original-qa/large-old-cold-live-result.json`、`large-verified-https.json`、`threaded-verified-interrupt.json`、`threaded-verified-part.json`、`threaded-verified-drawing.json`、`large-delivery-cache.json`，以及对应模型 BMP。

发布后复核通过：HTTP/HTTPS 主界面和审核浮层 HTML、脚本及版本清单一致；与已安装程序同哈希的真实 MainWindow 显示 `V2026.11.02.180012`，审核浮层使用相同配置版本，五种主题及延迟状态同步通过。证据为 `.local/large-preview-online-result.json` 与 `.local/assembly-original-qa/large-published-ui.json`。

## 原图首开继续优化（21:27 本机发布）

版本 `2026.11.02.212736`，程序 SHA-256 `4F0EAAC0151FD8851C3F036F540C538C452A629F5BD86CA19B8FFC367BB891CA`。本轮保留原图路径：空预览引擎在文件下载及校验期间初始化，完整原生界面的初始化按控件复用；实际打开前绑定当前容器，防止隐藏预热期间容器变化留下旧父窗口。

同一 58MB 样本真实 HTTPS 冷缓存：本轮已安装版对照 12.090 秒，保留的初始化重叠方案 10.681 秒；重复预览 0.686 秒，最大主窗口消息间隔 0.082 秒。此处冷缓存指独立测试文件触发完整下载，不清除操作系统文件页缓存。不同轮次耗时仍有波动。

本轮试验及取舍：

- 四路分段传输本身约 1.6–1.7 秒，单连接约 2.0–3.1 秒；但完整预览候选约 13.9 秒，未优于同环境已安装版约 12.1 秒，因此删除分段生产实现。实验性分段安全检查六项通过，但不构成上线依据。
- 原生完整界面与延后工具栏试验均约 8–9 秒，累计 CPU 约 9–10 秒，未保留延后工具栏方案。
- 隔离导出显示缓存试验：原图解析 9.034 秒，导出 6.716 秒，缓存加载 4.767 秒；58MB 变为约 27MB，组件数量均为 387，测量均可用。该派生文件只在实验目录，未进入生产；用户随后明确选择“仅继续原图优化”，不采用后台显示缓存，不能把该结果当作当前原图预览速度。

传输完整性四项、损坏缓存修复、固定历史版本、旧请求取消、加载中关闭重开、零件、工程图、完整界面、测量、五种主题、缩放尺寸及原生图形导出通过；审核浮层和延迟状态同步通过。原图和项目数据未修改，没有调整本机 eDrawings 全局选项，没有生成安装包。

本机两处客户端程序哈希一致，已同步并校验 4 处界面副本、每处 57 个关联文件，API 健康。证据：`.local/preview-overlap-deployment.json`、`.local/assembly-original-qa/next-installed-baseline.json`、`overlap-only-first.json`、`overlap-final-small.json`、`overlap-final-interrupt.json`、`overlap-final-part.json`、`overlap-final-drawing.json`、`overlap-ui-final.json`。

## 其他客户端缓慢与报错核查（2026-11-03）

本次为原因确认，读取服务器已收到的客户端与转图代理日志、更新清单及现有更新包，并下载两份失败版本的只读隔离副本。没有发布程序、生成安装包、改动原图、修改 eDrawings 选项或操作其他电脑的 SolidWorks 会话。

### 已确认的原因

- `BZH-LPW` 与 `SUNLC` 当日上午日志中的桌面版本均为 `2026.11.02.102503`，调用栈仍包含已从新客户端移除的 `DownloadLatestAssemblyPreviewAsync`，请求仍为 `POST /api/documents/{id}/assembly-preview`。因此这两台实际仍走“生成最新引用组合的 EASM，再预览”的旧路径。成功记录的缓存文件名也为 `preview.easm`。
- `BZH-LPW` 09:01:13 和 09:02:43 的两笔生成请求分别等待 43.252 秒和 32.675 秒后返回 400；另一笔等待 459.856 秒后取消。`SUNLC` 09:23:21 的生成请求等待 399.417 秒后返回 400。以上等待发生在原生预览打开之前，不能归因于本机 eDrawings 解析速度。
- `PC-07` 转图代理同一时间段多次报告“后台转图的进程归属已变化，已停止操作并保留当前 SolidWorks 窗口及文件”，涉及 `总装.SLDASM`、`700022.00.SLDASM`、`700001.00.SLDASM`；另有回传连接中止。此处是转图失败证据，不应通过取消进程归属保护来解决预览问题。
- 原图优化只替换了本开发机两处客户端程序，没有进入其他电脑的自动更新包。核查时网页配置已为 `2026.11.03.093211`，但 `Desktop.Version` 仍是 `2026.10.21.023420`，地址仍指向该日期的 ZIP。ZIP 内桌面程序 SHA-256 为 `E7F6573094CFE0FD28808DBDDD9EE33163D317712DDCE7296262B1C1E15684C7`，本机原图优化程序为 `4F0EAAC0151FD8851C3F036F540C538C452A629F5BD86CA19B8FFC367BB891CA`。网页配置更新与原生程序更新是两条独立路径；界面上的配置版本不能证明预览程序已经更新。

另有独立连接异常：09:30:30 `BZH-LPW` 请求连接被重置，09:30:59 `SUNLC` 对 `192.168.2.8:5173` 的连接被拒绝。当前 5080 与 5173 健康检查正常；现有证据不能确认当时监听中断的具体原因，不能把这一类错误全部归到旧客户端或 CAD 文件。

### 隔离验证和范围

读取失败的两个固定版本：`d86823ac-2482-4cb5-9593-a8f0d8c35ee7`（5,631,872 字节）和 `1772b2a6-f62a-418c-886d-42130c7f14d4`（2,252,144 字节），原文件接口均返回 200，完整 SHA-256 与版本元数据一致。本机 eDrawings 原图解析分别约 3.980 秒和 2.907 秒，第二次约 2.657 秒和 1.827 秒，完整原生界面可用。这是本机隔离原生解析结果，不是两台目标电脑的端到端验收，也不证明 142MB 的另一份总装已验证通过。

尝试完整 MainWindow 测试时测试界面未就绪，未将该次夹具结果报告为通过；上述解析结果来自不依赖在线界面的原生控件夹具。没有再次运行完整功能回归或执行部署。

建议后续先把已验证的原图客户端修复分发到目标电脑，核对实际程序哈希与调用路径，再分别测下载和原生解析；转图代理的会话归属异常单独保留处理。官方 [PDM 预览页](https://help.solidworks.com/2025/English/EnterprisePDM/fileexplorer/r_File_Preview_Tab.htm) 明确由 eDrawings 展示 SOLIDWORKS 零件与装配；“每次先生成最新引用组合”的链路是 UPLM 自定义机制。继续遵循 [eDrawings 完成加载事件](https://help.solidworks.com/2025/English/api/emodelapi/eDrawings.Interop.EModelViewControl~eDrawings.Interop.EModelViewControl._IEModelViewControlEvents_OnFinishedLoadingDocumentEventHandler.html) 的规则，不在加载完成前操作模型。

本次证据位于 `.local/preview-remote-diagnosis-20261103/`：`today-error-index.json`、`today-errors.json`、`batches.json`、`events.json`、`preview-requests.json`、`agent-errors.json`、`distribution-check.json`、`failed-document-versions.json`、`original-downloads.json`、`sunlc-original-parser.json`、`bzh-original-parser.json`。
