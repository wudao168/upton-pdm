# 随附图片资源的服务端批次登记修复

用户22:33的CW004-3.SLDASM存档已越过旧项目归属检查，在第5步预留图档身份失败。插件选择41份主体图档，并识别H620_外观.STEP、pplastic2.jpg两份辅助依赖，实际批次43份。错误由服务端PdmWorkflowService.RegisterDocumentAsync抛出，并非图片被用户要求独立上传。

原服务端只允许带ArchiveRegistrationContext的Other类型.stp/.step/.txt/.xls/.xlsx；图片虽已随父图档进入整批清单，却被错误挡在登记阶段。修复将批次辅助资源扩展为与既有受控引用定位相同的19类：STEP、方程文本、Excel设计表、IGES/Parasolid/SAT/STL、常见图片及图纸格式。普通独立登记规则仍不变，未知类型、无效批次或会话不能登记。本次不改归属、权限、文件名、设计内容和存档范围选择。

[PDM官方引用说明](https://help.solidworks.com/2022/English/EnterprisePDM/FileExplorer/c_Creating_Removing_References.htm)包括数字图片等附属文件引用。UPLM通过整批上下文预留资源身份、同一事务提交、打开清单分发实现随附保存，属于项目现有机制，不表述为PDM内部相同接口。

## 当前发布状态

本次编写该修复期间，服务端并行发布2026.11.02.230024已包含这段白名单修改。已使用发布程序集及其匹配PDB冻结副本验证，130份应用源码与发布一致；应用DLL SHA256为7B127633545F76C8E186C9BCC69A1D31BA03BF449D87179476936A7D78791392。没有再次发布或重启API，也没有替换SolidWorks插件，原生插件仍为2026.11.02.220000。

## 验证

- 47项服务端领域回归通过，包括19类资源逐类预留、整批提交、版本关系、BOM排除与幂等，以及原有整批原子性和属性索引保护。
- 新增HTTP端到端用例使用实际发布的Pdm.Api/Pdm.Application程序集、隔离InMemory仓库及临时文件存储，覆盖原生父图档、JPG和STEP预留、三份一起提交、重复提交、受控打开清单及固定版本下载哈希。不是只验证插件的模拟响应。
- HTTP用例采用与插件相同的UsesNativeFileInventory=true固定原生清单和treeScopeOnly=true请求；保留旧树投影排除未核实历史来源的规则，没有为测试放宽生产行为。
- 测试数据库明确断言为InMemory，后台业务任务关闭，未访问或修改实际业务数据库、图纸、旧登记或原目录。完整SolidWorks业务复测仍由用户手动完成。
- 本机API健康ok，服务器及4处客户端界面230024、每处56份资源一致；执行界面清单同步及VerifyOnly。未生成安装包。

证据目录：tmp/archive-supporting-resources-20261102。最终HTTP测试以test-results/resources-http.trx为准。
