import type { ProjectPermissionSettings } from '../src/types'

export const permissionDirectory: Pick<ProjectPermissionSettings, 'positions' | 'permissions' | 'customPositions' | 'schemaVersion'> = {
  schemaVersion: 2, customPositions: [],
  positions: [
    ['MainManager', '主项目经理／协同项目经理'], ['ChildManager', '子项目负责人'], ['MainDesigner', '主设'], ['Engineer', '执行工程师／电气硬件'], ['Other', '其他人员'],
  ].map(([key, name]) => ({ key: key!, name: name!, description: key === 'ChildManager' ? '仅负责的子项目' : '负责的项目', scope: 'self', isSystem: true, isActive: true, sortOrder: 0 })),
  permissions: [
    ['project.budget.view', '查看项目预算', '预算'], ['project.plan.edit', '维护项目计划', '项目管理'], ['project.edit', '编辑项目信息', '项目管理'],
    ['project.delete', '删除空项目', '项目管理'], ['project.child.create', '创建子项目', '项目管理'], ['project.designer.assign', '分配执行工程师', '项目管理'],
    ['bom.edit', 'BOM料号及关联配置', 'BOM'], ['bom.mechanical.edit', '标准件／非标件BOM', 'BOM'], ['bom.electrical.edit', '电气BOM', 'BOM'],
    ['validation-plan.edit', '维护质量计划', '质量'], ['release.manage', '创建及管理发布包', '发布'],
  ].map(([code, name, module]) => ({ code: code!, name: name!, module: module!, sensitive: false, scope: 'project', projectAuthorization: code === 'project.budget.view' ? 'budget' : 'position', action: 'edit', dependencies: [] })),
}
