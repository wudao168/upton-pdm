# P700017-5 存档属性与BOM未生成分析（仅诊断）

本文件保留最初只读诊断。用户随后确认两处修复，已随2026.11.02.200000发布；实施、PDM对照、验证范围和历史数据处理边界见[bom-session-fix-20261102.md](bom-session-fix-20261102.md)。

用户反馈图档具有属性，但存档后标准件/非标件等BOM为0。项目P700017-5，名称0002，存档247份图档，页面显示“属性索引待补齐”和“结构待补全”。

## 实际数据

使用本机API服务原有数据库环境配置，在START TRANSACTION READ ONLY事务中只读查询，结束ROLLBACK。没有更新属性、BOM或图档，没有读取CAD几何；凭据未输出或写入诊断文件。

- 已有正式存档版本的图档247份。
- 247份最新版本的属性快照均仅包含FileName、Extension、LastWriteTimeUtc、SourceFileSha256。
- 物料名称、物料分类、型号、材质等业务属性键均未提取到版本属性快照，业务字段数量为0。
- bom_item中该项目有效记录0条。
- 当前总装.SLDASM引用快照StructureIndexPending=true，并包含旧引用及属性索引待补齐提示。
- 当前BOM映射仍为物料编码、物料名称、材质、型号、品牌、单位等；此次主要问题在数据提取之前，并非已提取值仅因字段名不匹配而不可见。

## 代码路径

PdmAddin.ReadArchivePreflightProperties对可以按已保存原文件归档的3D走ReadSavedNativeProperties。该方法只复用以前版本的PropertySnapshot并加文件技术字段；新图档无历史时只得到文件名等信息，没有尝试读取已经加载图档可取得的自定义属性。优化原意是避免为非必填属性完整打开和重存图档，但当前实现连可读取的属性也直接跳过。

PdmWorkflowService.GenerateMechanicalBomFromSnapshotAsync在snapshot.Root.HasPendingStructureIndex为true时立即退出，甚至未到SourceAsync读取属性及分类阶段。这保护了已有BOM不被未读出的结构删除，但也导致本项目首次存档后没有任何自动生成的BOM来源条目。

因此包含两处原因：文件内属性没有成为PLM属性索引；结构待补全又整体停止BOM派生。仅补属性仍不能绕过当前结构保护，必须明确BOM生成范围才能解决当前页面空白。

## PDM对照与建议（待确认）

[PDM的BOM模板文档](https://help.solidworks.com/2025/english/EnterprisePDM/Admin/IDD_EDIT_COLUMN.htm?id=2.6.3)说明BOM列使用数据库已提取的文件卡变量，并可按引用配置优先、文件自定义属性回退。源文件存在属性不代表PLM数据库已具备这些变量。

建议恢复“能读到就读取”的属性提取：优先读取已加载图档的缓存属性；未加载项使用可验证的轻量方式，不能读取则继续保留提示，不逐份完整打开、重建或强制保存。按引用配置优先、全局属性回退，并沿用现有字段映射。

结构不完整时，建议只补充能够确认的BOM来源/物料，保留原有及人工BOM，不依据缺失索引删除或减少；未确认部分明确标记，正式发布继续保留完整性保护。此项会改变当前整批停止生成BOM的行为，需要用户确认。现有已存档247份还需补提取属性、重新派生BOM；补齐方式应先隔离验证，不能直接改写原CAD文件或绕过版本保护。

本次未修改产品代码、数据库记录或实际BOM。界面候选2026.11.02.110000仍按用户要求延后部署。

只读核对证据：tmp/bom-property-audit-20261102/database-summary.json。查询失败的首次连接未使用凭据，随后按API服务实际环境配置成功；没有修改账号、密码、数据库权限或服务配置。
