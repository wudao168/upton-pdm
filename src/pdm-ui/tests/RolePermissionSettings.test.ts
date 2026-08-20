import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, describe, expect, it, vi } from 'vitest'
import RolePermissionSettings from '../src/components/RolePermissionSettings.vue'
import type { RolePermissionDirectory } from '../src/types'

const directory: RolePermissionDirectory = {
  permissions: [{ code: 'project.view', name: '查看项目', module: '项目管理', sensitive: false }],
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

  it('系统角色无删除入口，自定义角色显示删除入口', async () => {
    const wrapper = mount(RolePermissionSettings, {
      props: { directory, canEdit: true, pending: false, onSave: vi.fn(), onCreate: vi.fn(), onDelete: vi.fn() },
      global: { plugins: [ElementPlus] },
    })
    expect(wrapper.text()).not.toContain('删除角色')
    await wrapper.findAll('aside[aria-label="角色列表"] > button').find(button => button.text().includes('设计复核'))!.trigger('click')
    expect(wrapper.text()).toContain('删除角色')
  })
})
