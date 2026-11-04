# 单个零件设计树循环展开修复

本机插件版本：2026.11.03.185733。网页及客户端共用界面资源保持2026.11.03.180047。

## 真实样本与原因

P700016-6，标签机钣金.SLDPRT，图档ea359afd-ce9a-42a8-b00d-b3afedddfd0e，V1版本be2bc003-4885-4ff8-82b1-f16856fd7949。

通过只读API核对，根快照含4条依赖，分别两次指向标签机.SLDASM和CW004-3.SLDASM；4条均为ExternalFileDependency且IsContextReference=true，原本没有子件。这些依赖来自受控引用和native-file索引两种记录，不能当作装配组件重复展开。

旧被动树按文件类型将依赖装配标记为可按需展开；展开后读取装配自身快照，再遇到原零件及上下文依赖，形成可持续展开的逻辑环。原生树显示也未检查同一祖先身份。

## 规则与实施

- 官方PDM的Contains表示文件引用的下游，Where Used表示引用该文件的上游：[File View Tabs](https://help.solidworks.com/2016/english/enterprisepdm/fileexplorer/r_Explorer_Tabs.htm)。外部特征引用可指向装配环境：[ListExternalFileReferences2](https://help.solidworks.com/2023/english/api/sldworksapi/SolidWorks.Interop.sldworks.IFeature~ListExternalFileReferences2.html)。文件依赖不等于装配组件包含关系。
- UPLM的设计树是组件层级显示。该显示规则属于UPLM实现，不将其表述为PDM Contains标签的全部行为。
- 设计树子节点排除文件依赖、上下文引用及未核实来源；原模型对象、版本快照及存档依赖闭包完整保留，不改模型或数据库引用。
- 原生树按祖先图档身份与配置停止重复展开，并用中文标记循环；不是全局去重，同级重复实例和不同配置保留。
- 被动加载在请求前及异步响应后检查目标是否仍在当前组件树及其祖先链，文件依赖和重复祖先不能继续请求分支。

## 验证与部署边界

- 对旧安装插件运行新增隔离回归，准确失败于“单个零件将4条父装配上下文依赖显示成了下级组件”。修复候选及最终安装目录的实际DLL通过同一回归。
- 真实V1快照经过原生树构建后仅显示零件，原4条依赖仍在内存数据中；已保存隔离控件渲染证据。只读API和模拟接口未提交业务数据，原生结构API没有被动读取调用。
- 关联工程图、普通外部依赖、未知来源、祖先环、同级重复、不同配置、嵌入子装配及内部零件已覆盖；设计树刷新、图档切换、所选根范围、虚拟祖先引用及会话续期相关回归通过。
- 插件构建0警告、0错误；Interop版本及文件与现安装一致。用户确认保存退出后，于2026-11-03 19:02完成两处插件DLL/PDB及版本标记替换并保留备份。两处DLL SHA256均为D91E364DE89550FB412DB8D4B8D617E6954B49B86FAEB84C8F84B96339CA14A2。
- Sync-LocalUiManifest与VerifyOnly已通过；未生成安装包，未改API程序、网页构建、存档快照或实际CAD文件。尚未自动重新打开用户CAD样本，正常重新启动SolidWorks后加载新版本，实际业务打开由用户复测。

证据位于当前会话外部验收目录：part-cycle-live-snapshots.json、part-cycle-before.log、part-cycle-after.log、part-cycle-installed.log、part-cycle-regression/part-cycle-render.png；部署结果为.local/part-tree-cycle-deployment.json。
