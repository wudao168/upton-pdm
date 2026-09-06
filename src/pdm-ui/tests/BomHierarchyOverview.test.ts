import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus, { ElMessageBox } from 'element-plus'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import BomHierarchyOverview from '../src/components/BomHierarchyOverview.vue'
import type { BomItem, ProjectSummary } from '../src/types'

const api = vi.hoisted(() => ({
  listBom: vi.fn(), listBomVersions: vi.fn(), listProjectBomHeaders: vi.fn(),
  previewProjectBomU9Sync: vi.fn(), executeProjectBomU9Sync: vi.fn(),
}))

vi.mock('../src/api', () => api)

function project(input: Partial<ProjectSummary> & Pick<ProjectSummary, 'id' | 'code' | 'name'>): ProjectSummary {
  return {
    owner: 'admin', stage: '设计', vaultName: 'vault', vaultLocation: '', releaseLocation: '', quantity: 1,
    serialNumbers: [], responsibleUsers: [], collaborativeProjectManagers: [], designers: [],
    canAssignExecutionUnit: true, canManageMainStaffing: true, canAssignDesigners: true, canReadContent: true, ...input,
  }
}

function item(drawingNumber: string, name: string): BomItem {
  return { id: drawingNumber, sequence: 1, drawingNumber, name, quantity: 1, unit: '001', revision: 'A1', complete: true }
}

