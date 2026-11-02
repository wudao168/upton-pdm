import { describe, expect, it } from 'vitest'
import { canViewNavigation, canViewProductionTab } from '../src/navigationAccess'

const can = (...permissions: string[]) => (permission: string) => permissions.includes(permission)
describe('导航查看权限', () => {
  it('未授权的业务模块隐藏，本人待办和本人工时沿用登录权限', () => {
    for (const key of ['project-center', 'project-workbench', 'project-calendar', 'projects', 'materials', 'standard-library', 'program-templates', 'production-drawings', 'admin'] as const)
      expect(canViewNavigation(key, can(), false), key).toBe(false)
    expect(canViewNavigation('tasks', can(), false)).toBe(true)
    expect(canViewNavigation('work-hours', can(), false)).toBe(true)
  })
  it('只读权限可进入相应模块，不要求编辑权限', () => {
    expect(canViewNavigation('materials', can('material.view'), false)).toBe(true)
    expect(canViewNavigation('program-templates', can('program-template.view'), false)).toBe(true)
    expect(canViewNavigation('standard-library', can('standard-library.view'), false)).toBe(true)
    expect(canViewNavigation('projects', can('project.view'), false)).toBe(true)
    expect(canViewNavigation('admin', can(), true)).toBe(true)
  })
  it('新版项目中心配置至少有一个可查看页签才显示入口', () => {
    expect(canViewNavigation('project-center', can('project.view', 'system.project-center-permissions.v1'), false)).toBe(false)
    expect(canViewNavigation('project-center', can('project.view', 'system.project-center-permissions.v1', 'project.tab.records.view'), false)).toBe(true)
    expect(canViewNavigation('project-center', can('project.view'), false)).toBe(true)
  })
  it('供应商只读角色只显示供应商页签', () => {
    expect(canViewNavigation('production-drawings', can('production.supplier.view'), false)).toBe(true)
    expect(canViewProductionTab('suppliers', can('production.supplier.view'))).toBe(true)
    for (const tab of ['tracking', 'external', 'schedule', 'capacity', 'execution', 'drawings'])
      expect(canViewProductionTab(tab, can('production.supplier.view')), tab).toBe(false)
  })
  it('操作角色不会得到未授权的产能及图纸查看入口', () => {
    expect(canViewProductionTab('execution', can('production.execute'))).toBe(true)
    expect(canViewProductionTab('capacity', can('production.execute'))).toBe(false)
    expect(canViewProductionTab('drawings', can('production.execute'))).toBe(false)
    expect(canViewProductionTab('capacity', can('project.content.view'))).toBe(true)
  })
})
