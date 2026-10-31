# 树控件生命周期诊断补充（2026-10-29）

## 目的及边界

针对 18:30 SolidWorks 闪退：已有转储显示主线程在处理 `UIAutomationCore!TreeViewItemProxy::GetIdentityString` 时检测到原生堆损坏，但旧日志没有插件树 HWND，无法确定涉及哪棵树。本次只补充下一次运行可用的控件身份记录，不声称已找到或修复最初破坏内存的调用。

## 对照 SOLIDWORKS/PDM

- [SOLIDWORKS Rx 官方说明](https://help.solidworks.com/2026/english/SolidWorks/Sldworks/c_SOLIDWORKS_Rx.htm)提供会话日志和异常退出调用栈，用于诊断崩溃。UPLM 补充插件自身的树身份以便与该类调用栈及转储比对；这是 UPLM 自定义诊断机制。
- [PDM 检入警告规则](https://help.solidworks.com/2022/english/EnterprisePDM/admin/IDD_USER_GROUP_WARNING.htm)要求真正的循环引用阻止检入。本次不改变存档范围、循环判定、权限、引用关联、缓存或图纸内容。两个零件之间双向依赖的业务拦截仍待另行核对。

## 实施

- 在 `PdmTaskPaneControl.StructureTreeView` 与 `BatchOperationDialog.FileSelectionTreeView` 的构造过程中绑定生命周期事件。
- “树控件生命周期”记录包含控件角色、类型、实例编号、句柄代次、创建/句柄销毁/控件释放事件、缓存的原始 HWND 和父 HWND、进程号、所属与当前 Windows 原生线程号、托管线程号及释放状态。
- 句柄重建保留同一个实例编号并递增代次，避免同一个 HWND 被复用时混淆。销毁记录使用创建时保存的句柄，不读取可能触发重建的 `Control.Handle`。
- 只在生命周期事件记录，沿用现有诊断日志同步落盘、轮转和上传；不枚举树节点、窗口或图档，不查询可访问性提供程序，不增加全局钩子。
- 日志失败不得影响控件创建与释放；控件释放后解除所附事件处理器。

## 验证

- 旧版插件缺少创建身份记录的反例失败；候选通过新增 9 项检查。
- 检查覆盖：绑定不会提前创建窗口；句柄、进程和原生线程对应；1000 个节点插入不增加逐节点记录；重建实例身份与代次；销毁不重建句柄；释放事件；两棵树位于不同 STA 时身份清晰。
- 完整候选通过 1757 项托管检查（51 个模式），包含原有存档、获取/放弃、工程图关联、范围、界面及缓存回归；159 份 PDB 对应源码全部匹配。
- 验证使用测试工具自建控件与模拟数据，没有启动 SolidWorks 或自动重放真实图纸操作。
- 本机候选版本 `2026.10.29.192800`，DLL SHA256 `B3656F27EDC7EBC527215C6C2319D5F25E43650758FF582E98418662C32A6745`。
- 部署与最终核对记录位于 `tmp/tree-lifecycle-diagnostics-20261029/release-verification.json`。仅更新三处插件 DLL、PDB、版本标记，不生成安装包；真实业务仍由用户手动操作，闪退根因状态保持“未确认”。
