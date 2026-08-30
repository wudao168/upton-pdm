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
    { id: 'ks-division', organizationId: 'org-ks', code: 'KS-AUTO', name: '昆山自动化事业部', kind: 'BusinessDivision', canManufacture: true, isActive: true, sortOrder: 1 },
    { id: 'ks-department', organizationId: 'org-ks', parentUnitId: 'ks-division', code: 'KS-DESIGN', name: '昆山设计部', kind: 'Department', canManufacture: false, isActive: true, sortOrder: 1 },
    { id: 'ks-other', organizationId: 'org-ks', code: 'KS-OTHER', name: '昆山其他部门', kind: 'BusinessDivision', canManufacture: false, isActive: true, sortOrder: 2 },
    { id: 'gz-division', organizationId: 'org-gz', code: 'GZ-AUTO', name: '广州自动化事业部', kind: 'BusinessDivision', canManufacture: true, isActive: true, sortOrder: 1 },
  ],
  memberships: [
    { unitId: 'ks-department', username: 'ks-user', isPrimary: true },
    { unitId: 'ks-other', username: 'existing-user', isPrimary: true },
    { unitId: 'gz-division', username: 'gz-user', isPrimary: true },
  ],
  managers: [{ unitId: 'ks-division', primaryManager: 'ks-user', collaborativeManagers: [] }],
  users: [
    { username: 'ks-user', displayName: '昆山设计员', role: 'Engineer', isActive: true, companyId: 'org-ks' },
    { username: 'gz-user', displayName: '广州设计员', role: 'Engineer', isActive: true, companyId: 'org-gz' },
    { username: 'new-user', displayName: '待分配人员', role: 'Engineer', isActive: true, companyId: 'org-ks' },
    { username: 'existing-user', displayName: '已分配人员', role: 'Engineer', isActive: true, companyId: 'org-ks' },
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

  it('公司直属部门可标记为制造部门并承接项目', async () => {
    const saveUnit = vi.fn().mockImplementation(input => Promise.resolve({ id: 'ks-division', ...input }))
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

    await buttonByText(wrapper, '编辑').trigger('click')
    await flushPromises()
    const dialog = document.body.querySelector('.el-dialog')!
    const manufacturingLabel = Array.from(dialog.querySelectorAll('label')).find(label => label.textContent?.includes('制造部门'))
    expect(manufacturingLabel?.textContent).toContain('可承接项目')
    const manufacturingCheckbox = manufacturingLabel!.querySelector<HTMLInputElement>('input[type="checkbox"]')!
    manufacturingCheckbox.checked = true
    manufacturingCheckbox.dispatchEvent(new Event('change'))
    Array.from(dialog.querySelectorAll<HTMLButtonElement>('button')).find(button => button.textContent?.trim() === '保存组织')!.click()
    await flushPromises()

    expect(saveUnit).toHaveBeenCalledWith(expect.objectContaining({ id: 'ks-division', canManufacture: true }))
  })

  it('可向当前组织添加人员并保留原归属', async () => {
    const updateMemberships = vi.fn().mockResolvedValue(directory)
    const wrapper = mount(OrganizationSettings, {
      attachTo: document.body,
      props: {
        directory,
        activeCompanyId: 'org-ks',
        pending: false,
        onSaveOrganization: vi.fn(),
        onSaveUnit: vi.fn(),
        onUpdateMemberships: updateMemberships,
        onUpdateManagers: vi.fn(),
      },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    await buttonByText(wrapper, '添加人员').trigger('click')
    await flushPromises()
    const select = wrapper.findComponent({ name: 'ElSelect' })
    expect(select.props('filterable')).toBe(true)
    expect(select.findAllComponents({ name: 'ElOption' }).map(option => option.props('label'))).toContain('已分配人员')
    select.vm.$emit('update:modelValue', 'existing-user')
    await flushPromises()
    Array.from(document.body.querySelectorAll<HTMLButtonElement>('button')).find(button => button.textContent?.trim() === '添加到本组织')!.click()
    await flushPromises()

    expect(updateMemberships).toHaveBeenCalledWith('existing-user', ['ks-other', 'ks-division'], 'ks-other')
  })

  it('清空主负责人后可删除部门负责人', async () => {
    const updateManagers = vi.fn().mockResolvedValue(directory)
    const wrapper = mount(OrganizationSettings, {
      attachTo: document.body,
      props: {
        directory,
        activeCompanyId: 'org-ks',
        pending: false,
        onSaveOrganization: vi.fn(),
        onSaveUnit: vi.fn(),
        onUpdateMemberships: vi.fn(),
        onUpdateManagers: updateManagers,
      },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    await buttonByText(wrapper, '设置负责人').trigger('click')
    await flushPromises()
    Array.from(document.body.querySelectorAll<HTMLButtonElement>('button')).find(button => button.textContent?.trim() === '清空负责人')!.click()
    await flushPromises()
    Array.from(document.body.querySelectorAll<HTMLButtonElement>('button')).find(button => button.textContent?.trim() === '保存负责人')!.click()
    await flushPromises()

    expect(updateManagers).toHaveBeenCalledWith('ks-division', '', [])
  })
})
