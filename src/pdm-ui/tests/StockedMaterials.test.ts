import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import StockedMaterials from '../src/components/StockedMaterials.vue'
import StockedMaterialSettings from '../src/components/StockedMaterialSettings.vue'

const api = vi.hoisted(() => ({ listStandardLibraryCategories: vi.fn(), listStockedMaterials: vi.fn(), listMaterials: vi.fn(),
  addStockedMaterials: vi.fn(), removeStockedMaterial: vi.fn(), maintainStockedMaterial: vi.fn(), readStockedMaterialPlan: vi.fn(), refreshStockedMaterials: vi.fn(),
  materialAttachmentObjectUrl: vi.fn(), listMaterialStockPlans: vi.fn(), readStockedMaterialSettings: vi.fn(), saveStockedMaterialSettings: vi.fn(),
  saveStandardLibraryCategory: vi.fn(), deleteStandardLibraryCategory: vi.fn(), listMaterialAttachments: vi.fn() }))
vi.mock('../src/api', () => api)
const category = { id: 'category', name: '气动元件', parentId: null, sortOrder: 1, isActive: true }
const material = { id: 'item', materialCode: '01020014079', name: '键盘', specification: 'K400+', brand: '罗技', unitCode: '001', isArchived: false, approvalStatus: 'Approved', u9SyncConfirmed: true }
const snapshot = { materialId: 'item', plan: { enterpriseCode: '001', organizationCode: '7', materialCode: '01020014079', safetyStockQuantity: 15, reorderQuantity: 50, isEffective: true, unitName: '个', syncedAt: '2026-10-26T00:00:00Z' }, stockQuantity: 5, inventorySyncedAt: '2026-10-26T00:00:00Z', isLowStock: true, lastError: null }
const settingsView = () => ({ settings: { syncIntervalMinutes: 240, notifyLowStock: true, useStandardAssistant: true, recipients: [] as string[] }, rowVersion: 1, users: [], effectiveRecipients: [], scope: { organizationCode: '7' }, lastSuccessAt: null as string | null, lastError: null as string | null })
let wrapper: ReturnType<typeof mount> | undefined
describe('常备件', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    api.refreshStockedMaterials.mockReset()
    api.listMaterialStockPlans.mockResolvedValue({ enterpriseCode: '001', organizationCode: '7', items: [] })
    api.listStandardLibraryCategories.mockResolvedValue([category])
    api.listStockedMaterials.mockResolvedValue({ items: [{ material, snapshot, categories: [category], coverImage: null }], total: 1, lowStockCount: 1, page: 1, pageSize: 50, scope: { organizationCode: '7' }, lastSuccessAt: null, lastError: null, canSynchronize: true, canManageSettings: false })
    api.readStockedMaterialSettings.mockReset().mockResolvedValue(settingsView())
  })
  afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = '' })
  it('shows the low stock warning and keeps the name as plain text', async () => {
    wrapper = mount(StockedMaterials, { props: { token: 'token', active: true, canManage: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    expect(wrapper.text()).toContain('低于安全库存 1 项，已置顶高亮')
    expect(wrapper.find('tr.is-low-stock').text()).toContain('键盘')
    expect(wrapper.find('tr.is-low-stock a').exists()).toBe(false)
    expect(wrapper.find('.pdm-stocked-material').exists()).toBe(false)
    expect(wrapper.text()).toContain('安全库存量')
    expect(wrapper.text()).toContain('再订货量')
  })
  it('preserves list data and displays a Chinese failure when readback fails', async () => {
    api.refreshStockedMaterials.mockRejectedValue(new Error('U9C库存数量回读失败，已保留上次成功数据。'))
    wrapper = mount(StockedMaterials, { props: { token: 'token', active: true, canManage: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '立即同步')!.trigger('click'); await flushPromises()
    expect(wrapper.find('[role="alert"]').text()).toContain('已保留上次成功数据')
    expect(wrapper.text()).toContain('键盘')
  })
  it('disables immediate sync without the assistant role and only reloads the list on refresh', async () => {
    api.listStockedMaterials.mockResolvedValue({ items: [{ material, snapshot, categories: [category], coverImage: null }], total: 1, lowStockCount: 1, page: 1, pageSize: 50, scope: { organizationCode: '7' }, lastSuccessAt: null, lastError: null, canSynchronize: false, canManageSettings: true })
    wrapper = mount(StockedMaterials, { props: { token: 'token', active: true, canManage: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    expect(wrapper.findAll('button').find(button => button.text() === '立即同步')!.attributes('disabled')).toBeDefined()
    await wrapper.findAll('button').find(button => button.text() === '刷新')!.trigger('click'); await flushPromises()
    expect(api.listStockedMaterials).toHaveBeenCalledTimes(2)
    expect(api.refreshStockedMaterials).not.toHaveBeenCalled()
  })
  it('defaults to four hours and warns when the assistant has no assigned users', async () => {
    wrapper = mount(StockedMaterialSettings, { props: { token: 'token' }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    expect((wrapper.find('input[aria-label="U9C自动回读间隔（小时）"]').element as HTMLInputElement).value).toBe('4.00')
    expect(wrapper.text()).toContain('未找到有效人员')
    api.saveStockedMaterialSettings.mockRejectedValue(new Error('常备件设置或回读数据已更新，请刷新后重新保存。'))
    await wrapper.findAll('button').find(button => button.text() === '保存设置')!.trigger('click'); await flushPromises()
    expect(api.saveStockedMaterialSettings).toHaveBeenCalledWith(expect.objectContaining({ syncIntervalMinutes: 240 }), 1, 'token')
    expect(wrapper.find('[role="alert"]').text()).toContain('请刷新后重新保存')
  })
  it('shows zero-parameter records as drafts and allows their parameters to be maintained without editing the material information', async () => {
    const draft = { ...snapshot, plan: { ...snapshot.plan, safetyStockQuantity: 0, reorderQuantity: 0 }, isFormal: false, state: 'Draft', isLowStock: false }
    api.listStockedMaterials.mockResolvedValue({ items: [{ material, snapshot: draft, categories: [category], coverImage: null }], total: 1, lowStockCount: 0, formalCount: 0, draftCount: 1, page: 1, pageSize: 50, scope: { organizationCode: '7' }, lastSuccessAt: null, lastError: null, canSynchronize: true })
    api.readStockedMaterialPlan.mockResolvedValue(draft)
    wrapper = mount(StockedMaterials, { attachTo: document.body, props: { token: 'token', active: true, canManage: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    expect(wrapper.text()).toContain('正式 0 项 · 草稿 1 项')
    expect(wrapper.find('tr.is-low-stock').exists()).toBe(false)
    await wrapper.find('.el-table__body tr label.el-checkbox').trigger('click'); await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '维护')!.trigger('click'); await flushPromises()
    expect(api.readStockedMaterialPlan).toHaveBeenCalledWith('item', 'token')
    const dialog = document.querySelector('.stocked-editor')!
    expect(dialog.querySelectorAll('.stocked-editor__readonly')).toHaveLength(6)
    expect(dialog.querySelectorAll('input')).toHaveLength(2)
    expect((dialog.querySelector('input[aria-label="安全库存量"]') as HTMLInputElement).value).toBe('0')
    expect((dialog.querySelector('input[aria-label="再订货量"]') as HTMLInputElement).value).toBe('0')
  })
  it('synchronizes immediately with a busy state while preserving unsaved settings and updating the data version', async () => {
    const latest = { ...settingsView(), rowVersion: 4, lastSuccessAt: '2026-10-26T01:00:00Z' }
    api.readStockedMaterialSettings.mockResolvedValueOnce(settingsView()).mockResolvedValueOnce(latest)
    let complete!: () => void
    api.refreshStockedMaterials.mockImplementation(() => new Promise<void>(resolve => { complete = resolve }))
    wrapper = mount(StockedMaterialSettings, { props: { token: 'token' }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    const interval = wrapper.find('input[aria-label="U9C自动回读间隔（小时）"]')
    await interval.setValue('6'); await interval.trigger('change')
    await wrapper.findAll('button').find(button => button.text() === '立即同步')!.trigger('click')
    expect(wrapper.find('[role="status"]').text()).toContain('正在从U9C回读')
    expect(wrapper.findAll('button').find(button => button.text() === '刷新设置')!.attributes('disabled')).toBeDefined()
    expect(api.refreshStockedMaterials).toHaveBeenCalledTimes(1)
    expect(api.saveStockedMaterialSettings).not.toHaveBeenCalled()
    complete(); await flushPromises()
    expect(api.listMaterialStockPlans).toHaveBeenCalledWith('token')
    expect(wrapper.find('[role="status"]').exists()).toBe(false)
    expect((interval.element as HTMLInputElement).value).toBe('6.00')
    expect(wrapper.text()).toContain('2026/10/26')
    api.saveStockedMaterialSettings.mockResolvedValue({ ...latest, settings: { ...latest.settings, syncIntervalMinutes: 360 } })
    await wrapper.findAll('button').find(button => button.text() === '保存设置')!.trigger('click'); await flushPromises()
    expect(api.saveStockedMaterialSettings).toHaveBeenCalledWith(expect.objectContaining({ syncIntervalMinutes: 360 }), 4, 'token')
  })
  it('shows readback failure and restores the immediate sync button for retry', async () => {
    api.refreshStockedMaterials.mockRejectedValue(new Error('U9C库存数量回读失败，已保留上次成功数据。'))
    wrapper = mount(StockedMaterialSettings, { props: { token: 'token' }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '立即同步')!.trigger('click'); await flushPromises()
    expect(wrapper.find('[role="alert"]').text()).toContain('已保留上次成功数据')
    expect(wrapper.findAll('button').find(button => button.text() === '立即同步')!.attributes('disabled')).toBeUndefined()
    expect(api.readStockedMaterialSettings).toHaveBeenCalledTimes(2)
  })
  it('does not adopt a new settings version when another user changes settings during sync', async () => {
    api.refreshStockedMaterials.mockResolvedValue(undefined)
    api.readStockedMaterialSettings.mockResolvedValueOnce(settingsView()).mockResolvedValueOnce({ ...settingsView(), rowVersion: 9, settings: { ...settingsView().settings, notifyLowStock: false } })
    wrapper = mount(StockedMaterialSettings, { props: { token: 'token' }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '立即同步')!.trigger('click'); await flushPromises()
    expect(wrapper.find('[role="alert"]').text()).toContain('设置已被其他人修改')
    api.saveStockedMaterialSettings.mockRejectedValue(new Error('设置已更新，请刷新。'))
    await wrapper.findAll('button').find(button => button.text() === '保存设置')!.trigger('click'); await flushPromises()
    expect(api.saveStockedMaterialSettings).toHaveBeenCalledWith(expect.objectContaining({ notifyLowStock: true }), 1, 'token')
  })
})
