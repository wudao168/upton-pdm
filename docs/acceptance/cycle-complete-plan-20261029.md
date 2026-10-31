# 循环引用完整整改方案（待用户确认，2026-10-29）

本轮仅只读审计、采集插件保留的关系树并离线分析；未修改产品代码、未部署、未打开/保存/重建/关闭图纸。当前部署版本仍为 2026.10.29.215312。

## 本次现场与已确认原因

22:28:40 报错链路为 `70004524.M01.03.01.SLDASM → 70004524.M01.03.01-01.SLDPRT → 70004524.M01.03.01.SLDASM`。日志确认该子装配和零件均未加载、没有原生归属证据、上下文数量为 0。本次分类记录中有 416 个未加载模型、43 个已加载模型，另有 139 个未加载工程图；这些是读取记录数量，不是提交数量。

22:40 对 PID 12452 做只读内存快照，仅读取唯一匹配的 `PdmAddin.currentTree`，未混合历史/其他根树，未导出几何和认证数据。当前树有 1686 个实例节点；按现有普通环校验的路径聚合规则（排除工程图、范围外与已标记上下文边）离线检查后，得到 **11 个含环分组、35 个文件、15 对直接双向关系、21 条缺少分类证据的外部依赖边，涉及 18 个来源文件**。这些是候选闭环，不能直接等同于 11 个原生设计循环。

主要代码缺口：

1. `PdmAddin.ReferenceRecovery.cs`、`PdmAddin.CheckInDependencies.cs` 只在模型已加载时读取原生特征分类；没有证据的文件仍生成普通外部依赖。
2. `CadTreeNode.IsContextReference` 布尔值 false 同时表示“已确认普通来源”和“未知类型”；`ArchiveReferenceCycles` 没有区分两者。
3. `ArchiveReferenceCycles.Validate` 只报告第一个环。修正一条边后，其他候选环才显现，导致逐条试错。
4. `ArchiveContextOwnership` 无法匹配旧实例名、配置或路径时没有明确保留未知状态；恢复已补齐引用的代码还存在丢失上下文标记/证据的路径。
5. 类型与健康状态混用：目前只识别锁定/在上下文/脱离上下文，悬空状态可被降为普通来源；配置选项、配置名也未作为分类证据保留。

前面局部修复及 1772 项托管检查未证明完整真实总装全部引用正确，不能以这些检查数量代替本次完整批次验收。

## SOLIDWORKS/PDM 官方边界

