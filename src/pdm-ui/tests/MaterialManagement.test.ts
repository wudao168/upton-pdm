import { flushPromises, mount } from '@vue/test-utils'
import { ElMessage } from '../src/statusMessage'
import ElementPlus, { ElMessageBox } from 'element-plus'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import MaterialManagement from '../src/components/MaterialManagement.vue'
import type { PdmMaterial } from '../src/types'

const api = vi.hoisted(() => ({
  listMaterials: vi.fn(),
  listMaterialPage: vi.fn(),
  listPendingMasterMaterials: vi.fn(),
  listMaterialRelationTemplates: vi.fn(),
  saveMaterialRelationTemplate: vi.fn(),
  publishMaterialRelationTemplate: vi.fn(),
  listMaterialInventory: vi.fn(),
  refreshMaterialInventory: vi.fn(),
  listMaterialCategories: vi.fn(),
  listMaterialSyncTasks: vi.fn(),
  getU9MaterialIntegration: vi.fn(),
  createMaterial: vi.fn(),
  updateMaterial: vi.fn(),
  changeApprovedMaterial: vi.fn(),
  continueProjectBomU9Automation: vi.fn(),
  archiveMaterial: vi.fn(),
  reactivateMaterial: vi.fn(),
  deleteMaterial: vi.fn(),
  listMaterialAttachments: vi.fn(),
  uploadMaterialAttachment: vi.fn(),
  downloadMaterialAttachment: vi.fn(),
  getMaterialRemovalReadiness: vi.fn(),
  approveMaterial: vi.fn(),
  executeMaterialSyncTask: vi.fn(),
  createMaterialSyncBatch: vi.fn(),
  getMaterialSyncBatch: vi.fn(),
  listMaterialSyncBatches: vi.fn(),
  retryMaterialSyncTask: vi.fn(),
  listMaterialCodeApplications: vi.fn(),
  decideMaterialCodeApplication: vi.fn(),
  queryU9Material: vi.fn(),
  saveMaterialCategory: vi.fn(),
  calibrateMaterialCategoryCounter: vi.fn(),
  testU9MaterialIntegration: vi.fn(),
  updateU9MaterialIntegration: vi.fn(),
  getMaterialNumberingSettings: vi.fn(),
  updateMaterialNumberingSettings: vi.fn(),
  getMaterialDuplicateRules: vi.fn(),
  updateMaterialDuplicateRules: vi.fn(),
}))

vi.mock('../src/api', () => api)

