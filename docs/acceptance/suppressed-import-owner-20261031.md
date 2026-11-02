# 压缩导入内部子件的保存归属

候选：2026.10.31.180000；基线：170000。仅修改ImportedComponentStorage.cs，保留独立存档窗口及现有性能优化。部署状态以 `tmp/import-leaf-owner-20261031/final-verification.json` 为准。

## 实际问题

P700016-16的“总装.SLDASM”在第3步被B11拦截，未上传。本次仅两条导入实例未确认归属，其他缺失引用按现有警告策略处理，未导致该失败。

只读核对PID27116当前总装的两个链路：

- 1018494 20230331-1 → 1018494 20230331.stp-1 → 1018380.stp-1 → 1028928.stp-1。
- 控制器P1018494 20230331-1 → 同一导入层级 → 1028928.stp-1。

叶子均为Suppression=0、GetModelDoc2=null；GetImportedPath与各级IC缓存祖先均指向同一1018494 20230331.stp。最高导入实例的父组件接口可提供名称匹配的Is3DInterconnectFeature=true证据，两个实际保存父文件分别为1018494 20230331.SLDASM和控制器P1018494 20230331.SLDASM。

旧代码在叶子没有模型对象时直接返回，尚未读取可用父级证明，因此产生B11。不是因为这两个父级没有被勾选，也不能从该提示推断原图内容缺失。

## 修复边界

- 未加载的内部子件只有在真实IC祖先链存在、每个缓存祖先与子件导入来源一致、最近持久父装配有效、最高导入实例原生特征名称与类型匹配时继承归属。
- 不接受不同目录同名来源、空/相对/无法确认的来源、错误特征、缺失父文件；未加载顶层缓存没有导入祖先时仍不放行。
- 未加载子件不能靠Pack and Go清单替代原生特征证明。
- 不解析组件、不改变压缩状态、不保存、改名或断链，不把内部子件改成独立登记或独立上传。
- PLM内部的随父存储标记不等同于修改SolidWorks的IsVirtual；真实样本的IsVirtual原本为false，未进行虚拟化转换。

## PDM及官方API对照

[GetModelDoc2](https://help.solidworks.com/2023/English/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.IComponent2~GetModelDoc2.html)明确说明压缩、轻化或未加载组件可能返回null；模型对象为空不能直接等同于内容缺失。

[GetPathName](https://help.solidworks.com/2024/english/api/sldworksapi/solidworks.interop.sldworks~solidworks.interop.sldworks.icomponent2~getpathname.html)说明未加载时仍可从组件取得保存路径。

[PDM的3D Interconnect文档](https://help.solidworks.com/2025/English/EnterprisePDM/FileExplorer/c_3d_interconnect_PDM.htm)描述父文件检入时识别CAD引用；其列出的格式没有STEP。这里依据真实原生特征确认STEP生成内容的父存储归属，是UPLM针对已确认“随父装配保存”规则的实现，不能称为PDM官方内部算法。

## 验证

- 真实会话旧版两个叶子均无归属；候选两个叶子均归到各自父文件，Suppression前后均为0；总装保存标记前后均为true。未打开、激活、解析、保存其他图档。
- 116项检查通过：未加载内部子件14、轻化父级4、导入扫描15、原生引用18、导入更新边界12、缺失引用策略18、本地同步16、整批上传边界19。
- 实际扫描器回写归属并保留IsSuppressed，完成后清空本轮待核对队列。
- 编译0错误、0警告，PDB172份源码匹配。
- DLL SHA256：224418027E082AF62D412F7BDFED7ABF9EDF7DAF6405E75307050400EF32CC5F。

证据：`live-owner.jsonl`、`candidate-owner.jsonl`、`checks.json`、`pdb-audit.json`。完整真实总装存档仍由用户手动复测，归属只读验证不等同于整批存档成功。
