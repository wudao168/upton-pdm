# 轻化父装配的导入归属与独立存档窗口恢复

候选版本：2026.10.31.130000。最终部署状态以 `tmp/import-owner-limiter-20261031/final-verification.json` 为准。

## 原因与真实样本

Limiter Press-in &Heat Stacking总装的AFG05-40AL-C-W-L500、PFC21-14S-R-PK原生父装配处于轻化状态，GetModelDoc2为空；实际SLDASM路径仍存在，IComponent2.FeatureByName可读取子实例的Is3DInterconnectFeature=true。

旧版仅从父ModelDoc2取路径/特征，缺少模型对象时越过实际保存父级，导致归属确认失败。内部IC~~生成组件因未识别到父文件而触发B11。该问题与此前OP170/FESTO样本的轻化父级遗漏一致。

同一真实会话（PID9088）只读对比：

- AFG导入装配及7个内部实例，PFC两个装配实例各含2个内部实例，共14项。
- 旧版确认0/14；修复候选确认14/14，分别归到AFG05-40AL-C-W-L500.SLDASM、PFC21-14S-R-PK.SLDASM。
- 所有读取对象的轻化/解析状态前后相同；总装DirtyBefore/DirtyAfter均为true，导入组件未修改。
- 禁用Pack and Go清单回退；没有解析、重建、激活、打开、保存图档。
- 此次是指定实例归属验证，不是完整总装检入成功验收。

证据：`live-owner-host.jsonl`、`owner-baseline.jsonl`、`owner-candidate.jsonl`。只读候选124500与最终130000使用完全相同的ImportedComponentStorage/NativeArchiveManifest源码；后者另外按用户新指令恢复独立存档窗口。

## 官方依据和实现边界

[GetModelDoc2](https://help.solidworks.com/2023/English/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.IComponent2~GetModelDoc2.html)明确说明轻化、压缩或未加载组件可能返回null。

[GetPathName](https://help.solidworks.com/2024/english/api/sldworksapi/solidworks.interop.sldworks~solidworks.interop.sldworks.icomponent2~getpathname.html)说明模型对象未加载时组件仍可提供保存路径，但不会代为应用搜索规则。因此仍检查所属原生文件存在，不能只凭同名路径确认。

[PDM的3D Interconnect说明](https://help.solidworks.com/2025/English/EnterprisePDM/FileExplorer/c_3d_interconnect_PDM.htm)描述原生/非原生CAD文件之间的库内引用管理。该文列出的支持格式不包含STEP，不能据此声称本文STEP缓存归属算法是PDM官方实现。UPLM沿用用户已确认的“内部导入组件随实际原生父装配保存”规则，通过原生实例特征证据识别归属。

## 修改范围

1. 父ModelDoc2为空时从IComponent2获取保存路径，停留在最近的持久原生父级。
2. 未加载父模型时从父组件接口获取精确实例名对应的原生导入特征；仍校验特征名和Is3DInterconnectFeature。
3. 内部后代沿用相同父级和本轮特征证据缓存；没有证据、父文件缺失、非导入特征时不强行放行。
4. B11提示优先显示已有的ComponentSelectionName，避免重复拼接的实例路径影响定位；不改持久引用路径或BOM层级。
5. 按用户新指令恢复002600版独立存档窗口，撤回120000的设计树内存档模式。原按钮、输入框、选择及完整过程记录恢复独立显示。

相对002600源码，最终候选仅ImportedComponentStorage.cs、NativeArchiveManifest.cs不同；界面文件与独立窗口基线一致。服务器、数据库、提交协议、版本规则未改。未生成安装包。

## 验证

- 编译0错误、0警告；PDB170份源码匹配。
- 93项检查通过：轻化归属4、导入扫描15、导入更新边界12、原生清单进度8、会话/入口12、UI重入6、选择规则36。
- 250个相同导入层级成员仅读取一次特征证据；取消、错误特征、缺失父文件、不同父文件同实例名等负例仍受保护。
- 旧进度夹具依赖“250 / 250”固定空格，调整为不依赖空格的数量断言；未修改业务进度生成逻辑。
- DLL SHA256：B4D54872EC30DAEECD074926F8ADE9E34ABFFB82026AE0F91490BC122E9CCB0A。

部署后核对三处原生组件、界面清单/在线入口、后台心跳及28份业务原图。完整总装存档继续由用户手动复测。
