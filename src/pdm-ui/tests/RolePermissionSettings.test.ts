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
  ],
  roles: [
    { role: 'Engineer', name: '工程师', description: '设计岗位', baseRole: 'Engineer', isSystem: true, isSystemAdministrator: false, permissions: ['project.view'], userCount: 1 },
    { role: 'custom-review', name: '设计复核', description: '自定义岗位', baseRole: 'Engineer', isSystem: false, isSystemAdministrator: false, permissions: ['project.view'], userCount: 0 },
    { role: 'Administrator', name: '系统管理员', description: '系统管理', baseRole: 'Administrator', isSystem: true, isSystemAdministrator: true, permissions: ['project.view'], userCount: 1 },
  ],
}

describe('RolePermissionSettings', () => {
  afterEach(() => { document.body.innerHTML = '' })

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
    expect(document.body.textContent).toContain('工程师 · 权限设置')
    expect(document.body.textContent).toContain('权限模块由系统功能目录自动生成')
    expect(document.body.textContent).toContain('3 个模块、3 项权限')
    expect(document.body.textContent).toContain('查看项目')
    expect(document.body.textContent).toContain('编辑图档')
    expect(document.body.textContent).toContain('查看新增模块')
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
})
