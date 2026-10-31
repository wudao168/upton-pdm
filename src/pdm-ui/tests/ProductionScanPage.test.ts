import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import ProductionScanPage from '../src/components/ProductionScanPage.vue'
import * as api from '../src/api'
import * as credentials from '../src/productionScanCredentials'
import type { ProductionReleaseRecord, ProductionScanResult } from '../src/productionTracking'

vi.mock('../src/components/ProductionCameraScanner.vue', () => ({ default: { template: '<div />' } }))
vi.mock('../src/productionScanCredentials', () => ({
  canRememberProductionScanCredentials: vi.fn(), readProductionScanCredentials: vi.fn(),
  saveProductionScanCredentials: vi.fn(), clearProductionScanCredentials: vi.fn(),
}))
const original: ProductionScanResult = { releasePackageId: 'original', lineId: 'original-line', packageNumber: '原发布包', drawingNumber: 'MODEL', name: '夹持座', revision: 'B', status: '执行中', requiredQuantity: 10, unassignedQuantity: 0, unit: '件' }
const supplement: ProductionScanResult = { ...original, releasePackageId: 'supplement', lineId: 'supplement-line', packageNumber: '增补发布包', isSupplement: true, requiredQuantity: 2, unassignedQuantity: 2, status: '待接收' }
function record(item: ProductionScanResult): ProductionReleaseRecord {
  return { rowVersion: 1, state: { releasePackageId: item.releasePackageId, projectId: 'project', projectCode: 'P001', projectName: '扫码增量', companyId: 'company', packageNumber: item.packageNumber,
    scope: item.isSupplement ? 'NonStandardSupplement' : 'NonStandardWithDrawing', publishedAt: '',
    lines: [{ id: item.lineId, materialKey: 'same-material', drawingNumber: 'MODEL', name: item.name, revision: 'B', unit: '件', requiredQuantity: item.requiredQuantity!, priority: 'Normal' }],
    lots: item.isSupplement ? [] : [{ id: 'original-lot', lineId: item.lineId, route: 'Self', quantity: 10, paused: false, frozen: false, schedule: [] }], movements: [] } }
}
beforeEach(() => {
  vi.clearAllMocks()
  sessionStorage.clear()
  vi.mocked(credentials.canRememberProductionScanCredentials).mockReturnValue(true)
  vi.mocked(credentials.readProductionScanCredentials).mockResolvedValue(null)
  vi.mocked(credentials.saveProductionScanCredentials).mockResolvedValue(undefined)
  vi.mocked(credentials.clearProductionScanCredentials).mockResolvedValue(undefined)
  vi.spyOn(api, 'login').mockResolvedValue({ accessToken: 'token', username: 'admin', activeCompanyId: 'company' } as Awaited<ReturnType<typeof api.login>>)
  vi.spyOn(api, 'listMaterialStockPlans').mockResolvedValue({ enterpriseCode: '', organizationCode: '', items: [] })
  vi.spyOn(api, 'readProductionTrackingRelease').mockImplementation(async id => record(id === 'original' ? original : supplement))
  vi.spyOn(api, 'readProductionActions').mockImplementation(async id => ({ actions: [], hint: '请核对批次分配。', relatedDemands: id === 'original'
    ? [{ ...supplement, relatedToLineId: 'original-line' }] : [{ ...original, relatedToLineId: 'supplement-line' }] }))
})
afterEach(() => vi.restoreAllMocks())
async function openAndScan() {
  const wrapper = mount(ProductionScanPage)
  await flushPromises()
  await wrapper.get('input[autocomplete=username]').setValue('admin')
  await wrapper.get('input[autocomplete=current-password]').setValue('test-only')
  await wrapper.get('form').trigger('submit'); await flushPromises()
  await wrapper.get('#production-scan-code').setValue('MODEL\nB')
  await wrapper.get('.production-scan-page__scan').trigger('submit'); await flushPromises()
  return wrapper
}
describe('生产现场记住账号和密码', () => {
  it('默认不勾选，登录成功后不保存密码', async () => {
    const wrapper = mount(ProductionScanPage)
    await flushPromises()
    expect((wrapper.get('input[type=checkbox]').element as HTMLInputElement).checked).toBe(false)
    await wrapper.get('input[autocomplete=username]').setValue('admin')
    await wrapper.get('input[type=password]').setValue('test-only')
    await wrapper.get('form').trigger('submit'); await flushPromises()
    expect(credentials.saveProductionScanCredentials).not.toHaveBeenCalled()
    expect(credentials.clearProductionScanCredentials).toHaveBeenCalled()
    expect(wrapper.find('#production-scan-code').exists()).toBe(true)
    wrapper.unmount()
  })
  it('勾选后仅在登录成功时保存，重新打开和过期后回填但不自动登录', async () => {
    const saved = { username: 'admin', password: 'test-only' }
    const wrapper = mount(ProductionScanPage)
    await flushPromises()
    await wrapper.get('input[autocomplete=username]').setValue(saved.username)
    await wrapper.get('input[type=password]').setValue(saved.password)
    await wrapper.get('input[type=checkbox]').setValue(true)
    expect(credentials.saveProductionScanCredentials).not.toHaveBeenCalled()
    await wrapper.get('form').trigger('submit'); await flushPromises()
    expect(credentials.saveProductionScanCredentials).toHaveBeenCalledWith(saved)
    vi.mocked(credentials.readProductionScanCredentials).mockResolvedValue(saved)
    window.dispatchEvent(new Event('pdm-session-expired')); await flushPromises()
    expect((wrapper.get('input[type=password]').element as HTMLInputElement).value).toBe(saved.password)
    expect((wrapper.get('input[type=checkbox]').element as HTMLInputElement).checked).toBe(true)
    wrapper.unmount()
    const reopened = mount(ProductionScanPage)
    await flushPromises()
    expect((reopened.get('input[autocomplete=username]').element as HTMLInputElement).value).toBe(saved.username)
    expect((reopened.get('input[type=password]').element as HTMLInputElement).value).toBe(saved.password)
    expect(api.login).toHaveBeenCalledTimes(1)
    reopened.unmount()
  })
  it('取消勾选立即删除保存的凭据', async () => {
    vi.mocked(credentials.readProductionScanCredentials).mockResolvedValue({ username: 'admin', password: 'test-only' })
    const wrapper = mount(ProductionScanPage)
    await flushPromises()
    await wrapper.get('input[type=checkbox]').setValue(false); await flushPromises()
    expect(credentials.clearProductionScanCredentials).toHaveBeenCalledTimes(1)
    expect((wrapper.get('input[type=checkbox]').element as HTMLInputElement).checked).toBe(false)
    wrapper.unmount()
  })
  it('登录失败不保存密码，保存失败也不阻止成功登录', async () => {
    vi.mocked(api.login).mockRejectedValueOnce(new Error('账号或密码不正确'))
    const wrapper = mount(ProductionScanPage)
    await flushPromises()
    await wrapper.get('input[autocomplete=username]').setValue('admin')
    await wrapper.get('input[type=password]').setValue('test-only')
    await wrapper.get('input[type=checkbox]').setValue(true)
    await wrapper.get('form').trigger('submit'); await flushPromises()
    expect(credentials.saveProductionScanCredentials).not.toHaveBeenCalled()
    expect(wrapper.get('[role=alert]').text()).toContain('账号或密码不正确')
    vi.mocked(credentials.saveProductionScanCredentials).mockRejectedValueOnce(new Error('storage unavailable'))
    await wrapper.get('form').trigger('submit'); await flushPromises()
    expect(wrapper.get('[role=status]').text()).toContain('已登录，但账号和密码未能保存')
    expect(wrapper.find('#production-scan-code').exists()).toBe(true)
    wrapper.unmount()
  })
  it('不支持加密保存时提供HTTPS提示并保留手动登录', async () => {
    vi.mocked(credentials.canRememberProductionScanCredentials).mockReturnValue(false)
    const wrapper = mount(ProductionScanPage)
    await flushPromises()
    expect(wrapper.get('input[type=checkbox]').attributes('disabled')).toBeDefined()
    expect(wrapper.text()).toContain('请使用 HTTPS 地址记住账号和密码')
    expect(wrapper.get('button[type=submit]').attributes('disabled')).toBeUndefined()
    wrapper.unmount()
  })
})
describe('同版本已发布增量扫码', () => {
  it('图纸扫码显示原10件及增补2件，选择增补进入其真实需求', async () => {
    vi.spyOn(api, 'resolveProductionScan').mockResolvedValue([original, supplement])
    const wrapper = await openAndScan()
    const choices = wrapper.get('.production-scan-page__choices')
    expect(choices.text()).toContain('原需求 10 件')
    expect(choices.text()).toContain('增补需求 2 件；待分配 2；待接收')
    await choices.findAll('button')[1].trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('本发布包增补需求：2 件')
    expect(wrapper.text()).toContain('该需求尚未分配生产批次')
    expect(api.readProductionTrackingRelease).toHaveBeenLastCalledWith('supplement', 'token')
    wrapper.unmount()
  })
  it('旧批次二维码直接定位原批次，并可从增补提示跳转', async () => {
    vi.spyOn(api, 'resolveProductionScan').mockResolvedValue([{ ...original, lotId: 'original-lot', lotQuantity: 10 }])
    const wrapper = await openAndScan()
    expect(wrapper.find('.production-scan-page__choices').exists()).toBe(false)
    expect(wrapper.text()).toContain('当前批次：10')
    const notice = wrapper.get('[aria-label="同版本已发布需求"]')
    expect(notice.text()).toContain('增补需求 2 件')
    await notice.get('button').trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('本发布包增补需求：2 件')
    expect(wrapper.text()).not.toContain('当前批次：10')
    wrapper.unmount()
  })
})
