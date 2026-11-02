# 引用类型互斥状态修复（2026-11-02）

候选2026.11.02.060000，基线为已部署2026.11.01.220000。用户限定仅处理“引用类型记录不完整”；闪退及原生命令等待不在本次范围。

## 确认的原因

P701970-1.SLDASM，493份图档，操作1d47f434cafe49d88725a18b5283154d。2026-11-02 00:15:49在第7步FrozenArchiveSubmissionGraph.IsDeferred中止，本次尚未上传。报错记录70005399.F1.01.SLDASM。

从仍运行的14936会话只读取得插件关系树；失败后的冻结快照已被回收，因此未声称取得该批原始HTTP请求体。现场关系记录存在唯一的冲突：70005399.F1.01-02.SLDPRT回指70005399.F1.01.SLDASM的外部依赖，IsContextReference=true、ReferenceTypeUnverified=true、ReferenceTypeVerified=false，且为无子件的普通装配引用。

00:09:11、00:09:13、00:09:14日志已给出对应父装配归属证据：M6六角圆柱头螺钉的柱形沉头孔1，所属零件实例70005399.F1.01-02<1>，状态4。ArchiveContextOwnership.Apply在确认后仅设置IsContextReference=true，漏掉清除早先的未知标记及设置已验证状态；最终快照严格校验因两种互斥标记同时存在而拒绝。

## 最小修复

仅改ArchiveContextOwnership.cs中的已证实上下文分支：

- 设置IsContextReference=true。
- 同步设置ReferenceTypeVerified=true。
- 清除ReferenceTypeUnverified。

原有实际成员唯一匹配、零件自身证据优先、精确路径/来源路径匹配条件均保留。没有唯一归属或没有证据时不清除未知标记。未修改最终快照、服务端、警告策略、文件保存方式或引用关系，不处理命令等待和闪退。

官方对照：[SOLIDWORKS上下文保存要求](https://help.solidworks.com/2025/english/SolidWorks/sldworks/c_Saving_Assemblies_with_In-context_Features.htm?id=7.9.4)要求保留零件与所属装配的关联；[PDM警告规则](https://help.solidworks.com/2026/english/EnterprisePDM/admin/IDD_USER_GROUP_WARNING.htm?id=2.27.15.13)保留真实循环等阻止条件。三个内部分类字段和转换方法是UPLM实现，本次不把它们表述为PDM官方字段。

## 验证

- 冻结220000已部署源码，仅上述1份产品文件有差异；编译0错误、0警告，PDB176份源码匹配。
- 按现场双标记、所属实例及日志原生证据重建隔离关系：旧版在真实CaptureArchiveSnapshot/FrozenArchiveSubmissionGraph入口复现同一错误；候选正确转换状态并通过。
- 专项26项：现场证据对应、原报错复现、来源别名、重复处理、直接证据优先、同名不同路径归属歧义、无证据、非法子树和非法虚拟引用保护；隔离原生文件字节与关联不变。
- 常规9组331项，历史4组50项，以及旧11组候选环完整证据7项，共414项插件检查通过。
- 当前已部署Application/Domain程序集的6项服务契约检查通过：旧双标记拒绝、修复后接受、真实普通循环拒绝、非法子树拒绝、非法虚拟状态拒绝、合法未知引用结构保留。
- 未对真实493份图档自动执行存档，不需要用户增加业务操作。

候选DLL SHA256：DBFD354ABF5B1C07D13DE551AD7BA4BC156DE4EB320C727366878D8CBF993FC8。

测试工具使用本任务独立副本，避免与共享验收工具的并行编译/运行占用相互影响；隔离工具编译及上述最终检查均通过。

## 部署状态

用户确认SolidWorks已退出后，已于2026-11-02 06:14完成本机部署。三处插件DLL/PDB/版本标记共9份文件与候选一致；309份受保护文件核对通过，3份相关业务原图的哈希、大小、属性与修改时间未变。后台监听器恢复、心跳正常，API健康检查通过。界面清单同步及VerifyOnly通过，保留当前独立发布的界面版本2026.11.01.214117，在线文件与服务器/客户端副本一致。

实际493份整批存档由用户手动复测，未自动提交；未生成安装包。

原始诊断：tmp/archive-reference-shape-20261102（actual-state.json、target-records.json、context-evidence.json）。

验证及发布证据：tmp/archive-context-state-20261102（context-state-final.log、checks.json、historical-checks.json、whole-cycle-evidence.log、server-contract-result.json、pdb-audit.json、fix-evidence.json）。
