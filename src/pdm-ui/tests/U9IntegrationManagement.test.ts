import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import U9IntegrationManagement from '../src/components/U9IntegrationManagement.vue'

const api = vi.hoisted(() => ({
  getU9MaterialIntegration: vi.fn(),
  updateU9MaterialIntegration: vi.fn(),
  testU9MaterialIntegration: vi.fn(),
  previewU9MaterialSample: vi.fn(),
  importU9MaterialSample: vi.fn(),
}))

vi.mock('../src/api', () => api)

const settings = {
  baseUrl: 'http://u9.example.test/U9', enterpriseCode: '01', organizationCode: '7', userCode: 'pdm', clientId: 'PDM',
  clientSecretConfigured: true, itemCreatePath: '/webapi/ItemMaster/Create', itemQueryPath: '/webapi/ItemMaster/Query',
  itemModifyPath: '/webapi/ItemMaster/Modify', itemDeletePath: '/webapi/ItemMaster/Delete', unitCodeMappings: {}, writeEnabled: false,
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
    api.updateU9MaterialIntegration.mockImplementation(async input => ({ ...input, clientSecretConfigured: true }))
    api.testU9MaterialIntegration.mockResolvedValue({ ...settings, testedAt: '2026-08-20T00:00:00Z' })
    api.previewU9MaterialSample.mockResolvedValue({ categoryCodes: ['0101', '0102', '0204'], limitPerCategory: 10, queriedAt: '2026-08-20T00:00:00Z', items: [] })
  })

  it('以基础设置为首个选项卡，并把客户和料品接口分开', async () => {
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.find('.u9-integration-page > .pdm-pagebar').exists()).toBe(false)
    const tabs = wrapper.findAll('[role="tab"]')
    expect(tabs.map(tab => tab.text())).toEqual(expect.arrayContaining(['基础设置', '客户查询', '料品同步', '料品接口']))
    expect(tabs.find(tab => tab.text().includes('基础设置'))?.attributes('aria-selected')).toBe('true')
    expect(wrapper.get('input[name="u9BaseUrl"]').element).toHaveProperty('value', settings.baseUrl)

    await tabs.find(tab => tab.text().includes('客户查询'))!.trigger('click')
    await flushPromises()
    expect(wrapper.get('[aria-label="U9C客户同步"]').text()).toContain('测试客户')
    expect(wrapper.get('[aria-label="U9C客户同步"]').text()).toContain('GetCommonReference/Create')

    await wrapper.findAll('[role="tab"]').find(tab => tab.text().includes('料品接口'))!.trigger('click')
    await flushPromises()
    expect(wrapper.get('[aria-label="U9C料品接口设置"]').text()).toContain('PDM直接使用U9C计量单位编码')
    expect(wrapper.get('[aria-label="U9C料品接口设置"]').text()).not.toContain('新增映射')
    expect(wrapper.get('[aria-label="U9C料品接口设置"]').text()).toContain('启用人工确认后的真实写入')
  })

  it('料品同步固定为三类且每类最多10条，只执行预览请求', async () => {
    const wrapper = mountPage()
    await flushPromises()
    await wrapper.findAll('[role="tab"]').find(tab => tab.text().includes('料品同步'))!.trigger('click')
    await flushPromises()

    const panel = wrapper.get('[aria-label="U9C料品样本同步"]')
    expect(panel.text()).toContain('非全量')
    expect(panel.text()).toContain('每类最多10条')
    await panel.findAll('button').find(button => button.text().includes('只读预览'))!.trigger('click')
    await flushPromises()

    expect(api.previewU9MaterialSample).toHaveBeenCalledWith(['0101', '0102', '0204'], 10, 'token')
    expect(api.importU9MaterialSample).not.toHaveBeenCalled()
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
      unitCodeMappings: {},
    }), 'token')
  })

  it('保存料品接口时保留基础设置并清空旧单位映射', async () => {
    const wrapper = mountPage()
    await flushPromises()
    await wrapper.findAll('[role="tab"]').find(tab => tab.text().includes('料品接口'))!.trigger('click')
    await flushPromises()

    await wrapper.findAll('button').find(button => button.text().includes('保存料品接口'))!.trigger('click')
    await flushPromises()

    expect(api.updateU9MaterialIntegration).toHaveBeenCalledWith(expect.objectContaining({
      baseUrl: settings.baseUrl,
      clientId: settings.clientId,
      clientSecret: null,
      unitCodeMappings: {},
    }), 'token')
  })
})
