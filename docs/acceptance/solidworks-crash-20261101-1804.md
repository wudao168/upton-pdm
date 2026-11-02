# 2026-11-01 18:04存档闪退诊断

本次仅诊断，未修改产品、图纸或系统转储设置，未自动复现存档。

## 已确认

- SLDWORKS.exe进程30928，版本30.5.0.49。Windows于18:04:08记录异常0xc0000374，报告ID为14c31058-7dea-4db4-8b1b-03a16c7615f8。
- 操作对象为70004449.SLDASM。本次在第3步本地准备阶段，六份子装配提交副本已返回成功；最后完成70004449.F3.SLDASM，18:04:02原生复制返回errors=0、warnings=0，18:04:03副本固定完成。
- 此后18:04:04登录查询和18:04:06心跳均返回200。此次窗口创建后，没有上传会话或整批提交写请求。
- SolidWorks将现有转储从CXPD目录自动压缩到CXPA/20261101180953_30.5.0.0049.zip。仅提取其中匹配进程的SLDWORKS.exe.30928.dmp作本地离线分析；转储79,158,572字节，没有上传第三方。
- 崩溃线程11788，是SolidWorks主线程。原生栈为UIAutomationCore树节点代理GetIdentityString → oleacc对象释放 → LocalFree → RtlFreeHeap → RtlReportFatalFailure。崩溃现场处于主消息循环处理可访问性请求，未见正在执行的SaveAs或上传调用栈。
- 该调用链与2026-10-29 18:30闪退相同；此前故障诊断记录见solidworks-crash-20261029-1830.md。

## 仍未确认

0xc0000374表示堆损坏；当前栈确认的是发现损坏的位置，不是最初写坏或释放错误的来源。不能据此直接归因于Windows、SolidWorks、UPLM或某个外部可访问性客户端，也不能把最后成功处理的F3图档当作故障文件。

本次生命周期日志记录了插件设计树句柄0x960bf4（线程11788）和存档选择树句柄0x340a1c（线程7844），无相应销毁日志。对GetIdentityString反汇编及故障帧恢复确认代理对象地址为0x5a4794d0，但该对象内存未包含在此小型转储中，无法读取其窗口句柄；尚不能确认具体属于哪一棵树。未据此关闭系统可访问性或绕过存档规则。

后续应优先补齐树控件身份/生命周期及必要的完整转储证据，再决定修复位置。当前证据不足以宣称闪退根因已完全确认或问题已修复。

## 官方对照

- [微软崩溃排查说明](https://learn.microsoft.com/en-us/troubleshoot/windows-server/performance/troubleshoot-application-service-crashing-behavior)：0xc0000374为堆损坏，ntdll等模块可能只是检测到其他模块此前造成的破坏。
- [PDM检入说明](https://help.solidworks.com/2024/english/EnterprisePDM/FileExplorer/r_File_Check_In_Dialog_Box.htm?id=0.3.0.2)：文件、引用和权限校验属于检入业务规则，不应与原生进程闪退混为一谈。本次不改变这些业务规则。

本地证据目录：tmp/archive-crash-20261101-1804。核心文件为crash-findings.json、native-stacks.txt、native-tree-owner2.txt及managed-stacks.json。工具只读取寄存器、线程栈及相关代理对象地址，未遍历或导出堆内的业务字符串。
