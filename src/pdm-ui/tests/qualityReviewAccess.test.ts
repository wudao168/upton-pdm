import { describe, expect, it } from 'vitest'
import { canViewNavigation } from '../src/navigationAccess'
import { canViewProjectTab, projectCenterSchema } from '../src/projectCenterAccess'
import { permissionNavigationLocation } from '../src/authorizationNavigation'

describe('复盘与改善新增授权', () => {
  it('旧项目查看权限不会自动获得公司汇总或总结入口', () => {
    const legacy = ['project.view', 'project.content.view']
    expect(canViewNavigation('quality-review', p => legacy.includes(p))).toBe(false)
    expect(canViewProjectTab(legacy, 'summary')).toBe(false)
    expect(canViewProjectTab([...legacy, projectCenterSchema], 'summary')).toBe(false)
  })
  it('明确授予只读权限可进入，撤销后入口消失', () => {
    const granted = ['project.view', projectCenterSchema, 'project.summary.view', 'quality.review.view']
    expect(canViewNavigation('quality-review', p => granted.includes(p))).toBe(true)
    expect(canViewNavigation('project-center', p => granted.includes(p))).toBe(true)
    expect(canViewProjectTab(granted, 'summary')).toBe(true)
    expect(canViewProjectTab(granted.filter(p => p !== 'project.summary.view'), 'summary')).toBe(false)
  })
  it('新增权限按真实页面归类', () => {
    expect(permissionNavigationLocation({ code: 'quality.improvement.verify', module: '公司质量改善' }).entryKey).toBe('quality-review')
    expect(permissionNavigationLocation({ code: 'project.summary.publish', module: '项目总结' }).entryKey).toBe('project-center')
  })
})
