import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { beforeEach, describe, expect, it, vi } from 'vitest'
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
})
