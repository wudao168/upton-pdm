import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus, { ElMessageBox } from 'element-plus'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import BomHierarchyOverview from '../src/components/BomHierarchyOverview.vue'
import type { BomItem, ProjectSummary } from '../src/types'

const api = vi.hoisted(() => ({
  listBom: vi.fn(),
  listBomVersions: vi.fn(),
  listProjectBomHeaders: vi.fn(),
  generateProjectBomHeaderHierarchy: vi.fn(),
}))

vi.mock('../src/api', () => api)

function project(input: Partial<ProjectSummary> & Pick<ProjectSummary, 'id' | 'code' | 'name'>): ProjectSummary {
  return {
    owner: 'admin', stage: '设计', vaultName: 'vault', vaultLocation: '', releaseLocation: '', quantity: 1,
    serialNumbers: [], responsibleUsers: [], collaborativeProjectManagers: [], designers: [],
    canAssignExecutionUnit: true, canManageMainStaffing: true, canAssignDesigners: true, canReadContent: true,
    ...input,
  }
}

function item(drawingNumber: string, name: string): BomItem {
  return { id: drawingNumber, sequence: 1, drawingNumber, name, quantity: 1, unit: '001', revision: 'A1', complete: true }
}

describe('BomHierarchyOverview', () => {
  beforeEach(() => {
    api.listBom.mockReset()
    api.listBomVersions.mockReset()
    api.listProjectBomHeaders.mockReset()
    api.generateProjectBomHeaderHierarchy.mockReset()
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('shows one status row per hierarchy BOM without rendering material details', async () => {
    const root = project({ id: 'root', code: 'P-0301', name: '装配产线', rootProjectId: 'root', bomItemCategoryCode: '0301' })
    const child = project({ id: 'child', code: 'P-0302-01', name: '上料设备', parentProjectId: 'root', rootProjectId: 'root', childSequence: 1, bomItemCategoryCode: '0302' })
    api.listBom.mockImplementation(async (projectId: string, kind: string) => kind === 'Standard' ? [item(`${projectId}-WORK`, '工作区物料')] : [])
    api.listBomVersions.mockImplementation(async (projectId: string) => projectId === 'root' ? [{
      id: 'released-standard', projectId: 'root', kind: 'Standard', versionNumber: 2, label: 'S-V02', state: 'Released',
      items: [item('STD-001', '正式版物料')], createdBy: 'admin', createdAt: '2026-08-22', updatedBy: 'admin', updatedAt: '2026-08-22', releasedAt: '2026-08-22T08:00:00Z',
    }] : [])
    api.listProjectBomHeaders.mockImplementation(async (projectId: string) => [
      { projectId, kind: 'Master', materialId: `${projectId}-master`, materialCode: `${projectId.toUpperCase()}-M`, materialName: '项目主BOM', categoryCode: projectId === 'root' ? '0301' : '0302', approvalStatus: 'Approved', rowVersion: 1 },
      { projectId, kind: 'Standard', parentKind: 'Master', materialId: `${projectId}-standard`, materialCode: `${projectId.toUpperCase()}-S`, materialName: '标准件BOM', categoryCode: '0201', approvalStatus: 'Approved', rowVersion: 1 },
      { projectId, kind: 'NonStandard', parentKind: 'Master', materialId: `${projectId}-non-standard`, materialName: '非标件BOM申请', categoryCode: '0201', approvalStatus: 'Draft', rowVersion: 1 },
      { projectId, kind: 'Electrical', parentKind: 'Master', rowVersion: 0 },
    ])

    const wrapper = mount(BomHierarchyOverview, { props: { project: root, projects: [root, child], token: 'token' } })
    await flushPromises()

    expect(wrapper.get('[aria-label="BOM多级总览"]').text()).toContain('P-0301')
    expect(wrapper.text()).toContain('装配产线')
    expect(wrapper.text()).toContain('P-0302-01')
    expect(wrapper.text()).toContain('上料设备')
    expect(wrapper.text()).toContain('本级BOM料号')
    expect(wrapper.text()).toContain('上级BOM料号')
    expect(wrapper.text()).toContain('申请中')
    expect(wrapper.text()).toContain('待申请')
    expect(wrapper.findAll('tbody tr')).toHaveLength(8)
    const rootMaster = wrapper.findAll('tbody tr').find(row => row.text().includes('P-0301') && row.text().includes('三类汇总'))
    expect(rootMaster?.text()).toContain('ROOT-M')
    expect(rootMaster?.text()).toContain('项目主BOM')
    const rootStandard = wrapper.findAll('tbody tr').find(row => row.text().includes('标准件BOM') && row.text().includes('S-V02'))
    expect(rootStandard?.text()).toContain('0201')
    expect(rootStandard?.text()).toContain('1')
    expect(rootStandard?.text()).toContain('ROOT-S')
    expect(rootStandard?.text()).toContain('ROOT-M')
    expect(rootStandard?.text()).toContain('S-V02')
    expect(rootStandard?.text()).toContain('已发布')
    const childStandard = wrapper.findAll('tbody tr').find(row => row.text().includes('标准件BOM') && row.text().includes('工作区'))
    expect(childStandard?.text()).toContain('工作区')
    expect(childStandard?.text()).toContain('未发布')
    expect(wrapper.text()).not.toContain('STD-001')
    expect(wrapper.text()).not.toContain('root-WORK')
    expect(wrapper.text()).not.toContain('正式版物料')
    expect(wrapper.text()).not.toContain('工作区物料')
    expect(wrapper.text()).not.toContain('归属主BOM')

    expect(wrapper.find('.bom-overview__header').exists()).toBe(false)
    expect(wrapper.find('input[type="search"]').exists()).toBe(false)
    expect(wrapper.find('button').exists()).toBe(false)
  })

  it('confirms once and submits every missing BOM code application in the hierarchy', async () => {
    const root = project({ id: 'root', code: 'P-0302', name: '气密设备', rootProjectId: 'root', bomItemCategoryCode: '0302' })
    api.listBom.mockResolvedValue([])
    api.listBomVersions.mockResolvedValue([])
    api.listProjectBomHeaders.mockResolvedValue([
      { projectId: 'root', kind: 'Master', rowVersion: 0 },
      { projectId: 'root', kind: 'Standard', parentKind: 'Master', rowVersion: 0 },
      { projectId: 'root', kind: 'NonStandard', parentKind: 'Master', rowVersion: 0 },
      { projectId: 'root', kind: 'Electrical', parentKind: 'Master', rowVersion: 0 },
    ])
    api.generateProjectBomHeaderHierarchy.mockResolvedValue({ rootProjectId: 'root', expectedCount: 4, generatedCount: 4, existingCount: 0, headers: [] })
    vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue(undefined as never)

    const wrapper = mount(BomHierarchyOverview, {
      props: { project: root, projects: [root], token: 'token', editable: true },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    expect(wrapper.text()).toContain('待申请 4 个BOM料号')
    expect(wrapper.findAll('tbody tr')[0].text()).toContain('设备 0302')
    expect(wrapper.findAll('tbody tr')[1].text()).toContain('0201')
    await wrapper.get('.bom-overview__generation button').trigger('click')
    await flushPromises()

    expect(ElMessageBox.confirm).toHaveBeenCalledWith(
      expect.stringContaining('缺失的 4 个BOM容器'),
      '确认申请BOM料号',
      expect.objectContaining({ confirmButtonText: '确认申请' }),
    )
    expect(api.generateProjectBomHeaderHierarchy).toHaveBeenCalledWith('root', 'token')
  })
})
