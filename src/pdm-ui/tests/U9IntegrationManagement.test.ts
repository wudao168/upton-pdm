import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import U9IntegrationManagement from '../src/components/U9IntegrationManagement.vue'

const api = vi.hoisted(() => ({
  getU9MaterialIntegration: vi.fn(),
  getU9MaterialFullSyncStatus: vi.fn(),
  getU9InventorySyncStatus: vi.fn(),
  startU9MaterialFullSync: vi.fn(),
  startU9InventoryFullSync: vi.fn(),
  updateU9InventorySyncSettings: vi.fn(),
  updateU9MaterialIntegration: vi.fn(),
  testU9MaterialIntegration: vi.fn(),
  previewU9MaterialSample: vi.fn(),
  importU9MaterialSample: vi.fn(),
  queryU9Bom: vi.fn(),
  previewU9BomWrite: vi.fn(),
  executeU9BomWrite: vi.fn(),
}))

vi.mock('../src/api', () => api)

const settings = {
  baseUrl: 'http://u9.example.test/U9', enterpriseCode: '01', organizationCode: '7', userCode: 'pdm', clientId: 'PDM',
  clientSecretConfigured: true, itemCreatePath: '/webapi/ItemMaster/Create', itemQueryPath: '/webapi/ItemMaster/Query',
  itemModifyPath: '/webapi/ItemMaster/Modify', itemDeletePath: '/webapi/ItemMaster/Delete',
  customerQueryPath: '/webapi/GetCommonReference/Create', bomCreatePath: '/webapi/BOM/Create', bomQueryPath: '/webapi/BOM/Query',
  bomModifyPath: '/webapi/BOM/Modify', bomDeletePath: '/webapi/BOM/Delete', bomBatchUnapprovePath: '/webapi/BOM/BatchUnApprove',
  bomBipQueryPagePath: '/webapi/BOM/BIPQueryPage', unitCodeMappings: {}, writeEnabled: false,
}

const customerSettings = {
  baseUrl: settings.baseUrl, username: 'pdm', passwordConfigured: true, autoSyncEnabled: false, autoSyncIntervalMinutes: 60,
  lastSyncAt: null, lastSyncCount: 0, lastAutoSyncAttemptAt: null, lastAutoSyncError: null,
}

function mountPage() {
  return mount(U9IntegrationManagement, {
    props: {
      token: 'token', customers: [{ id: 'customer-1', code: 'C001', name: '测试客户', isActive: true }], customerSettings,
      pending: false, canManageBase: true, canManageCustomers: true,
      onSaveCustomerSettings: vi.fn(async input => ({ ...customerSettings, ...input, passwordConfigured: true })),
      onTestCustomerConnection: vi.fn(async () => ({ customerCount: 1, skippedCount: 0, testedAt: '2026-08-20T00:00:00Z' })),
      onSyncCustomers: vi.fn(async () => ({ customerCount: 1, skippedCount: 0, syncedAt: '2026-08-20T00:00:00Z', settings: customerSettings, customers: [] })),
    },
    global: { plugins: [ElementPlus] },
  })
}