describe('MaterialManagement', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    api.listMaterials.mockResolvedValue([{
      id: 'material-1', materialCode: 'EL-001', name: '光电传感器', kind: 'Electrical', supplyMode: 'Purchase',
      unitCode: '001', specification: 'M18', material: 'PBT', brand: '欧姆龙', surfaceTreatment: '无', purchaseLink: 'https://shop.example.test/item/EL-001', weight: 0.15, weightUnit: 'kg', remark: '测试备注',
      categoryCode: '0101', u9CategoryCode: '0101', approvalStatus: 'Draft', syncStatus: 'NotQueued', createdBy: 'admin',
      createdAt: '2026-08-17T00:00:00Z', updatedBy: 'admin', updatedAt: '2026-08-17T00:00:00Z', rowVersion: 1,
      isArchived: false, u9SyncConfirmed: false, referenceCount: 3,
    }])
    api.listMaterialPage.mockImplementation(async (_token, input = {}) => {
      const source = await api.listMaterials() as PdmMaterial[]
      const normalizedQuery = input.query?.trim().toLocaleLowerCase() || ''
      const filtered = source
        .filter(item => !input.categoryCode || item.categoryCode?.startsWith(input.categoryCode))
        .filter(item => !input.brand || item.brand === input.brand)
        .filter(item => !normalizedQuery || [item.materialCode, item.name, item.specification, item.brand, item.categoryCode]
          .some(value => value?.toLocaleLowerCase().includes(normalizedQuery)))
      const page = input.page || 1
      const pageSize = input.pageSize || 50
      return { items: filtered.slice((page - 1) * pageSize, page * pageSize), total: filtered.length, page, pageSize }
    })
    api.getMaterialNumberingSettings.mockResolvedValue({ startSequence: 1000000, sequenceLength: 7 })
    api.updateMaterialNumberingSettings.mockResolvedValue({ startSequence: 1000000, sequenceLength: 7 })
    api.getMaterialDuplicateRules.mockResolvedValue([
      { categoryCode: '0101', fields: ['Specification', 'Brand'] },
      { categoryCode: '0102', fields: ['Specification', 'Brand'] },
      { categoryCode: '0204', fields: ['Name', 'Specification'] },
    ])
    api.updateMaterialDuplicateRules.mockImplementation(async rules => rules)
    api.listMaterialCategories.mockResolvedValue([
      { code: '01', name: '原材料', parentCode: null, pdmKind: null, defaultSupplyMode: 'Purchase', allowCreate: false, isVisible: true, isActive: true, numberPrefix: '01', sequenceLength: 7, counterScope: '01', sortOrder: 1, updatedBy: 'system', updatedAt: '2026-08-17T00:00:00Z', rowVersion: 1 },
      { code: '0101', name: '电气外购件', parentCode: '01', pdmKind: 'Electrical', defaultSupplyMode: 'Purchase', allowCreate: true, isVisible: true, isActive: true, numberPrefix: '0101', sequenceLength: 7, counterScope: '0101', sortOrder: 2, updatedBy: 'system', updatedAt: '2026-08-17T00:00:00Z', rowVersion: 1 },
      { code: '0102', name: '机械外购件', parentCode: '01', pdmKind: 'Standard', defaultSupplyMode: 'Purchase', allowCreate: true, isVisible: true, isActive: true, numberPrefix: '0102', sequenceLength: 7, counterScope: '0102', sortOrder: 3, updatedBy: 'system', updatedAt: '2026-08-17T00:00:00Z', rowVersion: 1 },
      { code: '0204', name: '非标机加件', parentCode: null, pdmKind: 'NonStandard', defaultSupplyMode: 'Manufacture', allowCreate: true, isVisible: true, isActive: true, numberPrefix: '0204', sequenceLength: 7, counterScope: '0204', sortOrder: 4, updatedBy: 'system', updatedAt: '2026-08-17T00:00:00Z', rowVersion: 1 },
    ])
    api.listMaterialSyncTasks.mockResolvedValue([])
    api.listPendingMasterMaterials.mockResolvedValue([])
    api.listMaterialRelationTemplates.mockResolvedValue([])
    api.listMaterialInventory.mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 50, lastSuccessfulRefreshAt: null })
    api.refreshMaterialInventory.mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 50, lastSuccessfulRefreshAt: null })
    api.listMaterialSyncBatches.mockResolvedValue([])
    api.listMaterialCodeApplications.mockResolvedValue([])
    api.listMaterialAttachments.mockResolvedValue([])
    api.continueProjectBomU9Automation.mockResolvedValue({ stage: 'Completed', message: '自动收尾完成', itemSync: null, boms: [] })
    api.getU9MaterialIntegration.mockResolvedValue({
      baseUrl: 'http://u9.example.test/U9', enterpriseCode: '01', organizationCode: '7', userCode: 'pdm',
      clientId: 'PDM', clientSecretConfigured: true, itemCreatePath: '/webapi/ItemMaster/Create', itemQueryPath: '/webapi/ItemMaster/Query', writeEnabled: false,
      itemModifyPath: '/webapi/ItemMaster/Modify', itemDeletePath: '/webapi/ItemMaster/Delete',
      unitCodeMappings: {},
    })
    api.testU9MaterialIntegration.mockResolvedValue({
      baseUrl: 'http://u9.example.test/U9', enterpriseCode: '01', organizationCode: '7', userCode: 'pdm', clientId: 'PDM',
      testedAt: '2026-08-17T00:00:00Z',
    })
    api.queryU9Material.mockResolvedValue({ responseCode: 0, items: [{ u9ItemId: 'u9-1', u9ItemCode: 'EL-001', u9ItemName: '光电传感器', u9Specification: 'M18' }] })
    api.getMaterialRemovalReadiness.mockImplementation((materialId: string) => Promise.resolve({
      materialId, materialCode: materialId === 'material-2' ? 'ME-002' : 'EL-001', pdmReferenceCount: 0,
      isPdmMaster: true, localDeletePreconditionsPassed: true, u9ReferenceCheckAvailable: false,
      synchronizedDeleteAvailable: false, decision: 'PLM未发现引用；U9C引用查询合同尚未验证，同步删除保持关闭。',
    }))
  })

  it('opens the selected material association from the material master list', async () => {
    const approvedMaterial = { ...(await api.listMaterials())[0], approvalStatus: 'Approved' }
    api.listMaterials.mockResolvedValue([approvedMaterial])
    api.listMaterialRelationTemplates.mockResolvedValue([{
      id: 'relation-1', mainMaterialId: approvedMaterial.id, mainMaterialCode: approvedMaterial.materialCode, mainMaterialName: approvedMaterial.name,
      name: `${approvedMaterial.materialCode}关联物料`, isArchived: false, updatedBy: 'standard', updatedAt: '', rowVersion: 1, draftRevision: null,
      publishedRevision: { id: 'revision-1', version: 1, state: 'Published', changeNote: '', createdBy: 'standard', createdAt: '', publishedBy: 'standard', publishedAt: '', rowVersion: 1, groups: [] },
    }])
    const wrapper = mount(MaterialManagement, {
      attachTo: document.body,
      props: { token: 'token', canEdit: false, canApprove: false, canManageIntegration: false, canViewRelations: true, canManageRelations: false, canPublishRelations: false },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    const labels = wrapper.findAllComponents({ name: 'ElTableColumn' }).map(column => column.props('label'))
    expect(labels).toContain('关联配置')
    const associationButton = wrapper.find('button[aria-label="查看 EL-001 的关联物料"]')
    expect(associationButton.text()).toBe('已生效')
    await associationButton.trigger('click')
    await flushPromises()

    expect(document.body.textContent).toContain('料品明细')
    expect(document.body.textContent).toContain('关联物料')
    expect(document.body.textContent).toContain('物料料号')
    expect(document.body.textContent).not.toContain('配套物料模板')
    expect(document.body.textContent).not.toContain('保存修改')
    wrapper.unmount()
  })

  it.each([['旧料品', ''], ['料品', '料品']])('新增草稿处理筛选 %s → %s 并返回第一页置顶高亮，保留附件上传弹窗', async (search, expectedSearch) => {
    const base = (await api.listMaterials())[0] as PdmMaterial
    const previous = Array.from({ length: 51 }, (_, index) => ({ ...base, id: `old-${index}`, materialCode: `OLD-${index}`, name: '旧料品', approvalStatus: 'Approved' as const }))
    const saved = { ...base, id: 'new-draft', materialCode: 'NEW-001', name: '新建料品', createdAt: '2026-09-09T09:00:00Z' }
    api.listMaterials.mockResolvedValue(previous)
    api.createMaterial.mockImplementationOnce(async () => {
      api.listMaterials.mockResolvedValue([saved, ...previous])
      return saved
    })
    const wrapper = mount(MaterialManagement, { props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    await wrapper.get('.material-toolbar .el-input input').setValue(search)
    await new Promise(resolve => setTimeout(resolve, 280))
    await flushPromises()
    wrapper.findComponent({ name: 'ElPagination' }).vm.$emit('update:current-page', 2)
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '新增料品')!.trigger('click')
    const editor = wrapper.findComponent({ name: 'MaterialEditorDialog' })
    Object.assign(editor.props('form'), { name: saved.name, categoryCode: '0101', unitCode: '001' })
    editor.vm.$emit('save')
    await flushPromises()
    expect(api.createMaterial).toHaveBeenCalledOnce()
    expect(api.listMaterialPage).toHaveBeenLastCalledWith('token', expect.objectContaining({ query: expectedSearch, page: 1 }))
    expect(wrapper.get('.material-toolbar .el-input input').element).toHaveProperty('value', expectedSearch)
    const rows = wrapper.findAll('.material-table .el-table__body-wrapper tbody tr')
    expect(rows).toHaveLength(50)
    expect(rows[0].text()).toContain('NEW-001')
    expect(rows[0].classes()).toContain('is-pending-material')
    expect(rows[1].classes()).not.toContain('is-pending-material')
    expect(editor.props('modelValue')).toBe(true)
    expect(editor.props('editingId')).toBe(saved.id)
    expect(wrapper.findComponent({ name: 'ElPagination' }).props('total')).toBe(52)
    wrapper.unmount()
  }, 15000)

  it('保存冲突在弹窗内显示且保留输入，重试及重新打开清除旧错误', async () => {
    const conflict = '按分类查重规则（型号+品牌）已存在相同料品 EL-001，请核对后重试'
    api.createMaterial.mockRejectedValueOnce(new Error(conflict))
    const wrapper = mount(MaterialManagement, { attachTo: document.body, props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    const open = wrapper.findAll('button').find(button => button.text() === '新增料品')!
    await open.trigger('click')
    const editor = wrapper.findComponent({ name: 'MaterialEditorDialog' })
    Object.assign(editor.props('form'), { name: '待保存传感器', specification: 'M18', brand: '欧姆龙', categoryCode: '0101', unitCode: '001' })
    editor.vm.$emit('save')
    await flushPromises()
    expect(document.querySelector('.material-editor-dialog .el-dialog__footer .material-editor-error')?.textContent).toContain(conflict)
    expect(editor.props('modelValue')).toBe(true)
    expect(editor.props('form')).toMatchObject({ name: '待保存传感器', specification: 'M18', brand: '欧姆龙' })
    expect(editor.props('saving')).toBe(false)
    editor.props('form').name = ''
    editor.vm.$emit('save')
    await flushPromises()
    expect(editor.props('errorMessage')).toBe('物料名称、分类和计量单位不能为空')
    expect(api.createMaterial).toHaveBeenCalledOnce()
    editor.vm.$emit('update:modelValue', false)
    await flushPromises()
    await open.trigger('click')
    expect(editor.props('errorMessage')).toBe('')
    wrapper.unmount()
  })

  it('料号审批刷新同时加载最新待审批和同步内容，不执行审批或同步', async () => {
    const wrapper = mount(MaterialManagement, { props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    await wrapper.get('#tab-code-approvals').trigger('click')
    await flushPromises()
    const draft = { ...(await api.listMaterials())[0], name: '刷新后的草稿' }
    api.listPendingMasterMaterials.mockResolvedValue([draft])
    api.listMaterialSyncTasks.mockResolvedValue([{ id: 'refresh-task', materialId: 'synced-material', materialCode: 'SYNC-001', materialName: '刷新后的同步料品', specification: 'M18', brand: '欧姆龙', status: 'PreviewReady', createdAt: draft.createdAt }])
    const before = api.listMaterialCodeApplications.mock.calls.length
    await wrapper.get('.material-code-refresh-toolbar button').trigger('click')
    await flushPromises()
    expect(api.listMaterialCodeApplications.mock.calls.length).toBe(before + 1)
    expect(api.listPendingMasterMaterials).toHaveBeenLastCalledWith('token')
    expect(api.listMaterialSyncTasks).toHaveBeenLastCalledWith('token', true)
    expect(wrapper.get('.material-code-approval-table--pending').text()).toContain('刷新后的草稿')
    expect(wrapper.get('.material-code-sync-stage').text()).toContain('刷新后的同步料品')
    expect(wrapper.get('#tab-code-approvals').text()).toContain('2')
    expect(api.approveMaterial).not.toHaveBeenCalled()
    expect(api.executeMaterialSyncTask).not.toHaveBeenCalled()
    expect(api.createMaterialSyncBatch).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('创建时间排序传给后端，翻页保留排序，取消后恢复默认', async () => {
    const base = (await api.listMaterials())[0]
    api.listMaterials.mockResolvedValue(Array.from({ length: 3 }, (_, index) => ({ ...base, id: `sort-${index}`, materialCode: `SORT-${index}` })))
    const wrapper = mount(MaterialManagement, { props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    const table = wrapper.findComponent({ name: 'ElTable' })
    const dateColumn = table.findAllComponents({ name: 'ElTableColumn' }).find(column => column.props('prop') === 'createdAt')!
    expect(dateColumn.props('sortable')).toBe('custom')
    const pagination = wrapper.findComponent({ name: 'ElPagination' })
    pagination.vm.$emit('update:pageSize', 2)
    await flushPromises()
    table.vm.$emit('sort-change', { prop: 'createdAt', order: 'descending' })
    await flushPromises()
    expect(api.listMaterialPage).toHaveBeenLastCalledWith('token', expect.objectContaining({ createdAtOrder: 'desc', page: 1 }))
    pagination.vm.$emit('update:currentPage', 2)
    await flushPromises()
    expect(api.listMaterialPage).toHaveBeenLastCalledWith('token', expect.objectContaining({ createdAtOrder: 'desc', page: 2 }))
    table.vm.$emit('sort-change', { prop: 'createdAt', order: 'ascending' })
    await flushPromises()
    expect(api.listMaterialPage).toHaveBeenLastCalledWith('token', expect.objectContaining({ createdAtOrder: 'asc', page: 1 }))
    table.vm.$emit('sort-change', { prop: undefined, order: null })
    await flushPromises()
    expect(api.listMaterialPage).toHaveBeenLastCalledWith('token', expect.objectContaining({ createdAtOrder: undefined, page: 1 }))
    wrapper.unmount()
  })

  it('主档页外的普通草稿进入第一步，批准后在第二步保留统一信息且不自动同步', async () => {
    const material = (await api.listMaterials())[0] as PdmMaterial
    api.listMaterials.mockResolvedValue([{ ...material, id: 'unrelated', name: '当前主档页', approvalStatus: 'Approved' }])
    api.listPendingMasterMaterials.mockResolvedValue([material])
    const task = { id: 'master-task', materialId: material.id, materialCode: material.materialCode, materialName: material.name,
      specification: material.specification, brand: material.brand, remark: material.remark, categoryCode: material.categoryCode,
      requestedBy: material.createdBy, requestedAt: material.createdAt, status: 'PreviewReady', createdAt: material.createdAt }
    api.approveMaterial.mockImplementationOnce(async () => {
      api.listPendingMasterMaterials.mockResolvedValue([])
      api.listMaterialSyncTasks.mockResolvedValue([task])
      return { material: { ...material, approvalStatus: 'Approved' }, task }
    })
    const confirm = vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
    const wrapper = mount(MaterialManagement, { props: { token: 'token', canEdit: true, canApprove: true, canDecideMaterialCode: false,
      canManageIntegration: false, requestedTab: 'code-approvals' }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    const approvalTable = wrapper.findAllComponents({ name: 'ElTable' }).find(table => table.classes().includes('material-code-approval-table--pending'))!
    const syncTable = wrapper.findAllComponents({ name: 'ElTable' }).find(table => table.classes().includes('material-code-sync-table--pending'))!
    const commonLabels = ['来源', '料号分类', 'PLM料号', '名称', '型号', '品牌', '备注', '申请人', '申请时间']
    for (const table of [approvalTable, syncTable]) expect(table.findAllComponents({ name: 'ElTableColumn' }).map(column => column.props('label')).filter(label => commonLabels.includes(label))).toEqual(commonLabels)
    expect(approvalTable.text()).toContain(material.name)
    expect(approvalTable.text()).toContain(material.specification)
    expect(approvalTable.text()).toContain(material.brand)
    expect(approvalTable.text()).toContain('料品主档')
    expect(approvalTable.findAll('button').some(button => button.text() === '退回')).toBe(false)
    await approvalTable.findAll('button').find(button => button.text() === '批准')!.trigger('click')
    await flushPromises()
    expect(confirm).toHaveBeenCalled()
    expect(api.approveMaterial).toHaveBeenCalledWith(material.id, material.rowVersion, 'token')
    expect(api.decideMaterialCodeApplication).not.toHaveBeenCalled()
    expect(approvalTable.text()).toContain('当前没有待审批申请')
    for (const value of [material.name, material.materialCode, material.specification, material.brand, material.remark]) expect(syncTable.text()).toContain(value)
    expect(syncTable.text()).not.toContain('普通料品')
    expect(api.executeMaterialSyncTask).not.toHaveBeenCalled()
    expect(api.createMaterialSyncBatch).not.toHaveBeenCalled()
    confirm.mockRestore()
    wrapper.unmount()
  })

  it('普通草稿与BOM申请按料品去重，BOM审批权限不能批准普通主档', async () => {
    const material = (await api.listMaterials())[0] as PdmMaterial
    const duplicate = { ...material, id: 'with-application', name: '已有申请' }
    api.listPendingMasterMaterials.mockResolvedValue([material, duplicate, { ...material, id: 'archived', isArchived: true }, { ...material, id: 'approved', approvalStatus: 'Approved' }])
    api.listMaterialCodeApplications.mockResolvedValue([{ id: 'application', applicationType: 'StandardBomItem', status: 'Pending',
      materialId: duplicate.id, applicationName: duplicate.name, requestedAt: material.createdAt, projectId: 'project-1', rowVersion: 1 }])
    const wrapper = mount(MaterialManagement, { props: { token: 'token', canEdit: false, canApprove: false, canDecideMaterialCode: true,
      canManageIntegration: false, requestedTab: 'code-approvals' }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    const table = wrapper.findAllComponents({ name: 'ElTable' }).find(table => table.classes().includes('material-code-approval-table--pending'))!
    const rows = table.props('data') as Array<{ masterMaterial?: PdmMaterial }>
    expect(rows).toHaveLength(2)
    const master = rows.find(row => row.masterMaterial)!
    const selection = table.findAllComponents({ name: 'ElTableColumn' }).find(column => column.props('type') === 'selection')!
    expect(selection.props('selectable')(master)).toBe(false)
    table.vm.$emit('selection-change', [master])
    await flushPromises()
    await wrapper.get('.material-code-approval-toolbar').findAll('button').find(button => button.text() === '批量批准')!.trigger('click')
    await flushPromises()
    expect(api.approveMaterial).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('只高亮未停用草稿，批准后重新加载列表并取消黄色标记', async () => {
    const base = (await api.listMaterials())[0] as PdmMaterial
    const approved = { ...base, id: 'approved', materialCode: 'A-001', approvalStatus: 'Approved' as const, isRecommended: true }
    const archived = { ...base, id: 'archived', materialCode: 'X-001', isArchived: true }
    api.listMaterials.mockResolvedValue([base, approved, archived])
    const confirm = vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
    api.approveMaterial.mockImplementationOnce(async () => {
      const material = { ...base, approvalStatus: 'Approved' as const }
      api.listMaterials.mockResolvedValue([approved, material, archived])
      return { material, task: { id: 'preview-task' } }
    })
    const wrapper = mount(MaterialManagement, { props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    expect(wrapper.findAll('.material-table tr.is-pending-material')).toHaveLength(1)
    wrapper.findComponent({ name: 'ElTable' }).vm.$emit('selection-change', [base])
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '批准')!.trigger('click')
    await flushPromises()
    expect(api.approveMaterial).toHaveBeenCalledWith(base.id, base.rowVersion, 'token')
    expect(wrapper.findAll('.material-table tr.is-pending-material')).toHaveLength(0)
    expect(wrapper.findAll('.material-table .el-table__body-wrapper tbody tr')[0].text()).toContain('A-001')
    confirm.mockRestore()
    wrapper.unmount()
  })

  it.each([
    [2.5, 3, '6'],
    [2.4, 3, '5'],
    [0, 0, '0'],
    [1000, 234, '1,234'],
  ])('queries inventory in place and displays the sum of %s and %s as integer %s', async (firstQuantity, nextQuantity, expected) => {
    api.refreshMaterialInventory.mockResolvedValue({
      items: [{ materialCode: 'EL-001', stockQuantity: firstQuantity, refreshedAt: '2026-09-05T10:00:00Z' }],
      total: 2,
      page: 1,
      pageSize: 50,
      lastSuccessfulRefreshAt: '2026-09-05T10:00:00Z',
    })
    api.listMaterialInventory.mockImplementation(async filters => filters.materialCode === 'EL-001' && filters.page === 2
      ? { items: [{ materialCode: 'EL-001', stockQuantity: nextQuantity, refreshedAt: '2026-09-05T10:00:00Z' }], total: 2, page: 2, pageSize: 200, lastSuccessfulRefreshAt: '2026-09-05T10:00:00Z' }
      : { items: [], total: 0, page: 1, pageSize: 50, lastSuccessfulRefreshAt: null })
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    const queryButton = wrapper.get('button[aria-label="查询 EL-001 的库存"]')
    expect(queryButton.text()).toBe('查询')
    await queryButton.trigger('click')
    await flushPromises()

    expect(api.refreshMaterialInventory).toHaveBeenCalledWith('EL-001', 'token')
    expect(api.listMaterialInventory).toHaveBeenCalledWith({ materialCode: 'EL-001', positiveStockOnly: false, page: 2, pageSize: 200 }, 'token')
    expect(queryButton.text()).toBe(expected)
    expect(wrapper.get('[role="tab"][aria-selected="true"]').text()).toContain('料品主档')
  })

  it('一键只勾选本页未停用草稿，清除非草稿选择，重复点击不取消', async () => {
    const base = (await api.listMaterials())[0] as PdmMaterial
    const drafts = [{ ...base, id: 'draft-1' }, { ...base, id: 'draft-2', materialCode: 'PDM-PENDING-2' }]
    const approved = { ...base, id: 'approved', approvalStatus: 'Approved' as const }
    const archived = { ...base, id: 'archived', isArchived: true }
    api.listMaterials.mockResolvedValue([...drafts, archived, ...Array.from({ length: 47 }, (_, i) => ({ ...approved, id: `approved-${i}` })), { ...base, id: 'next-page-draft' }])
    const wrapper = mount(MaterialManagement, { props: { token: 'token', canEdit: false, canApprove: false, canManageIntegration: false }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    const button = wrapper.findAll('button').find(b => b.text() === '勾选本页草稿')!
    const selectedRows = () => wrapper.findAll('.material-table .el-table__body tbody tr').flatMap((row, i) => (row.get('input[type="checkbox"]').element as HTMLInputElement).checked ? [i] : [])
    await wrapper.findAll('.material-table .el-table__body tbody tr')[3].get('input[type="checkbox"]').setValue(true)
    await button.trigger('click')
    await flushPromises()
    expect(selectedRows()).toEqual([0, 1])
    await button.trigger('click')
    expect(selectedRows()).toEqual([0, 1])
    wrapper.findComponent({ name: 'ElPagination' }).vm.$emit('update:current-page', 2)
    await flushPromises()
    await button.trigger('click')
    expect(wrapper.findAll('.material-table .el-table__body tbody tr')).toHaveLength(1)
    expect(selectedRows()).toEqual([0])
    expect(api.approveMaterial).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('多选库存原列回填整数和零，失败可重试，临时编号跳过且不切换页面', async () => {
    const base = (await api.listMaterials())[0] as PdmMaterial
    const rows = ['EL-001', 'EL-002', 'EL-003', 'PDM-PENDING-4'].map((materialCode, i) => ({ ...base, id: `stock-${i}`, materialCode }))
    api.listMaterials.mockResolvedValue(rows)
    api.refreshMaterialInventory.mockImplementation(async code => {
      if (code === 'EL-003') throw new Error('库存服务暂不可用')
      return { items: code === 'EL-001' ? [{ stockQuantity: 21.4 }] : [], total: code === 'EL-001' ? 2 : 0, page: 1, pageSize: 50 }
    })
    api.listMaterialInventory.mockResolvedValue({ items: [{ stockQuantity: 1.6 }], total: 2, page: 2, pageSize: 200 })
    const warning = vi.spyOn(ElMessage, 'warning').mockImplementation(() => ({ close: vi.fn() }))
    const wrapper = mount(MaterialManagement, { props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    wrapper.findComponent({ name: 'ElTable' }).vm.$emit('selection-change', rows)
    await flushPromises()
    await wrapper.findAll('button').find(b => b.text() === '库存查询')!.trigger('click')
    await flushPromises()
    expect(api.refreshMaterialInventory.mock.calls.map(call => call[0])).toEqual(['EL-001', 'EL-002', 'EL-003'])
    expect(wrapper.get('button[aria-label="查询 EL-001 的库存"]').text()).toBe('23')
    expect(wrapper.get('button[aria-label="查询 EL-002 的库存"]').text()).toBe('0')
    expect(wrapper.get('button[aria-label="查询 EL-003 的库存"]').text()).toBe('查询失败')
    expect(wrapper.get('[role="tab"][aria-selected="true"]').text()).toContain('料品主档')
    expect(warning).toHaveBeenCalledWith(expect.stringContaining('成功 2，失败 1，跳过 1'))
    api.refreshMaterialInventory.mockResolvedValue({ items: [{ stockQuantity: 8 }], total: 1, page: 1, pageSize: 50 })
    await wrapper.get('button[aria-label="查询 EL-003 的库存"]').trigger('click')
    await flushPromises()
    expect(wrapper.get('button[aria-label="查询 EL-003 的库存"]').text()).toBe('8')
    expect(api.approveMaterial).not.toHaveBeenCalled()
    expect(api.executeMaterialSyncTask).not.toHaveBeenCalled()
    warning.mockRestore()
    wrapper.unmount()
  })

  it('没有本页草稿时禁用勾选按钮，未选料品的库存查询保留库存页入口', async () => {
    api.listMaterials.mockResolvedValue([{ ...(await api.listMaterials())[0], approvalStatus: 'Approved' }])
    const wrapper = mount(MaterialManagement, { props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    expect(wrapper.findAll('button').find(b => b.text() === '勾选本页草稿')!.attributes('disabled')).toBeDefined()
    await wrapper.findAll('button').find(b => b.text() === '库存查询')!.trigger('click')
    await flushPromises()
    expect(wrapper.get('[role="tab"][aria-selected="true"]').text()).toContain('料品库存')
    expect(api.refreshMaterialInventory).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('加载料品且不在主档顶部显示U9C写入提示', async () => {
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    expect(wrapper.find('.el-alert').exists()).toBe(false)
    expect(wrapper.find('.material-header').exists()).toBe(false)
    expect(wrapper.findAll('[role="tab"]').slice(0, 5).map(tab => tab.text().trim())).toEqual(['料品主档', '料品库存', 'BOM表头料号', '料号审批', '分类维护'])
    expect(api.listMaterialPage).toHaveBeenCalledWith('token', expect.objectContaining({ ordinaryOnly: true }))
    const toolbar = wrapper.get('.material-toolbar')
    expect(toolbar.findAll('button').some(button => button.text() === '刷新')).toBe(true)
    expect(toolbar.findAll('button').some(button => button.text() === '新增料品')).toBe(true)
    expect(toolbar.text()).toContain('显示已停用')
    expect(toolbar.get('input[placeholder="搜索编码、名称、规格、品牌或分类"]')).toBeTruthy()
    expect(wrapper.text()).toContain('EL-001')
    expect(wrapper.text()).toContain('光电传感器')
    expect(wrapper.find('.material-table').text()).not.toContain('PLM业务类型')
    expect(wrapper.find('.material-table').text()).not.toContain('供给方式')
    expect(wrapper.text()).toContain('计量单位')
    expect(wrapper.find('.material-table').text()).toContain('个')
    expect(wrapper.find('.material-table').text()).not.toContain('001 个')
    expect(wrapper.text()).toContain('品牌')
    expect(wrapper.text()).toContain('欧姆龙')
    expect(wrapper.text()).toContain('表面处理')
    expect(wrapper.text()).toContain('0.15 kg')
    expect(wrapper.text()).toContain('测试备注')
    expect(wrapper.find('.material-table').text()).toContain('物料编码')
    expect(wrapper.find('.material-table').text()).not.toContain('PLM物料编码')
    expect(wrapper.find('.material-table').text()).not.toContain('采购链接')
    expect(wrapper.find('.material-table').text()).toContain('创建人')
    expect(wrapper.find('.material-table').text()).toContain('创建时间')
    expect(wrapper.find('.material-table').text()).toContain('引用')
    expect(wrapper.find('.material-table').text()).not.toContain('引用次数')
    expect(wrapper.find('.material-table').text()).toContain('admin')
    expect(wrapper.find('.material-table').text()).toContain('3')
    const headers = wrapper.findAll('.material-table .el-table__header-wrapper th .cell').map(header => header.text().trim())
    expect(headers).not.toContain('U9C对应分类')
    expect(headers).not.toContain('采购链接')
    expect(headers.indexOf('引用')).toBe(headers.indexOf('名称') + 1)
    expect(headers.indexOf('库存')).toBe(headers.indexOf('引用') + 1)
    const columns = wrapper.findComponent({ name: 'ElTable' }).findAllComponents({ name: 'ElTableColumn' })
    expect(columns.find(column => column.props('label') === '引用')?.props('minWidth')).toBe('48')
    expect(columns.find(column => column.props('label') === '备注')?.props('minWidth')).toBe('180')
    expect(headers.indexOf('品牌')).toBe(headers.indexOf('材质') - 1)
    expect(headers.indexOf('计量单位')).toBe(headers.indexOf('来源/主控') - 1)
    expect(headers.indexOf('来源/主控')).toBe(headers.indexOf('状态') - 1)

    const ruleTab = wrapper.findAll('[role="tab"]').find(tab => tab.text().includes('分类维护'))
    await ruleTab!.trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('0101 电气外购件')
    expect(wrapper.text()).toContain('0204 非标机加件')
    expect(api.getU9MaterialIntegration).not.toHaveBeenCalled()
  })

  it('料号审批页签汇总待审批和待同步数量', async () => {
    api.listMaterialSyncTasks.mockResolvedValue([
      { id: 'sync-1', materialId: 'material-1', status: 'PreviewReady' },
      { id: 'sync-2', materialId: 'material-2', status: 'Failed' },
      { id: 'sync-3', materialId: 'material-3', status: 'Succeeded' },
      { id: 'sync-4', materialId: 'material-4', status: 'Superseded' },
    ])
    api.listMaterialCodeApplications.mockResolvedValue([
      { id: 'application-1', status: 'Pending', workflowState: 'PendingApproval' },
      { id: 'application-2', materialId: 'material-3', status: 'Approved', workflowState: 'Completed' },
    ])
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canDecideMaterialCode: true, canManageIntegration: false },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    const tabs = wrapper.findAll('[role="tab"]').map(tab => tab.text().replace(/\s/g, ''))
    expect(tabs).not.toContain(expect.stringContaining('同步任务'))
    expect(tabs).toContain('料号审批3')
    expect(wrapper.emitted('noticeCountsChange')?.at(-1)).toEqual([{ syncTasks: 2, codeApprovals: 1 }])
  })

  it('U9C重量为零时按未填写显示且不拼接重量单位代码', async () => {
    const zeroWeightMaterial = {
      id: 'material-zero-weight', materialCode: '01010000002', name: 'PLC_CPU', kind: 'Electrical', supplyMode: 'Purchase',
      unitCode: '001', specification: '6ES7516-3AN01-0AB0', weight: 0, weightUnit: '001', approvalStatus: 'Approved',
      syncStatus: 'Succeeded', createdBy: 'admin', createdAt: '2026-08-20T02:56:26Z', updatedBy: 'admin',
      updatedAt: '2026-08-20T02:56:26Z', rowVersion: 1, categoryCode: '0101', isArchived: false, u9SyncConfirmed: true,
    }
    api.listMaterials.mockResolvedValueOnce([zeroWeightMaterial])
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    const headers = wrapper.findAll('.material-table .el-table__header-wrapper th .cell').map(header => header.text().trim())
    const cells = wrapper.get('.material-table .el-table__body-wrapper tbody tr').findAll('td')
    expect(cells[headers.indexOf('重量')].text()).toBe('—')
    expect(wrapper.find('.material-table').text()).not.toContain('0 001')

    wrapper.findComponent({ name: 'ElTable' }).vm.$emit('selection-change', [zeroWeightMaterial])
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '编辑')!.trigger('click')
    await flushPromises()
    const weightItem = wrapper.findAll('.el-form-item').find(item => item.text().includes('重量'))!
    expect((weightItem.get('input[role="spinbutton"]').element as HTMLInputElement).value).toBe('')
    expect((weightItem.findAll('input').at(-1)!.element as HTMLInputElement).value).toBe('')
  })

  it('可按品牌筛选料品列表', async () => {
    api.listMaterials.mockResolvedValue([
      {
        id: 'material-omron', materialCode: '01010000001', name: '欧姆龙传感器', kind: 'Electrical', supplyMode: 'Purchase',
        unitCode: '001', brand: '欧姆龙', approvalStatus: 'Approved', syncStatus: 'NotQueued', createdBy: 'admin',
        createdAt: '2026-08-17T00:00:00Z', updatedBy: 'admin', updatedAt: '2026-08-17T00:00:00Z', rowVersion: 1,
        categoryCode: '0101', isArchived: false, u9SyncConfirmed: false,
      },
      {
        id: 'material-smc', materialCode: '01020000050', name: 'SMC气缸', kind: 'Standard', supplyMode: 'Purchase',
        unitCode: '001', brand: 'SMC', approvalStatus: 'Approved', syncStatus: 'NotQueued', createdBy: 'admin',
        createdAt: '2026-08-17T00:00:00Z', updatedBy: 'admin', updatedAt: '2026-08-17T00:00:00Z', rowVersion: 1,
        categoryCode: '0102', isArchived: false, u9SyncConfirmed: false,
      },
    ])
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    const brandSelect = wrapper.findComponent({ name: 'ElSelect' })
    expect(brandSelect.exists()).toBe(true)
    brandSelect.vm.$emit('update:modelValue', '欧姆龙')
    await flushPromises()

    const rows = wrapper.findAll('.el-table__body-wrapper tbody tr')
    expect(rows).toHaveLength(1)
    expect(rows[0].text()).toContain('欧姆龙传感器')
    expect(rows[0].text()).not.toContain('SMC气缸')
  })

  it('料品列表默认每页50条并可切换为100或200条', async () => {
    api.listMaterials.mockResolvedValue(Array.from({ length: 55 }, (_, index) => ({
      id: `material-${index + 1}`, materialCode: `0101${String(index + 1).padStart(7, '0')}`, name: `料品${index + 1}`,
      kind: 'Electrical', supplyMode: 'Purchase', unitCode: '001', approvalStatus: 'Approved', syncStatus: 'NotQueued', createdBy: 'admin',
      createdAt: '2026-08-17T00:00:00Z', updatedBy: 'admin', updatedAt: '2026-08-17T00:00:00Z', rowVersion: 1,
      categoryCode: '0101', isArchived: false, u9SyncConfirmed: false,
    })))
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    const pagination = wrapper.findComponent({ name: 'ElPagination' })
    expect(pagination.props('pageSize')).toBe(50)
    expect(pagination.props('pageSizes')).toEqual([50, 100, 200])
    expect(pagination.props('total')).toBe(55)
    expect(wrapper.findAll('.material-table .el-table__body-wrapper tbody tr')).toHaveLength(50)

    pagination.vm.$emit('update:page-size', 100)
    await flushPromises()
    expect(wrapper.findAll('.material-table .el-table__body-wrapper tbody tr')).toHaveLength(55)
  })

  it('料品主档左侧显示启用分类并可快速筛选', async () => {
    api.listMaterials.mockResolvedValue([
      {
        id: 'material-omron', materialCode: '01010000001', name: '欧姆龙传感器', kind: 'Electrical', supplyMode: 'Purchase',
        unitCode: '001', approvalStatus: 'Approved', syncStatus: 'NotQueued', createdBy: 'admin',
        createdAt: '2026-08-17T00:00:00Z', updatedBy: 'admin', updatedAt: '2026-08-17T00:00:00Z', rowVersion: 1,
        categoryCode: '0101', isArchived: false, u9SyncConfirmed: false,
      },
      {
        id: 'material-smc', materialCode: '01020000050', name: 'SMC气缸', kind: 'Standard', supplyMode: 'Purchase',
        unitCode: '001', approvalStatus: 'Approved', syncStatus: 'NotQueued', createdBy: 'admin',
        createdAt: '2026-08-17T00:00:00Z', updatedBy: 'admin', updatedAt: '2026-08-17T00:00:00Z', rowVersion: 1,
        categoryCode: '0102', isArchived: false, u9SyncConfirmed: false,
      },
    ])
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    const layout = wrapper.get('.material-master-layout')
    const nav = wrapper.get('.material-category-nav')
    expect(layout.element.children).toHaveLength(2)
    expect(nav.attributes('aria-label')).toBe('料品分类')
    expect(wrapper.get('.material-master-content').attributes('aria-label')).toBe('料品列表')
    expect(layout.classes()).toContain('is-category-collapsed')
    expect(wrapper.find('.material-category-all').exists()).toBe(false)
    expect(wrapper.find('.material-category-nav .el-tree').exists()).toBe(false)
    const expand = wrapper.get('button[aria-label="展开料品分类"]')
    expect(expand.attributes('aria-expanded')).toBe('false')
    await expand.trigger('click')
    expect(layout.classes()).not.toContain('is-category-collapsed')
    expect(nav.text()).toContain('全部料品')
    expect(nav.text()).toContain('01 原材料')
    expect(nav.text()).toContain('0101 电气外购件')
    expect(nav.text()).toContain('0102 机械外购件')
    const mechanicalCategory = nav.findAll('.el-tree-node__content').find(node => node.text().includes('0102 机械外购件'))!
    await mechanicalCategory.trigger('click')
    await flushPromises()

    const rows = wrapper.findAll('.material-table .el-table__body-wrapper tbody tr')
    expect(rows).toHaveLength(1)
    expect(rows[0].text()).toContain('SMC气缸')
    expect(rows[0].text()).not.toContain('欧姆龙传感器')
  })

  it('U9C接口设置已迁移到系统管理，不再出现在料品管理', async () => {
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: true },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    expect(wrapper.findAll('[role="tab"]').some(tab => tab.text().includes('U9C配置'))).toBe(false)
    expect(api.getU9MaterialIntegration).not.toHaveBeenCalled()
  })

  it('取号设置可按料品分类维护并保存查重字段', async () => {
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: true },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    const settingsTab = wrapper.findAll('[role="tab"]').find(tab => tab.text().includes('取号设置'))!
    await settingsTab.trigger('click')
    await flushPromises()

    const rules = wrapper.get('.material-duplicate-settings')
    expect(rules.text()).toContain('分类查重规则')
    expect(rules.text()).toContain('0101 电气外购件')
    expect(rules.text()).toContain('0102 机械外购件')
    expect(rules.text()).toContain('0204 非标机加件')
    expect(rules.text()).toContain('名称')
    expect(rules.text()).toContain('型号')
    expect(rules.text()).toContain('品牌')

    await rules.findAll('button').find(button => button.text().includes('保存查重规则'))!.trigger('click')
    await flushPromises()

    expect(api.updateMaterialDuplicateRules).toHaveBeenCalledWith([
      { categoryCode: '0101', fields: ['Specification', 'Brand'] },
      { categoryCode: '0102', fields: ['Specification', 'Brand'] },
      { categoryCode: '0204', fields: ['Name', 'Specification'] },
    ], 'token')
  })

  it('料号审批表使用固定自适应列宽且右侧操作列保留在表格内', async () => {
    api.listMaterialCodeApplications.mockResolvedValue([{
      id: 'application-1', bomItemId: null, bomHeaderKind: 'Standard', projectId: 'project-1', projectCode: 'P700003',
      projectName: '氮检设备', categoryCode: '0201', applicationName: 'P700003 标准件BOM', status: 'Pending',
      requestedBy: 'developer', requestedAt: '2026-08-23T14:47:51Z', createdAt: '2026-08-23T14:47:51Z', updatedAt: '2026-08-23T14:47:51Z',
    }])
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canDecideMaterialCode: true, canManageIntegration: false },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    await wrapper.findAll('[role="tab"]').find(tab => tab.text().includes('料号审批'))!.trigger('click')
    await flushPromises()

    const table = wrapper.findAllComponents({ name: 'ElTable' }).find(component => component.classes().includes('material-code-approval-table'))!
    expect(table.props('tableLayout')).toBe('fixed')
    expect(wrapper.find('.material-code-approval-table-shell').exists()).toBe(true)
    expect(table.text()).toContain('PLM料号')
    expect(table.text()).toContain('操作')
    expect(table.text()).toContain('批准')
    expect(table.text()).toContain('退回')
    expect(table.findAllComponents({ name: 'ElTableColumn' }).some(column => column.props('type') === 'selection')).toBe(true)
    expect(wrapper.get('.material-code-approval-toolbar').text()).toContain('已选择 0 项待审批申请')
  })

  it('料号审批默认只显示待审批并将已批准和已退回记录放入只读历史页', async () => {
    const applications = [
      {
        id: 'pending-1', applicationType: 'StandardBomItem' as const, bomItemId: 'item-1', bomHeaderKind: 'Standard' as const,
        projectId: 'project-1', projectCode: 'P700003', projectName: '氮检设备', categoryCode: '0102',
        applicationName: '待审批气缸', status: 'Pending' as const, workflowState: 'PendingApproval' as const, requestedBy: 'engineer', requestedAt: '2026-09-01T02:00:00Z', rowVersion: 1,
      },
      {
        id: 'approved-1', applicationType: 'StandardBomItem' as const, bomItemId: 'approved-item', bomHeaderKind: 'Standard' as const,
        projectId: 'project-2', projectCode: 'P700004', projectName: '氦检设备', categoryCode: '0302', applicationName: '已批准主BOM',
        status: 'Approved' as const, workflowState: 'Completed' as const, requestedBy: 'developer', requestedAt: '2026-08-26T11:00:00Z', decidedBy: 'standardizer', materialCode: '03020005424', rowVersion: 2,
      },
      {
        id: 'rejected-1', applicationType: 'StandardBomItem' as const, bomItemId: 'rejected-item', bomHeaderKind: 'Standard' as const,
        projectId: 'project-2', projectCode: 'P700004', projectName: '氦检设备', categoryCode: '0201', applicationName: '已退回电气BOM',
        status: 'Rejected' as const, workflowState: 'Rejected' as const, requestedBy: 'developer', requestedAt: '2026-08-26T11:01:00Z', decidedBy: 'standardizer', rowVersion: 3,
      },
    ]
    api.listMaterialSyncTasks.mockResolvedValue([{
      id: 'sync-history-1', materialId: 'material-history-1', materialCode: '01020000065', materialName: '历史气缸',
      operation: 'Create' as const, status: 'Succeeded' as const, correlationId: 'history-v1', payloadJson: '{}', payloadSha256: 'HISTORY',
      attemptCount: 1, requestedBy: 'engineer', requestedAt: '2026-08-26T11:02:00Z', createdAt: '2026-08-26T11:02:00Z', updatedAt: '2026-08-26T11:03:00Z',
    }])
    api.listMaterialCodeApplications.mockResolvedValue(applications)
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canDecideMaterialCode: true, canManageIntegration: false, requestedTab: 'code-approvals' },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    const pendingTable = wrapper.get('.material-code-approval-table--pending')
    const historyTable = wrapper.get('.material-code-approval-table--history')
    expect(pendingTable.text()).toContain('待审批气缸')
    expect(pendingTable.text()).not.toContain('已批准主BOM')
    expect(pendingTable.findAllComponents({ name: 'ElTableColumn' }).some((column: { props: (name: string) => unknown }) => column.props('type') === 'selection')).toBe(true)
    expect(historyTable.text()).toContain('03020005424')
    expect(historyTable.text()).toContain('已退回')
    expect(historyTable.findAllComponents({ name: 'ElTableColumn' }).map((column: { props: (name: string) => unknown }) => column.props('label')))
      .toEqual(['申请类型', '来源项目', '申请人', '申请时间', '状态', '审批料号', '审批人', '退回原因'])
    for (const label of ['BOM层级', '料号分类', '名称']) {
      expect(pendingTable.findAllComponents({ name: 'ElTableColumn' }).some((column: { props: (name: string) => unknown }) => column.props('label') === label)).toBe(true)
    }
    expect(historyTable.text()).not.toContain('待审批气缸')
    expect(historyTable.findAllComponents({ name: 'ElTableColumn' }).some((column: { props: (name: string) => unknown }) => column.props('type') === 'selection')).toBe(false)
    expect(historyTable.findAll('button')).toHaveLength(0)
    expect(wrapper.get('.material-sync-table--history').text()).toContain('01020000065')
    expect(wrapper.get('.material-sync-table--history').text()).toContain('历史气缸')
    const historyPaginators = wrapper.findAllComponents({ name: 'ElPagination' })
    expect(historyPaginators.find(pagination => pagination.classes().includes('material-approval-history-pagination'))?.props('total')).toBe(2)
    expect(historyPaginators.find(pagination => pagination.classes().includes('material-sync-history-pagination'))?.props('total')).toBe(1)

    const nestedTabs = wrapper.findAll('[role="tab"]').filter(tab => ['当前处理 1', '审批/同步历史 3'].includes(tab.text().trim()))
    expect(nestedTabs.map(tab => tab.text().trim())).toEqual(['当前处理 1', '审批/同步历史 3'])
    expect(wrapper.find('.material-workflow-result').exists()).toBe(false)
    expect(wrapper.get('.material-approval-feedback').text()).toContain('运行状态空闲')
    expect(wrapper.get('.material-approval-feedback').text()).toContain('完成料号批准或退回后，结果将在此固定显示')
    expect(wrapper.get('.material-sync-feedback').text()).toContain('运行状态空闲')
    expect(wrapper.get('.material-sync-feedback').text()).toContain('完成U9C同步后，结果将在此固定显示')
    expect(wrapper.get('.material-approval-feedback').findAll('strong').map(item => item.text())).toEqual(['运行状态', '处理结果'])
    expect(wrapper.get('.material-sync-feedback').findAll('strong').map(item => item.text())).toEqual(['运行状态', '处理结果'])
    expect(wrapper.findAll('.material-code-workflow-stage').map(stage => stage.attributes('aria-label')))
      .toEqual(['第一步料号审批', '第二步同步到U9C', '第一步料号审批历史', '第二步U9C同步历史'])
  })

  it('当前批准与U9C同步列表各自固定每页50条并独立翻页', async () => {
    api.listMaterialCodeApplications.mockResolvedValue(Array.from({ length: 55 }, (_, index) => ({
      id: `pending-${index + 1}`, applicationType: 'StandardBomItem' as const, bomItemId: `item-${index + 1}`, bomHeaderKind: 'Standard' as const,
      projectId: 'project-1', projectCode: 'P700003', projectName: '氮检设备', categoryCode: '0102',
      applicationName: `待审批料品${String(index + 1).padStart(2, '0')}`, status: 'Pending' as const, workflowState: 'PendingApproval' as const,
      requestedBy: 'engineer', requestedAt: `2026-09-01T02:${String(index).padStart(2, '0')}:00Z`, rowVersion: 1,
    })))
    api.listMaterialSyncTasks.mockResolvedValue(Array.from({ length: 55 }, (_, index) => ({
      id: `sync-${index + 1}`, materialId: `material-${index + 1}`, materialCode: `0102${String(index + 1).padStart(7, '0')}`,
      materialName: `待同步料品${String(index + 1).padStart(2, '0')}`, operation: 'Create' as const, status: 'Failed' as const,
      correlationId: `sync-${index + 1}`, payloadJson: '{}', payloadSha256: `HASH-${index + 1}`, attemptCount: 1,
      requestedBy: 'engineer', requestedAt: '2026-09-01T03:00:00Z', createdAt: '2026-09-01T03:00:00Z', updatedAt: '2026-09-01T03:00:00Z',
    })))
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canDecideMaterialCode: true, canManageIntegration: false, requestedTab: 'code-approvals' },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    const workflowPaginators = wrapper.findAllComponents({ name: 'ElPagination' })
    const approvalPagination = workflowPaginators.find(pagination => pagination.classes().includes('material-pending-approval-pagination'))!
    const syncPagination = workflowPaginators.find(pagination => pagination.classes().includes('material-pending-sync-pagination'))!
    expect(approvalPagination.props('pageSize')).toBe(50)
    expect(approvalPagination.props('total')).toBe(55)
    expect(syncPagination.props('pageSize')).toBe(50)
    expect(syncPagination.props('total')).toBe(55)
    expect(wrapper.findAll('.material-code-approval-table--pending .el-table__body-wrapper tbody tr')).toHaveLength(50)
    expect(wrapper.findAll('.material-code-sync-table--pending .el-table__body-wrapper tbody tr')).toHaveLength(50)

    approvalPagination.vm.$emit('update:current-page', 2)
    syncPagination.vm.$emit('update:current-page', 2)
    await flushPromises()

    expect(wrapper.findAll('.material-code-approval-table--pending .el-table__body-wrapper tbody tr')).toHaveLength(5)
    expect(wrapper.findAll('.material-code-sync-table--pending .el-table__body-wrapper tbody tr')).toHaveLength(5)
    expect(wrapper.get('.material-code-approval-table--pending').text()).toContain('待审批料品55')
    expect(wrapper.get('.material-code-sync-table--pending').text()).toContain('01020000055')
  })

  it('人工行项目申请仍留在当前处理，完成U9C与A1 BOM后才进入历史', async () => {
    const application = {
      id: 'approved-pending-sync', applicationType: 'StandardBomItem' as const, bomItemId: 'item-1',
      projectId: 'project-1', projectCode: 'P700003', projectName: '氮检设备', categoryCode: '0302', applicationName: 'P700003 项目主BOM',
      materialId: 'material-master', materialCode: '03020005425', status: 'Approved' as const, workflowState: 'PendingBomSync' as const,
      syncTaskId: 'task-master', syncStatus: 'Succeeded' as const, requestedBy: 'developer', requestedAt: '2026-09-01T02:00:00Z',
      decidedBy: 'standardizer', rowVersion: 2,
    }
    const task = {
      id: 'task-master', materialId: 'material-master', materialCode: '03020005425', materialName: 'P700003 项目主BOM',
      projectCode: 'P700003', projectName: '氮检设备', requestedBy: 'developer', requestedAt: '2026-09-01T02:00:00Z',
      operation: 'Create' as const, status: 'Succeeded' as const, correlationId: 'master-v1', payloadJson: '{}', payloadSha256: 'HASH', attemptCount: 1,
      createdAt: '2026-09-01T02:00:00Z', updatedAt: '2026-09-01T02:01:00Z',
    }
    api.listMaterialCodeApplications
      .mockResolvedValueOnce([application])
      .mockResolvedValue([{ ...application, workflowState: 'Completed' }])
    api.listMaterialSyncTasks.mockResolvedValue([task])
    api.executeMaterialSyncTask.mockResolvedValue({
      material: { id: 'material-master' }, task, created: false, alreadyExisted: true, updated: false,
      completed: true, message: '料号审批与U9C同步流程已完成。',
    })
    const confirm = vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canDecideMaterialCode: true, canManageIntegration: false, requestedTab: 'code-approvals' },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    expect(wrapper.get('.material-sync-table').text()).toContain('待同步A1 BOM')
    expect(wrapper.get('.material-code-approval-table--history').text()).not.toContain('03020005425')
    await wrapper.get('.material-sync-table').findAll('button').find(button => button.text() === '同步')!.trigger('click')
    await flushPromises()

    expect(api.executeMaterialSyncTask).toHaveBeenCalledWith('task-master', 'token')
    expect(wrapper.get('.material-code-approval-table--history').text()).toContain('03020005425')
    expect(wrapper.get('.material-sync-feedback').text()).toContain('同步完成')
    expect(wrapper.get('.material-sync-feedback').text()).toContain('料号审批与U9C同步流程已完成')
    expect(wrapper.get('.material-approval-feedback').text()).toContain('暂无结果')
    confirm.mockRestore()
  })

  it.each(['Succeeded', 'Failed', 'PreviewReady', 'Running'] as const)('自动BOM同步任务 %s 不进入人工处理及批量选择', async status => {
    const task = {
      id: 'auto-task', materialId: 'auto-material', materialCode: '03021000002', materialName: 'P700005-4 主BOM',
      operation: 'Create' as const, status, correlationId: 'auto', payloadJson: '{}', payloadSha256: 'HASH',
      attemptCount: 1, createdAt: '2026-09-08T15:00:00Z', updatedAt: '2026-09-08T15:00:00Z',
    }
    api.listMaterialCodeApplications.mockResolvedValue([{
      id: 'auto-app', applicationType: 'BomHeader', bomHeaderKind: 'Master', bomItemId: null,
      projectId: 'project-4', materialId: 'auto-material', status: 'Approved', workflowState: 'PendingBomSync',
      requestedBy: 'engineer', requestedAt: '2026-09-08T15:00:00Z', rowVersion: 2,
    }])
    // Also cover a task carrying header metadata without its application in this page.
    api.listMaterialSyncTasks.mockResolvedValue([task, { ...task, id: 'auto-orphan', materialId: 'auto-orphan', bomHeaderKind: 'Standard' }])
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canDecideMaterialCode: true, canManageIntegration: false, requestedTab: 'code-approvals' },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()
    const table = wrapper.findAllComponents({ name: 'ElTable' }).find(component => component.classes().includes('material-code-sync-table--pending'))!
    expect(table.props('data')).toHaveLength(0)
    table.vm.$emit('selection-change', [task])
    await flushPromises()
    expect(wrapper.get('.material-sync-toolbar').text()).toContain('已选择 0 个可执行任务')
    expect(wrapper.get('.material-sync-table').text()).not.toContain('03021000002')
    expect(wrapper.get('.material-code-approval-note').text()).toContain('进度、失败原因和重试请到项目BOM多级总览查看')
    expect(api.executeMaterialSyncTask).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('自动BOM料号不进入人工审批列表及数量，也不能被残留选择批量批准', async () => {
    const applications = [
      { id: 'master', applicationType: 'BomHeader' as const, bomItemId: null, bomHeaderKind: 'Master' as const, categoryCode: '0302', applicationName: 'P700002 项目主BOM', rowVersion: 1 },
      { id: 'standard', applicationType: 'BomHeader' as const, bomItemId: null, bomHeaderKind: 'Standard' as const, categoryCode: '0201', applicationName: 'P700002 标准件BOM', rowVersion: 2 },
      { id: 'non-standard', applicationType: 'BomHeader' as const, bomItemId: null, bomHeaderKind: 'NonStandard' as const, categoryCode: '0201', applicationName: 'P700002 非标件BOM', rowVersion: 3 },
      { id: 'electrical', applicationType: 'BomHeader' as const, bomItemId: null, bomHeaderKind: 'Electrical' as const, categoryCode: '0201', applicationName: 'P700002 电气BOM', rowVersion: 4 },
    ].map((application, index) => ({
      ...application,
      projectId: 'project-2', projectCode: 'P700002', projectName: 'XXX设备', status: 'Pending' as const,
      requestedMaterialCode: index === 0 ? '03020000013' : `02010000${250 + index - 1}`,
      requestedBy: 'developer', requestedAt: '2026-08-25T10:19:10Z', createdAt: '2026-08-25T10:19:10Z', updatedAt: '2026-08-25T10:19:10Z',
    }))
    api.listMaterialCodeApplications.mockResolvedValue(applications)
    api.decideMaterialCodeApplication.mockImplementation((id: string) => Promise.resolve({
      application: { ...applications.find(application => application.id === id)!, status: 'Approved' },
    }))
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canDecideMaterialCode: true, canManageIntegration: false, requestedTab: 'code-approvals' },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    const table = wrapper.findAllComponents({ name: 'ElTable' }).find(component => component.classes().includes('material-code-approval-table'))!
    const rows = table.findAll('.el-table__body-wrapper tbody tr')
    expect(rows).toHaveLength(0)
    expect(table.text()).not.toContain('P700002')
    expect(wrapper.get('.material-code-approval-subtabs').text()).toContain('当前处理 0')
    table.vm.$emit('selection-change', applications)
    await flushPromises()
    await wrapper.get('.material-code-approval-toolbar').findAll('button').find(button => button.text() === '批量批准')!.trigger('click')
    await flushPromises()
    expect(api.decideMaterialCodeApplication).not.toHaveBeenCalled()
    expect(api.continueProjectBomU9Automation).not.toHaveBeenCalled()
    expect(wrapper.get('.material-approval-feedback').text()).toContain('暂无结果')
    expect(wrapper.get('.material-sync-feedback').text()).toContain('暂无结果')
  })

  it('可选择多项待审批申请并批量批准', async () => {
    const applications = [
      {
        id: 'application-1', applicationType: 'StandardBomItem' as const, bomItemId: 'item-1', bomHeaderKind: 'Standard' as const,
        projectId: 'project-1', projectCode: 'P700003', projectName: '氮检设备', categoryCode: '0201',
        applicationName: 'P700003 标准件BOM', status: 'Pending' as const, requestedBy: 'developer',
        requestedAt: '2026-08-23T14:47:51Z', createdAt: '2026-08-23T14:47:51Z', updatedAt: '2026-08-23T14:47:51Z', rowVersion: 3,
      },
      {
        id: 'application-2', applicationType: 'StandardBomItem' as const, bomItemId: 'item-2', bomHeaderKind: 'Standard' as const,
        projectId: 'project-1', projectCode: 'P700003', projectName: '氮检设备', categoryCode: '0201',
        applicationName: '标准件A', status: 'Pending' as const, requestedBy: 'developer',
        requestedAt: '2026-08-23T14:48:51Z', createdAt: '2026-08-23T14:48:51Z', updatedAt: '2026-08-23T14:48:51Z', rowVersion: 5,
      },
    ]
    api.listMaterialCodeApplications.mockResolvedValue(applications)
    api.decideMaterialCodeApplication.mockResolvedValue({ application: { ...applications[0], status: 'Approved' } })
    const confirm = vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canDecideMaterialCode: true, canManageIntegration: false, requestedTab: 'code-approvals' },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    const table = wrapper.findAllComponents({ name: 'ElTable' }).find(component => component.classes().includes('material-code-approval-table'))!
    table.vm.$emit('selection-change', applications)
    await flushPromises()
    const toolbar = wrapper.get('.material-code-approval-toolbar')
    expect(toolbar.text()).toContain('已选择 2 项待审批申请')
    await toolbar.findAll('button').find(button => button.text() === '批量批准')!.trigger('click')
    await flushPromises()

    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('2 项料号申请'), '批量批准料号申请', expect.any(Object))
    expect(api.decideMaterialCodeApplication).toHaveBeenNthCalledWith(1, 'application-1', 3, true, '', 'token')
    expect(api.decideMaterialCodeApplication).toHaveBeenNthCalledWith(2, 'application-2', 5, true, '', 'token')
    confirm.mockRestore()
  })

  it('等待U9C返回正式料号时持续显示当前处理进度', async () => {
    const confirm = vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
    const application = {
      id: 'application-1', applicationType: 'StandardBomItem' as const, bomItemId: 'item-1', bomHeaderKind: 'Standard' as const,
      projectId: 'project-1', projectCode: 'P700003', projectName: '氮检设备', categoryCode: '0302',
      applicationName: 'P700003 项目主BOM', status: 'Pending' as const, requestedBy: 'developer',
      requestedAt: '2026-08-23T14:47:51Z', createdAt: '2026-08-23T14:47:51Z', updatedAt: '2026-08-23T14:47:51Z', rowVersion: 3,
    }
    api.listMaterialCodeApplications.mockResolvedValue([application])
    let finishDecision!: (value: unknown) => void
    api.decideMaterialCodeApplication.mockImplementation(() => new Promise(resolve => { finishDecision = resolve }))
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canDecideMaterialCode: true, canManageIntegration: false, requestedTab: 'code-approvals' },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    const table = wrapper.findAllComponents({ name: 'ElTable' }).find(component => component.classes().includes('material-code-approval-table'))!
    await table.findAll('.el-table__body-wrapper tbody tr')[0].findAll('button').find(button => button.text() === '批准')!.trigger('click')
    await flushPromises()

    expect(wrapper.get('.material-approval-feedback .material-step-feedback__status').text()).toContain('按PLM基线分配料号，批准后请在本页选择对应记录完成U9C同步')
    finishDecision({ application: { ...application, status: 'Approved' } })
    await flushPromises()
    expect(wrapper.get('.material-approval-feedback .material-step-feedback__status').text()).toContain('空闲')
    confirm.mockRestore()
  })

  it('批量退回只填写一次统一原因并逐项提交', async () => {
    const applications = [
      {
        id: 'application-1', applicationType: 'StandardBomItem' as const, bomItemId: 'item-1', bomHeaderKind: 'Standard' as const,
        projectId: 'project-1', projectCode: 'P700003', projectName: '氮检设备', categoryCode: '0201',
        applicationName: 'P700003 电气BOM', status: 'Pending' as const, requestedBy: 'developer',
        requestedAt: '2026-08-23T14:47:51Z', createdAt: '2026-08-23T14:47:51Z', updatedAt: '2026-08-23T14:47:51Z', rowVersion: 7,
      },
      {
        id: 'application-2', applicationType: 'StandardBomItem' as const, bomItemId: 'item-2', bomHeaderKind: 'Standard' as const,
        projectId: 'project-1', projectCode: 'P700003', projectName: '氮检设备', categoryCode: '0201',
        applicationName: '标准件B', status: 'Pending' as const, requestedBy: 'developer',
        requestedAt: '2026-08-23T14:48:51Z', createdAt: '2026-08-23T14:48:51Z', updatedAt: '2026-08-23T14:48:51Z', rowVersion: 9,
      },
    ]
    api.listMaterialCodeApplications.mockResolvedValue(applications)
    api.decideMaterialCodeApplication.mockResolvedValue({ application: { ...applications[0], status: 'Rejected' } })
    const prompt = vi.spyOn(ElMessageBox, 'prompt').mockResolvedValue({ value: '资料不完整', action: 'confirm' } as never)
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canDecideMaterialCode: true, canManageIntegration: false, requestedTab: 'code-approvals' },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    const table = wrapper.findAllComponents({ name: 'ElTable' }).find(component => component.classes().includes('material-code-approval-table'))!
    table.vm.$emit('selection-change', applications)
    await flushPromises()
    await wrapper.get('.material-code-approval-toolbar').findAll('button').find(button => button.text() === '批量退回')!.trigger('click')
    await flushPromises()

    expect(prompt).toHaveBeenCalledOnce()
    const inputValidator = prompt.mock.calls[0]?.[2]?.inputValidator as (value: string) => true | string
    expect(inputValidator('   ')).toBe('请填写退回原因')
    expect(inputValidator('x'.repeat(1001))).toBe('退回原因不能超过1000个字符')
    expect(inputValidator('资料不完整')).toBe(true)
    expect(api.decideMaterialCodeApplication).toHaveBeenNthCalledWith(1, 'application-1', 7, false, '资料不完整', 'token')
    expect(api.decideMaterialCodeApplication).toHaveBeenNthCalledWith(2, 'application-2', 9, false, '资料不完整', 'token')
    expect(wrapper.get('.material-approval-feedback').text()).toContain('审批完成')
    expect(wrapper.get('.material-approval-feedback').text()).toContain('共 2 项，已退回 2 项，失败 0 项')
    prompt.mockRestore()
  })

  it('单项退回要求填写原因并在审批历史中供只读用户查看', async () => {
    const application = {
      id: 'application-rejected', applicationType: 'StandardBomItem' as const, bomItemId: 'item-rejected', bomHeaderKind: 'Standard' as const,
      projectId: 'project-1', projectCode: 'P700003', projectName: '氮检设备', categoryCode: '0102',
      applicationName: '气缸', status: 'Pending' as const, requestedBy: 'engineer', requestedAt: '2026-09-04T01:00:00Z', rowVersion: 3,
    }
    const rejected = { ...application, status: 'Rejected' as const, decisionComment: '型号资料不完整', decidedBy: 'standardizer', decidedAt: '2026-09-04T02:00:00Z', rowVersion: 4 }
    api.listMaterialCodeApplications.mockResolvedValueOnce([application]).mockResolvedValue([rejected])
    api.decideMaterialCodeApplication.mockResolvedValue({ application: rejected })
    const prompt = vi.spyOn(ElMessageBox, 'prompt').mockResolvedValue({ value: '  型号资料不完整  ', action: 'confirm' } as never)
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: false, canApprove: false, canDecideMaterialCode: true, canManageIntegration: false, requestedTab: 'code-approvals' },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    const pendingTable = wrapper.get('.material-code-approval-table--pending')
    await pendingTable.findAll('button').find(button => button.text() === '退回')!.trigger('click')
    await flushPromises()

    const inputValidator = prompt.mock.calls[0]?.[2]?.inputValidator as (value: string) => true | string
    expect(inputValidator('  ')).toBe('请填写退回原因')
    expect(inputValidator('原因明确')).toBe(true)
    expect(api.decideMaterialCodeApplication).toHaveBeenCalledWith('application-rejected', 3, false, '型号资料不完整', 'token')
    expect(wrapper.get('.material-code-approval-table--history').text()).toContain('退回原因')
    expect(wrapper.get('.material-code-approval-table--history').text()).toContain('型号资料不完整')
    prompt.mockRestore()
  })

  it('新增料品时必须主动选择分类后才生成料号', async () => {
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    const createButton = wrapper.findAll('button').find(button => button.text().includes('新增料品'))
    await createButton!.trigger('click')
    await flushPromises()

    const codeItem = wrapper.findAll('.el-form-item').find(item => item.text().includes('PLM物料编码'))!
    const codeInput = codeItem.get('input')
    expect(codeInput.attributes('disabled')).toBeDefined()
    expect(codeInput.attributes('placeholder')).toBe('选择开放分类后自动生成')
    const categoryItem = wrapper.findAll('.el-form-item').find(item => item.text().includes('U9C对应分类'))!
    expect((categoryItem.get('input').element as HTMLInputElement).value).toBe('')
    expect(categoryItem.text()).toContain('请选择U9C对应分类')
    expect(categoryItem.text()).not.toContain('新增时必须主动选择')
    expect(codeItem.text()).toContain('从PLM分类当前基线向后预留编号')
    expect(codeItem.text()).toContain('U9C在后台同步')
    expect(wrapper.text()).toContain('001 个')
    expect(wrapper.text()).not.toContain('PLM直接保存并使用U9C计量单位编码')
    expect(wrapper.get('.material-editor-dialog').text()).not.toContain('供给方式')
    expect(wrapper.getComponent({ name: 'ElDialog' }).props('width')).toBe('688px')
    expect(wrapper.text()).toContain('料品采购链接')
    expect(wrapper.get('.material-editor-grid').findAll('.el-form-item')).toHaveLength(18)
    expect(wrapper.text()).toContain('选型建议')
    expect(wrapper.text()).toContain('参考价格')
    expect(wrapper.text()).toContain('3D')
    expect(wrapper.text()).toContain('资料')
    expect(wrapper.findAll('button').filter(button => button.text() === '保存后上传')).toHaveLength(3)
    const editorItems = wrapper.get('.material-editor-grid').findAll('.el-form-item')
    const remarkItem = editorItems.find(item => item.text().includes('备注'))!
    const selectionAdviceItem = editorItems.find(item => item.text().includes('选型建议'))!
    expect(remarkItem.classes()).toContain('material-editor-grid__wide')
    expect(selectionAdviceItem.classes()).not.toContain('material-editor-grid__wide')
    expect(editorItems.indexOf(remarkItem)).toBeLessThan(editorItems.indexOf(selectionAdviceItem))
    const weightItem = editorItems.find(item => item.text().includes('重量'))!
    expect(weightItem.find('.weight-unit').exists()).toBe(false)

    const recommendButton = wrapper.findAll('button').find(button => button.text() === '推荐')!
    expect(recommendButton.attributes('aria-pressed')).toBe('false')
    await recommendButton.trigger('click')
    expect(recommendButton.text()).toBe('已推荐')
    expect(recommendButton.attributes('aria-pressed')).toBe('true')
  })

  it('编辑已保存料品时加载附件并允许下载', async () => {
    const material = {
      id: 'material-1', materialCode: 'EL-001', name: '光电传感器', kind: 'Electrical', supplyMode: 'Purchase',
      unitCode: '001', approvalStatus: 'Draft', syncStatus: 'NotQueued', createdBy: 'admin', createdAt: '2026-08-17T00:00:00Z',
      updatedBy: 'admin', updatedAt: '2026-08-17T00:00:00Z', rowVersion: 1, categoryCode: '0101', isArchived: false,
      u9SyncConfirmed: false, sourceSystem: 'Pdm', masterOwner: 'Pdm', referenceCount: 0, model3DAttachmentCount: 1,
      documentAttachmentCount: 0,
    }
    const attachment = {
      id: 'attachment-1', materialId: 'material-1', kind: 'Model3D', originalFileName: 'sensor.step', fileLength: 1024,
      sha256: 'A'.repeat(64), uploadedBy: 'admin', uploadedAt: '2026-08-24T08:00:00Z',
    }
    api.listMaterials.mockResolvedValueOnce([material])
    api.listMaterialAttachments.mockResolvedValueOnce([attachment])
    api.downloadMaterialAttachment.mockResolvedValueOnce(undefined)
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    wrapper.findComponent({ name: 'ElTable' }).vm.$emit('selection-change', [material])
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '编辑')!.trigger('click')
    await flushPromises()

    expect(api.listMaterialAttachments).toHaveBeenCalledWith('material-1', 'token')
    expect(wrapper.findAll('button').filter(button => button.text() === '上传附件')).toHaveLength(2)
    const downloadButton = wrapper.findAll('button').find(button => button.text() === 'sensor.step')!
    await downloadButton.trigger('click')
    expect(api.downloadMaterialAttachment).toHaveBeenCalledWith('material-1', attachment, 'token')
  })

  it('料品操作移到列表上方并通过复选框选择', async () => {
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    const table = wrapper.findComponent({ name: 'ElTable' })
    table.vm.$emit('selection-change', [{
      id: 'material-1', materialCode: 'EL-001', name: '光电传感器', kind: 'Electrical', supplyMode: 'Purchase',
      unitCode: '001', specification: 'M18', approvalStatus: 'Draft', syncStatus: 'NotQueued', rowVersion: 1,
      categoryCode: '0101', isArchived: false, u9SyncConfirmed: false,
    }])
    await flushPromises()

    expect(wrapper.findAll('button').some(button => button.text() === '删除')).toBe(true)
    expect(wrapper.findAll('button').some(button => button.text() === '停用')).toBe(true)
    expect(wrapper.findAll('button').some(button => button.text() === '启用')).toBe(true)
    expect(wrapper.find('.material-row-actions').exists()).toBe(false)
    expect(wrapper.find('.el-table-column--selection').exists()).toBe(true)
    expect(wrapper.find('.el-table__fixed-right').exists()).toBe(false)
  })

  it('已停用但未确认写入U9C的料品仍可删除', async () => {
    api.listMaterials.mockResolvedValueOnce([{
      id: 'material-archived', materialCode: '01020000001', name: '气缸', kind: 'Standard', supplyMode: 'Purchase',
      unitCode: '001', specification: 'CDQ2B32-100', approvalStatus: 'Approved', syncStatus: 'Failed', createdBy: 'admin',
      createdAt: '2026-08-17T00:00:00Z', updatedBy: 'admin', updatedAt: '2026-08-17T00:00:00Z', rowVersion: 3,
      isArchived: true, u9SyncConfirmed: false,
    }])

    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    wrapper.findComponent({ name: 'ElTable' }).vm.$emit('selection-change', [{
      id: 'material-archived', materialCode: '01020000001', name: '气缸', kind: 'Standard', supplyMode: 'Purchase',
      unitCode: '001', approvalStatus: 'Approved', syncStatus: 'Failed', rowVersion: 3, isArchived: true, u9SyncConfirmed: false,
    }])
    await flushPromises()

    const deleteButton = wrapper.findAll('button').find(button => button.text() === '删除')!
    expect(deleteButton.attributes('disabled')).toBeUndefined()
    expect(wrapper.text()).toContain('已停用')
  })

  it('选择单个已停用料品后可保留原料号直接启用', async () => {
    const confirmSpy = vi.spyOn(ElMessageBox, 'confirm').mockResolvedValueOnce({ action: 'confirm' } as never)
    const archived = {
      id: 'material-archived', materialCode: '01020000001', name: '气缸', kind: 'Standard', supplyMode: 'Purchase',
      unitCode: '001', specification: 'CDQ2B32-100', approvalStatus: 'Approved', syncStatus: 'Succeeded', rowVersion: 3,
      categoryCode: '0102', isArchived: true, u9SyncConfirmed: true, sourceSystem: 'Pdm', masterOwner: 'Pdm',
    } as PdmMaterial
    api.reactivateMaterial.mockResolvedValueOnce({ ...archived, isArchived: false, rowVersion: 4 })
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    wrapper.findComponent({ name: 'ElTable' }).vm.$emit('selection-change', [archived])
    await flushPromises()
    const reactivateButton = wrapper.findAll('button').find(button => button.text() === '启用')!
    expect(reactivateButton.attributes('disabled')).toBeUndefined()
    expect(wrapper.findAll('button').find(button => button.text() === '停用')!.attributes('disabled')).toBeDefined()
    await reactivateButton.trigger('click')
    await flushPromises()

    expect(confirmSpy).toHaveBeenCalledWith(
      expect.stringContaining('保留原料号、审批状态和历史记录'),
      '启用料品',
      expect.objectContaining({ confirmButtonText: '确认启用' }),
    )
    expect(api.reactivateMaterial).toHaveBeenCalledWith('material-archived', 3, 'token')
    expect(reactivateButton.attributes('disabled')).toBeDefined()
    confirmSpy.mockRestore()
  })

  it('安全删除会把所选料品逐条交给后端校验', async () => {
    const confirmSpy = vi.spyOn(ElMessageBox, 'confirm').mockResolvedValueOnce({ action: 'confirm' } as never)
    const material = {
      id: 'material-1', materialCode: 'EL-001', name: '光电传感器', kind: 'Electrical', supplyMode: 'Purchase',
      unitCode: '001', specification: 'M18', approvalStatus: 'Draft', syncStatus: 'NotQueued', rowVersion: 1,
      categoryCode: '0101', isArchived: false, u9SyncConfirmed: false,
    }
    api.deleteMaterial.mockResolvedValueOnce({ material, deleted: true, archived: false })
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    wrapper.findComponent({ name: 'ElTable' }).vm.$emit('selection-change', [material])
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '删除')!.trigger('click')
    await flushPromises()

    expect(confirmSpy).toHaveBeenCalledWith(
      expect.stringContaining('仅PLM主控'),
      '安全删除料品',
      expect.objectContaining({ confirmButtonText: '确认删除' }),
    )
    expect(api.deleteMaterial).toHaveBeenCalledWith('material-1', 1, 'token')
    confirmSpy.mockRestore()
  })

  it('安全删除失败后保留失败料品的选择状态以便重试', async () => {
    const confirmSpy = vi.spyOn(ElMessageBox, 'confirm').mockResolvedValueOnce({ action: 'confirm' } as never)
    const material = {
      id: 'material-1', materialCode: 'EL-001', name: '光电传感器', kind: 'Electrical', supplyMode: 'Purchase',
      unitCode: '001', specification: 'M18', approvalStatus: 'Draft', syncStatus: 'NotQueued', rowVersion: 1,
      categoryCode: '0101', isArchived: false, u9SyncConfirmed: false,
    }
    api.deleteMaterial.mockRejectedValueOnce(new Error('删除前实时校验失败'))
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    wrapper.findComponent({ name: 'ElTable' }).vm.$emit('selection-change', [material])
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '删除')!.trigger('click')
    await flushPromises()

    expect(api.deleteMaterial).toHaveBeenCalledWith('material-1', 1, 'token')
    expect(wrapper.findAll('button').find(button => button.text() === '删除')!.attributes('disabled')).toBeUndefined()
    expect(wrapper.text()).toContain('EL-001')
    confirmSpy.mockRestore()
  })

  it('历史已同步但实时U9可能缺失的料品允许提交安全删除校验', async () => {
    const confirmSpy = vi.spyOn(ElMessageBox, 'confirm').mockRejectedValueOnce('cancel')
    api.listMaterials.mockResolvedValueOnce([{
      id: 'material-synced', materialCode: '01020000050', name: '气缸', kind: 'Standard', supplyMode: 'Purchase',
      unitCode: '001', specification: '11GGG', approvalStatus: 'Approved', syncStatus: 'Succeeded', createdBy: 'admin',
      createdAt: '2026-08-17T00:00:00Z', updatedBy: 'admin', updatedAt: '2026-08-20T00:00:00Z', rowVersion: 4,
      isArchived: false, u9SyncConfirmed: true,
    }])

    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    wrapper.findComponent({ name: 'ElTable' }).vm.$emit('selection-change', [{
      id: 'material-synced', materialCode: '01020000050', name: '气缸', kind: 'Standard', supplyMode: 'Purchase',
      unitCode: '001', approvalStatus: 'Approved', syncStatus: 'Succeeded', rowVersion: 4, isArchived: false, u9SyncConfirmed: true,
    }])
    await flushPromises()
    const disableButton = wrapper.findAll('button').find(button => button.text() === '停用')!
    expect(disableButton).toBeTruthy()
    expect(wrapper.findAll('button').find(button => button.text() === '删除')!.attributes('disabled')).toBeUndefined()
    await disableButton.trigger('click')
    await flushPromises()

    expect(confirmSpy).toHaveBeenCalledWith(
      expect.stringContaining('本操作只停用PLM料品，不会停用或物理删除U9C料品'),
      '停用料品',
      expect.objectContaining({ confirmButtonText: '确认停用' }),
    )
    expect(api.archiveMaterial).not.toHaveBeenCalled()
    confirmSpy.mockRestore()
  })

  it('supports on-demand read-only U9C lookup from the material list', async () => {
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    wrapper.findComponent({ name: 'ElTable' }).vm.$emit('selection-change', [{
      id: 'material-1', materialCode: 'EL-001', name: '光电传感器', kind: 'Electrical', supplyMode: 'Purchase',
      unitCode: '001', specification: 'M18', approvalStatus: 'Draft', syncStatus: 'NotQueued', rowVersion: 1,
      categoryCode: '0101', isArchived: false, u9SyncConfirmed: false,
    }])
    await flushPromises()
    const queryButton = wrapper.findAll('button').find(button => button.text() === '查询U9C')!
    await queryButton.trigger('click')
    await flushPromises()

    expect(api.queryU9Material).toHaveBeenCalledWith('EL-001', 'token')
    expect(api.getMaterialRemovalReadiness).toHaveBeenCalledWith('material-1', 'token')
    expect(wrapper.text()).toContain('一致·同步删除未启用')
  })

  it('U9C存在且同步删除合同已启用时显示可同步删除', async () => {
    api.getMaterialRemovalReadiness.mockResolvedValueOnce({
      materialId: 'material-1', materialCode: 'EL-001', pdmReferenceCount: 0, isPdmMaster: true,
      localDeletePreconditionsPassed: true, u9ReferenceCheckAvailable: false, synchronizedDeleteAvailable: true,
      decision: 'PLM未发现引用；若U9C存在，将先由U9C删除接口校验引用并删除，回查确认不存在后才删除PLM主档。',
    })
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    wrapper.findComponent({ name: 'ElTable' }).vm.$emit('selection-change', [{
      id: 'material-1', materialCode: 'EL-001', name: '光电传感器', kind: 'Electrical', supplyMode: 'Purchase',
      unitCode: '001', specification: 'M18', approvalStatus: 'Draft', syncStatus: 'NotQueued', rowVersion: 1,
      categoryCode: '0101', isArchived: false, u9SyncConfirmed: false,
    }])
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '查询U9C')!.trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('一致·可同步删除')
  })

  it('多选料品后逐条查询U9C并分别显示校验结果', async () => {
    const electrical = {
      id: 'material-1', materialCode: 'EL-001', name: '光电传感器', kind: 'Electrical', supplyMode: 'Purchase',
      unitCode: '001', specification: 'M18', approvalStatus: 'Draft', syncStatus: 'NotQueued', rowVersion: 1,
      categoryCode: '0101', isArchived: false, u9SyncConfirmed: false,
    }
    const mechanical = {
      id: 'material-2', materialCode: 'ME-002', name: '气缸', kind: 'Standard', supplyMode: 'Purchase',
      unitCode: '001', specification: 'M20', approvalStatus: 'Approved', syncStatus: 'Succeeded', rowVersion: 2,
      categoryCode: '0102', isArchived: false, u9SyncConfirmed: true,
    }
    api.listMaterials.mockResolvedValueOnce([electrical, mechanical])
    api.queryU9Material
      .mockResolvedValueOnce({ responseCode: 0, items: [{ u9ItemId: 'u9-1', u9ItemCode: 'EL-001', u9ItemName: '光电传感器', u9Specification: 'M18' }] })
      .mockResolvedValueOnce({ responseCode: 0, items: [] })
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    wrapper.findComponent({ name: 'ElTable' }).vm.$emit('selection-change', [electrical, mechanical])
    await flushPromises()
    const queryButton = wrapper.findAll('button').find(button => button.text() === '查询U9C')!
    expect(queryButton.attributes('disabled')).toBeUndefined()
    await queryButton.trigger('click')
    await flushPromises()

    expect(api.queryU9Material).toHaveBeenNthCalledWith(1, 'EL-001', 'token')
    expect(api.queryU9Material).toHaveBeenNthCalledWith(2, 'ME-002', 'token')
    expect(wrapper.text()).toContain('一致·同步删除未启用')
    expect(wrapper.text()).toContain('U9未找到·可安全删除')
  })

  it('查询U9C时显示主控限制并禁止删除U9C主控料品', async () => {
    const u9Owned = {
      id: 'material-u9', materialCode: 'U9-001', name: 'U9料品', kind: 'Electrical', supplyMode: 'Purchase',
      unitCode: '001', specification: 'M18', approvalStatus: 'Approved', syncStatus: 'Succeeded', rowVersion: 1,
      categoryCode: '0101', isArchived: false, u9SyncConfirmed: true, sourceSystem: 'U9C', masterOwner: 'U9C',
    }
    api.listMaterials.mockResolvedValueOnce([u9Owned])
    api.queryU9Material.mockResolvedValueOnce({ responseCode: 0, items: [{ u9ItemId: 'u9-1', u9ItemCode: 'U9-001', u9Specification: 'M18' }] })
    api.getMaterialRemovalReadiness.mockResolvedValueOnce({
      materialId: 'material-u9', materialCode: 'U9-001', pdmReferenceCount: 2, isPdmMaster: false,
      localDeletePreconditionsPassed: false, u9ReferenceCheckAvailable: false, synchronizedDeleteAvailable: false,
      decision: 'U9C主控料品不允许从PLM发起物理删除。',
    })
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    wrapper.findComponent({ name: 'ElTable' }).vm.$emit('selection-change', [u9Owned])
    await flushPromises()
    expect(wrapper.findAll('button').find(button => button.text() === '删除')!.attributes('disabled')).toBeDefined()
    await wrapper.findAll('button').find(button => button.text() === '查询U9C')!.trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('一致·U9主控')
  })

  it('查询U9C时同时显示PLM引用数量', async () => {
    api.getMaterialRemovalReadiness.mockResolvedValueOnce({
      materialId: 'material-1', materialCode: 'EL-001', pdmReferenceCount: 2, isPdmMaster: true,
      localDeletePreconditionsPassed: false, u9ReferenceCheckAvailable: false, synchronizedDeleteAvailable: false,
      decision: 'PLM中已有2处BOM引用，不能删除。',
    })
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    wrapper.findComponent({ name: 'ElTable' }).vm.$emit('selection-change', [{
      id: 'material-1', materialCode: 'EL-001', name: '光电传感器', kind: 'Electrical', supplyMode: 'Purchase',
      unitCode: '001', specification: 'M18', approvalStatus: 'Draft', syncStatus: 'NotQueued', rowVersion: 1,
      categoryCode: '0101', isArchived: false, u9SyncConfirmed: false,
    }])
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '查询U9C')!.trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('一致·PLM引用2')
  })

  it('删除预检接口不可用时保留U9查询结果并关闭删除判定', async () => {
    api.getMaterialRemovalReadiness.mockRejectedValueOnce(new Error('接口未部署'))
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    wrapper.findComponent({ name: 'ElTable' }).vm.$emit('selection-change', [{
      id: 'material-1', materialCode: 'EL-001', name: '光电传感器', kind: 'Electrical', supplyMode: 'Purchase',
      unitCode: '001', specification: 'M18', approvalStatus: 'Draft', syncStatus: 'NotQueued', rowVersion: 1,
      categoryCode: '0101', isArchived: false, u9SyncConfirmed: false,
    }])
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '查询U9C')!.trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('一致·删除校验不可用')
  })

  it('查询U9C时对同编码料品校验规格并显示冲突', async () => {
    api.queryU9Material.mockResolvedValueOnce({
      responseCode: 0,
      items: [{ u9ItemId: 'u9-2', u9ItemCode: 'EL-001', u9ItemName: '光电传感器', u9Specification: 'M12' }],
    })
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    wrapper.findComponent({ name: 'ElTable' }).vm.$emit('selection-change', [{
      id: 'material-1', materialCode: 'EL-001', name: '光电传感器', kind: 'Electrical', supplyMode: 'Purchase',
      unitCode: '001', specification: 'M18', approvalStatus: 'Draft', syncStatus: 'NotQueued', rowVersion: 1,
      categoryCode: '0101', isArchived: false, u9SyncConfirmed: false,
    }])
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '查询U9C')!.trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('规格冲突')
    expect(wrapper.find('.u9-validation').attributes('aria-label')).toContain('U9C为“M12”')
  })

  it('已批准但未同步成功的料品可编辑并生成新的创建预览', async () => {
    const approvedMaterial = {
      id: 'material-approved', materialCode: '01020000050', name: '气缸', kind: 'Standard', supplyMode: 'Purchase',
      unitCode: '001', specification: '11GGG', approvalStatus: 'Approved', syncStatus: 'Failed', createdBy: 'admin',
      createdAt: '2026-08-17T00:00:00Z', updatedBy: 'admin', updatedAt: '2026-08-17T00:00:00Z', rowVersion: 3,
      categoryCode: '0102', u9CategoryCode: '0102', isArchived: false, u9SyncConfirmed: false,
    }
    api.listMaterials.mockResolvedValueOnce([approvedMaterial])
    api.changeApprovedMaterial.mockResolvedValueOnce({
      material: { ...approvedMaterial, name: '气缸新名称', syncStatus: 'PreviewReady', rowVersion: 4 },
      task: {
        id: 'task-new', materialId: approvedMaterial.id, operation: 'Create', status: 'PreviewReady', correlationId: 'new-v3',
        payloadJson: '{}', payloadSha256: 'ABC', attemptCount: 0, createdAt: '2026-08-19T00:00:00Z', updatedAt: '2026-08-19T00:00:00Z',
      },
    })
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    wrapper.findComponent({ name: 'ElTable' }).vm.$emit('selection-change', [approvedMaterial])
    await flushPromises()
    expect(wrapper.findAll('button').filter(button => ['编辑', '批量编辑'].includes(button.text())).map(button => button.text())).toEqual(['编辑'])
    await wrapper.findAll('button').find(button => button.text() === '编辑')!.trigger('click')
    await flushPromises()
    const nameItem = wrapper.findAll('.el-form-item').find(item => item.text().includes('物料名称'))!
    await nameItem.get('input').setValue('气缸新名称')
    await wrapper.findAll('button').find(button => button.text() === '保存修改')!.trigger('click')
    await flushPromises()

    expect(api.changeApprovedMaterial).toHaveBeenCalledWith(
      approvedMaterial.id,
      expect.objectContaining({ name: '气缸新名称', expectedRowVersion: 3 }),
      'token',
    )
    expect(wrapper.text()).toContain('气缸新名称')
  })

  it('勾选多行后可批量修改指定字段', async () => {
    const selected = [
      {
        id: 'material-a', materialCode: '01010000001', name: '传感器A', kind: 'Electrical', supplyMode: 'Purchase',
        unitCode: '001', specification: 'M18', brand: '旧品牌', approvalStatus: 'Draft', syncStatus: 'NotQueued', createdBy: 'admin',
        createdAt: '2026-08-17T00:00:00Z', updatedBy: 'admin', updatedAt: '2026-08-17T00:00:00Z', rowVersion: 1,
        categoryCode: '0101', isArchived: false, u9SyncConfirmed: false,
      },
      {
        id: 'material-b', materialCode: '01010000002', name: '传感器B', kind: 'Electrical', supplyMode: 'Purchase',
        unitCode: '001', specification: 'M12', brand: '旧品牌', approvalStatus: 'Draft', syncStatus: 'NotQueued', createdBy: 'admin',
        createdAt: '2026-08-17T00:00:00Z', updatedBy: 'admin', updatedAt: '2026-08-17T00:00:00Z', rowVersion: 2,
        categoryCode: '0101', isArchived: false, u9SyncConfirmed: false,
      },
    ]
    api.listMaterials.mockResolvedValueOnce(selected)
    api.updateMaterial.mockImplementation(async (id, input) => ({
      ...selected.find(item => item.id === id)!, ...input, id, rowVersion: (input.expectedRowVersion ?? 0) + 1,
    }))
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    wrapper.findComponent({ name: 'ElTable' }).vm.$emit('selection-change', selected)
    await flushPromises()
    expect(wrapper.findAll('button').filter(button => ['编辑', '批量编辑'].includes(button.text())).map(button => button.text())).toEqual(['批量编辑'])
    await wrapper.findAll('button').find(button => button.text() === '批量编辑')!.trigger('click')
    await flushPromises()
    await wrapper.findAll('.batch-editor-form .el-checkbox').find(checkbox => checkbox.text() === '品牌')!.get('input').setValue(true)
    await flushPromises()
    const brandItem = wrapper.findAll('.batch-editor-form .el-form-item').find(item => item.text().includes('品牌'))!
    await brandItem.get('input.el-input__inner').setValue('统一品牌')
    await wrapper.findAll('button').find(button => button.text() === '保存批量修改')!.trigger('click')
    await flushPromises()

    expect(api.updateMaterial).toHaveBeenCalledTimes(2)
    expect(api.updateMaterial).toHaveBeenCalledWith('material-a', expect.objectContaining({ brand: '统一品牌' }), 'token')
    expect(api.updateMaterial).toHaveBeenCalledWith('material-b', expect.objectContaining({ brand: '统一品牌' }), 'token')
  })

  it('没有BOM和料品编辑权限时不显示编辑操作', async () => {
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: false, canApprove: false, canManageIntegration: false },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    expect(wrapper.findAll('button').some(button => button.text() === '编辑')).toBe(false)
    expect(wrapper.findAll('button').some(button => button.text() === '查询U9C')).toBe(true)
  })

  it('旧同步任务入口重定向到料号审批且不显示已废止任务', async () => {
    api.listMaterialSyncTasks.mockResolvedValueOnce([{
      id: 'task-old', materialId: 'material-1', operation: 'Create', status: 'Superseded', correlationId: 'old-v1',
      payloadJson: '{}', payloadSha256: 'OLD', attemptCount: 1, lastError: '料品已编辑，旧请求已废止。',
      createdAt: '2026-08-17T00:00:00Z', updatedAt: '2026-08-19T00:00:00Z',
    }])
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()
    expect(wrapper.findAll('[role="tab"]').some(tab => tab.text().includes('同步任务'))).toBe(false)
    expect(wrapper.get('.material-code-approval-note').text()).toContain('项目多级BOM表头料号由系统自动批准')
    expect(wrapper.find('.material-sync-table').text()).not.toContain('已废止')
  })

  it('可多选任务创建后台批次并按固定状态显示最终结果', async () => {
    vi.useFakeTimers()
    const syncTasks = [
      {
        id: 'task-failed', materialId: 'material-2', materialCode: '02010000102', materialName: '非标件BOM', operation: 'Create' as const,
        status: 'Failed' as const, correlationId: 'failed-v1', payloadJson: '{}', payloadSha256: 'FAILED', attemptCount: 1,
        createdAt: '2026-08-24T10:01:00Z', updatedAt: '2026-08-24T10:01:00Z',
      },
      {
        id: 'task-ready', materialId: 'material-1', materialCode: '02010000101', materialName: '标准件BOM', operation: 'Create' as const,
        status: 'PreviewReady' as const, correlationId: 'ready-v1', payloadJson: '{}', payloadSha256: 'READY', attemptCount: 0,
        createdAt: '2026-08-24T10:00:00Z', updatedAt: '2026-08-24T10:00:00Z',
      },
      {
        id: 'task-succeeded', materialId: 'material-3', materialCode: '02010000103', materialName: '电气BOM', operation: 'Create' as const,
        status: 'Succeeded' as const, correlationId: 'done-v1', payloadJson: '{}', payloadSha256: 'DONE', attemptCount: 1,
        createdAt: '2026-08-24T10:02:00Z', updatedAt: '2026-08-24T10:02:00Z',
      },
    ]
    api.listMaterialSyncTasks.mockResolvedValueOnce(syncTasks)
    api.createMaterialSyncBatch.mockResolvedValue({
      id: 'batch-1', status: 'Queued', requestedBy: 'admin', requestedRole: 'Administrator', totalCount: 2,
      completedCount: 0, succeededCount: 0, waitingCount: 0, failedCount: 0, createdAt: '2026-09-01T00:00:00Z', items: [],
    })
    api.getMaterialSyncBatch.mockResolvedValue({
      id: 'batch-1', status: 'PartiallySucceeded', requestedBy: 'admin', requestedRole: 'Administrator', totalCount: 2,
      completedCount: 2, succeededCount: 1, waitingCount: 0, failedCount: 1, createdAt: '2026-09-01T00:00:00Z', completedAt: '2026-09-01T00:01:00Z',
      items: [
        { id: 'item-1', batchId: 'batch-1', taskId: 'task-ready', ordinal: 1, status: 'Succeeded' },
        { id: 'item-2', batchId: 'batch-1', taskId: 'task-failed', ordinal: 2, status: 'Failed', message: '02010000102：U9C参数校验失败' },
      ],
    })
    const confirm = vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false, requestedTab: 'tasks' },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    const table = wrapper.findAllComponents({ name: 'ElTable' }).find(component => component.classes().includes('material-sync-table'))!
    expect((table.props('data') as typeof syncTasks).map(task => task.materialCode)).toEqual(['02010000101', '02010000102'])
    const selectionColumn = table.findAllComponents({ name: 'ElTableColumn' }).find(column => column.props('type') === 'selection')!
    expect(selectionColumn.exists()).toBe(true)
    const rowSelectionInputs = wrapper.findAll('.material-sync-table .el-table__body-wrapper input[type="checkbox"]')
    expect(rowSelectionInputs).toHaveLength(2)
    expect(rowSelectionInputs[0].attributes('disabled')).toBeUndefined()
    expect(rowSelectionInputs[1].attributes('disabled')).toBeUndefined()
    table.vm.$emit('selection-change', syncTasks.slice(0, 2))
    await flushPromises()

    const toolbar = wrapper.get('.material-sync-toolbar')
    expect(toolbar.text()).toContain('已选择 2 个可执行任务')
    await toolbar.get('button').trigger('click')
    await flushPromises()
    await vi.advanceTimersByTimeAsync(1000)
    await flushPromises()

    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('2 个任务'), '批量同步到 U9C', expect.any(Object))
    expect(api.createMaterialSyncBatch).toHaveBeenCalledWith(['task-ready', 'task-failed'], 'token')
    expect(api.getMaterialSyncBatch).toHaveBeenCalledWith('batch-1', 'token')
    expect(api.executeMaterialSyncTask).not.toHaveBeenCalled()
    const result = wrapper.get('.material-sync-feedback')
    expect(result.text()).toContain('同步部分完成')
    expect(result.text()).toContain('共 2 项，完成 1 项，等待A1 BOM 0 项，失败 1 项')
    expect(result.text()).toContain('02010000102：U9C参数校验失败')
    expect(wrapper.get('.material-approval-feedback').text()).toContain('暂无结果')
    confirm.mockRestore()
    vi.useRealTimers()
  })

  it('批量同步只提交后端允许的任务状态并在结束后清除勾选', async () => {
    vi.useFakeTimers()
    const syncTasks = [
      {
        id: 'task-ready', materialId: 'material-ready', materialCode: '01020000001', materialName: '待同步料品', operation: 'Create' as const,
        status: 'PreviewReady' as const, correlationId: 'ready-v1', payloadJson: '{}', payloadSha256: 'READY', attemptCount: 0,
        createdAt: '2026-09-02T01:00:00Z', updatedAt: '2026-09-02T01:00:00Z',
      },
      {
        id: 'task-succeeded', materialId: 'material-succeeded', materialCode: '01020000002', materialName: '已同步料品', operation: 'Create' as const,
        status: 'Succeeded' as const, correlationId: 'succeeded-v1', payloadJson: '{}', payloadSha256: 'SUCCEEDED', attemptCount: 1,
        createdAt: '2026-09-02T01:01:00Z', updatedAt: '2026-09-02T01:01:00Z',
      },
    ]
    api.listMaterialSyncTasks.mockResolvedValue(syncTasks)
    api.listMaterialCodeApplications.mockResolvedValue([{
      id: 'application-succeeded', materialId: 'material-succeeded', applicationType: 'StandardBomItem', status: 'Approved', workflowState: 'PendingBomSync',
    }])
    api.createMaterialSyncBatch.mockResolvedValue({
      id: 'batch-filtered', status: 'Queued', requestedBy: 'admin', requestedRole: 'Administrator', totalCount: 1,
      completedCount: 0, succeededCount: 0, waitingCount: 0, failedCount: 0, createdAt: '2026-09-02T01:02:00Z', items: [],
    })
    api.getMaterialSyncBatch.mockResolvedValue({
      id: 'batch-filtered', status: 'Succeeded', requestedBy: 'admin', requestedRole: 'Administrator', totalCount: 1,
      completedCount: 1, succeededCount: 1, waitingCount: 0, failedCount: 0, createdAt: '2026-09-02T01:02:00Z',
      completedAt: '2026-09-02T01:03:00Z', items: [],
    })
    const confirm = vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false, requestedTab: 'tasks' },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    const table = wrapper.findAllComponents({ name: 'ElTable' }).find(component => component.classes().includes('material-sync-table'))!
    const selectionInputs = wrapper.findAll('.material-sync-table .el-table__body-wrapper input[type="checkbox"]')
    expect(selectionInputs).toHaveLength(2)
    expect(selectionInputs[0].attributes('disabled')).toBeUndefined()
    expect(selectionInputs[1].attributes('disabled')).toBeDefined()
    table.vm.$emit('selection-change', syncTasks)
    await flushPromises()

    const toolbar = wrapper.get('.material-sync-toolbar')
    expect(toolbar.text()).toContain('已选择 1 个可执行任务')
    await toolbar.get('button').trigger('click')
    await flushPromises()
    await vi.advanceTimersByTimeAsync(1000)
    await flushPromises()

    expect(api.createMaterialSyncBatch).toHaveBeenCalledWith(['task-ready'], 'token')
    expect(toolbar.text()).toContain('已选择 0 个可执行任务')
    confirm.mockRestore()
    vi.useRealTimers()
  })

  it('重复料号失败任务明确显示重新分配并同步入口', async () => {
    const conflictTask = {
      id: 'task-conflict', materialId: 'material-conflict', materialCode: '01020018749', materialName: '重复号标准件', operation: 'Create' as const,
      status: 'Failed' as const, correlationId: 'conflict-v1', payloadJson: '{}', payloadSha256: 'CONFLICT', attemptCount: 5,
      lastError: 'U9C已存在料号 01020018749，需要按最新分类流水重新分配。',
      createdAt: '2026-09-02T02:00:00Z', updatedAt: '2026-09-02T02:01:00Z',
    }
    api.listMaterialSyncTasks.mockResolvedValue([conflictTask])
    const confirm = vi.spyOn(ElMessageBox, 'confirm').mockRejectedValue('cancel')
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false, requestedTab: 'tasks' },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    const table = wrapper.findAllComponents({ name: 'ElTable' }).find(component => component.classes().includes('material-sync-table'))!
    const rowButtons = wrapper.get('.material-sync-table').findAll('button')
    const reassignButton = rowButtons.find(button => button.text() === '重新分配并同步')!
    expect(reassignButton.exists()).toBe(true)
    await reassignButton.trigger('click')
    await flushPromises()

    expect(confirm).toHaveBeenCalledWith(
      expect.stringContaining('按最新分类流水为 01020018749 重新分配料号'),
      '重新分配并同步',
      expect.objectContaining({ confirmButtonText: '确认重新分配并同步' }),
    )
    expect(api.executeMaterialSyncTask).not.toHaveBeenCalled()

    table.vm.$emit('selection-change', [conflictTask])
    await flushPromises()
    expect(wrapper.get('.material-sync-toolbar button').text()).toBe('批量重新分配并同步')
    confirm.mockRestore()
  })

  it('只读工程师不请求受限的同步批次，也不显示权限错误', async () => {
    api.listMaterialSyncBatches.mockRejectedValue(new Error('当前角色无权查看料号同步批次。'))
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: false, canApprove: false, canDecideMaterialCode: false, canManageIntegration: false, requestedTab: 'code-approvals' },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()
    expect(api.listMaterialSyncBatches).not.toHaveBeenCalled()
    expect(wrapper.get('.material-sync-feedback').text()).toContain('暂无结果')
    expect(wrapper.get('.material-sync-feedback').text()).not.toContain('读取失败')
    wrapper.unmount()
  })

  it('刷新页面后恢复本人运行中的后台批次并显示最终结果', async () => {
    vi.useFakeTimers()
    api.listMaterialSyncBatches.mockResolvedValue([{
      id: 'batch-resume', status: 'Running', requestedBy: 'admin', requestedRole: 'Administrator', totalCount: 3,
      completedCount: 1, succeededCount: 1, waitingCount: 0, failedCount: 0, currentMaterialCode: '01020000002',
      createdAt: '2026-09-01T00:00:00Z', startedAt: '2026-09-01T00:00:01Z', items: [],
    }])
    api.getMaterialSyncBatch.mockResolvedValue({
      id: 'batch-resume', status: 'Succeeded', requestedBy: 'admin', requestedRole: 'Administrator', totalCount: 3,
      completedCount: 3, succeededCount: 3, waitingCount: 0, failedCount: 0,
      createdAt: '2026-09-01T00:00:00Z', startedAt: '2026-09-01T00:00:01Z', completedAt: '2026-09-01T00:01:00Z',
      items: [],
    })

    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false, requestedTab: 'tasks' },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    expect(api.listMaterialSyncBatches).toHaveBeenCalledWith('token')
    expect(wrapper.text()).toContain('正在按料号升序同步第 2/3 项：01020000002')
    await vi.advanceTimersByTimeAsync(1000)
    await flushPromises()

    expect(api.getMaterialSyncBatch).toHaveBeenCalledWith('batch-resume', 'token')
    expect(wrapper.get('.material-sync-feedback').text()).toContain('同步完成')
    expect(wrapper.get('.material-sync-feedback').text()).toContain('共 3 项，完成 3 项')
    vi.useRealTimers()
  })
})
