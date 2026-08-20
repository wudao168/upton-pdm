import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus, { ElMessageBox } from 'element-plus'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import MaterialManagement from '../src/components/MaterialManagement.vue'

const api = vi.hoisted(() => ({
  listMaterials: vi.fn(),
  listMaterialCategories: vi.fn(),
  listMaterialSyncTasks: vi.fn(),
  getU9MaterialIntegration: vi.fn(),
  createMaterial: vi.fn(),
  updateMaterial: vi.fn(),
  changeApprovedMaterial: vi.fn(),
  archiveMaterial: vi.fn(),
  deleteMaterial: vi.fn(),
  getMaterialRemovalReadiness: vi.fn(),
  approveMaterial: vi.fn(),
  executeMaterialSyncTask: vi.fn(),
  retryMaterialSyncTask: vi.fn(),
  queryU9Material: vi.fn(),
  saveMaterialCategory: vi.fn(),
  calibrateMaterialCategoryCounter: vi.fn(),
  testU9MaterialIntegration: vi.fn(),
  updateU9MaterialIntegration: vi.fn(),
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
      isArchived: false, u9SyncConfirmed: false,
    }])
    api.listMaterialCategories.mockResolvedValue([
      { code: '01', name: '原材料', parentCode: null, pdmKind: null, defaultSupplyMode: 'Purchase', allowCreate: false, isVisible: true, isActive: true, numberPrefix: '01', sequenceLength: 7, counterScope: '01', sortOrder: 1, updatedBy: 'system', updatedAt: '2026-08-17T00:00:00Z', rowVersion: 1 },
      { code: '0101', name: '电气外购件', parentCode: '01', pdmKind: 'Electrical', defaultSupplyMode: 'Purchase', allowCreate: true, isVisible: true, isActive: true, numberPrefix: '0101', sequenceLength: 7, counterScope: '0101', sortOrder: 2, updatedBy: 'system', updatedAt: '2026-08-17T00:00:00Z', rowVersion: 1 },
      { code: '0102', name: '机械外购件', parentCode: '01', pdmKind: 'Standard', defaultSupplyMode: 'Purchase', allowCreate: true, isVisible: true, isActive: true, numberPrefix: '0102', sequenceLength: 7, counterScope: '0102', sortOrder: 3, updatedBy: 'system', updatedAt: '2026-08-17T00:00:00Z', rowVersion: 1 },
      { code: '0204', name: '非标机加件', parentCode: null, pdmKind: 'NonStandard', defaultSupplyMode: 'Manufacture', allowCreate: true, isVisible: true, isActive: true, numberPrefix: '0204', sequenceLength: 7, counterScope: '0204', sortOrder: 4, updatedBy: 'system', updatedAt: '2026-08-17T00:00:00Z', rowVersion: 1 },
    ])
    api.listMaterialSyncTasks.mockResolvedValue([])
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
      synchronizedDeleteAvailable: false, decision: 'PDM未发现引用；U9C引用查询合同尚未验证，同步删除保持关闭。',
    }))
  })

  it('加载料品且不在主档顶部显示U9C写入提示', async () => {
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    expect(wrapper.find('.el-alert').exists()).toBe(false)
    expect(wrapper.find('.material-header').exists()).toBe(false)
    expect(wrapper.findAll('[role="tab"]').map(tab => tab.text().trim())).toEqual(['料品主档', '同步任务', '分类维护'])
    const toolbar = wrapper.get('.material-toolbar')
    expect(toolbar.findAll('button').some(button => button.text() === '刷新')).toBe(true)
    expect(toolbar.findAll('button').some(button => button.text() === '新增料品')).toBe(true)
    expect(toolbar.text()).toContain('显示已停用')
    expect(toolbar.get('input[placeholder="搜索编码、名称、规格、品牌或分类"]')).toBeTruthy()
    expect(wrapper.text()).toContain('EL-001')
    expect(wrapper.text()).toContain('光电传感器')
    expect(wrapper.find('.material-table').text()).not.toContain('PDM业务类型')
    expect(wrapper.find('.material-table').text()).not.toContain('供给方式')
    expect(wrapper.text()).toContain('计量单位')
    expect(wrapper.text()).toContain('品牌')
    expect(wrapper.text()).toContain('欧姆龙')
    expect(wrapper.text()).toContain('表面处理')
    expect(wrapper.text()).toContain('0.15 kg')
    expect(wrapper.text()).toContain('测试备注')
    expect(wrapper.text()).toContain('料品采购链接')
    expect(wrapper.get('a[href="https://shop.example.test/item/EL-001"]').text()).toBe('打开')

    const ruleTab = wrapper.findAll('[role="tab"]').find(tab => tab.text().includes('分类维护'))
    await ruleTab!.trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('0101 电气外购件')
    expect(wrapper.text()).toContain('0204 非标机加件')
    expect(api.getU9MaterialIntegration).not.toHaveBeenCalled()
  })

  it('可按品牌筛选料品列表', async () => {
    api.listMaterials.mockResolvedValueOnce([
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
    api.listMaterials.mockResolvedValueOnce(Array.from({ length: 55 }, (_, index) => ({
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
    api.listMaterials.mockResolvedValueOnce([
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

  it('新增料品时必须主动选择分类后才生成料号', async () => {
    const wrapper = mount(MaterialManagement, {
      props: { token: 'token', canEdit: true, canApprove: true, canManageIntegration: false },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    const createButton = wrapper.findAll('button').find(button => button.text().includes('新增料品'))
    await createButton!.trigger('click')
    await flushPromises()

    const codeItem = wrapper.findAll('.el-form-item').find(item => item.text().includes('PDM物料编码'))!
    const codeInput = codeItem.get('input')
    expect(codeInput.attributes('disabled')).toBeDefined()
    expect(codeInput.attributes('placeholder')).toBe('选择开放分类后自动生成')
    const categoryItem = wrapper.findAll('.el-form-item').find(item => item.text().includes('U9C对应分类'))!
    expect((categoryItem.get('input').element as HTMLInputElement).value).toBe('')
    expect(categoryItem.text()).toContain('请选择U9C对应分类')
    expect(categoryItem.text()).toContain('新增时必须主动选择')
    expect(codeItem.text()).toContain('创建后不可修改')
    expect(codeItem.text()).toContain('逐号只读查询U9C')
    expect(wrapper.text()).toContain('001 个')
    expect(wrapper.text()).toContain('PDM直接保存并使用U9C计量单位编码')
    expect(wrapper.text()).toContain('料品采购链接')
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
      expect.stringContaining('仅PDM主控'),
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
      expect.stringContaining('本操作只停用PDM料品，不会停用或物理删除U9C料品'),
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
      decision: 'PDM未发现引用；若U9C存在，将先由U9C删除接口校验引用并删除，回查确认不存在后才删除PDM主档。',
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
      decision: 'U9C主控料品不允许从PDM发起物理删除。',
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

  it('查询U9C时同时显示PDM引用数量', async () => {
    api.getMaterialRemovalReadiness.mockResolvedValueOnce({
      materialId: 'material-1', materialCode: 'EL-001', pdmReferenceCount: 2, isPdmMaster: true,
      localDeletePreconditionsPassed: false, u9ReferenceCheckAvailable: false, synchronizedDeleteAvailable: false,
      decision: 'PDM中已有2处BOM引用，不能删除。',
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

    expect(wrapper.text()).toContain('一致·PDM引用2')
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

  it('已废止同步任务只允许查看请求，不能重试或执行', async () => {
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
    await wrapper.findAll('[role="tab"]').find(tab => tab.text().includes('同步任务'))!.trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('已废止')
    expect(wrapper.findAll('button').some(button => button.text() === '查看请求')).toBe(true)
    expect(wrapper.findAll('button').some(button => button.text() === '重试')).toBe(false)
    expect(wrapper.findAll('button').some(button => button.text() === '同步到U9C')).toBe(false)
  })
})
