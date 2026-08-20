import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, describe, expect, it, vi } from 'vitest'
import UserSettings from '../src/components/UserSettings.vue'
import type { OrganizationDirectory, RolePermissionDirectory } from '../src/types'

const directory: OrganizationDirectory = {
  organizations: [{ id: 'org-1', name: '昆山阿普顿自动化系统有限公司', projectCompanyCode: '7', modelCompanyCode: 'AK', crmCompanyName: '', isActive: true, currentProjectSequence: 1, currentSerialSequence: 1 }],
  units: [{ id: 'unit-1', organizationId: 'org-1', code: 'DESIGN', name: '设计部', kind: 'BusinessDivision', isActive: true, sortOrder: 1 }],
  memberships: [{ unitId: 'unit-1', username: 'engineer', isPrimary: true }],
  managers: [],
  users: [{ username: 'engineer', displayName: '设计工程师', role: 'Engineer', isActive: true }],
}

const roles: RolePermissionDirectory = {
  permissions: [{ code: 'project.view', name: '查看项目', module: '项目管理', sensitive: false }],
  roles: [
    { role: 'Engineer', name: '工程师', description: '设计岗位', baseRole: 'Engineer', isSystem: true, isSystemAdministrator: false, permissions: ['project.view'], userCount: 1 },
    { role: 'Administrator', name: '系统管理员', description: '系统管理', baseRole: 'Administrator', isSystem: true, isSystemAdministrator: true, permissions: ['project.view'], userCount: 1 },
  ],
}

describe('UserSettings', () => {
  afterEach(() => { document.body.innerHTML = '' })

  it('在一个页面提供用户、角色权限和组织关系，并可创建用户', async () => {
    const saveUser = vi.fn().mockResolvedValue({ username: 'new-user', displayName: '新用户', role: 'Engineer', isActive: true })
    const wrapper = mount(UserSettings, {
      attachTo: document.body,
      props: {
        directory,
        roleDirectory: roles,
        permissions: ['settings.organization.manage', 'system.role.view', 'system.role.edit'],
        currentUsername: 'admin',
        pending: false,
        onSaveUser: saveUser,
        onResetPassword: vi.fn(),
        onSaveRolePermissions: vi.fn(),
        onCreateRole: vi.fn(),
        onDeleteRole: vi.fn(),
        onSaveOrganization: vi.fn(),
        onSaveUnit: vi.fn(),
        onUpdateMemberships: vi.fn(),
        onUpdateManagers: vi.fn(),
      },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    expect(wrapper.find('.pdm-pagebar').exists()).toBe(false)
    expect(wrapper.get('[aria-label="用户设置功能"]').text()).toContain('用户角色权限组织关系公司管理')
    expect(wrapper.get('[aria-label="用户列表"]').text()).toContain('工程师')
    expect(wrapper.get('[aria-label="用户列表"]').text()).toContain('设计部')

    await wrapper.findAll('button').find(button => button.text() === '新建用户')!.trigger('click')
    await flushPromises()
    const dialog = document.body.querySelector('.el-dialog')!
    const inputs = Array.from(dialog.querySelectorAll<HTMLInputElement>('input'))
    inputs[0].value = 'new-user'; inputs[0].dispatchEvent(new Event('input'))
    inputs[1].value = '新用户'; inputs[1].dispatchEvent(new Event('input'))
    const save = Array.from(dialog.querySelectorAll<HTMLButtonElement>('button')).find(button => button.textContent?.trim() === '保存')!
    save.click()
    await flushPromises()

    expect(saveUser).toHaveBeenCalledWith(expect.objectContaining({ username: 'new-user', displayName: '新用户', password: '11111111' }), true)
  })
})