| 场景 | 官方明确内容 | 本方案原则 |
| --- | --- | --- |
| 真实循环 | [PDM 检入警告](https://help.solidworks.com/2025/english/EnterprisePDM/admin/IDD_USER_GROUP_WARNING.htm)规定 Cyclic reference 始终阻止检入 | 保留已确认真实循环拦截；未知不能冒充普通来源或静默放行 |
| 装配上下文 | [更新路径](https://help.solidworks.com/2026/english/SolidWorks/sldworks/c_Understanding_the_Update_Path.htm)位于所属装配体 | 装配成员边与零件的装配环境回指分别识别 |
| 上下文真循环 | [性能评估](https://help.solidworks.com/2016/english/SolidWorks/sldworks/HIDD_ASSYXPERT_INCONTEXT_CIRC.htm?id=579e37beab064a0ca8f095f4b38acf43)能报告妨碍正常重建的上下文循环 | 不能把所有上下文或双向几何关系一律豁免；文件图检查不能代替原生重建检查 |
| 锁定/断开 | [官方说明](https://help.solidworks.com/2023/english/Solidworks/sldworks/t_Locking_and_Breaking_External_References.htm)：锁定停止更新且可解锁，断开不可恢复 | 类型与健康状态独立保留，不自动断链、解锁或固化几何 |
| 配置/实例 | [外部引用规则](https://help.solidworks.com/2026/english/SolidWorks/sldworks/c_External_References.htm)禁止同一文件通过另一实例建立自引用，即使配置不同 | 配置用于判断证据适用范围，不能拆分配置节点绕过真实自引用 |

官方没有披露 PDM 完整内部建图算法；本方案是对照规则制定的 UPLM 实现，不保证未经真实检查的样本一定能通过 PDM 检入。

## 建议一次确认的实施范围

1. **统一引用结果与证据。** 区分实际装配成员、普通文档/派生来源、装配环境、上下文几何来源和类型未知；另行记录锁定/脱离/悬空/断开等状态。保留来源接口、原路径/解析路径、所属实例、配置及内容指纹。未知不默认转普通。
2. **一次收集全部候选环。** 用含环分组而非首环异常覆盖全部候选关系，每组给出涉及文件、代表链路和缺证据的边。不要枚举可能指数增长的全部闭合路径。
3. **按需自动补证据。** 首轮集中处理当前 21 条未知边的 18 个来源文件及必要上下文，优先已加载模型、可用组件接口和可信缓存；不为此全量打开几百份文件。先验证实际接口对轻化/压缩状态的能力。若仍无法取得可信分类，显示具体“引用类型待核对”原因，不误称已确认循环，也不靠取消保护放行。
4. **全流程共用结果。** 范围准备、检入准备、引用补齐、缓存恢复、提交快照及服务端验证使用一致类型语义；恢复/克隆保留证据。文件、配置或内存状态发生变化时只使相关证据失效，再核对改变部分。
5. **提前给完整结果并控制重复读取。** 尽早发现所有候选关系，自动补读并给出一份完整核对结果。提交前复核改变项，不重复完整解析相同图档；保持当前勾选后自动继续的流程和选取范围。
6. **按同一真实批次验收。** 固定本次完整总装及 11 组候选关系，覆盖单独/总装内、加载/未加载、轻化/压缩、旧路径/别名、配置、锁定/脱离/悬空/断开、恢复缓存和重试。所有分组都须有分类结论。真实总装检入、关闭后从 PLM 重开仍由用户手动验证。不能只验证首个环消失后就宣布完成。

真实模型若经原生检查确认有设计循环，仍需用户决定如何调整设计依赖；保留原文件名、原特征和设计关联，不自动解除关系。不改变现有缺失引用默认提示规则，不因此扩大提交、获取权限或升版范围。

## 未加载文件读取能力的约束

[`IComponent2.ListExternalFileReferences2`](https://help.solidworks.com/2024/english/api/sldworksapi/SOLIDWORKS.Interop.sldworks~SOLIDWORKS.Interop.sldworks.IComponent2~ListExternalFileReferences2.html)可作为组件层取证入口，但不能假设轻化/压缩状态总能完整返回，须在本次真实样本上先验证。

Document Manager 需要单独有效许可；[GetExternalFeatureReferences3](https://help.solidworks.com/2021/english/api/swdocmgrapi/SolidWorks.Interop.swdocumentmgr~SolidWorks.Interop.swdocumentmgr.ISwDMDocument27~GetExternalFeatureReferences3.html)可考察未加载零件/装配的路径与配置，但不承诺完整类型判别。[SwDmSearchInContextReference](https://help.solidworks.com/2025/english/api/swdocmgrapi/SolidWorks.Interop.swdocumentmgr~SolidWorks.Interop.swdocumentmgr.SwDmSearchFilters.html)同时涵盖派生基体、镜像、派生组件及装配上下文，不能将命中结果一律豁免。先验证能力，不把引入 Document Manager 当作解决全部未知关系的保证。

## 证据文件

- `tmp/cycle-complete-audit-20261029/runtime-timeline.json`：本次业务日志摘录。
- `tmp/cycle-complete-audit-20261029/current-tree.json`：唯一当前根树的只读关系快照。
- `tmp/cycle-complete-audit-20261029/all-cycle-groups.json`：所有候选含环分组、内部边及证据状态。
- `tmp/cycle-complete-audit-20261029/analyze.cjs`：离线分析工具，不进入产品代码。

以下列表是当前保留图的候选分组，均含未核实边，并非已确认设计错误清单。

| 组 | 涉及文件 | 未核实边数 |
| --- | --- | --- |
| 1 | 70004524.M01.03.01-16.SLDPRT；E2S_W22-A.SLDASM | 1 |
| 2 | 70004524.M01.03.01-09.SLDPRT；70004524.M01.03.01-01.SLDPRT；70004524.M01.03.01.SLDASM | 2 |
| 3 | 70004524.M01.03.02.06-02.SLDPRT；70004524.M01.03.02.06-01.SLDPRT；70004524.M01.03.02.06.SLDASM | 2 |
| 4 | 70004524.M01.03.02.01-02.SLDPRT；70004524.M01.03.02.05-01.SLDPRT；70004524.M01.03.02.05.SLDASM | 2 |
| 5 | 70004524.M01.03.02.04-01.SLDPRT；70004524.M01.03.02.04.SLDASM | 1 |
| 6 | 70004524.M01.03.02.03-01.SLDPRT；70004524.M01.03.02.03.SLDASM | 1 |
| 7 | 70004524.M01.03.02.07-01.SLDPRT；70004524.M01.03.02.07.SLDASM；70004524.M01.03.02.08-01.SLDPRT；70004524.M01.03.02.08.SLDASM；70004524.M01.03.02.SLDASM | 4 |
| 8 | 70004524.M01.01.03-01.SLDPRT；70004524.M01.01.03.SLDASM | 1 |
| 9 | 70004524.M01.02.02-01.SLDPRT；70004524.M01.02.02.SLDASM | 1 |
| 10 | 70004524.M01.02.04-06.SLDPRT；70004524.M01.02.04.SLDASM；70004524.M01.02.SLDASM | 1 |
| 11 | 70004524.M01.01.01.01-03.SLDPRT；70004524.M01.01.01.01-02.SLDPRT；70004524.M01.01.01.01-01.SLDPRT；70004524.M01.01.01.01.SLDASM；70004524.M01.01.01.SLDASM；70004524.M01.01-05.SLDPRT；70004524.M01.01.SLDASM；70004524.M01（SHCM93检漏机）.SLDASM | 5 |
