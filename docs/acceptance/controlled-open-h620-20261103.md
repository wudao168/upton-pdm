# P700016-6 打开失败：H620 外部来源定位修复

## 问题与边界

本机 2026-11-03 15:55 的日志显示，文件准备已完成，打开阶段在 `H620-XX-ES2F5SRDRB-20250410043747953.SLDPRT` 的 `H620_外观.STEP` 引用定位处停止。该文件的原始来源路径仍指向旧目录；SolidWorks 的 `ReplaceReferencedDocument` 在该真实文件上返回 `false`。

本次只修改两个产品文件：`ControlledReferenceMaterializer.cs` 和 `PdmAddin.ControlledReferences.cs`。页签、按钮及空闲调度诊断改动已排除，存档、权限、版本及缺失引用规则不变。

## 修复方式

先进入现有的 PLM 受控搜索与打开边界，再准备引用。若引用替换失败，先恢复所有部分改写，然后查询 SolidWorks 按搜索规则得到的依赖路径。只有每个相关外部来源都精确指向本次清单内的受控文件、文件哈希也匹配，才继续原有打开及实际加载来源校验。旧目录、其他目录、内容不匹配、查询失败或无受控边界的情况仍停止打开。

不改名，不断开设计关联，不调用额外保存，不改写原目录文件或服务器版本。通过受控搜索继续打开的副本不被误记为已经持久改写了引用。

## 与 SOLIDWORKS PDM 对照

- [PDM Reference Dialog 官方说明](https://help.solidworks.com/2026/english/EnterprisePDM/Admin/IDD_PAGE_LOCK_FILES.htm)：引用版本按获取策略进入本地缓存。本次继续使用已获取的 PLM 受控副本。
- [OpenDoc6 官方说明](https://help.solidworks.com/2022/english/api/sldworksapi/solidworks.interop.sldworks~solidworks.interop.sldworks.isldworks~opendoc6.html?verRedirect=1)：工作目录与搜索目录会影响引用加载；程序打开不会自动设置工作目录。
- [GetDocumentDependencies2 官方说明](https://help.solidworks.com/2013/english/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.ISldWorks~GetDocumentDependencies2.html)：`Searchflag=true` 按搜索规则解析依赖。按精确清单和哈希限制可用来源，是 UPLM 的受控打开机制，不能表述为 PDM 原生实现细节。

## 验证

- 真实 H620 隔离副本复现 `ReplaceReferencedDocument=false`；受控搜索正确解析到隔离目录内同名 STEP。原始文件哈希不变，未打开或保存业务总装。
- 7 项本次新增场景：正常受控来源、旧来源、错误目录、目标内容变化、读取失败、缺少受控边界、部分改写回滚。
- 连同既有引用、锁定、实际加载边界、会话及项目确认回归，共 296 项通过。
- 候选版本 `2026.11.03.173000`，PDB 的 177 份源码校验值全部匹配；相对已部署版本仅有上述两个产品文件差异。
- 本机部署及原图、版本、心跳、界面清单证据位于 `tmp/controlled-open-h620-20261103/final-verification.json`。部署不生成安装包。

用户已选择手动业务复测。本次没有执行真实项目的存档或总装重开验收，不能把隔离副本与模拟接口回归表述为 P700016-6 已实际打开成功。