describe('U9IntegrationManagement', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    api.getU9MaterialIntegration.mockResolvedValue({ ...settings })
    api.getU9MaterialFullSyncStatus.mockResolvedValue({
      scheduleTime: '02:00', checkIntervalMinutes: 30,
      categories: [{ code: '0101', name: '电气外购件' }, { code: '0302', name: '设备' }],
      latestRun: {
        id: 'run-1', triggerKind: 'Scheduled', status: 'Succeeded', categoryCodes: ['0101', '0302'],
        categoryCount: 2, completedCategoryCount: 2, discoveredCount: 120, createdCount: 10,
        refreshedCount: 108, skippedCount: 2, failedCategoryCount: 0,
        startedAt: '2026-09-01T18:00:00Z', completedAt: '2026-09-01T18:10:00Z',
        categoryResults: [{ categoryCode: '0302', categoryName: '设备', discoveredCount: 20, createdCount: 2, refreshedCount: 18, skippedCount: 0, maximumSequence: 5424, succeeded: true }],
      },
    })
    api.startU9MaterialFullSync.mockResolvedValue({ message: '全量同步已启动' })
    api.getU9InventorySyncStatus.mockResolvedValue({
      settings: { autoSyncEnabled: true, syncIntervalMinutes: 60, queryPath: '/webapi/Invtrans/QueryQohAndAvailable' },
      latestRun: {
        id: 'inventory-run-1', triggerKind: 'Scheduled', status: 'Succeeded', sourceRowCount: 200,
        storedRowCount: 120, materialCount: 45, startedAt: '2026-09-05T00:00:00Z', completedAt: '2026-09-05T00:01:00Z',
      },
    })
    api.startU9InventoryFullSync.mockResolvedValue({ message: '库存全量刷新已启动' })
    api.updateU9InventorySyncSettings.mockImplementation(async input => ({ ...input }))
    api.updateU9MaterialIntegration.mockImplementation(async input => ({ ...input, clientSecretConfigured: true }))
    api.testU9MaterialIntegration.mockResolvedValue({ ...settings, testedAt: '2026-08-20T00:00:00Z' })
    api.previewU9MaterialSample.mockResolvedValue({ categoryCodes: ['0101', '0102', '0204'], limitPerCategory: 10, queriedAt: '2026-08-20T00:00:00Z', items: [] })
    api.queryU9Bom.mockResolvedValue({
      queryPath: '/webapi/BOM/Query', requestPreview: '[{"Org":{"Code":"7"},"ItemMaster":{"Code":"03010000001"}}]', queriedAt: '2026-08-21T00:00:00Z',
      result: { responseCode: 0, boms: [{ itemCode: '03010000001', itemName: '测试设备', bomVersionCode: 'V1', organizationCode: '7', productUomCode: '001', lot: 1, status: 2, bomType: 1, bomSort: 0, projectMapNum: 'ASM-001', explain: '测试BOM', components: [{ sequence: 10, itemCode: '01020000057', itemName: '阀岛', usageQty: 2, issueUomCode: '001', parentQty: 1 }] }] },
    })
    api.previewU9BomWrite.mockResolvedValue({ operation: 0, path: '/webapi/BOM/Create', requestPreview: '[{"ItemMaster":{"Code":"02010003168"}}]', requestSha256: 'ABC123', baselineSha256: 'EMPTY', requiredConfirmation: '创建 02010003168/A1', addedComponentCount: 1, retainedHistoricalComponentCount: 0, generatedAt: '2026-08-21T00:00:00Z' })
    api.executeU9BomWrite.mockResolvedValue({ preview: {}, writeResult: { responseCode: 0, rows: [{ isSuccess: true }] }, verification: { responseCode: 0, boms: [] }, executedAt: '2026-08-21T00:01:00Z' })
  })

  it('把接口设置放在基础设置右侧，并统一展示客户料品和BOM接口', async () => {
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.find('.u9-integration-page > .pdm-pagebar').exists()).toBe(false)
    const tabs = wrapper.findAll('[role="tab"]')
    expect(tabs.map(tab => tab.text())).toEqual(['基础设置', '接口设置', '客户查询', '料品同步', 'BOM维护'])
    expect(tabs.find(tab => tab.text().includes('基础设置'))?.attributes('aria-selected')).toBe('true')
    expect(wrapper.get('input[name="u9BaseUrl"]').element).toHaveProperty('value', settings.baseUrl)

    await tabs.find(tab => tab.text().includes('客户查询'))!.trigger('click')
    await flushPromises()
    const customerPage = wrapper.get('[aria-label="U9C客户同步"]')
    expect(customerPage.text()).toContain('测试客户')
    expect(customerPage.text()).toContain('GetCommonReference/Create')
    expect(customerPage.text()).not.toContain('U9C服务地址')
    expect(customerPage.text()).not.toContain('U9C用户')
    expect(customerPage.find('input[name="u9CustomerBaseUrl"]').exists()).toBe(false)
    expect(customerPage.find('input[name="u9CustomerUser"]').exists()).toBe(false)
    expect(customerPage.find('.pdm-pagebar').exists()).toBe(false)
    expect(customerPage.find('.pdm-crm-customer-list').exists()).toBe(true)
    expect(customerPage.get('.pdm-manager-actions').findAll('button').map(button => button.text().trim())).toEqual(['测试连接', '保存同步计划', '从U9C同步'])

    await wrapper.findAll('[role="tab"]').find(tab => tab.text().includes('接口设置'))!.trigger('click')
    await flushPromises()
    const interfacePage = wrapper.get('[aria-label="U9C接口设置"]')
    expect(interfacePage.text()).toContain('客户接口')
    expect(interfacePage.text()).toContain('料品接口')
    expect(interfacePage.text()).toContain('库存接口')
    expect(interfacePage.text()).toContain('BOM接口')
    expect(interfacePage.get('input[name="u9CustomerQueryPath"]').element).toHaveProperty('value', settings.customerQueryPath)
    expect(interfacePage.get('input[name="u9InventoryQueryPath"]').element).toHaveProperty('value', '/webapi/Invtrans/QueryQohAndAvailable')
    expect(interfacePage.get('input[name="u9BomCreatePath"]').element).toHaveProperty('value', settings.bomCreatePath)
    expect(interfacePage.text()).toContain('启用人工确认后的真实写入')
  })

  it('料品同步显示动态分类的定时全量同步状态，不再提供样本导入', async () => {
    const wrapper = mountPage()
    await flushPromises()
    await wrapper.findAll('[role="tab"]').find(tab => tab.text().includes('料品同步'))!.trigger('click')
    await flushPromises()

    const panel = wrapper.get('[aria-label="U9C料品自动全量同步"]')
    expect(panel.text()).toContain('每日 02:00，每 30 分钟检查')
    expect(panel.text()).toContain('0302 设备')
    expect(panel.text()).toContain('发现料品120')
    expect(panel.text()).toContain('最大流水')
    expect(panel.text()).toContain('立即全量同步')
    expect(panel.text()).not.toContain('每类最多10条')
    expect(panel.text()).not.toContain('只读预览')
    expect(api.getU9MaterialFullSyncStatus).toHaveBeenCalledWith('token')
    expect(api.previewU9MaterialSample).not.toHaveBeenCalled()
    expect(api.importU9MaterialSample).not.toHaveBeenCalled()
    const inventoryPanel = wrapper.get('[aria-label="U9C库存自动全量刷新"]')
    expect(inventoryPanel.text()).toContain('每 60 分钟')
    expect(inventoryPanel.text()).toContain('库存快照明细120')
    expect(inventoryPanel.text()).toContain('覆盖料号45')
    expect(inventoryPanel.text()).toContain('立即全量刷新')
    expect(inventoryPanel.text()).not.toContain('库存查询接口路径')
    expect(inventoryPanel.find('input[name="u9InventoryQueryPath"]').exists()).toBe(false)
    expect(api.getU9InventorySyncStatus).toHaveBeenCalledWith('token')
  })

  it('保存基础设置时保留料品接口参数', async () => {
    const wrapper = mountPage()
    await flushPromises()

    await wrapper.get('input[name="u9OrganizationCode"]').setValue('8')
    await wrapper.findAll('button').find(button => button.text().includes('保存基础设置'))!.trigger('click')
    await flushPromises()

    expect(api.updateU9MaterialIntegration).toHaveBeenCalledWith(expect.objectContaining({
      organizationCode: '8',
      itemCreatePath: settings.itemCreatePath,
      bomCreatePath: settings.bomCreatePath,
      unitCodeMappings: {},
    }), 'token')
  })

  it('保存统一接口设置时同时提交客户料品库存和BOM路径', async () => {
    const wrapper = mountPage()
    await flushPromises()
    await wrapper.findAll('[role="tab"]').find(tab => tab.text().includes('接口设置'))!.trigger('click')
    await flushPromises()

    await wrapper.findAll('button').find(button => button.text().includes('保存接口设置'))!.trigger('click')
    await flushPromises()

    expect(api.updateU9MaterialIntegration).toHaveBeenCalledWith(expect.objectContaining({
      baseUrl: settings.baseUrl,
      clientId: settings.clientId,
      clientSecret: null,
      customerQueryPath: settings.customerQueryPath,
      bomCreatePath: settings.bomCreatePath,
      bomBipQueryPagePath: settings.bomBipQueryPagePath,
      unitCodeMappings: {},
    }), 'token')
    expect(api.updateU9InventorySyncSettings).toHaveBeenCalledWith({
      autoSyncEnabled: true,
      syncIntervalMinutes: 60,
      queryPath: '/webapi/Invtrans/QueryQohAndAvailable',
    }, 'token')
  })

  it('BOM维护可查询U9C并展示请求预览和子项', async () => {
    const wrapper = mountPage()
    await flushPromises()
    await wrapper.findAll('[role="tab"]').find(tab => tab.text().includes('BOM维护'))!.trigger('click')
    await flushPromises()

    const panel = wrapper.get('[aria-label="U9C BOM查询与维护"]')
    expect(panel.text()).toContain('当前只能查询')
    expect(panel.get('input[name="u9BomOrganizationCode"]').element).toHaveProperty('value', '7')
    await panel.get('input[name="u9BomItemCode"]').setValue('03010000001')
    await panel.get('input[name="u9BomVersionCode"]').setValue('V1')
    await panel.get('form').trigger('submit')
    await flushPromises()

    expect(api.queryU9Bom).toHaveBeenCalledWith(expect.objectContaining({ itemCode: '03010000001', bomVersionCode: 'V1' }), 'token')
    expect(panel.text()).toContain('/webapi/BOM/Query')
    expect(panel.text()).toContain('测试设备')
    expect(panel.text()).toContain('子项数')
  })

  it('BOM真实写入必须先预览并输入完全一致的确认文字', async () => {
    api.getU9MaterialIntegration.mockResolvedValueOnce({ ...settings, writeEnabled: true })
    const wrapper = mountPage()
    await flushPromises()
    await wrapper.findAll('[role="tab"]').find(tab => tab.text().includes('BOM维护'))!.trigger('click')
    await flushPromises()

    const panel = wrapper.get('[aria-label="U9C BOM查询与维护"]')
    await panel.findAll('button').find(button => button.text().includes('新建BOM'))!.trigger('click')
    await flushPromises()
    await wrapper.get('input[name="u9BomWriteItemCode"]').setValue('02010003168')
    expect(wrapper.get('input[name="u9BomWriteVersionCode"]').element).toHaveProperty('value', 'A1')
    expect(wrapper.get('input[name="u9BomWriteVersionCode"]').attributes('disabled')).toBeDefined()
    await wrapper.findAll('button').find(button => button.text().includes('生成请求预览'))!.trigger('click')
    await flushPromises()

    expect(api.previewU9BomWrite).toHaveBeenCalledWith(expect.objectContaining({ operation: 0, itemCode: '02010003168', bomVersionCode: 'A1' }), 'token')
    expect(document.body.textContent).toContain('/webapi/BOM/Create')
    const executeButton = Array.from(document.body.querySelectorAll('button')).find(button => button.textContent?.includes('确认执行并自动回查'))!
    expect(executeButton.disabled).toBe(true)
    expect(api.executeU9BomWrite).not.toHaveBeenCalled()
  })
})
