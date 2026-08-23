import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import OrganizationSettings from '../src/components/OrganizationSettings.vue'
import type { OrganizationDirectory } from '../src/types'

const directory: OrganizationDirectory = {
  organizations: [
    { id: 'org-ks', name: '昆山阿普顿自动化系统有限公司', projectCompanyCode: '7', modelCompanyCode: 'AK', crmCompanyName: '昆山阿普顿自动化系统有限公司', isActive: true, currentProjectSequence: 1, currentSerialSequence: 1 },
    { id: 'org-gz', name: '广州阿普顿自动化系统有限公司', projectCompanyCode: '3', modelCompanyCode: 'AG', crmCompanyName: '广州阿普顿自动化系统有限公司', isActive: true, currentProjectSequence: 1, currentSerialSequence: 1 },
  ],
  units: [
    { id: 'ks-division', organizationId: 'org-ks', code: 'KS-AUTO', name: '昆山自动化事业部', kind: 'BusinessDivision', isActive: true, sortOrder: 1 },
    { id: 'ks-department', organizationId: 'org-ks', parentUnitId: 'ks-division', code: 'KS-DESIGN', name: '昆山设计部', kind: 'Department', isActive: true, sortOrder: 1 },
    { id: 'gz-division', organizationId: 'org-gz', code: 'GZ-AUTO', name: '广州自动化事业部', kind: 'BusinessDivision', isActive: true, sortOrder: 1 },
  ],
  memberships: [
    { unitId: 'ks-department', username: 'ks-user', isPrimary: true },
    { unitId: 'gz-division', username: 'gz-user', isPrimary: true },
  ],
  managers: [{ unitId: 'ks-division', primaryManager: 'ks-user', collaborativeManagers: [] }],
  users: [
    { username: 'ks-user', displayName: '昆山设计员', role: 'Engineer', isActive: true },
    { username: 'gz-user', displayName: '广州设计员', role: 'Engineer', isActive: true },
    { username: 'new-user', displayName: '待分配人员', role: 'Engineer', isActive: true },
  ],
}

function buttonByText(wrapper: ReturnType<typeof mount>, text: string) {
  const button = wrapper.findAll('button').find(item => item.text().trim() === text)
  if (!button) throw new Error(`Button not found: ${text}`)
  return button
}

describe('OrganizationSettings', () => {
  beforeEach(() => localStorage.clear())
  afterEach(() => { document.body.innerHTML = '' })

  it('按当前公司隔离组织树，并将公司维护与组织架构分开', async () => {
    localStorage.setItem('pdm_active_organization', 'org-ks')
    const wrapper = mount(OrganizationSettings, {
      attachTo: document.body,
      props: {
        directory,
        pending: false,
        onSaveOrganization: vi.fn(),
        onSaveUnit: vi.fn(),
        onUpdateMemberships: vi.fn(),
        onUpdateManagers: vi.fn(),
      },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    const companySelect = wrapper.get('select[aria-label="选择当前公司"]')
    expect((companySelect.element as HTMLSelectElement).value).toBe('org-ks')
    expect(buttonByText(wrapper, '新建部门').text()).toBe('新建部门')
    expect(wrapper.text()).not.toContain('新增事业部')
    expect(wrapper.get('[aria-label="公司组织树"]').text()).toContain('昆山自动化事业部')
    expect(wrapper.get('[aria-label="公司组织树"]').text()).not.toContain('广州自动化事业部')
    expect(wrapper.get('[aria-label="组织详情"]').text()).toContain('昆山设计员')
    expect(wrapper.get('[aria-label="公司组织树"]').text()).toContain('未分配人员1')

    await companySelect.setValue('org-gz')
    await flushPromises()
    expect(localStorage.getItem('pdm_active_organization')).toBe('org-gz')
    expect(wrapper.get('[aria-label="公司组织树"]').text()).toContain('广州自动化事业部')
    expect(wrapper.get('[aria-label="公司组织树"]').text()).not.toContain('昆山自动化事业部')
    expect(wrapper.get('[aria-label="组织详情"]').text()).toContain('广州设计员')

    await buttonByText(wrapper, '公司管理').trigger('click')
    expect(wrapper.get('[aria-label="公司管理"]').text()).toContain('昆山阿普顿自动化系统有限公司')
    expect(wrapper.get('[aria-label="公司管理"]').text()).toContain('广州阿普顿自动化系统有限公司')
  })

  it('新建组织显示明确保存按钮且不再要求排序，所有层级均可设置负责人', async () => {
    const saveUnit = vi.fn().mockResolvedValue({ id: 'new-unit', organizationId: 'org-ks', code: 'KS-NEW', name: '新部门', kind: 'BusinessDivision', isActive: true, sortOrder: 2 })
    const wrapper = mount(OrganizationSettings, {
      attachTo: document.body,
      props: {
        directory,
        activeCompanyId: 'org-ks',
        pending: false,
        onSaveOrganization: vi.fn(),
        onSaveUnit: saveUnit,
        onUpdateMemberships: vi.fn(),
        onUpdateManagers: vi.fn(),
      },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    await buttonByText(wrapper, '新建部门').trigger('click')
    await flushPromises()
    const dialog = document.body.querySelector('.el-dialog')!
    expect(dialog.textContent).not.toContain('排序')
    expect(dialog.textContent).toContain('保存组织')
    const inputs = Array.from(dialog.querySelectorAll<HTMLInputElement>('input:not([disabled]):not([type="checkbox"])'))
    inputs[0].value = 'KS-NEW'; inputs[0].dispatchEvent(new Event('input'))
    inputs[1].value = '新部门'; inputs[1].dispatchEvent(new Event('input'))
    Array.from(dialog.querySelectorAll<HTMLButtonElement>('button')).find(button => button.textContent?.trim() === '保存组织')!.click()
    await flushPromises()
    expect(saveUnit).toHaveBeenCalledWith(expect.objectContaining({ organizationId: 'org-ks', code: 'KS-NEW', name: '新部门' }))

    await wrapper.get('[aria-label="组织架构树"]').findAll('[role="treeitem"]')[1].trigger('click')
    await flushPromises()
    expect(wrapper.get('[aria-label="组织详情"]').text()).toContain('设置负责人')
    expect(wrapper.get('[aria-label="组织详情"]').text()).toContain('主负责人')
  })
})
