import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import ProductionAllocationDialog from '../src/components/ProductionAllocationDialog.vue'
import * as api from '../src/api'
import type { ProductionReleaseRecord } from '../src/productionTracking'

vi.mock('../src/statusMessage', () => ({ ElMessage: { success: vi.fn(), warning: vi.fn() } }))
beforeEach(() => vi.spyOn(api, 'readProductionSuppliers').mockResolvedValue({ companyId: 'company', rowVersion: 1, categories: [{ id: 'category', name: '机加工', isActive: true }], suppliers: [{ id: 'supplier', code: 'S01', name: '外协厂', fullName: '外协厂有限公司', categoryIds: ['category'], isActive: true, address: '', phone: '', contact: '', contactPhone: '', remarks: '', updatedAt: '' }] }))
afterEach(() => vi.restoreAllMocks())
function record(): ProductionReleaseRecord {
  return { rowVersion: 7, state: { releasePackageId: 'release', projectId: 'project', companyId: 'company', projectCode: 'P001', projectName: '项目', packageNumber: 'RP001', scope: 'NonStandardWithDrawing', publishedAt: '', movements: [],
    lines: [{ id: 'line', materialKey: 'M', drawingNumber: 'D001', name: '机架', revision: 'W1', requiredQuantity: 10, unit: '001', priority: 'Normal' }],
    lots: [{ id: 'one', lineId: 'line', route: 'Self', quantity: 6, paused: false, frozen: false, promisedOn: '2026-11-01', schedule: [{ resourceGroupId: 'CNC组', date: '2026-11-01', hours: 8 }] }, { id: 'two', lineId: 'line', route: 'Self', quantity: 4, paused: false, frozen: false, schedule: [] }] } }
}
const create = (value: ProductionReleaseRecord) => mount(ProductionAllocationDialog, { props: { token: 'token', record: value, lot: value.state.lots[0] }, global: { plugins: [ElementPlus], stubs: { teleport: true } } })
describe('修改已有生产分配', () => {
  it('需求已分完仍可修改原批次方式，负责人和原因可留空，失败重试保持同一请求', async () => {
    const value = record()
    const save = vi.spyOn(api, 'updateProductionLotAllocation').mockRejectedValueOnce(new Error('保存结果未确认，请重试。')).mockResolvedValue(value)
    vi.spyOn(console, 'error').mockImplementation(() => {})
    const wrapper = create(value)
    await flushPromises()
    expect(wrapper.text()).not.toContain('最多可分配')
    await wrapper.get('input[aria-label="修改分配数量"]').setValue(7)
    await wrapper.get('form').trigger('submit')
    expect(save).not.toHaveBeenCalled()
    await wrapper.get('input[aria-label="修改分配数量"]').setValue(6)
    await wrapper.get('select[aria-label="修改分配方式"]').setValue('External')
    await flushPromises()
    await wrapper.get('.production-supplier-picker__supplier select').setValue('supplier')
    await wrapper.get('input[aria-label="修改分配负责人"]').setValue('')
    expect(wrapper.text()).toContain('清空原排程并释放工时')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(save.mock.calls[0]).toEqual(['release', expect.objectContaining({ lotId: 'one', route: 'External', quantity: 6, supplierId: 'supplier', supplier: '外协厂', expectedRowVersion: 7, reason: '', assignee: '' }), 'token'])
    expect(wrapper.get('fieldset').attributes()).toHaveProperty('disabled')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(save.mock.calls[1]).toEqual(save.mock.calls[0])
    expect(wrapper.emitted('updated')).toEqual([[value]])
    expect(value.state.lots[0].route).toBe('Self')
    wrapper.unmount()
  })
  it('外协改自制时清除供应商引用，冻结或已开工批次不允许提交', async () => {
    const value = record()
    value.state.lots[0] = { ...value.state.lots[0], route: 'External', supplier: '外协厂', supplierId: 'supplier', schedule: [] }
    const save = vi.spyOn(api, 'updateProductionLotAllocation').mockResolvedValue(value)
    const wrapper = create(value)
    await flushPromises()
    await wrapper.get('select[aria-label="修改分配方式"]').setValue('Self')
    await wrapper.get('input[placeholder="填写分配调整原因"]').setValue('改为内部加工')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(save.mock.calls[0][1]).toMatchObject({ lotId: 'one', route: 'Self', supplierId: null, supplier: '' })
    wrapper.unmount()
    save.mockClear(); value.state.lots[0].frozen = true
    const blocked = create(value)
    await flushPromises()
    await blocked.get('form').trigger('submit')
    expect(save).not.toHaveBeenCalled()
    blocked.unmount()
  })
})
