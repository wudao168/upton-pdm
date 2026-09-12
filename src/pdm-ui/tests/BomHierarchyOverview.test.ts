import { flushPromises, mount } from '@vue/test-utils'
import type { VNode } from 'vue'
import ElementPlus, { ElMessageBox } from 'element-plus'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import BomHierarchyOverview from '../src/components/BomHierarchyOverview.vue'
import type { BomItem, ProjectSummary } from '../src/types'

const api = vi.hoisted(() => ({
  listBom: vi.fn(), listBomVersions: vi.fn(), listProjectBomHeaders: vi.fn(), listReleasePackages: vi.fn(),
  previewProjectBomU9Sync: vi.fn(), executeProjectBomU9Sync: vi.fn(),
  retryProjectBomHeaderAutomatic: vi.fn(),
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
    api.listReleasePackages.mockResolvedValue([])
    api.previewProjectBomU9Sync.mockImplementation(async (projectId: string, kind: string) => ({
      projectId, kind, itemCode: 'U9-CODE', componentCount: 0, state: 'UpToDate', writePreview: null,
    }))
  })
  afterEach(() => vi.restoreAllMocks())

  it('collapses unnumbered children, preserves all master rows and counts, and remembers toggles on refresh', async () => {
    const root = project({ id: 'root', code: 'ROOT', name: '主项目' })
    const child = project({ id: 'child', code: 'CHILD', name: '子项目', parentProjectId: 'root', rootProjectId: 'root' })
    const grandchild = project({ id: 'grandchild', code: 'GRANDCHILD', name: '下级项目', parentProjectId: 'child', rootProjectId: 'root' })
    api.listBom.mockResolvedValue([item('A', '物料')])
    api.listBomVersions.mockResolvedValue([])
    api.listProjectBomHeaders.mockResolvedValue([])
    const wrapper = mount(BomHierarchyOverview, { props: { project: root, projects: [root, child, grandchild], token: 'token', editable: true } })
    await flushPromises()
    expect(wrapper.findAll('tbody tr')).toHaveLength(6)
    expect(wrapper.findAll('.is-master-row')).toHaveLength(3)
    expect(wrapper.text()).toContain('待BOM发布后自动生成并同步 9 个BOM料号')
    expect(wrapper.find('[aria-label="进入CHILD的标准件BOM"]').exists()).toBe(false)
    await wrapper.get('[aria-label="展开CHILD的三类BOM"]').trigger('click')
    expect(wrapper.findAll('tbody tr')).toHaveLength(9)
    expect(wrapper.get('[aria-label="折叠CHILD的三类BOM"]').attributes('aria-expanded')).toBe('true')
    await wrapper.get('[aria-label="刷新多级BOM"]').trigger('click')
    await flushPromises()
    expect(wrapper.findAll('tbody tr')).toHaveLength(9)
    expect(wrapper.text()).toContain('待BOM发布后自动生成并同步 9 个BOM料号')
    await wrapper.get('[aria-label="折叠CHILD的三类BOM"]').trigger('click')
    expect(wrapper.findAll('tbody tr')).toHaveLength(6)
    expect(wrapper.findAll('.is-master-row')).toHaveLength(3)
    expect(api.executeProjectBomU9Sync).not.toHaveBeenCalled()
    expect(api.retryProjectBomHeaderAutomatic).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it.each(['Master', 'Standard'])('expands a child when its %s receives a material code, but not for a draft material', async kind => {
    const root = project({ id: 'root', code: 'ROOT', name: '主项目' })
    const child = project({ id: 'child', code: 'CHILD', name: '子项目', parentProjectId: 'root', rootProjectId: 'root' })
    let materialCode: string | undefined
    api.listBom.mockResolvedValue([])
    api.listBomVersions.mockResolvedValue([])
    api.listProjectBomHeaders.mockImplementation(async (projectId: string) => projectId === 'child'
      ? [{ projectId, kind, materialId: 'draft', materialCode, rowVersion: 1 }] : [])
    const wrapper = mount(BomHierarchyOverview, { props: { project: root, projects: [root, child], token: 'token' } })
    await flushPromises()
    expect(wrapper.findAll('tbody tr')).toHaveLength(5)
    materialCode = '02011000001'
    await wrapper.get('[aria-label="刷新多级BOM"]').trigger('click')
    await flushPromises()
    expect(wrapper.findAll('tbody tr')).toHaveLength(8)
    expect(wrapper.get('[aria-label="折叠CHILD的三类BOM"]').attributes('aria-expanded')).toBe('true')
    wrapper.unmount()
  })

  it.each([
    ['已发布', '2026-09-08T08:21:00Z', undefined, '2026-09-08T08:21:00Z'],
    ['已发布', '2026-09-08T08:21:00Z', '2026-09-09T08:00:00Z', '2026-09-09T08:00:00Z'],
    ['已发布', '2026-09-09T08:21:00Z', '2026-09-08T08:00:00Z', '2026-09-09T08:21:00Z'],
    ['审批中', '2026-09-08T08:21:00Z', undefined, undefined],
    ['发布失败', '2026-09-08T08:21:00Z', undefined, undefined],
    ['已发布', undefined, undefined, undefined],
  ])('includes only completed long-lead release dates without changing formal status (%s, %s)', async (state, publishedAt, formalAt, expectedAt) => {
    const root = project({ id: 'root', code: 'P-ROOT', name: '设备' })
    api.listBom.mockResolvedValue([item('A', '测试物料')])
    api.listProjectBomHeaders.mockResolvedValue([])
    api.listBomVersions.mockResolvedValue(formalAt ? [{ kind: 'Standard', state: 'Released', versionNumber: 1, label: 'S-V01', releasedAt: formalAt }] : [])
    api.listReleasePackages.mockResolvedValue([{ scope: 'StandardLongLead', state, publishedAt }])
    const wrapper = mount(BomHierarchyOverview, { props: { project: root, projects: [root], token: 'token' } })
    await flushPromises()
    const rows = wrapper.findAll('tbody tr')
    const date = expectedAt ? new Date(expectedAt).toLocaleString('zh-CN', { hour12: false }) : '—'
    expect(rows[0].findAll('td')[8].text()).toBe(date)
    expect(rows[1].findAll('td')[8].text()).toBe(date)
    expect(rows[1].findAll('td')[6].text()).toBe(formalAt ? '已发布' : '未发布')
    expect(rows[2].findAll('td')[8].text()).toBe('—')
    expect(rows[3].findAll('td')[8].text()).toBe('—')
    wrapper.unmount()
  })

  it('opens the selected project and category even for an empty read-only BOM', async () => {
    const root = project({ id: 'root', code: 'P-ROOT', name: '产线' })
    const child = project({ id: 'child', code: 'P-CHILD', name: '设备', parentProjectId: 'root', rootProjectId: 'root' })
    api.listBom.mockResolvedValue([])
    api.listBomVersions.mockResolvedValue([])
    api.listProjectBomHeaders.mockResolvedValue([])
    const wrapper = mount(BomHierarchyOverview, { props: { project: root, projects: [root, child], token: 'token', editable: false } })
    await flushPromises()
    await wrapper.get('button[aria-label="展开P-CHILD的三类BOM"]').trigger('click')
    for (const [label, kind] of [['标准件BOM', 'Standard'], ['非标件BOM', 'NonStandard'], ['电气BOM', 'Electrical']]) {
      await wrapper.get(`button[aria-label="进入P-CHILD的${label}"]`).trigger('click')
      expect(wrapper.emitted('openBom')?.at(-1)).toEqual(['child', kind])
    }
    expect(api.executeProjectBomU9Sync).not.toHaveBeenCalled()
    wrapper.unmount()
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
    expect(wrapper.text()).toContain('待同步')
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
    expect(wrapper.findAll('.bom-overview__link')).toHaveLength(6)
    expect(wrapper.findAll('button')).toHaveLength(8)
    expect(wrapper.get('button[aria-label="刷新多级BOM"]').text()).toBe('刷新')
  })

  it('refreshes cached BOM data and U9 state without writes and blocks repeat clicks', async () => {
    const root = project({ id: 'root', code: 'P-0302', name: '设备', rootProjectId: 'root' })
    api.listBom.mockResolvedValue([])
    api.listBomVersions.mockResolvedValue([])
    api.listProjectBomHeaders.mockResolvedValue([{ projectId: 'root', kind: 'Master', materialCode: 'ROOT-M', rowVersion: 1 }])
    const wrapper = mount(BomHierarchyOverview, { props: { project: root, projects: [root], token: 'token', editable: false } })
    await flushPromises()
    const previews = api.previewProjectBomU9Sync.mock.calls.length
    let finish!: (value: BomItem[]) => void
    api.listBom.mockImplementationOnce(() => new Promise<BomItem[]>(resolve => { finish = resolve }))
    const button = wrapper.get('button[aria-label="刷新多级BOM"]')
    await button.trigger('click')
    expect(button.attributes('disabled')).toBeDefined()
    expect(button.text()).toBe('刷新中…')
    await button.trigger('click')
    expect(api.listBom).toHaveBeenCalledTimes(6)
    finish([item('NEW', '新物料')])
    await flushPromises()
    expect(button.attributes('disabled')).toBeUndefined()
    expect(api.listBomVersions).toHaveBeenCalledTimes(2)
    expect(api.listProjectBomHeaders).toHaveBeenCalledTimes(2)
    expect(api.previewProjectBomU9Sync.mock.calls.length).toBeGreaterThan(previews)
    expect(api.executeProjectBomU9Sync).not.toHaveBeenCalled()
    expect(wrapper.findAll('tbody tr')[1].findAll('td')[4].text()).toBe('1')
    api.listBom.mockRejectedValueOnce(new Error('刷新失败测试'))
    await button.trigger('click')
    await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toBe('刷新失败测试')
    expect(button.attributes('disabled')).toBeUndefined()
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
    expect(wrapper.findAll('.bom-overview__generation button').map(button => button.text())).toEqual(['刷新'])
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
    expect(wrapper.findAll('.bom-overview__generation button').map(button => button.text())).toEqual(['刷新'])
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
      expect.objectContaining({ type: expect.any(Object) }),
      '确认同步审核通过的子件', expect.any(Object),
    )
    const preview = mount({ render: () => confirm.mock.calls[0]![0] as VNode })
    expect(preview.get('table[aria-label="完整数量核对"] tbody tr').findAll('td').map(cell => cell.text())).toEqual(['01020000056', '001', '1', '4', '1', '+3'])
    expect(preview.get('details').attributes('open')).toBeDefined()
    preview.unmount()
    expect(api.executeProjectBomU9Sync).not.toHaveBeenCalled()
  })

  it('previews reductions and deletions and does not write when cancelled', async () => {
    const root = project({ id: 'root', code: 'P700005-3', name: '测试项目', rootProjectId: 'root' })
    api.listBom.mockResolvedValue([])
    api.listBomVersions.mockResolvedValue([])
    api.listProjectBomHeaders.mockResolvedValue([{ projectId: 'root', kind: 'Standard', materialCode: '02011000000', materialId: 'standard', rowVersion: 1 }])
    api.previewProjectBomU9Sync.mockResolvedValue({ projectId: 'root', kind: 'Standard', itemCode: '02011000000', componentCount: 1, state: 'ModifyRequired',
      writePreview: { requestSha256: 'sha', requiredConfirmation: '修改 02011000000/A1', quantityReconciliations: [],
        componentChanges: [{ sequence: 140, itemCode: '01021000007', change: '修改', previousQuantity: 12, quantity: 8 },
          { sequence: 10, itemCode: '01020000056', change: '删除', previousQuantity: 4, quantity: 0 }] } })
    const confirm = vi.spyOn(ElMessageBox, 'confirm').mockRejectedValue(new Error('cancel'))
    const wrapper = mount(BomHierarchyOverview, { props: { project: root, projects: [root], token: 'token', editable: true } })
    await flushPromises()
    await wrapper.get('button.bom-overview__sync').trigger('click')
    await flushPromises()
    const preview = mount({ render: () => confirm.mock.calls[0]![0] as VNode })
    const rows = preview.findAll('table[aria-label="本次子件变更"] tbody tr')
    expect(rows[0]!.findAll('td').map(cell => cell.text())).toEqual(['修改', '140', '01021000007', '12', '8', '-4'])
    expect(rows[1]!.findAll('td').map(cell => cell.text())).toEqual(['删除', '10', '01020000056', '4', '0', '-4'])
    expect(preview.text()).toContain('不删除整张BOM')
    expect(preview.text()).toContain('减量/删除前记录快照')
    preview.unmount()
    expect(api.executeProjectBomU9Sync).not.toHaveBeenCalled()
    confirm.mockResolvedValue('confirm' as Awaited<ReturnType<typeof ElMessageBox.confirm>>)
    await wrapper.get('button.bom-overview__sync').trigger('click')
    await flushPromises()
    expect(api.executeProjectBomU9Sync).toHaveBeenCalledWith('root', 'Standard', 'sha', '修改 02011000000/A1', 'token')
    wrapper.unmount()
  })

  it('失败状态显示原因，仅确认后重试原申请，取消和只读查看不发起写入', async () => {
    const root = project({ id: 'root', code: 'P700005-4', name: '项目', rootProjectId: 'root' })
    const header = { projectId: 'root', kind: 'Master', materialId: 'material', rowVersion: 1,
      applicationId: 'application', applicationStatus: 'Pending', applicationRowVersion: 3,
      automaticStatus: 'Failed', automaticMessage: 'U9C客户参照查询请求超时。', canRetryAutomatic: true }
    api.listBom.mockResolvedValue([])
    api.listBomVersions.mockResolvedValue([])
    api.listProjectBomHeaders.mockResolvedValue([header])
    const confirm = vi.spyOn(ElMessageBox, 'confirm').mockRejectedValue('cancel')
    const wrapper = mount(BomHierarchyOverview, { props: { project: root, projects: [root], token: 'token', editable: true } })
    await flushPromises()
    expect(wrapper.text()).toContain('失败待重试')
    expect(wrapper.text()).toContain('未同步')
    expect(wrapper.text()).not.toContain('自动处理中')
    const retry = wrapper.get('[aria-label="重试 P700005-4 Master 料号自动处理"]')
    expect(retry.element.parentElement?.title).toContain('客户参照查询请求超时')
    await retry.trigger('click')
    await flushPromises()
    expect(api.retryProjectBomHeaderAutomatic).not.toHaveBeenCalled()
    confirm.mockResolvedValue('confirm' as Awaited<ReturnType<typeof ElMessageBox.confirm>>)
    api.retryProjectBomHeaderAutomatic.mockResolvedValue({ ...header, automaticStatus: 'Queued', automaticMessage: '已进入队列', canRetryAutomatic: false, applicationStatus: 'Approved' })
    await retry.trigger('click')
    await flushPromises()
    expect(api.retryProjectBomHeaderAutomatic).toHaveBeenCalledWith('root', 'Master', 'application', 3, 'token')
    expect(wrapper.text()).toContain('已批准待同步')
    expect(wrapper.find('[aria-label="重试 P700005-4 Master 料号自动处理"]').exists()).toBe(false)
    wrapper.unmount()
    const readonly = mount(BomHierarchyOverview, { props: { project: root, projects: [root], token: 'token', editable: false } })
    await flushPromises()
    expect(readonly.text()).toContain('失败待重试')
    expect(readonly.find('[aria-label="重试 P700005-4 Master 料号自动处理"]').exists()).toBe(false)
    readonly.unmount()
  })
})
