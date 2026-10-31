# 普通存档取消强制 Pack and Go 与引用读取去重

用户已确认按建议实施。本次按 PDM 公开的文件引用树、数据库引用信息与按需本地读取机制调整 UPLM 普通存档路径，不推断 PDM 闭源内部是否调用某个 SolidWorks 接口。

官方依据：

- [PDM 引用树](https://help.solidworks.com/2022/English/api/epdmapi/EPDM.Interop.epdm~EPDM.Interop.epdm.IEdmFile5~GetReferenceTree.html)
- [数据库中没有引用信息时允许读取本地引用](https://help.solidworks.com/2025/english/api/epdmapi/EPDM.Interop.epdm~EPDM.Interop.epdm.IEdmReference10~GetFirstChildPosition4.html)
- [读取文件引用与 SearchFlag](https://help.solidworks.com/2023/english/api/sldworksapi/SOLIDWORKS.Interop.sldworks~SOLIDWORKS.Interop.sldworks.ISldWorks~GetDocumentDependencies2.html)

## 修改

- 普通插件存档在确认勾选范围后，按已读取的实际结构和确认范围收集已知文件，不调用 GetPackAndGo、不激活或临时打开文档来生成清单。
- 不为补全压缩子装配或工程图模型的结构索引额外临时打开大装配。未完整读取的部分保留“结构待补全”，不伪称所有成员已核实；选中原生文件仍走真实引用读取、保存状态、内容指纹、权限和服务端提交校验。
- 嵌入组件保持在父文件与原结构中，不制造独立上传；未勾选工程图不会把其引用的其他独立结构混入当前文件清单。
- 文档查找使用单次依赖读取过程内的索引；原生加载代次变化时重建，失效或已关闭的 COM 对象不继续复用，按完整路径匹配。
- 每个所属模型一次建立嵌入归属索引，供该模型的全部引用查询使用；同一模型的导入来源与库特征检查共享一次特征目录读取。
- 已确认按警告处理的未选缺失引用不再先触发 SearchFlag=true 的搜索。已有有效关联和必要的其他引用解析仍保留。
- 引用读取、搜索式解析、设计来源读取、嵌入索引建立均记录图档路径、开始、返回或中断与耗时。当前文件提示显示具体步骤，不能把整段耗时误算给某个已完成调用。

“缺失默认仅提示、可配置阻止”、选中内容范围、文件名及关联保留规则继续生效。未取消真实上传与整批提交校验，不生成安装包。原有独立浏览器导入、改名等非普通存档路径中的显式 Pack and Go 操作不在本次变更范围。

## 验证

隔离回归验证普通确认清单零 Pack and Go、零原生打开/激活/保存；文件成员、压缩引用、虚拟层级、未选工程图的边界；1500 次查询只枚举一次 500 个文档，加载变化使索引失效；已关闭及已释放 COM 包装对象安全失效；1200 个特征仅建立一次目录。继续运行缺失引用贯穿上传和提交路径、权限、版本、恢复、绘图关联及既有回归。

本次未操作真实正在运行的存档。大装配的实际耗时由用户在部署后手动复测，不把模拟调用次数的改善换算成真实节省比例。部署版本和最终核对以 `tmp/archive-no-packandgo-20261029/` 中的记录为准。
