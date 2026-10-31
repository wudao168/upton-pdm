# 2026-10-29 18:30 SolidWorks 闪退排查

## 已核实的现场

- 进程：SLDWORKS.exe，PID 29496，SOLIDWORKS 2022 SP5（30.5.0.49）；插件实际加载版本 2026.10.29.175742。
- 用户操作对象：`K:\技术共享\李鹏伟\气检设备\70004524.M01（SHCM93检漏机）.SLDASM`。
- 18:30:04.084，插件在上传前的依赖检查中发现 `70004524.M01.01-01.SLDPRT → 70004524.M01.01-02.SLDPRT → 70004524.M01.01-01.SLDPRT`，以托管异常正常结束此次准备流程；未进入上传副本和服务器提交阶段。
- 18:30:04.099，记录 `batch-dialog-exit`。此时原图窗口仍在原进程中；现有日志没有此后图档保存、关闭、上传调用的记录。
- 18:30:20，Windows Application Error 1000 记录原生异常 `0xc0000374`；报告 ID `159c6725-8345-42a1-9b18-16883322bf80`。18:30:31 WER 归档报告。
- SolidWorks 自行保存转储：`C:\Users\wangyh\AppData\Local\SOLIDWORKS\CXPD\SLDWORKS.exe.29496.dmp`，78,331,168 字节，18:30:33。本次仅读取该现有转储，没有再次打开或操作图纸。

## 崩溃栈与结论边界

使用系统自带调试引擎离线读取，并从微软公开符号服务器取得与本机模块 GUID/age 匹配的 ntdll、oleacc、UIAutomationCore PDB；未上传转储或图纸，未安装软件、修改系统调试策略或全局可访问性设置。

异常线程是主线程，调用链包含：

`UIAutomationCore!HookBasedServerConnectionManager::HookCallback → RemoteUiaNodeStub::Incoming_RespondToWinEvent → UiaNodeFactory::CompleteNodeInfo → TreeViewItemProxy::GetIdentityString → oleacc → LocalFree → RtlFreeHeap → RtlpLogHeapFailure → RtlReportFatalFailure`。

这能确认树节点可访问性请求处理期间检测到了原生堆损坏。不能据此确认内存最初被破坏的调用，也不能直接归因于 Windows、某个外部可访问性客户端、SolidWorks 或 UPLM。崩溃现场没有正在执行的存档、上传或原生模型读取栈，但这不排除之前调用的间接影响。

转储只包含寄存器、线程栈和部分内存，未保留可遍历的 CLR 托管堆。插件现有日志不含树控件 HWND，无法将异常涉及的树句柄映射到 SolidWorks 原生设计树或插件树。需要先补充控件身份、创建/销毁线程及相关生命周期证据，再决定修改位置；不能声称本次闪退已经修复。

本次存档拦截的两条零件依赖也应继续核对其特征/上下文来源，不能仅凭文件级双向路径认定原图有真实循环，更不能以断链或关闭循环检查代替验证。

## 对照官方规则

- [SOLIDWORKS PDM 检入警告](https://help.solidworks.com/2022/english/EnterprisePDM/admin/IDD_USER_GROUP_WARNING.htm)：真正的循环引用阻止检入。该业务规则不能解释或容许原生进程闪退；应区分业务拦截与程序稳定性。
- [微软应用崩溃排查](https://learn.microsoft.com/en-us/troubleshoot/windows-server/performance/troubleshoot-application-service-crashing-behavior)：`0xc0000374` 表示堆损坏，ntdll 等模块可能只是检测到此前破坏的位置。
- [SOLIDWORKS Rx](https://help.solidworks.com/2026/english/SolidWorks/Sldworks/c_SOLIDWORKS_Rx.htm)：异常结束会话的调用栈用于分析崩溃。本次使用的是已保存转储，没有自动复现真实 CAD 操作。

原始诊断摘录、离线分析工具及带符号栈位于 `tmp/archive-crash-20261029-1830/`；其中 `runtime-timeline.json` 为相关业务日志时间线，`native-symbolized-stacks.txt` 为首次带符号栈。此次排查未改产品代码或再次部署。
