# PLM 全场景回归矩阵

本矩阵用于模拟真实业务链路。所有可写测试必须使用唯一 QA 编号、隔离数据库或内存仓储；禁止向正式 U9C 写入数据。真实 SolidWorks 装配验收只在已启动桌面客户端并提供测试装配时执行。

## 业务场景

| 场景 | 角色与关键动作 | 自动化证据 |
| --- | --- | --- |
| 登录与个人设置 | 登录、续期、修改资料/密码、失效令牌、找回密码 | `ApiSmokeTests`、`LoginView.test.ts`、`PersonalSettings.test.ts` |
| 公司与组织 | 公司切换、组织树、制造部门、成员与负责人、跨事业部范围 | `CompanySessionServiceTests`、`OrganizationAndStaffing_EnforcesDivisionAssignmentAndCrossDivisionChildScope`、`OrganizationSettings.test.ts` |
| 用户与角色权限 | 用户增改、复合角色、权限目录、自定义角色、403 拒绝 | `UserAdministration_CreatesUpdatesAndResetsUser`、`ProjectContentVisibility_FollowsRolePermissionSetting`、`RolePermissionSettings.test.ts` |
| 项目创建与编号 | 客户同步、主/子项目编号、设备/序列号、删除保护 | `Administrator_CreatesNumberedHierarchyAndEngineerCannotDelete`、`ProjectNumberingTests`、`ProjectManager.test.ts` |
| 项目列表与人员配置 | 全员项目可见、事业部分配、项目经理/协同经理/主设/工程师配置 | `OrganizationAndStaffing_EnforcesDivisionAssignmentAndCrossDivisionChildScope`、`ProjectManager.test.ts`、浏览器角色验收 |
| 项目普通文件 | 文件夹创建/重命名/移动、分块上传、SHA-256、版本、下载、回收站/恢复、权限拒绝 | `ProjectFiles_CompleteBusinessFileLifecycleAndRejectReadOnlyRoleUploads`、`ProjectFileStorageTests`、`ProjectFileLibrary.test.ts` |
| 受控图档与设计树 | 登记、引用快照、关系、检入检出、锁恢复、版本、预览、属性回写 | `DocumentVersionTests`、`ReferenceTreeDiffTests`、`EditSessionApi_*`、SolidWorks 验收工具 |
| BOM | 机械/电气/标准 BOM、表头、层级、校验、草稿/发布、导入导出 | `BomHeaderServiceTests`、`BomWorkbookTests`、`BomReleaseAggregationTests`、`BomManager.test.ts` |
| 料品与 U9C | 分类、创建/审批/修改/停用、附件、编码、同步任务、U9C 查询/写入模拟 | `MaterialApiTests`、`MaterialServiceTests`、`U9MaterialIntegrationServiceTests`、`MaterialManagement.test.ts` |
| 标准物料与标准结构 | 分类层级、成员、推荐料品、筛选、权限拒绝 | `StandardLibrary_ApiCoversCategoryMembershipRecommendationAndRoleDenial`、`StandardLibraryServiceTests`、`StandardLibrary.test.ts` |
| 程序模板 | 创建、参数、附件、版本、提交、审核、批准、下载、归档 | `ProgramTemplateWorkflowTests`、`ProgramTemplateStorageTests`、`ProgramTemplateLibrary.test.ts` |
| 图纸评审 | 评审包、标注、逐项结论、撤回、签核与发布门禁 | `DrawingReviewWorkflowTests`、`DrawingReviewPanel.test.ts`、`DrawingReviewOverlayApp.test.ts` |
| 发布与审批 | 发布包、BOM 快照、审核/批准/紧急批准、原子发布、失败回滚 | `Phase1ReleaseWorkflowTests`、`ReleasePackagePublisherTests`、`ReleaseCenter.test.ts` |
| 待办、版本与审计 | 我的待办、项目版本、项目审计、全局审计 | `ProjectWorkspaceFeeds_ReturnOnlyAccessibleProjectDataAndAssignedTasks`、`MyTasks.test.ts`、浏览器项目页签验收 |
| 系统配置与外部接口 | 存储、文件夹模板、审批流、CRM/U9C、客户端配置 | `StorageLocationPolicyTests`、`CrmCustomerIntegrationTests`、相关系统设置组件测试 |
| 页面与响应式 | 主导航、项目七页签、表单、遮罩、紧凑宽度、125%/150% 视口 | 32 个 Vitest 文件、`workspace.spec.ts`、真实浏览器巡检 |
| 数据库兼容性 | 空库迁移、模拟账号/项目/图档/BOM、不可变版本触发器、自动清理 | `.local/qa/Run-IsolatedAcceptance.ps1`、`Pdm.Acceptance` |

## 完成标准

1. .NET、Vitest、Playwright、Release 构建全部通过。
2. 隔离 MySQL 从空库执行到最新迁移，模拟身份校验成功，随后删除临时库和文件。
3. 已登录无权限返回 403；未登录或无效账号返回 401。
4. 真实浏览器所有主模块和项目页签可加载，关键筛选和只读弹窗可交互，不出现加载失败或重新登录状态。
5. SolidWorks 性能宿主通过 5001 节点和 10GB 稀疏文件验证；真实 COM/装配测试需在 SolidWorks 已启动时单独执行。
