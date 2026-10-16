import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, describe, expect, it, vi } from 'vitest'
import RolePermissionSettings from '../src/components/RolePermissionSettings.vue'
import type { RolePermissionDirectory } from '../src/types'

const directory: RolePermissionDirectory = {
  permissions: [
    { code: 'project.view', name: '查看项目', module: '项目管理', sensitive: false },
    { code: 'document.edit', name: '编辑图档', module: '项目内容', sensitive: true },
    { code: 'future-module.view', name: '查看新增模块', module: '新增模块', sensitive: false },
    { code: 'future-module.publish', name: '发布新增模块', module: '新增模块', sensitive: true },
  ],
  roles: [
    { role: 'Engineer', name: '工程师', description: '设计岗位', baseRole: 'Engineer', isSystem: true, isSystemAdministrator: false, permissions: ['project.view'], userCount: 1 },
    { role: 'custom-review', name: '设计复核', description: '自定义岗位', baseRole: 'Engineer', isSystem: false, isSystemAdministrator: false, permissions: ['project.view'], userCount: 0 },
    { role: 'Administrator', name: '系统管理员', description: '系统管理', baseRole: 'Administrator', isSystem: true, isSystemAdministrator: true, permissions: ['project.view'], userCount: 1 },
  ],
}

describe('RolePermissionSettings', () => {
  afterEach(() => { document.body.innerHTML = '' })

  it.each([
    ['drawing-review.decide', ['project.content.view', 'project.view']],
    ['quality.inspection.upload', ['project.content.view', 'project.view']],
    ['document.recycle', ['project.content.view', 'project.view']],
    ['production.drawing.manage', ['project.content.view', 'project.view']],
    ['approval.emergency-substitute', ['project.content.view', 'project.view']],
    ['project.budget.settlement.edit', ['project.budget.settlement.view', 'project.budget.view', 'project.content.view', 'project.view']],
    ['material.manage', ['material.apply', 'material.view']],
    ['system.project-permission.edit', ['system.project-permission.view']],
    ['program-template.approve', ['program-template.view']],
    ['standard-library.manage', ['standard-library.view']],
    ['material-relation.publish', ['material-relation.view']],
  ])('授权 %s 补齐读取前提，撤销读取时清除依赖操作', async (operation, prerequisites) => {
    const codes = [operation, ...prerequisites]
    const roleDirectory: RolePermissionDirectory = {
      permissions: codes.map(code => ({ code, name: code, module: '测试权限', sensitive: false })),
      roles: [{ ...directory.roles[0]!, permissions: [] }],
    }
    const onSave = vi.fn().mockImplementation((_role, permissions) => Promise.resolve({ ...roleDirectory, roles: [{ ...roleDirectory.roles[0], permissions }] }))
    const wrapper = mount(RolePermissionSettings, {
      attachTo: document.body,
      props: { directory: roleDirectory, canEdit: true, pending: false, onSave, onCreate: vi.fn(), onDelete: vi.fn() },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text().trim() === '权限设置')!.trigger('click')
    await flushPromises()
    const checkbox = (code: string) => Array.from(document.body.querySelectorAll<HTMLElement>('.pdm-permission-cell'))
      .find(label => label.querySelector('code')?.textContent === code)!.querySelector<HTMLInputElement>('input')!
    checkbox(operation).click()
    await flushPromises()
    for (const code of codes) expect(checkbox(code).checked, code).toBe(true)
    checkbox(prerequisites[prerequisites.length - 1]!).click()
    await flushPromises()
    for (const code of codes) expect(checkbox(code).checked, code).toBe(false)
    Array.from(document.body.querySelectorAll<HTMLButtonElement>('.el-dialog button')).find(button => button.textContent?.trim() === '保存并立即生效')!.click()
    await flushPromises()
    expect(onSave).toHaveBeenCalledWith('Engineer', [])
    wrapper.unmount()
  })

  it('平台管理员展示固定管理权限且不能用作复制来源', async () => {
    const roleDirectory = { ...directory, roles: [...directory.roles, { ...directory.roles[0]!, role: 'platform_admin', baseRole: 'PlatformAdministrator', name: '平台管理员', isSystemAdministrator: true }] }
    const wrapper = mount(RolePermissionSettings, {
      attachTo: document.body,
      props: { directory: roleDirectory, canEdit: true, pending: false, onSave: vi.fn(), onCreate: vi.fn(), onDelete: vi.fn() },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()
    await wrapper.findAll('.el-table__row').find(row => row.text().includes('平台管理员'))!.find('button').trigger('click')
    await flushPromises()
    expect(document.body.textContent).toContain('平台管理员使用固定管理权限，不可修改或复制')
    expect(Array.from(document.body.querySelectorAll<HTMLInputElement>('.pdm-permission-cell input')).every(input => input.disabled)).toBe(true)
    expect(Array.from(document.body.querySelectorAll('button')).some(button => button.textContent?.trim() === '保存并立即生效')).toBe(false)
    await wrapper.get('[aria-label="复制角色新建"]').trigger('click')
    await flushPromises()
    expect(document.body.querySelector('.pdm-role-create-form select')?.textContent).not.toContain('平台管理员')
    wrapper.unmount()
  })

  it('可复制非管理员角色创建自定义角色', async () => {
    const onCreate = vi.fn().mockResolvedValue(directory)
    const wrapper = mount(RolePermissionSettings, {
      attachTo: document.body,
      props: { directory, canEdit: true, pending: false, onSave: vi.fn(), onCreate, onDelete: vi.fn() },
      global: { plugins: [ElementPlus] },
    })
    await wrapper.get('[aria-label="复制角色新建"]').trigger('click')
    await flushPromises()
    const dialog = document.body.querySelector('.el-dialog')!
    const name = dialog.querySelector<HTMLInputElement>('input')!
    name.value = '测试角色'; name.dispatchEvent(new Event('input'))
    const create = Array.from(dialog.querySelectorAll<HTMLButtonElement>('button')).find(button => button.textContent?.trim() === '创建角色')!
    create.click()
    await flushPromises()
    expect(onCreate).toHaveBeenCalledWith(expect.objectContaining({ name: '测试角色', sourceRoleCode: 'Engineer' }))
  })

  it('按系统功能目录显示单一权限入口并自动纳入新增模块', async () => {
    const wrapper = mount(RolePermissionSettings, {
      attachTo: document.body,
      props: { directory, canEdit: true, pending: false, onSave: vi.fn(), onCreate: vi.fn(), onDelete: vi.fn() },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()
    expect(wrapper.text()).toContain('角色编码说明用户数类型操作')
    expect(wrapper.text()).not.toContain('基础权限')
    expect(wrapper.text()).not.toContain('单据权限')
    const actions = wrapper.findAll('button').filter(button => button.text().trim() === '权限设置')
    expect(actions).toHaveLength(directory.roles.length)
    await actions[0].trigger('click')
    await flushPromises()
    expect(document.body.querySelector('.el-dialog__title')?.textContent).toBe('角色权限')
    expect(document.body.querySelector<HTMLInputElement>('.pdm-role-overview input')?.value).toBe('工程师')
    expect(document.body.textContent).toContain('权限模块由系统功能目录自动生成')
    expect(document.body.textContent).toContain('3 个模块、4 项权限')
    expect(document.body.textContent).toContain('查看项目')
    expect(document.body.querySelectorAll('.pdm-permission-sidebar button')).toHaveLength(3)
    expect(Array.from(document.body.querySelectorAll('.pdm-permission-table-wrap thead th')).map(item => item.textContent)).toEqual(['分类', '查看', '新增', '编辑', '删除/停用', '其他操作'])
    expect(document.body.querySelector('.pdm-permission-category-cell')?.textContent).toBe('项目管理')
    expect(document.body.textContent).not.toContain('编辑图档')
    const newModuleButton = Array.from(document.body.querySelectorAll<HTMLButtonElement>('.pdm-permission-sidebar button')).find(button => button.textContent?.includes('新增模块'))!
    newModuleButton.click()
    await flushPromises()
    expect(document.body.textContent).toContain('查看新增模块')
    expect(document.body.querySelectorAll('.pdm-permission-matrix td')[4]?.textContent).toContain('发布新增模块')
    expect(document.body.querySelector('.pdm-permission-category-cell')?.textContent).toBe('新增模块')
    expect(document.body.textContent).not.toContain('查看项目')
  })

  it('自定义角色的权限设置保留安全删除入口', async () => {
    const wrapper = mount(RolePermissionSettings, {
      attachTo: document.body,
      props: { directory, canEdit: true, pending: false, onSave: vi.fn(), onCreate: vi.fn(), onDelete: vi.fn() },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()
    const customRow = wrapper.findAll('.el-table__row').find(row => row.text().includes('设计复核'))!
    await customRow.findAll('button').find(button => button.text().trim() === '权限设置')!.trigger('click')
    await flushPromises()
    expect(document.body.textContent).toContain('删除角色')
  })

  it('保存权限后保留当前弹窗以便继续编辑', async () => {
    const onSave = vi.fn().mockResolvedValue(directory)
    const wrapper = mount(RolePermissionSettings, {
      attachTo: document.body,
      props: { directory, canEdit: true, pending: false, onSave, onCreate: vi.fn(), onDelete: vi.fn() },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text().trim() === '权限设置')!.trigger('click')
    await flushPromises()

    const save = Array.from(document.body.querySelectorAll<HTMLButtonElement>('.el-dialog button')).find(button => button.textContent?.trim() === '保存并立即生效')!
    save.click()
    await flushPromises()

    expect(onSave).toHaveBeenCalledWith('Engineer', ['project.view'])
    expect(document.body.querySelector('.pdm-role-permission-dialog')).not.toBeNull()
  })
})