describe('BomHierarchyOverview', () => {
  beforeEach(() => {
    Object.values(api).forEach(mock => mock.mockReset())
    api.previewProjectBomU9Sync.mockImplementation(async (projectId: string, kind: string) => ({
      projectId, kind, itemCode: 'U9-CODE', componentCount: 0, state: 'UpToDate', writePreview: null,
    }))
  })
  afterEach(() => vi.restoreAllMocks())

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
    expect(wrapper.text()).toContain('自动同步中')
    expect(wrapper.text()).toContain('待BOM发布')
    expect(wrapper.findAll('tbody tr')).toHaveLength(8)
    const rootMaster = wrapper.findAll('tbody tr').find(row => row.text().includes('P-0301') && row.text().includes('三类汇总'))
    expect(rootMaster?.text()).toContain('ROOT-M')
    expect(rootMaster?.text()).toContain('项目主BOM')
    expect(rootMaster?.findAll('td')[4].text()).toBe('2')
    expect(rootMaster?.findAll('td')[7].text()).toBe('正常')
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

  it('does not reload the hierarchy when background summaries only replace non-hierarchy fields', async () => {
    const root = project({
      id: 'root', code: 'P-0302', name: '气密设备', rootProjectId: 'root',
      bomItemCategoryCode: '0302', documentCount: 7, modelDocumentCount: 4, drawingDocumentCount: 3,
    })
    api.listBom.mockResolvedValue([])
    api.listBomVersions.mockResolvedValue([])
    api.listProjectBomHeaders.mockResolvedValue([])
    const wrapper = mount(BomHierarchyOverview, { props: { project: root, projects: [root], token: 'token' } })
    await flushPromises()
    expect(api.listBom).toHaveBeenCalledTimes(3)

    const refreshedSummary = { ...root, documentCount: 8, modelDocumentCount: 5 }
    await wrapper.setProps({ project: refreshedSummary, projects: [refreshedSummary] })
    await flushPromises()

    expect(api.listBom).toHaveBeenCalledTimes(3)
    expect(api.listBomVersions).toHaveBeenCalledTimes(1)
    expect(api.listProjectBomHeaders).toHaveBeenCalledTimes(1)
    expect(wrapper.findAll('tbody tr')).toHaveLength(4)
  })

  it('shows automatic application status without a manual BOM code application entry', async () => {
    const root = project({ id: 'root', code: 'P-0302', name: '气密设备', rootProjectId: 'root', bomItemCategoryCode: '0302' })
    api.listBom.mockResolvedValue([])
    api.listBomVersions.mockResolvedValue([])
    api.listProjectBomHeaders.mockResolvedValue([
      { projectId: 'root', kind: 'Master', rowVersion: 0 }, { projectId: 'root', kind: 'Standard', parentKind: 'Master', rowVersion: 0 },
      { projectId: 'root', kind: 'NonStandard', parentKind: 'Master', rowVersion: 0 }, { projectId: 'root', kind: 'Electrical', parentKind: 'Master', rowVersion: 0 },
    ])
    const wrapper = mount(BomHierarchyOverview, { props: { project: root, projects: [root], token: 'token', editable: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()

    expect(wrapper.text()).toContain('待BOM发布后自动生成并同步 4 个BOM料号')
    expect(wrapper.text()).toContain('自动流程')
    expect(wrapper.findAll('tbody tr')[0].text()).toContain('设备 0302')
    expect(wrapper.findAll('tbody tr')[1].text()).toContain('0201')
    expect(wrapper.find('.bom-overview__generation button').exists()).toBe(false)
  })

  it('excludes the main project category BOMs when child projects exist', async () => {
    const root = project({ id: 'root', code: 'P-0302', name: '气密设备', rootProjectId: 'root', bomItemCategoryCode: '0302' })
    const child = project({ id: 'child', code: 'P-0302-1', name: '泵组单元', parentProjectId: 'root', rootProjectId: 'root', childSequence: 1, bomItemCategoryCode: '0302' })
    api.listBom.mockResolvedValue([])
    api.listBomVersions.mockResolvedValue([])
    api.listProjectBomHeaders.mockImplementation(async (projectId: string) => [
      { projectId, kind: 'Master', rowVersion: 0 },
      { projectId, kind: 'Standard', parentKind: 'Master', rowVersion: 0 },
      { projectId, kind: 'NonStandard', parentKind: 'Master', rowVersion: 0 },
      { projectId, kind: 'Electrical', parentKind: 'Master', rowVersion: 0 },
    ])
    const wrapper = mount(BomHierarchyOverview, {
      props: { project: root, projects: [root, child], token: 'token', editable: true },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    expect(wrapper.text()).toContain('待BOM发布后自动生成并同步 5 个BOM料号')
    const rootRows = wrapper.findAll('tbody tr').slice(0, 4)
    expect(rootRows[0].text()).toContain('待BOM发布')
    expect(rootRows.slice(1).every(row => row.text().includes('不生成'))).toBe(true)
    expect(wrapper.find('.bom-overview__generation button').exists()).toBe(false)
  })

  it('shows an unfinished automatic empty-BOM creation without requiring a second manual operation', async () => {
    const root = project({ id: 'root', code: 'P700001', name: '气密设备', rootProjectId: 'root', bomItemCategoryCode: '0302' })
    api.listBom.mockImplementation(async (_projectId: string, kind: string) => kind === 'Standard' ? [item('01020000057', '阀岛')] : [])
    api.listBomVersions.mockResolvedValue([])
    api.listProjectBomHeaders.mockResolvedValue([
      { projectId: 'root', kind: 'Master', materialId: 'material-master', materialCode: '03020000009', rowVersion: 1, applicationStatus: 'Approved' },
      { projectId: 'root', kind: 'Standard', parentKind: 'Master', materialId: 'material-standard', materialCode: '02010000101', rowVersion: 1, applicationStatus: 'Approved' },
      { projectId: 'root', kind: 'NonStandard', parentKind: 'Master', materialId: 'material-nonstandard', materialCode: '02010000116', rowVersion: 1, applicationStatus: 'Approved' },
      { projectId: 'root', kind: 'Electrical', parentKind: 'Master', materialId: 'material-electrical', materialCode: '02010000117', rowVersion: 1, applicationStatus: 'Approved' },
    ])
    api.previewProjectBomU9Sync.mockResolvedValue({ projectId: 'root', kind: 'Master', itemCode: '03020000009', componentCount: 0, state: 'CreateRequired', writePreview: {
      operation: 0, path: '/webapi/BOM/Create', requestPreview: '[]', requestSha256: 'sha-1', baselineSha256: 'base-1',
      requiredConfirmation: '创建 03020000009/A1', addedComponentCount: 0, retainedHistoricalComponentCount: 0, generatedAt: '2026-08-24T12:00:00Z',
    } })
    api.executeProjectBomU9Sync.mockResolvedValue({})
    vi.spyOn(ElMessageBox, 'confirm')
    const wrapper = mount(BomHierarchyOverview, { props: { project: root, projects: [root], token: 'token', editable: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()

    expect(wrapper.text()).toContain('U9C料品')
    expect(wrapper.text()).toContain('U9C BOM')
    expect(wrapper.text()).toContain('已回写')
    expect(wrapper.text()).toContain('待首个正式子件')
    expect(api.previewProjectBomU9Sync).toHaveBeenCalledWith('root', 'Master', 'token')
    await wrapper.find('button.bom-overview__sync').trigger('click')
    await flushPromises()
    expect(api.executeProjectBomU9Sync).not.toHaveBeenCalled()
    expect(ElMessageBox.confirm).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('待首个正式子件')
  })

  it('shows the automatic empty-BOM result without requiring a second operation', async () => {
    const root = project({ id: 'root', code: 'P700001', name: '气密设备', rootProjectId: 'root', bomItemCategoryCode: '0302' })
    api.listBom.mockResolvedValue([])
    api.listBomVersions.mockResolvedValue([])
    api.listProjectBomHeaders.mockResolvedValue([
      { projectId: 'root', kind: 'Master', materialId: 'material-master', materialCode: '03020000009', rowVersion: 1, applicationStatus: 'Approved' },
    ])
    api.previewProjectBomU9Sync.mockResolvedValue({
      projectId: 'root', kind: 'Master', itemCode: '03020000009', componentCount: 0,
      state: 'AwaitingApproval', writePreview: null,
    })
    const wrapper = mount(BomHierarchyOverview, {
      props: { project: root, projects: [root], token: 'token', editable: true },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    expect(api.previewProjectBomU9Sync).toHaveBeenCalledWith('root', 'Master', 'token')
    expect(api.executeProjectBomU9Sync).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('待BOM审核')
  })

  it('shows the PLM total, U9C total, and upload delta before synchronization', async () => {
    const root = project({ id: 'root', code: 'P700005-3', name: '测试项目', rootProjectId: 'root', bomItemCategoryCode: '0302' })
    api.listBom.mockResolvedValue([])
    api.listBomVersions.mockResolvedValue([])
    api.listProjectBomHeaders.mockResolvedValue([
      { projectId: 'root', kind: 'Standard', parentKind: 'Master', materialId: 'material-standard', materialCode: '02011000000', rowVersion: 1, applicationStatus: 'Approved' },
    ])
    api.previewProjectBomU9Sync.mockImplementation(async (_projectId: string, kind: string) => kind === 'Standard' ? ({
      projectId: 'root', kind, itemCode: '02011000000', componentCount: 1, state: 'ModifyRequired',
      writePreview: {
        operation: 1, path: '/webapi/BOM/Modify', requestPreview: '[]', requestSha256: 'sha-1', baselineSha256: 'base-1',
        requiredConfirmation: '修改 02011000000/A1', addedComponentCount: 1, retainedHistoricalComponentCount: 1,
        quantityReconciliations: [{ itemCode: '01020000056', issueUomCode: '001', parentQty: 1, plmApprovedTotal: 4, u9ExistingTotal: 1, uploadDelta: 3 }],
        generatedAt: '2026-09-05T08:00:00Z',
      },
    }) : ({ projectId: 'root', kind, itemCode: '', componentCount: 0, state: 'UpToDate', writePreview: null }))
    const confirm = vi.spyOn(ElMessageBox, 'confirm').mockRejectedValue(new Error('cancel'))
    const wrapper = mount(BomHierarchyOverview, {
      props: { project: root, projects: [root], token: 'token', editable: true },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    await wrapper.get('button.bom-overview__sync').trigger('click')
    await flushPromises()

    expect(confirm).toHaveBeenCalledWith(
      expect.stringContaining('PLM审核总量 4 / U9C现有总量 1 / 本次上传 3'),
      '确认同步审核通过的子件', expect.any(Object),
    )
    expect(api.executeProjectBomU9Sync).not.toHaveBeenCalled()
  })

})
