import { expect, type Page, type TestInfo } from '@playwright/test'
import type { AuthorizationDirectory, AuthorizationSettings } from '../../src/authorization'
import type { PermissionDefinition } from '../../src/types'

/** 系统权限、消息提示和项目岗位均通过同一权限中心维护。 */
export async function exerciseAuthorizationCenter(page: Page, testInfo: TestInfo, mode: 'notice' | 'directory' | 'positions' | 'preserve') {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  const target: PermissionDefinition = mode === 'notice'
    ? { code: 'material.notify', name: '接收料品管理消息提示', module: '料品管理', scope: 'system', sensitive: false, dependencies: ['material.view'] }
    : mode === 'positions'
      ? { code: 'project.change.create', name: '创建项目变更', module: '项目变更', scope: 'project', sensitive: true, dependencies: ['project.view'] }
      : { code: 'document.edit', name: '编辑项目图档', module: '项目内容', scope: 'project', sensitive: true, dependencies: ['project.content.view'] }
  const directory: AuthorizationDirectory = {
    settings: { revision: 7, migratedAt: '', templates: [
      { key: 'reviewer', name: '验收角色', description: '明确授权的测试角色', roleCode: null, isActive: true, projectAssignable: true, grants: [{ permission: 'project.view', scopes: ['company'] }] },
      { key: 'budget', name: '预算角色', description: '', roleCode: null, isActive: true, projectAssignable: true, grants: [{ permission: 'project.budget.view', scopes: ['Custom:budget-review'] }] },
    ], positions: [{ key: 'Custom:change-review', name: '变更复核', description: '', scope: 'self', isSystem: false, isActive: true, sortOrder: 1 }], assignments: [{ id: 'assignment-existing', username: 'reviewer', templateKey: 'budget', companyId: 'org-ks', scope: 'project', targetId: 'existing-project' }] },
    permissions: [
      { code: 'project.view', name: '查看项目', module: '项目', scope: 'project', sensitive: false, dependencies: [] },
      { code: 'project.content.view', name: '查看项目内容', module: '项目内容', scope: 'project', sensitive: false, dependencies: ['project.view'] },
      { code: 'project.budget.view', name: '查看项目预算', module: '项目预算', scope: 'project', sensitive: true, dependencies: ['project.view'] },
      { code: 'material.view', name: '查看料品管理', module: '料品管理', scope: 'system', sensitive: false, dependencies: [] },
      { code: 'future-module.view', name: '查看新增模块', module: '新增模块', scope: 'system', sensitive: false, dependencies: [] }, target,
    ], scopes: { company: '授权范围内全部', 'Custom:change-review': '变更复核', 'Custom:budget-review': '预算复核' }, users: [], projects: [], departments: [], canEdit: true,
  }
  const originalBudget = structuredClone(directory.settings.templates[1])
  const originalAssignments = structuredClone(directory.settings.assignments)
  const originalPositions = structuredClone(directory.settings.positions)
  let saved: AuthorizationSettings | undefined
  await page.route('**/api/authorization-center', async route => {
    if (route.request().method() === 'PUT') {
      saved = route.request().postDataJSON()
      directory.settings = { ...saved!, revision: 8 }
      return route.fulfill({ json: directory.settings })
    }
    return route.fulfill({ json: directory })
  })
  await page.setViewportSize({ width: 1600, height: 1000 })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await page.getByRole('button', { name: '系统设置', exact: true }).click()
  await page.getByRole('region', { name: '系统管理', exact: true }).getByRole('button', { name: '用户设置', exact: true }).click()
  await page.getByLabel('用户设置功能').getByRole('button', { name: '权限中心', exact: true }).click()
  const panel = page.getByRole('region', { name: '权限中心', exact: true })
  await expect(panel).toContainText('查看新增模块')
  const checkbox = panel.getByRole('checkbox', { name: `授权${target.name}`, exact: true })
  await expect(checkbox).not.toBeChecked()
  await checkbox.check()
  if (mode === 'directory') {
    const view = panel.getByRole('checkbox', { name: '授权查看项目内容', exact: true })
    await expect(view).toBeChecked()
    await view.uncheck()
    await expect(checkbox).not.toBeChecked()
    await checkbox.check()
  }
  if (mode === 'positions') {
    await panel.getByRole('button', { name: `${target.name}适用关系`, exact: true }).click()
    const dialog = page.getByRole('dialog', { name: `适用关系 · ${target.name}`, exact: true })
    await dialog.getByText('其他适用条件', { exact: false }).click()
    await dialog.getByRole('checkbox', { name: '变更复核', exact: true }).check()
    await dialog.getByRole('button', { name: '完成', exact: true }).click()
  }
  await panel.getByRole('button', { name: '保存生效', exact: true }).click()
  await page.getByRole('dialog', { name: '确认权限变更', exact: true }).getByRole('button', { name: '保存生效', exact: true }).click()
  await expect.poll(() => saved?.templates[0]?.grants.map(grant => grant.permission)).toContain(target.code)
  expect(saved?.templates[1]).toEqual(originalBudget)
  expect(saved?.assignments).toEqual(originalAssignments)
  expect(saved?.positions).toEqual(originalPositions)
  if (mode === 'notice') expect(saved?.templates[0]?.grants.map(grant => grant.permission)).toContain('material.view')
  if (mode === 'positions') expect(saved?.templates[0]?.grants.find(grant => grant.permission === target.code)?.scopes).toContain('Custom:change-review')
  await expect(panel).toContainText('配置版本 8')
  await page.screenshot({ path: testInfo.outputPath(`permission-center-${mode}-desktop.png`) })
  await page.setViewportSize({ width: 834, height: 900 })
  await expect(checkbox).toBeVisible()
  await page.screenshot({ path: testInfo.outputPath(`permission-center-${mode}-narrow.png`) })
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
}
